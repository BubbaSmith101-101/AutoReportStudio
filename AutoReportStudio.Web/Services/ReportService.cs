using System.Text.RegularExpressions;
using AutoReportStudio.Web.Models;
using Microsoft.Data.SqlClient;
namespace AutoReportStudio.Web.Services;

public interface IReportService 
{ 
    Task<ReportResult> Generate(string cs, List<string> selected, string request, string? selectedModel = null, CancellationToken ct = default); 
}

public class ReportService : IReportService
{
    readonly ISchemaService schema; 
    readonly IOllamaService ollama;
    readonly ILLMCorrectionService correctionService;
    readonly ILogger<ReportService> logger;
    static readonly Regex Bad = new(@"\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|GRANT|REVOKE|DENY|DBCC|BACKUP|RESTORE|OPENROWSET|OPENDATASOURCE|KILL)\b", RegexOptions.IgnoreCase);

    public ReportService(ISchemaService s, IOllamaService o, ILLMCorrectionService c, ILogger<ReportService> l) 
    { 
        schema = s; 
        ollama = o;
        correctionService = c;
        logger = l;
    }

    public async Task<ReportResult> Generate(string cs, List<string> selected, string request, string? selectedModel = null, CancellationToken ct = default)
    {
        logger.LogDebug("Entering Generate method with {TableCount} selected tables", selected.Count);
        var startTime = DateTime.UtcNow;
        long timeToFirstTokenMs = 0;
        using var firstTokenCts = new CancellationTokenSource();
        var firstTokenWatch = Task.Run(async () =>
        {
            try
            {
                while (!firstTokenCts.IsCancellationRequested)
                {
                    if (ollama.GetTotalTokensGenerated() > 0)
                    {
                        timeToFirstTokenMs = (long)(DateTime.UtcNow - startTime).TotalMilliseconds;
                        return;
                    }
                    await Task.Delay(15, firstTokenCts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            logger.LogInformation("Starting report generation for {SelectedTableCount} tables with model {SelectedModel}", selected.Count, selectedModel ?? "default");

            var db = await schema.Discover(cs, ct); 
            db.Tables = db.Tables.Where(t => selected.Contains($"{t.Schema}.{t.Name}", StringComparer.OrdinalIgnoreCase)).ToList();
            logger.LogDebug("Filtered to {FilteredTableCount} tables after discovery", db.Tables.Count);

            if (db.Tables.Count == 0)
            {
                logger.LogWarning("No tables selected for report generation");
                throw new Exception("Select at least one table.");
            }

            logger.LogDebug("Requesting AI plan for report with selectedModel: {SelectedModel}", selectedModel);
            var plan = await ollama.Plan(db, request, selectedModel, ct, cs); 
            logger.LogInformation("AI plan generated with {SectionCount} sections", plan.Sections.Count);

            var result = new ReportResult { Title = plan.Title, ModelUsed = selectedModel ?? "", UserRequest = request };
            await using var cn = new SqlConnection(cs); 
            await cn.OpenAsync(ct);
            logger.LogDebug("Database connection opened");

            int processedSections = 0;
            foreach (var p in plan.Sections.Take(6))
            {
                logger.LogDebug("Processing section: {SectionHeading}", p.Heading);
                var sec = new ReportSection
                {
                    Heading = p.Heading,
                    Purpose = p.Purpose,
                    Sql = p.Sql,
                    Type = string.IsNullOrWhiteSpace(p.Type) ? "table" : p.Type.ToLowerInvariant(),
                    ChartType = p.ChartType,
                    XAxis = p.XAxis,
                    YAxis = p.YAxis,
                    XAxisTitle = p.XAxisTitle,
                    YAxisTitle = p.YAxisTitle,
                    Confidence = Math.Clamp(p.Confidence, 0, 100)
                };
                result.Sections.Add(sec);

                var q = Regex.Replace(p.Sql, @"(--.*?$)|(/\*.*?\*/)", " ", RegexOptions.Multiline | RegexOptions.Singleline).Trim();
                if (!(q.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || q.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) || Bad.IsMatch(q)) 
                { 
                    logger.LogWarning("Query rejected for section {SectionHeading} by read-only validator", p.Heading);
                    sec.Purpose += " [Query rejected by read-only validator.]"; 
                    continue; 
                }

                try
                {
                    logger.LogDebug("Executing query for section {SectionHeading}", p.Heading);
                    await ExecuteSectionQuery(sec, p, db, cn, request, selectedModel, ct);
                }
                catch (Exception ex) 
                { 
                    logger.LogError(ex, "Error executing query for section {SectionHeading}: {ErrorMessage}. SQL: {Sql}", p.Heading, ex.Message, sec.Sql); 
                    sec.Purpose += " Query failed: " + ex.Message; 
                }
                processedSections++;
            }

            logger.LogDebug("Processed {ProcessedSectionCount} sections, requesting summary with selectedModel: {SelectedModel}", processedSections, selectedModel);
            result.Summary = await ollama.Summarize(result, selectedModel, ct);

            // Capture processing time and token count
            var elapsed = DateTime.UtcNow - startTime;
            result.ProcessingTimeMs = (long)elapsed.TotalMilliseconds;
            result.TokensGenerated = ollama.GetTotalTokensGenerated();

            firstTokenCts.Cancel();
            try { await firstTokenWatch; } catch (OperationCanceledException) { }
            result.TimeToFirstTokenMs = timeToFirstTokenMs;

            logger.LogInformation("Report generation completed successfully in {ProcessingTimeMs}ms with {TokensGenerated} tokens (first token at {TimeToFirstTokenMs}ms)", result.ProcessingTimeMs, result.TokensGenerated, result.TimeToFirstTokenMs);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during report generation: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            firstTokenCts.Cancel();
            logger.LogDebug("Exiting Generate method");
        }
    }

    /// <summary>
    /// Executes a section's SQL query. If execution fails, asks the LLM to correct the SQL
    /// (bounded by LLMCorrectionService's internal max-attempt safeguard) and retries once
    /// with the corrected query.
    /// </summary>
    private async Task ExecuteSectionQuery(
        ReportSection sec,
        SectionPlan p,
        DbSchema db,
        SqlConnection cn,
        string request,
        string? selectedModel,
        CancellationToken ct)
    {
        try
        {
            await RunQuery(sec, p.Sql, cn, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Query failed for section {SectionHeading}, attempting LLM self-correction: {ErrorMessage}. SQL: {Sql}", p.Heading, ex.Message, p.Sql);

            var corrected = await correctionService.CorrectSqlExecutionError(
                p, db, ex.Message, request, selectedModel, ct);

            if (corrected == null || string.IsNullOrWhiteSpace(corrected.Sql))
            {
                logger.LogError("LLM was unable to correct failing SQL for section {SectionHeading}. Original error: {ErrorMessage}. SQL: {Sql}", p.Heading, ex.Message, p.Sql);
                throw;
            }

            var q = Regex.Replace(corrected.Sql, @"(--.*?$)|(/\*.*?\*/)", " ", RegexOptions.Multiline | RegexOptions.Singleline).Trim();
            if (!(q.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || q.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) || Bad.IsMatch(q))
            {
                logger.LogWarning("Corrected query for section {SectionHeading} rejected by read-only validator. SQL: {Sql}", p.Heading, corrected.Sql);
                throw;
            }

            sec.Sql = corrected.Sql;
            p.Sql = corrected.Sql;
            // Execution failed and required LLM self-correction, so reduce confidence accordingly
            sec.Confidence = Math.Clamp(Math.Min(sec.Confidence, 50), 0, 100);
            logger.LogInformation("Retrying execution for section {SectionHeading} with LLM-corrected SQL: {Sql}", p.Heading, corrected.Sql);
            try
            {
                await RunQuery(sec, corrected.Sql, cn, ct);
            }
            catch (Exception retryEx)
            {
                logger.LogError(retryEx, "Corrected SQL still failed to execute for section {SectionHeading}: {ErrorMessage}. SQL: {Sql}", p.Heading, retryEx.Message, corrected.Sql);
                throw;
            }
            return;
        }

        // Query executed successfully but returned no data. Ask the LLM to review the query in
        // case overly restrictive filters/joins are excluding data that should have matched.
        if (sec.Rows.Count == 0)
        {
            logger.LogInformation("Query for section {SectionHeading} returned zero rows, attempting LLM self-correction", p.Heading);

            var revised = await correctionService.CorrectZeroResultQuery(p, db, request, selectedModel, ct);

            if (revised == null || string.IsNullOrWhiteSpace(revised.Sql))
            {
                // Either the LLM confirmed zero rows is correct, or correction failed. Keep original result.
                return;
            }

            var q = Regex.Replace(revised.Sql, @"(--.*?$)|(/\*.*?\*/)", " ", RegexOptions.Multiline | RegexOptions.Singleline).Trim();
            if (!(q.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || q.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)) || Bad.IsMatch(q))
            {
                logger.LogWarning("Zero-result-corrected query for section {SectionHeading} rejected by read-only validator. SQL: {Sql}", p.Heading, revised.Sql);
                return;
            }

            try
            {
                await RunQuery(sec, revised.Sql, cn, ct);
                sec.Sql = revised.Sql;
                p.Sql = revised.Sql;
                sec.Confidence = Math.Clamp(Math.Min(sec.Confidence, 60), 0, 100);
                logger.LogInformation("Zero-result correction succeeded for section {SectionHeading}, revised query returned {RowCount} row(s)", p.Heading, sec.Rows.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Revised query from zero-result correction failed to execute for section {SectionHeading}, keeping original zero-row result. SQL: {Sql}", p.Heading, revised.Sql);
                sec.Columns.Clear();
                sec.Rows.Clear();
            }
        }
    }

    private async Task RunQuery(ReportSection sec, string sql, SqlConnection cn, CancellationToken ct)
    {
        sec.Columns.Clear();
        sec.Rows.Clear();

        await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);

        for (int i = 0; i < r.FieldCount; i++)
            sec.Columns.Add(r.GetName(i));

        int count = 0;
        while (await r.ReadAsync(ct) && count++ < 500)
        {
            var row = new List<string?>();
            for (int i = 0; i < r.FieldCount; i++)
                row.Add(r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i)));

            sec.Rows.Add(row);
        }
        logger.LogInformation("Query executed successfully with {RowCount} rows", count);
    }
}
