using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using AutoReportStudio.Web.Models;
namespace AutoReportStudio.Web.Services;

// Container for Ollama generation result with token tracking
public class OllamaGenerationResult
{
    public string Text { get; set; } = "";
    public long TokensGenerated { get; set; } = 0;
}

// Sanitizes raw LLM JSON output by escaping unescaped control characters (e.g. literal
// newlines/tabs inside multi-line SQL) that appear within JSON string literals, which
// would otherwise cause System.Text.Json to throw "invalid within a JSON string".
static class JsonSanitizer
{
    public static string SanitizeControlCharsInStrings(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        // Narrow to the outermost JSON object/array if there is surrounding text (e.g. markdown fences)
        var start = json.IndexOfAny(new[] { '{', '[' });
        var end = json.LastIndexOfAny(new[] { '}', ']' });
        var span = (start >= 0 && end > start) ? json.Substring(start, end - start + 1) : json;

        var sb = new System.Text.StringBuilder(span.Length + 16);
        var inString = false;

        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];

            if (inString)
            {
                if (c == '\\')
                {
                    // Look ahead to determine if this is a valid JSON escape sequence.
                    // Valid: \" \\ \/ \b \f \n \r \t \uXXXX
                    var next = (i + 1 < span.Length) ? span[i + 1] : '\0';
                    switch (next)
                    {
                        case '"':
                        case '\\':
                        case '/':
                        case 'b':
                        case 'f':
                        case 'n':
                        case 'r':
                        case 't':
                            sb.Append(c).Append(next);
                            i++;
                            continue;
                        case 'u':
                            // Only treat as a valid unicode escape if followed by 4 hex digits
                            if (i + 5 < span.Length && IsHex(span[i + 2]) && IsHex(span[i + 3]) && IsHex(span[i + 4]) && IsHex(span[i + 5]))
                            {
                                sb.Append(c).Append(next);
                                i++;
                                continue;
                            }
                            // Invalid \u escape - escape the backslash itself so it is treated literally
                            sb.Append("\\\\");
                            continue;
                        default:
                            // Invalid escape sequence (e.g. "\)"). Escape the backslash itself
                            // so the LLM's stray backslash is preserved as a literal character
                            // instead of producing an invalid JSON escape.
                            sb.Append("\\\\");
                            continue;
                    }
                }

                if (c == '"')
                {
                    sb.Append(c);
                    inString = false;
                    continue;
                }

                switch (c)
                {
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            else
            {
                sb.Append(c);
                if (c == '"') inString = true;
            }
        }

        return sb.ToString();
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

static class SqlValidator
{
    public static void ValidateAndRepairPlan(ReportPlan plan, DbSchema schema, ILogger logger)
    {
        if (plan == null || schema == null) return;

        DbTable? FindTable(string sch, string name)
        {
            return schema.Tables.FirstOrDefault(t => string.Equals(t.Schema, sch, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        string? MatchColumnName(string target, List<DbColumn> cols)
        {
            if (cols == null) return null;

            // Exact match ONLY - no fuzzy matching
            var match = cols.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Name;

            // No repair - column not found exactly
            return null;
        }

        var tableAliasRegex = new Regex(@"\b(?:FROM|JOIN)\s+\[([^\]]+)\]\.\[([^\]]+)\]\s+(?:AS\s+)?([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        var aliasColRegex = new Regex(@"\b([A-Za-z0-9_]+)\.\[([^\]]+)\]", RegexOptions.IgnoreCase);
        var unqualifiedColRegex = new Regex(@"\[([^\]]+)\](?=\s*(?:,|FROM|WHERE|GROUP|ORDER|HAVING|;|$))", RegexOptions.IgnoreCase);

        foreach (var sec in plan.Sections)
        {
            if (string.IsNullOrWhiteSpace(sec.Sql)) continue;
            var sql = sec.Sql;
            var aliasMap = new Dictionary<string, (string sch, string tbl)>();
            foreach (Match m in tableAliasRegex.Matches(sql))
            {
                aliasMap[m.Groups[3].Value] = (m.Groups[1].Value, m.Groups[2].Value);
            }

            var repaired = sql;
            var madeChange = false;

            // Check alias-qualified columns
            foreach (Match m in aliasColRegex.Matches(sql))
            {
                var alias = m.Groups[1].Value;
                var col = m.Groups[2].Value;
                if (!aliasMap.ContainsKey(alias)) continue;
                var (sch, tbl) = aliasMap[alias];
                var table = FindTable(sch, tbl);
                if (table == null) continue;
                var found = table.Columns.FirstOrDefault(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase));
                if (found != null) continue;
                var candidate = MatchColumnName(col, table.Columns);
                if (!string.IsNullOrEmpty(candidate))
                {
                    var oldToken = $"{alias}.[{col}]";
                    var newToken = $"{alias}.[{candidate}]";
                    repaired = repaired.Replace(oldToken, newToken);
                    logger.LogInformation("Repaired column reference in section '{Heading}': {Old} -> {New}", sec.Heading, oldToken, newToken);
                    madeChange = true;
                }
                else
                {
                    logger.LogWarning("Missing column '{Col}' for table {Schema}.{Table} (alias {Alias}) in generated SQL for section '{Heading}'. Check if this is a typo or if the column exists in the schema.", col, sch, tbl, alias, sec.Heading);
                }
            }

            if (madeChange) sec.Sql = repaired;
        }
    }

    private static readonly Regex AggregateFuncRegex = new(
        @"\b(COUNT|SUM|AVG|MIN|MAX)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TableAliasExtractRegex = new(
        @"\b(?:FROM|JOIN)\s+\[([^\]]+)\]\.\[([^\]]+)\]\s+(?:AS\s+)?([A-Za-z0-9_]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StringLiteralComparisonRegex = new(
        @"\b([A-Za-z0-9_]+)\.\[([^\]]+)\]\s*(?:=|LIKE)\s*N?'([^']*)'",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Extracts the alias -> (schema, table) map from a query's FROM/JOIN clauses.
    /// </summary>
    public static Dictionary<string, (string Schema, string Table)> ExtractAliasMap(string sql)
    {
        var aliasMap = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sql)) return aliasMap;

        foreach (Match m in TableAliasExtractRegex.Matches(sql))
        {
            aliasMap[m.Groups[3].Value] = (m.Groups[1].Value, m.Groups[2].Value);
        }
        return aliasMap;
    }

    /// <summary>
    /// Extracts alias-qualified string literal equality/LIKE comparisons (e.g. ast.[StatusName] = 'Absent')
    /// so callers can verify the literal actually exists in the referenced column's data.
    /// </summary>
    public static List<(string Alias, string Column, string Value)> ExtractStringLiteralComparisons(string sql)
    {
        var results = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(sql)) return results;

        foreach (Match m in StringLiteralComparisonRegex.Matches(sql))
        {
            results.Add((m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
        }
        return results;
    }

    /// <summary>
    /// Detects nested aggregate function calls (e.g. AVG(COUNT(*)), SUM(AVG(x)), AVG(CAST(COUNT(*) AS float)))
    /// which SQL Server rejects with "Cannot perform an aggregate function on an expression containing
    /// an aggregate or a subquery." Returns a description of the first offending nested call found, or
    /// null if no nested aggregates are detected.
    /// </summary>
    public static string? FindNestedAggregate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;

        foreach (Match outer in AggregateFuncRegex.Matches(sql))
        {
            var outerFunc = outer.Groups[1].Value;
            var openParenIndex = outer.Index + outer.Length - 1; // index of the '(' just matched

            // Find the matching closing parenthesis for this aggregate call
            var depth = 0;
            var closeIndex = -1;
            for (var i = openParenIndex; i < sql.Length; i++)
            {
                if (sql[i] == '(') depth++;
                else if (sql[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeIndex = i;
                        break;
                    }
                }
            }

            if (closeIndex < 0) continue; // unbalanced parens, let other validation catch it

            var inner = sql.Substring(openParenIndex + 1, closeIndex - openParenIndex - 1);

            // Look for another aggregate function call inside this one's argument list.
            // (CAST/CONVERT wrapping is fine to skip through since we search the whole inner text.)
            var innerMatch = AggregateFuncRegex.Match(inner);
            if (innerMatch.Success)
            {
                // Ignore the case where the "nested" match is actually inside a subquery's own
                // SELECT that is itself wrapped as a scalar subquery correlated differently - a
                // simple heuristic: if inner contains "SELECT", treat it as a subquery (allowed
                // pattern like AVG((SELECT COUNT(*) ...)) is unusual but not the common failure mode).
                if (inner.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
                    continue;

                var innerFunc = innerMatch.Groups[1].Value;
                return $"{outerFunc}(...{innerFunc}(...)...) - nested aggregate functions are not allowed; use a CTE or subquery to compute the inner aggregate first";
            }
        }

        return null;
    }

    private static readonly Regex GroupByClauseRegex = new(
        @"\bGROUP\s+BY\s+(?<list>.+?)(?=\b(?:ORDER\s+BY|HAVING|;)|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Detects GROUP BY clauses whose grouping items are all string/numeric literals
    /// (e.g. GROUP BY 'Active') rather than actual column references. SQL Server rejects
    /// this with "Each GROUP BY expression must contain at least one column that is not
    /// an outer reference." Returns a description of the offending clause, or null if the
    /// GROUP BY clause contains at least one real column reference.
    /// </summary>
    public static string? FindGroupByLiteralOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return null;

        var m = GroupByClauseRegex.Match(sql);
        if (!m.Success) return null;

        var list = m.Groups["list"].Value.Trim().TrimEnd(')');

        // Split on top-level commas (grouping items are rarely nested with parens here).
        var items = list.Split(',').Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
        if (items.Count == 0) return null;

        var allLiterals = items.All(i =>
            (i.StartsWith("'") && i.EndsWith("'")) ||
            (i.StartsWith("N'") && i.EndsWith("'")) ||
            Regex.IsMatch(i, @"^-?\d+(\.\d+)?$"));

        if (allLiterals)
        {
            return $"GROUP BY {list} - grouping by only a constant literal is not allowed; group by an actual column, or remove the GROUP BY and use the literal directly in the SELECT list";
        }

        return null;
    }

    public static string? GetInvalidColumnErrors(ReportPlan plan, DbSchema schema, ILogger logger)
    {
        if (plan == null || schema == null) return null;

        DbTable? FindTable(string sch, string name)
        {
            return schema.Tables.FirstOrDefault(t => string.Equals(t.Schema, sch, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        var tableAliasRegex = new Regex(@"\b(?:FROM|JOIN)\s+\[([^\]]+)\]\.\[([^\]]+)\]\s+(?:AS\s+)?([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        var cteNameRegex = new Regex(@"\bWITH\s+([A-Za-z0-9_]+)\s+AS\s*\(", RegexOptions.IgnoreCase);
        var aliasColRegex = new Regex(@"\b([A-Za-z0-9_]+)\.\[([^\]]+)\]", RegexOptions.IgnoreCase);
        var unqualifiedInJoinRegex = new Regex(@"\bON\s+([^\=]+)\s*=", RegexOptions.IgnoreCase);
        var errors = new System.Text.StringBuilder();

        foreach (var sec in plan.Sections)
        {
            if (string.IsNullOrWhiteSpace(sec.Sql)) continue;
            var sql = sec.Sql;
            var aliasMap = new Dictionary<string, (string sch, string tbl)>();

            // Find CTE names
            var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in cteNameRegex.Matches(sql))
            {
                cteNames.Add(m.Groups[1].Value);
            }

            // Find table aliases from FROM/JOIN
            foreach (Match m in tableAliasRegex.Matches(sql))
            {
                aliasMap[m.Groups[3].Value] = (m.Groups[1].Value, m.Groups[2].Value);
            }

            // Check for alias-qualified columns
            foreach (Match m in aliasColRegex.Matches(sql))
            {
                var alias = m.Groups[1].Value;
                var col = m.Groups[2].Value;

                // Skip if it's a CTE reference or known alias
                if (cteNames.Contains(alias)) continue;

                if (!aliasMap.ContainsKey(alias))
                {
                    errors.AppendLine($"Section '{sec.Heading}': Undefined table alias '{alias}' in reference {alias}.[{col}]. All tables must be aliased in FROM/JOIN clauses.");
                    continue;
                }

                var (sch, tbl) = aliasMap[alias];
                var table = FindTable(sch, tbl);

                if (table == null)
                {
                    errors.AppendLine($"Section '{sec.Heading}': Table {sch}.{tbl} not found in schema");
                    continue;
                }

                var found = table.Columns.FirstOrDefault(c => string.Equals(c.Name, col, StringComparison.OrdinalIgnoreCase));
                if (found == null)
                {
                    var validCols = string.Join(", ", table.Columns.Select(c => c.Name).OrderBy(x => x));
                    errors.AppendLine($"Section '{sec.Heading}': Invalid column '{col}' in table {sch}.{tbl}. Valid columns are: {validCols}");
                }
            }

            // Check for unqualified columns in JOIN ON clauses
            foreach (Match m in unqualifiedInJoinRegex.Matches(sql))
            {
                var joinCondition = m.Groups[1].Value.Trim();
                // Look for unqualified column references (no dot before bracket)
                if (System.Text.RegularExpressions.Regex.IsMatch(joinCondition, @"\b(?<!\.)(?<![A-Za-z0-9_])\["))
                {
                    errors.AppendLine($"Section '{sec.Heading}': Unqualified column reference in JOIN ON clause: '{joinCondition}'. All columns in JOIN conditions must use table aliases (e.g., e.[EmployeeID], not [EmployeeID])");
                }
            }
        }

        return errors.Length > 0 ? errors.ToString() : null;
    }
}

// Response shape returned by the Ollama /api/generate endpoint
public class OllamaResponse
{
    public string? response { get; set; }
    public bool? done { get; set; }
    public long eval_count { get; set; } = 0;
    public long prompt_eval_count { get; set; } = 0;
}

// Model metadata returned by the Ollama /api/tags endpoint
public class OllamaModelInfo
{
    public string Name { get; set; } = "";
    public string? ParameterSize { get; set; }
    public long SizeBytes { get; set; } = 0;
}

public interface IOllamaService 
{ 
    Task<List<string>> ListModels(CancellationToken ct = default);
    Task<List<OllamaModelInfo>> ListModelsWithDetails(CancellationToken ct = default);
    Task<ReportPlan> Plan(DbSchema schema, string request, string? selectedModel = null, CancellationToken ct = default, string? connectionString = null); 
    Task<string> Summarize(ReportResult report, string? selectedModel = null, CancellationToken ct = default);
    long GetTotalTokensGenerated();
    void ResetTokenCount();
}

public class OllamaService : IOllamaService
{
    readonly IHttpClientFactory f; 
    readonly IConfiguration cfg;
    readonly ILogger<OllamaService> logger;
    readonly ILLMCorrectionService correctionService;
    readonly ISchemaService schemaService;
    private long totalTokensGenerated = 0;

    public OllamaService(IHttpClientFactory f, IConfiguration cfg, ILogger<OllamaService> l, ILLMCorrectionService correctionService, ISchemaService schemaService) 
    { 
        this.f = f; 
        this.cfg = cfg;
        logger = l;
        this.correctionService = correctionService;
        this.schemaService = schemaService;
    }

    public long GetTotalTokensGenerated()
    {
        return totalTokensGenerated;
    }

    public void ResetTokenCount()
    {
        totalTokensGenerated = 0;
        logger.LogDebug("Token count reset to 0");
    }

    public async Task<List<string>> ListModels(CancellationToken ct = default)
    {
        logger.LogDebug("Entering ListModels method");
        try
        {
            var client = f.CreateClient();
            var url = (cfg["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
            logger.LogInformation("Fetching available models from Ollama at {Url}", url);

            var res = await client.GetAsync(url + "/api/tags", ct);
            logger.LogDebug("Ollama tags API response status: {StatusCode}", res.StatusCode);

            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to fetch models from Ollama, status: {StatusCode}", res.StatusCode);
                return new List<string>();
            }

            var responseBody = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var nameElement))
                    {
                        var modelName = nameElement.GetString();
                        if (!string.IsNullOrEmpty(modelName))
                        {
                            models.Add(modelName);
                        }
                    }
                }
            }

            logger.LogInformation("Successfully fetched {ModelCount} models from Ollama", models.Count);
            return models;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to connect to Ollama for listing models");
            return new List<string>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing models from Ollama: {ErrorMessage}", ex.Message);
            return new List<string>();
        }
        finally
        {
            logger.LogDebug("Exiting ListModels method");
        }
    }

    public async Task<List<OllamaModelInfo>> ListModelsWithDetails(CancellationToken ct = default)
    {
        logger.LogDebug("Entering ListModelsWithDetails method");
        try
        {
            var client = f.CreateClient();
            var url = (cfg["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
            logger.LogInformation("Fetching available models with details from Ollama at {Url}", url);

            var res = await client.GetAsync(url + "/api/tags", ct);
            logger.LogDebug("Ollama tags API response status: {StatusCode}", res.StatusCode);

            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to fetch models from Ollama, status: {StatusCode}", res.StatusCode);
                return new List<OllamaModelInfo>();
            }

            var responseBody = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseBody);

            var models = new List<OllamaModelInfo>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (!model.TryGetProperty("name", out var nameElement))
                    {
                        continue;
                    }

                    var modelName = nameElement.GetString();
                    if (string.IsNullOrEmpty(modelName))
                    {
                        continue;
                    }

                    var info = new OllamaModelInfo { Name = modelName };

                    if (model.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var sizeBytes))
                    {
                        info.SizeBytes = sizeBytes;
                    }

                    if (model.TryGetProperty("details", out var detailsElement) &&
                        detailsElement.TryGetProperty("parameter_size", out var paramSizeElement))
                    {
                        info.ParameterSize = paramSizeElement.GetString();
                    }

                    models.Add(info);
                }
            }

            logger.LogInformation("Successfully fetched {ModelCount} models with details from Ollama", models.Count);
            return models;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Failed to connect to Ollama for listing models with details");
            return new List<OllamaModelInfo>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error listing models with details from Ollama: {ErrorMessage}", ex.Message);
            return new List<OllamaModelInfo>();
        }
        finally
        {
            logger.LogDebug("Exiting ListModelsWithDetails method");
        }
    }

    private static string BuildColumnReferenceGuide(DbSchema schema)
    {
        if (schema?.Tables == null || schema.Tables.Count == 0)
            return "No tables available in schema.";

        var sb = new System.Text.StringBuilder();
        foreach (var table in schema.Tables.OrderBy(t => t.Name))
        {
            sb.AppendLine($"[{table.Schema}].[{table.Name}]");
            if (table.Columns != null && table.Columns.Count > 0)
            {
                foreach (var col in table.Columns.OrderBy(c => c.Name))
                {
                    var line = $"  - [{col.Name}] ({col.Type})" + (col.PrimaryKey ? " [PK]" : "");
                    if (col.SampleValues != null && col.SampleValues.Count > 0)
                    {
                        line += $" [Actual values: {string.Join(", ", col.SampleValues.Select(v => $"'{v}'"))}]";
                    }
                    sb.AppendLine(line);
                }
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public async Task<ReportPlan> Plan(DbSchema schema, string request, string? selectedModel = null, CancellationToken ct = default, string? connectionString = null)
    {
        logger.LogDebug("Entering Plan method");
        try
        {
            logger.LogInformation("Generating report plan with request: {Request}", request);

            var prompt = $@"You are a SQL Server report and visualization planner.

Your job is to analyze the user's reporting request and create a report plan using ONLY the supplied database schema.

GENERAL RULES

* Return valid JSON only.
* When generating SQL, make certain that all fields joined are of the correct data type and that all joins are valid based on the supplied schema.
  
  1. Ensure that the following error does not occur: Operand type clash: date is incompatible with int.
  2. Ensure that you do not use ambiguous column names in your SQL queries, for example 'MonthLabel'.
  3. When constructing JOIN clauses, prefer using declared foreign keys from the supplied schema. If a foreign key exists between two tables, use its Column and RefColumn exactly for the ON clause (for example: ON e.[EmployeeStatusTypeId] = s.[Id]). Never invent or guess join column names; only use column names that appear in the supplied schema JSON.

* Do not include Markdown.
* Do not include ```json code fences.
* Do not include explanations before or after the JSON.
* Use ONLY tables and columns that exist in the supplied schema.
* Never invent tables, columns, relationships, or data.
* Use fully qualified bracketed SQL Server table names, for example [dbo].[Employees].

CRITICAL COLUMN SELECTION RULES

* NEVER guess or invent column names. Every column name you write MUST exist in the supplied schema for its table.
* Before using any column in your SQL, verify it exists in the schema by checking the Columns array for that table.
* If you intend to join on a relationship, ALWAYS check the ForeignKeys array first. Use the exact Column and RefColumn names specified in the foreign key (e.g., if a foreign key shows Column: EmployeeStatusTypeId and RefColumn: Id, write: ON e.[EmployeeStatusTypeId] = s.[Id], NOT e.[StatusID] or e.[Status_Id]).
* Do not attempt to derive or abbreviate column names. For example:
  - WRONG: e.[StatusID] (this column may not exist; the actual FK column might be EmployeeStatusTypeId)
  - CORRECT: e.[EmployeeStatusTypeId] (use the exact name from the schema)
* If the schema shows a column named EmployeeStatusTypeId but not StatusID, you must use EmployeeStatusTypeId.
* Every column reference must be wrapped in square brackets [ColumnName] for safety.
* When in doubt, copy the exact column name directly from the supplied schema JSON.
* Every SQL query must be read-only.

MANDATORY TABLE ALIAS RULES

* EVERY table in a FROM or JOIN clause MUST have a table alias defined.
  - WRONG: FROM [dbo].[Employee] WHERE [EmployeeID] = 1
  - CORRECT: FROM [dbo].[Employee] e WHERE e.[EmployeeID] = 1
* EVERY column reference MUST be qualified with its table alias.
  - WRONG: SELECT [EmployeeID], [FirstName] FROM [dbo].[Employee] e
  - CORRECT: SELECT e.[EmployeeID], e.[FirstName] FROM [dbo].[Employee] e
* In JOIN ON conditions, BOTH sides must use table aliases:
  - WRONG: ON EmployeeID = StatusID
  - WRONG: ON [EmployeeID] = est.[StatusID]
  - CORRECT: ON e.[EmployeeStatusTypeId] = est.[Id]
* Alias names should be short (e.g., e, emp, est, status) and used consistently throughout the query.
* In GROUP BY and ORDER BY clauses, use the alias-qualified column name:
  - WRONG: GROUP BY [StatusName]
  - CORRECT: GROUP BY est.[StatusName]
* Every column must be unique and unambiguous. If the same column name exists in multiple tables, you MUST use the alias to disambiguate.

* The work week runs from Sunday through Saturday.
* Every query must be either:

  1. A single SELECT statement, or
  2. A SELECT-based CTE followed by a SELECT.
* Never generate INSERT, UPDATE, DELETE, MERGE, DROP, ALTER, CREATE, TRUNCATE, EXEC, EXECUTE, or other data-changing statements.
* For detail/tabular queries that could return many rows, use TOP (200).
* Aggregated queries used for charts do not require TOP (200) unless the result could reasonably contain more than 200 groups.
* If a section's heading or purpose states or implies a specific number of records, such as Top 20, Most Recent 10, or 50 Highest, the SQL's TOP (N) value MUST exactly match that stated number instead of the default 200.
* Never state a specific record count in a heading or purpose unless the SQL's TOP (N) matches that count exactly.
* Create between 2 and 6 useful report sections.

CHART RULES

* If the user explicitly asks for a chart, graph, visualization, trend, comparison, distribution, breakdown, or similar visual representation, include one or more appropriate chart sections.
* If the user asks for charts, do not return only tables.
* Charts must be based entirely on the SQL query contained in that section.
* Prefer aggregated SQL suitable for visualization.
* Choose the chart type that best represents the requested information.
* Legends used in a chart must use the came color as what was used in the chart.
* Supported chart types are:

  * bar
  * column
  * line
  * pie
  * doughnut
  * area
  * scatter
* Use line charts primarily for trends over time.
* Use bar or column charts for category comparisons.
* Use pie or doughnut charts only when showing a small number of parts of a whole.
* Use scatter charts only when comparing two numeric measures.
* Do not use pie or doughnut charts when there are too many categories to display clearly.
* A chart section must specify which returned SQL column supplies the category/X-axis and which returned SQL column supplies the numeric Y-axis/value.
* The xAxis and yAxis values MUST exactly match column aliases returned by the section's SQL query.
* For pie and doughnut charts, xAxis represents the category/label column and yAxis represents the numeric value column.
* If a chart would not meaningfully help answer the user's request, use a table section instead unless the user explicitly requested a chart.
* A report may contain both tables and charts.

SECTION RULES
Each section must contain:

* heading: Display heading for the section.
* purpose: Short explanation of what the section shows.
* type: Either ""table"" or ""chart"".
* sql: SQL Server SELECT query.
* confidence: A number from 0 to 100 representing how confident you are that the SQL is syntactically correct, uses only real columns from the supplied schema, and accurately answers the section's purpose. Use 90-100 only when you are certain every table, column, join, and aggregate is correct. Lower the value when you are uncertain about column names, join conditions, or whether the query fully satisfies the user's request.

For a chart section, also include:

* Charts should always show a key metric or trend that is relevant to the user's request.
* chartType: One of the supported chart types.
* xAxis: Exact SQL result column alias used for categories or X-axis values.
* yAxis: Exact SQL result column alias used for numeric values.
* xAxisTitle: Human-readable X-axis title.
* yAxisTitle: Human-readable Y-axis title.

For table sections:

* chartType must be null.
* xAxis must be null.
* yAxis must be null.
* xAxisTitle must be null.
* yAxisTitle must be null.

SQL FOR CHARTS
When generating SQL for a chart:

* Give selected expressions clear aliases.
* Use aliases that can be referenced directly by xAxis and yAxis.
* Order results in a logical display order.
* For time-series charts, order chronologically.
* For category comparisons, order logically or by the measured value when appropriate.
* Aggregate the data when necessary using COUNT, SUM, AVG, MIN, or MAX.
* Protect calculations from divide-by-zero errors when applicable.
* Do not reference schema elements that were not supplied.
* CRITICAL: Never nest aggregate functions. For example, do NOT write AVG(CAST(COUNT(*) AS float)) or SUM(AVG(...)). If you need to compute an average of counts, use a CTE or a subquery. Example: WITH counts AS (SELECT category, COUNT(*) as cnt FROM table GROUP BY category) SELECT category, AVG(cnt * 1.0) FROM counts GROUP BY category.
* If a query returns zero rows, it means either the WHERE/JOIN conditions are too restrictive, the columns do not exist, or the foreign keys are wrong. Always verify that your JOINs reference actual foreign keys from the schema and that your WHERE conditions use columns with values.

WORKING SQL EXAMPLES FOR COMMON JOINS

For grouping by a status type:
SELECT est.[StatusName] AS [Status], COUNT(*) AS [Count]
FROM [dbo].[Employee] e
INNER JOIN [dbo].[EmployeeStatusType] est ON e.[EmployeeStatusTypeId] = est.[Id]
WHERE e.[IsActive] = 1
GROUP BY est.[StatusName]
ORDER BY [Count] DESC

For counting active employees:
SELECT COUNT(*) AS [ActiveCount]
FROM [dbo].[Employee] e
WHERE e.[IsActive] = 1

For joining with status to get employee details:
SELECT e.[EmployeeID], e.[FirstName], e.[LastName], est.[StatusName]
FROM [dbo].[Employee] e
INNER JOIN [dbo].[EmployeeStatusType] est ON e.[EmployeeStatusTypeId] = est.[Id]
WHERE e.[IsActive] = 1

The above examples show correct column usage. Notice:
- Employee table columns: EmployeeID, FirstName, LastName, EmployeeStatusTypeId, IsActive
- EmployeeStatusType table columns: Id, StatusName
- Correct JOIN condition: e.[EmployeeStatusTypeId] = est.[Id]
- Never use: StatusID, Status_Id, or other variations

EXAMPLE OUTPUT SHAPE
{{
""title"": ""Report Title"",
""sections"": [
{{
""heading"": ""Report Details"",
""purpose"": ""Shows detailed report information."",
""type"": ""table"",
""sql"": ""SELECT TOP (200) ..."",
""chartType"": null,
""xAxis"": null,
""yAxis"": null,
""xAxisTitle"": null,
""yAxisTitle"": null,
""confidence"": 95
}},
{{
""heading"": ""Monthly Trend"",
""purpose"": ""Shows how the requested metric changes over time."",
""type"": ""chart"",
""sql"": ""SELECT ... AS [Month], COUNT(*) AS [Total] ... GROUP BY ... ORDER BY ..."",
""chartType"": ""line"",
""xAxis"": ""Month"",
""yAxis"": ""Total"",
""xAxisTitle"": ""Month"",
""yAxisTitle"": ""Total"",
""confidence"": 85
}}
]
}}

IMPORTANT
Before returning the plan, verify that:

* Every referenced table exists in the supplied schema.
* Every referenced column exists in the supplied schema.
* Every query is read-only.
* Every chart's xAxis and yAxis correspond exactly to aliases returned by its SQL.
* Numeric chart values come from numeric SQL expressions.
* The JSON is syntactically valid.
* The response contains JSON and nothing else.
* When reporting on a date field, I only want to see the date and not the time portion. Use CAST or CONVERT to ensure the time portion is removed.
* Every bracketed identifier must be well-formed: each opening [ must be closed with a matching ] (e.g. [dateStamp]), never mixed with parentheses (e.g. [dateStamp)] is invalid).
* Every opening parenthesis ( must have a matching closing parenthesis ), and brackets/parentheses must never be interleaved or swapped.
* Double-check GROUP BY and ORDER BY clauses in particular, since expressions like DATEPART(WEEKDAY, [dateStamp]) are easy to mistype as DATEPART(WEEKDAY, [dateStamp)].
* Re-read the full generated SQL string once more before returning the JSON to confirm every bracket and parenthesis pair is correctly matched.
* For each section, if the heading or purpose mentions a specific record count, such as Top 20, confirm the SQL's TOP (N) matches that exact number; correct either the wording or the TOP (N) value so they agree.
* Every SQL query in every section must be tested mentally: does the WHERE clause correctly filter data, are the JOINs using valid foreign keys, will the GROUP BY produce meaningful groups, and will the result set contain at least one row if the table is not empty?
* NO nested aggregate functions are permitted. If the design requires averaging a count or summing a sum, use a CTE with a subquery first.
* Ensure that all date-range filters (such as DATEADD(DAY, -90, CAST(GETDATE() AS DATE))) use the correct offset direction; the example (DAY, -90, ...) means 90 days in the past.
* CRITICAL: When a column shows an ""[Actual values: ...]"" list in the column reference guide below, that column is a lookup/status/type column and you MUST use one of the exact listed values (matching case and spelling) in any WHERE/HAVING/CASE comparison against that column. Do NOT guess a plausible-sounding value (e.g. 'Absent' or 'Inactive') if it is not in the listed actual values - doing so will silently return zero rows even though matching data exists.

COLUMN REFERENCE GUIDE

The following table lists each table's name and its actual columns. Use ONLY these exact column names in your SQL. Where shown, [Actual values: ...] lists the real distinct values stored in that column - use those exact values, not guesses:

{BuildColumnReferenceGuide(schema)}

USER REQUEST:
{request}

SUPPLIED SCHEMA (JSON):
{JsonSerializer.Serialize(schema)}
";

            var txt = await Generate(prompt, selectedModel, ct);
            logger.LogDebug("AI plan response received, deserializing");

            var sanitizedTxt = JsonSanitizer.SanitizeControlCharsInStrings(txt);
            var plan = JsonSerializer.Deserialize<ReportPlan>(sanitizedTxt, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new Exception("Invalid Ollama report plan.");
            logger.LogInformation("Report plan generated successfully with {SectionCount} sections", plan.Sections.Count);

            foreach (var s in plan.Sections)
                s.Confidence = Math.Clamp(s.Confidence, 0, 100);

            // Validate and attempt to repair SQL using the supplied schema to avoid invalid column/table names
            try
            {
                SqlValidator.ValidateAndRepairPlan(plan, schema, logger);

                // Check for any remaining invalid columns and log them
                var errors = SqlValidator.GetInvalidColumnErrors(plan, schema, logger);
                if (!string.IsNullOrEmpty(errors))
                {
                    logger.LogWarning("Generated SQL contains invalid columns:\n{Errors}", errors);
                }

                // For each section, detect hallucinated columns and ask the LLM to self-correct.
                // LLMCorrectionService enforces a maximum number of correction attempts internally
                // so we never call the LLM in an unbounded loop.
                foreach (var sec in plan.Sections)
                {
                    if (string.IsNullOrWhiteSpace(sec.Sql)) continue;

                    var hallucinatedColumns = correctionService.ValidateSQL(sec.Sql, schema);
                    if (hallucinatedColumns.Count == 0) continue;

                    logger.LogWarning(
                        "Section '{Heading}' contains {Count} hallucinated column(s): {Columns}. Requesting LLM self-correction.",
                        sec.Heading, hallucinatedColumns.Count, string.Join(", ", hallucinatedColumns));

                    var corrected = await correctionService.CorrectHallucinations(
                        sec, schema, hallucinatedColumns, request, selectedModel, ct);

                    if (corrected != null)
                    {
                        sec.Sql = corrected.Sql;
                        // Self-correction was needed, so reduce confidence to reflect the uncertainty introduced
                        sec.Confidence = Math.Clamp(Math.Min(sec.Confidence, 60) - (hallucinatedColumns.Count * 5), 0, 100);
                        logger.LogInformation("Section '{Heading}' SQL successfully self-corrected by LLM.", sec.Heading);
                    }
                    else
                    {
                        logger.LogError(
                            "Section '{Heading}' still contains hallucinated columns after correction attempts. Marking query as invalid.",
                            sec.Heading);
                        // Invalidate the SQL so the read-only validator in ReportService rejects it
                        // instead of executing a query against non-existent columns.
                        sec.Sql = $"-- Unable to generate valid SQL: hallucinated columns could not be corrected ({string.Join(", ", hallucinatedColumns)})";
                        sec.Confidence = 0;
                    }
                }

                // Proactively detect nested aggregate functions (e.g. AVG(COUNT(*))) before execution,
                // since SQL Server rejects these with "Cannot perform an aggregate function on an
                // expression containing an aggregate or a subquery." Ask the LLM to self-correct rather
                // than waiting for an execution failure.
                foreach (var sec in plan.Sections)
                {
                    if (string.IsNullOrWhiteSpace(sec.Sql) || sec.Sql.StartsWith("--")) continue;

                    var nestedAggregateDescription = SqlValidator.FindNestedAggregate(sec.Sql);
                    if (nestedAggregateDescription == null) continue;

                    logger.LogWarning(
                        "Section '{Heading}' contains a nested aggregate function: {Description}. Requesting LLM self-correction.",
                        sec.Heading, nestedAggregateDescription);

                    var errorMessage =
                        $"Cannot perform an aggregate function on an expression containing an aggregate or a subquery. Detected pattern: {nestedAggregateDescription}";

                    var corrected = await correctionService.CorrectSqlExecutionError(
                        sec, schema, errorMessage, request, selectedModel, ct);

                    if (corrected != null && !string.IsNullOrWhiteSpace(corrected.Sql) &&
                        SqlValidator.FindNestedAggregate(corrected.Sql) == null)
                    {
                        sec.Sql = corrected.Sql;
                        sec.Confidence = Math.Clamp(Math.Min(sec.Confidence, 60), 0, 100);
                        logger.LogInformation("Section '{Heading}' nested aggregate SQL successfully self-corrected by LLM.", sec.Heading);
                    }
                    else
                    {
                        logger.LogError(
                            "Section '{Heading}' still contains a nested aggregate function after correction attempts. Marking query as invalid.",
                            sec.Heading);
                        sec.Sql = $"-- Unable to generate valid SQL: nested aggregate function could not be corrected ({nestedAggregateDescription})";
                        sec.Confidence = 0;
                    }
                }

                // Proactively detect GROUP BY clauses that group only by constant literals
                // (e.g. GROUP BY 'Active'), which SQL Server rejects with "Each GROUP BY
                // expression must contain at least one column that is not an outer reference."
                foreach (var sec in plan.Sections)
                {
                    if (string.IsNullOrWhiteSpace(sec.Sql) || sec.Sql.StartsWith("--")) continue;

                    var groupByLiteralDescription = SqlValidator.FindGroupByLiteralOnly(sec.Sql);
                    if (groupByLiteralDescription == null) continue;

                    logger.LogWarning(
                        "Section '{Heading}' has a GROUP BY clause with only constant literals: {Description}. Requesting LLM self-correction.",
                        sec.Heading, groupByLiteralDescription);

                    var errorMessage =
                        $"Each GROUP BY expression must contain at least one column that is not an outer reference. Detected pattern: {groupByLiteralDescription}";

                    var corrected = await correctionService.CorrectSqlExecutionError(
                        sec, schema, errorMessage, request, selectedModel, ct);

                    if (corrected != null && !string.IsNullOrWhiteSpace(corrected.Sql) &&
                        SqlValidator.FindGroupByLiteralOnly(corrected.Sql) == null)
                    {
                        sec.Sql = corrected.Sql;
                        sec.Confidence = Math.Clamp(Math.Min(sec.Confidence, 60), 0, 100);
                        logger.LogInformation("Section '{Heading}' literal-only GROUP BY successfully self-corrected by LLM.", sec.Heading);
                    }
                    else
                    {
                        logger.LogError(
                            "Section '{Heading}' still has a literal-only GROUP BY clause after correction attempts. Marking query as invalid.",
                            sec.Heading);
                        sec.Sql = $"-- Unable to generate valid SQL: literal-only GROUP BY clause could not be corrected ({groupByLiteralDescription})";
                        sec.Confidence = 0;
                    }
                }

                // Verify that string literals compared against lookup/status/type columns
                // (e.g. ast.[StatusName] = 'Absent') actually exist in the database, rather than
                // being a plausible-sounding guess by the LLM. This directly targets queries
                // that execute successfully but return zero rows because the literal doesn't
                // match any real value. Only runs when a connection string was supplied.
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    foreach (var sec in plan.Sections)
                    {
                        if (string.IsNullOrWhiteSpace(sec.Sql) || sec.Sql.StartsWith("--")) continue;

                        var aliasMap = SqlValidator.ExtractAliasMap(sec.Sql);
                        var literalComparisons = SqlValidator.ExtractStringLiteralComparisons(sec.Sql);
                        if (literalComparisons.Count == 0) continue;

                        foreach (var (alias, column, value) in literalComparisons)
                        {
                            if (!aliasMap.TryGetValue(alias, out var tableRef)) continue;

                            var table = schema.Tables.FirstOrDefault(t =>
                                string.Equals(t.Schema, tableRef.Schema, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(t.Name, tableRef.Table, StringComparison.OrdinalIgnoreCase));
                            if (table == null) continue;

                            var col = table.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
                            if (col == null) continue;
                            if (col.Type is not ("varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext")) continue;

                            // Reuse already-sampled values if available; otherwise ask the database directly
                            var actualValues = col.SampleValues != null && col.SampleValues.Count > 0
                                ? col.SampleValues
                                : await schemaService.GetDistinctValues(connectionString, table.Schema, table.Name, col.Name, 50, ct);

                            if (actualValues.Count == 0) continue; // couldn't verify, leave as-is

                            var matches = actualValues.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
                            if (matches) continue;

                            logger.LogWarning(
                                "Section '{Heading}' compares {Table}.{Column} to literal '{Value}' which does not match any actual value ({ActualValues}). Requesting LLM self-correction.",
                                sec.Heading, table.FullName, col.Name, value, string.Join(", ", actualValues));

                            var errorMessage =
                                $"The value '{value}' does not exist in column [{table.Schema}].[{table.Name}].[{col.Name}]. " +
                                $"The actual distinct values in that column are: {string.Join(", ", actualValues.Select(v => $"'{v}'"))}. " +
                                $"Rewrite the query to use the correct matching value from this list.";

                            var corrected = await correctionService.CorrectSqlExecutionError(
                                sec, schema, errorMessage, request, selectedModel, ct);

                            if (corrected != null && !string.IsNullOrWhiteSpace(corrected.Sql))
                            {
                                sec.Sql = corrected.Sql;
                                sec.Confidence = Math.Clamp(Math.Min(sec.Confidence, 70), 0, 100);
                                logger.LogInformation("Section '{Heading}' corrected to use an actual column value instead of guessed literal '{Value}'.", sec.Heading, value);
                            }
                            else
                            {
                                logger.LogWarning("Section '{Heading}' could not be corrected for mismatched literal '{Value}'. Leaving original SQL in place.", sec.Heading, value);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Validation/repair of generated SQL failed: {Message}", ex.Message);
            }

            return plan;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating report plan: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            logger.LogDebug("Exiting Plan method");
        }
    }

    public async Task<string> Summarize(ReportResult report, string? selectedModel = null, CancellationToken ct = default)
    {
        logger.LogDebug("Entering Summarize method");
        try
        {
            logger.LogInformation("Generating summary for report with {SectionCount} sections", report.Sections.Count);
            var data = JsonSerializer.Serialize(report.Sections.Select(x => new { x.Heading, Rows = x.Rows.Take(30) }));
            var summary = await Generate("Return JSON with one property named summary. Write a concise factual executive summary using only these results: " + data, selectedModel, ct, true);
            logger.LogInformation("Report summary generated successfully");
            return summary;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error generating report summary: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            logger.LogDebug("Exiting Summarize method");
        }
    }

    async Task<string> Generate(string prompt, string? selectedModel = null, CancellationToken ct = default, bool summary = false)
    {
        logger.LogDebug("Entering Generate method, summary={IsSummary}", summary);
        try
        {
            var client = f.CreateClient();
            // Set extended timeout for Ollama requests (5 minutes)
            client.Timeout = TimeSpan.FromSeconds(300);

            var url = (cfg["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/'); 
            var model = selectedModel ?? cfg["Ollama:Model"] ?? "qwen3-coder:30b";
            logger.LogDebug("Calling Ollama API at {Url} with model {Model}", url, model);

            var res = await client.PostAsJsonAsync(url + "/api/generate", new
            {
                model,
                prompt,
                stream = false
            }, ct);
            logger.LogDebug("Ollama API response status: {StatusCode}", res.StatusCode);

            if (!res.IsSuccessStatusCode)
            {
                var errorContent = await res.Content.ReadAsStringAsync(ct);
                string errorMessage = res.StatusCode switch
                {
                    System.Net.HttpStatusCode.NotFound => $"Unable to select the AI language model '{model}'. The Ollama API endpoint at '{url}' returned 404 Not Found. Please verify that Ollama is running and the model '{model}' is available.",
                    System.Net.HttpStatusCode.ServiceUnavailable => $"The Ollama AI service at '{url}' is unavailable. Please ensure Ollama is running and accessible.",
                    System.Net.HttpStatusCode.BadRequest => $"Invalid request to Ollama API. The model '{model}' or request format may not be supported. Error: {errorContent}",
                    _ => $"Ollama API returned error {(int)res.StatusCode} ({res.StatusCode}). Please check your Ollama configuration and ensure the service is running."
                };
                logger.LogError("Ollama API error {StatusCode}: {ErrorMessage}", res.StatusCode, errorMessage);
                throw new Exception(errorMessage);
            }

            var body = await res.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: ct);
            var text = body?.response ?? throw new Exception("Unable to select the AI language model. No response received from Ollama API. Please verify that Ollama is running and the model is properly configured.");

            // Track tokens generated
            if (body != null)
            {
                var tokensThisCall = body.eval_count + body.prompt_eval_count;
                totalTokensGenerated += tokensThisCall;
                logger.LogInformation("Tokens generated in this call: {TokensThisCall}, total so far: {TotalTokens}", tokensThisCall, totalTokensGenerated);
            }

            logger.LogDebug("Ollama response received, length={ResponseLength}", text.Length);

            if (summary) 
            { 
                var sanitizedSummaryJson = JsonSanitizer.SanitizeControlCharsInStrings(text);
                using var doc = JsonDocument.Parse(sanitizedSummaryJson); 
                return doc.RootElement.GetProperty("summary").GetString() ?? ""; 
            }
            return text;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to connect to Ollama API: {ErrorMessage}", ex.Message);
            throw new Exception($"Unable to connect to Ollama. Please verify that Ollama is running. Details: {ex.Message}", ex);
        }
        catch (Exception ex) when (!(ex is HttpRequestException))
        {
            logger.LogError(ex, "Error generating text from Ollama: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            logger.LogDebug("Exiting Generate method");
        }
    }
}
