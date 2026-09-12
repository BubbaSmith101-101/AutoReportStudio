using AutoReportStudio.Web.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoReportStudio.Web.Services;

/// <summary>
/// Formats database schema information for LLM prompts to prevent hallucinated columns
/// </summary>
public static class SchemaFormatterService
{
    /// <summary>
    /// Formats the schema as a detailed text description for LLM context
    /// This explicitly lists ONLY the actual columns to prevent hallucination
    /// </summary>
    public static string FormatSchemaForLLMPrompt(DbSchema schema)
    {
        if (schema?.Tables == null || schema.Tables.Count == 0)
            return "No tables available.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== DATABASE SCHEMA ===");
        sb.AppendLine("The database contains the following tables and columns ONLY.");
        sb.AppendLine("You MUST use ONLY these exact column names. Do NOT invent or hallucinate column names.");
        sb.AppendLine();

        foreach (var table in schema.Tables.OrderBy(t => t.Schema).ThenBy(t => t.Name))
        {
            sb.AppendLine($"TABLE: [{table.Schema}].[{table.Name}]");
            sb.AppendLine("COLUMNS:");

            if (table.Columns.Count == 0)
            {
                sb.AppendLine("  (No columns found)");
            }
            else
            {
                foreach (var col in table.Columns.OrderBy(c => c.Name))
                {
                    var pkMarker = col.PrimaryKey ? " [PRIMARY KEY]" : "";
                    var nullableMarker = col.Nullable ? " [NULLABLE]" : " [NOT NULL]";
                    sb.AppendLine($"  - [{col.Name}] {col.Type}{nullableMarker}{pkMarker}");
                }
            }

            // Show foreign keys if any
            if (table.ForeignKeys.Count > 0)
            {
                sb.AppendLine("RELATIONSHIPS:");
                foreach (var fk in table.ForeignKeys)
                {
                    sb.AppendLine($"  - [{fk.Column}] references [{fk.RefSchema}].[{fk.RefTable}].[{fk.RefColumn}]");
                }
            }

            sb.AppendLine();
        }

        sb.AppendLine("=== CRITICAL RULES ===");
        sb.AppendLine("1. ONLY use columns that are explicitly listed above");
        sb.AppendLine("2. If a column name you want to use is not listed, use one that IS listed");
        sb.AppendLine("3. Do NOT create or assume columns that don't exist");
        sb.AppendLine("4. Always qualify column names with table aliases: alias.[ColumnName]");
        sb.AppendLine("5. Match column names EXACTLY as shown (including case and special characters like spaces)");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Creates a corrective prompt to send back to the LLM when hallucinations are detected
    /// </summary>
    public static string CreateCorrectionPrompt(
        string originalSql, 
        string sectionHeading,
        List<string> halluccinatedColumns, 
        DbSchema schema)
    {
        if (halluccinatedColumns == null || halluccinatedColumns.Count == 0)
            return "";

        var sb = new System.Text.StringBuilder();

        sb.AppendLine("=== SQL CORRECTION NEEDED ===");
        sb.AppendLine($"Section: {sectionHeading}");
        sb.AppendLine();
        sb.AppendLine("The following columns DO NOT EXIST in the database and must be corrected:");
        foreach (var col in halluccinatedColumns)
        {
            sb.AppendLine($"  ❌ {col}");
        }
        sb.AppendLine();

        // Extract which tables are being used and show their actual columns
        sb.AppendLine("Available columns in the tables being used:");
        var usedTableNames = ExtractTableNamesFromSql(originalSql);
        foreach (var tableName in usedTableNames.OrderBy(t => t))
        {
            var table = schema.Tables.FirstOrDefault(t =>
                t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));

            if (table != null)
            {
                sb.AppendLine($"  [{table.Schema}].[{table.Name}]:");
                foreach (var col in table.Columns.OrderBy(c => c.Name))
                {
                    sb.AppendLine($"    - [{col.Name}] ({col.Type})");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("ORIGINAL SQL (with errors):");
        sb.AppendLine(originalSql);
        sb.AppendLine();
        sb.AppendLine("Please rewrite the SQL above, replacing all hallucinated columns with valid columns from the available columns listed above.");
        sb.AppendLine("IMPORTANT: Use ONLY the column names shown above. Do not invent or guess column names.");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Creates a corrective prompt to send back to the LLM when a query fails at execution
    /// time (syntax errors, invalid GROUP BY expressions, aggregate misuse, etc.)
    /// </summary>
    public static string CreateSqlErrorCorrectionPrompt(
        string originalSql,
        string sectionHeading,
        string sqlErrorMessage,
        DbSchema schema)
    {
        if (string.IsNullOrWhiteSpace(sqlErrorMessage))
            return "";

        var sb = new System.Text.StringBuilder();

        sb.AppendLine("=== SQL EXECUTION ERROR - CORRECTION NEEDED ===");
        sb.AppendLine($"Section: {sectionHeading}");
        sb.AppendLine();
        sb.AppendLine("The SQL below failed to execute against SQL Server with this error:");
        sb.AppendLine($"  {sqlErrorMessage}");
        sb.AppendLine();

        var usedTableNames = ExtractTableNamesFromSql(originalSql);
        if (usedTableNames.Count > 0)
        {
            sb.AppendLine("Available columns in the tables being used:");
            foreach (var tableName in usedTableNames.OrderBy(t => t))
            {
                var table = schema.Tables.FirstOrDefault(t =>
                    t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));

                if (table != null)
                {
                    sb.AppendLine($"  [{table.Schema}].[{table.Name}]:");
                    foreach (var col in table.Columns.OrderBy(c => c.Name))
                    {
                        sb.AppendLine($"    - [{col.Name}] ({col.Type})");
                    }
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("ORIGINAL SQL (fails to execute):");
        sb.AppendLine(originalSql);
        sb.AppendLine();
        sb.AppendLine("Please rewrite the SQL above so it executes successfully and fixes the reported error.");
        sb.AppendLine("Common causes to check for:");
        sb.AppendLine("- GROUP BY must include every non-aggregated column/expression that appears in SELECT; string/numeric literals used as fake group keys (e.g. GROUP BY 'Active') are invalid and must be replaced with the actual source column(s), or removed if no grouping is needed.");
        sb.AppendLine("- Nested aggregate functions (e.g. AVG(COUNT(*))) are not allowed; use a CTE or subquery instead.");
        sb.AppendLine("- All bracketed identifiers must have matching [ and ] and correctly paired parentheses.");
        sb.AppendLine("- Only use column names that are confirmed to exist in the schema shown above.");
        sb.AppendLine("IMPORTANT: Return ONLY the corrected SQL query, nothing else.");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Extracts table names from SQL (simplified - looks for table references)
    /// </summary>
    private static List<string> ExtractTableNamesFromSql(string sql)
    {
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Simple pattern matching for [schema].[tablename]
        var pattern = @"\[([^\]]+)\]\.\[([^\]]+)\]";
        var regex = new System.Text.RegularExpressions.Regex(pattern);

        foreach (System.Text.RegularExpressions.Match match in regex.Matches(sql))
        {
            if (match.Groups.Count >= 3)
            {
                var tableName = match.Groups[2].Value;
                tableNames.Add(tableName);
            }
        }

        return tableNames.ToList();
    }

    /// <summary>
    /// Validates that a query only references columns that actually exist
    /// Returns error messages if invalid columns are found
    /// </summary>
    public static List<string> ValidateQueryColumns(string sql, DbSchema schema)
    {
        var errors = new List<string>();
        if (schema?.Tables == null || string.IsNullOrWhiteSpace(sql))
            return errors;

        // Build a set of all valid columns for quick lookup
        var validColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in schema.Tables)
        {
            foreach (var col in table.Columns)
            {
                // Add in format "TableName.ColumnName" and "[ColumnName]"
                validColumns.Add($"{table.Name}.{col.Name}");
                validColumns.Add(col.Name);
                validColumns.Add($"[{col.Name}]");
            }
        }

        // Extract column references from SQL (simplified pattern)
        var columnPattern = @"\[([^\]]+)\]";
        var matches = System.Text.RegularExpressions.Regex.Matches(sql, columnPattern);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var columnName = match.Groups[1].Value;
            if (!validColumns.Contains(columnName))
            {
                errors.Add($"Column '[{columnName}]' does not exist in the schema");
            }
        }

        return errors;
    }
}
