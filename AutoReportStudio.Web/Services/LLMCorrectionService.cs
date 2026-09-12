using AutoReportStudio.Web.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AutoReportStudio.Web.Services;

/// <summary>
/// Handles LLM self-correction when hallucinated columns are detected
/// Allows the LLM to fix its mistakes, but stops after max retries
/// </summary>
public interface ILLMCorrectionService
{
    /// <summary>
    /// Attempts to correct a section plan with hallucinated columns
    /// Returns the corrected plan if successful, null if max retries exceeded
    /// </summary>
    Task<SectionPlan?> CorrectHallucinations(
        SectionPlan originalPlan,
        DbSchema schema,
        List<string> halluccinatedColumns,
        string userRequest,
        string? selectedModel = null,
        CancellationToken ct = default);

    /// <summary>
    /// Validates SQL for hallucinated (non-existent) columns against the supplied schema
    /// </summary>
    List<string> ValidateSQL(string sql, DbSchema schema);

    /// <summary>
    /// Attempts to correct a section plan whose SQL failed at execution time (e.g. syntax
    /// or semantic errors such as invalid GROUP BY expressions). Returns the corrected plan
    /// if the LLM produces SQL that no longer contains hallucinated columns, or null if max
    /// retries are exceeded.
    /// </summary>
    Task<SectionPlan?> CorrectSqlExecutionError(
        SectionPlan originalPlan,
        DbSchema schema,
        string sqlErrorMessage,
        string userRequest,
        string? selectedModel = null,
        CancellationToken ct = default);

    /// <summary>
    /// Attempts to correct a section plan whose SQL executed successfully but returned zero
    /// rows, in case overly restrictive WHERE/JOIN conditions are excluding data that should
    /// have matched. Returns the corrected plan if the LLM produces a different query, or
    /// null if max retries are exceeded or the LLM confirms zero rows is correct.
    /// </summary>
    Task<SectionPlan?> CorrectZeroResultQuery(
        SectionPlan originalPlan,
        DbSchema schema,
        string userRequest,
        string? selectedModel = null,
        CancellationToken ct = default);
}

/// <summary>
/// Implements LLM self-correction by asking the LLM to fix hallucinated SQL columns
/// </summary>
public class LLMCorrectionService : ILLMCorrectionService
{
    private readonly ILogger<LLMCorrectionService> logger;
    private readonly HttpClient httpClient;
    private readonly IConfiguration configuration;

    // Maximum number of correction attempts before giving up
    private const int MaxCorrectionAttempts = 3;

    // Zero-row results are not necessarily wrong (there may genuinely be no data), so we
    // cap correction attempts lower than the hallucination/error correction flows.
    private const int MaxZeroResultCorrectionAttempts = 2;

    public LLMCorrectionService(ILogger<LLMCorrectionService> l, HttpClient client, IConfiguration config)
    {
        logger = l;
        httpClient = client;
        configuration = config;
    }

    /// <summary>
    /// Attempts to correct hallucinated columns in a section by requesting LLM to fix it
    /// </summary>
    public async Task<SectionPlan?> CorrectHallucinations(
        SectionPlan originalPlan,
        DbSchema schema,
        List<string> halluccinatedColumns,
        string userRequest,
        string? selectedModel = null,
        CancellationToken ct = default)
    {
        if (originalPlan == null || halluccinatedColumns.Count == 0)
            return null;

        logger.LogInformation(
            "Starting LLM self-correction for section '{SectionHeading}' with {HallucColumnCount} hallucinated columns",
            originalPlan.Heading, halluccinatedColumns.Count);

        var currentPlan = new SectionPlan
        {
            Heading = originalPlan.Heading,
            Purpose = originalPlan.Purpose,
            Sql = originalPlan.Sql,
            Type = originalPlan.Type,
            ChartType = originalPlan.ChartType,
            XAxis = originalPlan.XAxis,
            YAxis = originalPlan.YAxis,
            XAxisTitle = originalPlan.XAxisTitle,
            YAxisTitle = originalPlan.YAxisTitle
        };

        var attemptNumber = 0;

        while (attemptNumber < MaxCorrectionAttempts)
        {
            attemptNumber++;
            logger.LogInformation(
                "Correction attempt {AttemptNumber}/{MaxAttempts} for section '{SectionHeading}'",
                attemptNumber, MaxCorrectionAttempts, currentPlan.Heading);

            try
            {
                // Create a correction prompt
                var correctionPrompt = SchemaFormatterService.CreateCorrectionPrompt(
                    currentPlan.Sql,
                    currentPlan.Heading,
                    halluccinatedColumns,
                    schema);

                logger.LogDebug("Sending correction prompt to LLM for section '{SectionHeading}'", currentPlan.Heading);

                // Call LLM to get corrected SQL
                var correctionResponse = await GenerateCorrectionFromLLM(
                    correctionPrompt,
                    selectedModel,
                    ct);

                if (string.IsNullOrWhiteSpace(correctionResponse))
                {
                    logger.LogWarning(
                        "LLM returned empty correction response for section '{SectionHeading}'",
                        currentPlan.Heading);
                    continue;
                }

                logger.LogDebug("LLM correction response received for section '{SectionHeading}'", currentPlan.Heading);

                // Extract SQL from LLM response (might be wrapped in markdown code blocks)
                var correctedSql = ExtractSqlFromResponse(correctionResponse);
                currentPlan.Sql = correctedSql;

                logger.LogInformation(
                    "LLM provided corrected SQL for section '{SectionHeading}'",
                    currentPlan.Heading);

                // Validate the corrected SQL for hallucinations
                var validationErrors = ValidateSQL(currentPlan.Sql, schema);

                if (validationErrors.Count == 0)
                {
                    // Success! No more hallucinations
                    logger.LogInformation(
                        "Correction successful for section '{SectionHeading}' on attempt {AttemptNumber}",
                        currentPlan.Heading, attemptNumber);
                    return currentPlan;
                }

                // Still has hallucinations, update list and try again
                halluccinatedColumns = validationErrors;
                logger.LogWarning(
                    "Corrected SQL still contains {HallucColumnCount} hallucinated columns. Retrying...",
                    validationErrors.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error during LLM correction attempt {AttemptNumber} for section '{SectionHeading}': {ErrorMessage}",
                    attemptNumber, currentPlan.Heading, ex.Message);
                continue;
            }
        }

        // Max retries exceeded
        logger.LogError(
            "Failed to correct hallucinations in section '{SectionHeading}' after {MaxAttempts} attempts. Giving up.",
            originalPlan.Heading, MaxCorrectionAttempts);

        return null; // Indicate correction failed
    }

    /// <summary>
    /// Attempts to correct a section plan whose SQL failed at execution time
    /// </summary>
    public async Task<SectionPlan?> CorrectSqlExecutionError(
        SectionPlan originalPlan,
        DbSchema schema,
        string sqlErrorMessage,
        string userRequest,
        string? selectedModel = null,
        CancellationToken ct = default)
    {
        if (originalPlan == null || string.IsNullOrWhiteSpace(sqlErrorMessage))
            return null;

        logger.LogInformation(
            "Starting LLM self-correction for section '{SectionHeading}' after SQL execution error: {ErrorMessage}",
            originalPlan.Heading, sqlErrorMessage);

        var currentPlan = new SectionPlan
        {
            Heading = originalPlan.Heading,
            Purpose = originalPlan.Purpose,
            Sql = originalPlan.Sql,
            Type = originalPlan.Type,
            ChartType = originalPlan.ChartType,
            XAxis = originalPlan.XAxis,
            YAxis = originalPlan.YAxis,
            XAxisTitle = originalPlan.XAxisTitle,
            YAxisTitle = originalPlan.YAxisTitle
        };

        var currentErrorMessage = sqlErrorMessage;
        var attemptNumber = 0;

        while (attemptNumber < MaxCorrectionAttempts)
        {
            attemptNumber++;
            logger.LogInformation(
                "SQL error correction attempt {AttemptNumber}/{MaxAttempts} for section '{SectionHeading}'",
                attemptNumber, MaxCorrectionAttempts, currentPlan.Heading);

            try
            {
                var correctionPrompt = SchemaFormatterService.CreateSqlErrorCorrectionPrompt(
                    currentPlan.Sql,
                    currentPlan.Heading,
                    currentErrorMessage,
                    schema);

                var correctionResponse = await GenerateCorrectionFromLLM(correctionPrompt, selectedModel, ct);

                if (string.IsNullOrWhiteSpace(correctionResponse))
                {
                    logger.LogWarning(
                        "LLM returned empty correction response for section '{SectionHeading}'",
                        currentPlan.Heading);
                    continue;
                }

                var correctedSql = ExtractSqlFromResponse(correctionResponse);
                if (string.IsNullOrWhiteSpace(correctedSql))
                    continue;

                currentPlan.Sql = correctedSql;

                // Ensure the fix didn't introduce hallucinated columns
                var hallucinatedColumns = ValidateSQL(currentPlan.Sql, schema);
                if (hallucinatedColumns.Count > 0)
                {
                    logger.LogWarning(
                        "Corrected SQL for section '{SectionHeading}' introduced {Count} hallucinated column(s). Retrying...",
                        currentPlan.Heading, hallucinatedColumns.Count);
                    currentErrorMessage = $"The previous correction introduced invalid columns: {string.Join(", ", hallucinatedColumns)}";
                    continue;
                }

                logger.LogInformation(
                    "SQL execution error correction successful for section '{SectionHeading}' on attempt {AttemptNumber}",
                    currentPlan.Heading, attemptNumber);
                return currentPlan;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error during SQL error correction attempt {AttemptNumber} for section '{SectionHeading}': {ErrorMessage}",
                    attemptNumber, currentPlan.Heading, ex.Message);
                continue;
            }
        }

        logger.LogError(
            "Failed to correct SQL execution error in section '{SectionHeading}' after {MaxAttempts} attempts. Giving up.",
            originalPlan.Heading, MaxCorrectionAttempts);

        return null;
    }

    /// <summary>
    /// Attempts to correct a section plan whose SQL executed successfully but returned zero rows
    /// </summary>
    public async Task<SectionPlan?> CorrectZeroResultQuery(
        SectionPlan originalPlan,
        DbSchema schema,
        string userRequest,
        string? selectedModel = null,
        CancellationToken ct = default)
    {
        if (originalPlan == null || string.IsNullOrWhiteSpace(originalPlan.Sql))
            return null;

        logger.LogInformation(
            "Starting LLM self-correction for section '{SectionHeading}' after query returned zero rows",
            originalPlan.Heading);

        var currentPlan = new SectionPlan
        {
            Heading = originalPlan.Heading,
            Purpose = originalPlan.Purpose,
            Sql = originalPlan.Sql,
            Type = originalPlan.Type,
            ChartType = originalPlan.ChartType,
            XAxis = originalPlan.XAxis,
            YAxis = originalPlan.YAxis,
            XAxisTitle = originalPlan.XAxisTitle,
            YAxisTitle = originalPlan.YAxisTitle,
            Confidence = originalPlan.Confidence
        };

        var attemptNumber = 0;

        while (attemptNumber < MaxZeroResultCorrectionAttempts)
        {
            attemptNumber++;
            logger.LogInformation(
                "Zero-result correction attempt {AttemptNumber}/{MaxAttempts} for section '{SectionHeading}'",
                attemptNumber, MaxZeroResultCorrectionAttempts, currentPlan.Heading);

            try
            {
                var correctionPrompt = SchemaFormatterService.CreateZeroResultCorrectionPrompt(
                    currentPlan.Sql,
                    currentPlan.Heading,
                    currentPlan.Purpose,
                    userRequest,
                    schema);

                var correctionResponse = await GenerateCorrectionFromLLM(correctionPrompt, selectedModel, ct);

                if (string.IsNullOrWhiteSpace(correctionResponse))
                {
                    logger.LogWarning(
                        "LLM returned empty zero-result correction response for section '{SectionHeading}'",
                        currentPlan.Heading);
                    continue;
                }

                var correctedSql = ExtractSqlFromResponse(correctionResponse);
                if (string.IsNullOrWhiteSpace(correctedSql))
                    continue;

                // If the LLM decided the query is already correct (zero rows is the right answer),
                // it may return the exact same SQL. Stop retrying in that case.
                if (string.Equals(NormalizeSql(correctedSql), NormalizeSql(currentPlan.Sql), StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation(
                        "LLM confirmed zero rows is the correct result for section '{SectionHeading}'. No further correction attempted.",
                        currentPlan.Heading);
                    return null;
                }

                // Ensure the rewritten query didn't introduce hallucinated columns
                var hallucinatedColumns = ValidateSQL(correctedSql, schema);
                if (hallucinatedColumns.Count > 0)
                {
                    logger.LogWarning(
                        "Zero-result correction for section '{SectionHeading}' introduced {Count} hallucinated column(s). Retrying...",
                        currentPlan.Heading, hallucinatedColumns.Count);
                    continue;
                }

                currentPlan.Sql = correctedSql;
                logger.LogInformation(
                    "Zero-result correction produced a revised query for section '{SectionHeading}' on attempt {AttemptNumber}",
                    currentPlan.Heading, attemptNumber);
                return currentPlan;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error during zero-result correction attempt {AttemptNumber} for section '{SectionHeading}': {ErrorMessage}",
                    attemptNumber, currentPlan.Heading, ex.Message);
                continue;
            }
        }

        logger.LogWarning(
            "Unable to obtain a revised query for section '{SectionHeading}' after {MaxAttempts} zero-result correction attempts. Keeping original zero-row result.",
            originalPlan.Heading, MaxZeroResultCorrectionAttempts);

        return null;
    }

    private static string NormalizeSql(string sql) =>
        Regex.Replace(sql ?? "", @"\s+", " ").Trim().TrimEnd(';');

    /// <summary>
    /// Calls the LLM to generate corrected SQL
    /// </summary>
    private async Task<string> GenerateCorrectionFromLLM(
        string correctionPrompt,
        string? selectedModel,
        CancellationToken ct)
    {
        try
        {
            var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
            var model = selectedModel ?? configuration["Ollama:Model"] ?? "qwen3-coder:30b";

            var requestBody = new
            {
                model = model,
                prompt = correctionPrompt,
                stream = false
            };

            var response = await httpClient.PostAsJsonAsync(
                $"{baseUrl}/api/generate",
                requestBody,
                cancellationToken: ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("LLM API returned status {StatusCode}", response.StatusCode);
                return "";
            }

            var jsonString = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(jsonString))
                return "";

            using var doc = JsonDocument.Parse(jsonString);
            if (doc.RootElement.TryGetProperty("response", out var responseProp))
            {
                return responseProp.GetString() ?? "";
            }

            return "";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling LLM for correction: {ErrorMessage}", ex.Message);
            return "";
        }
    }

    /// <summary>
    /// Validates SQL for hallucinated columns
    /// </summary>
    public List<string> ValidateSQL(string sql, DbSchema schema)
    {
        var invalidColumns = new List<string>();

        if (string.IsNullOrWhiteSpace(sql) || schema?.Tables == null)
            return invalidColumns;

        var tableAliasRegex = new Regex(
            @"\b(?:FROM|JOIN)\s+\[([^\]]+)\]\.\[([^\]]+)\]\s+(?:AS\s+)?([A-Za-z0-9_]+)",
            RegexOptions.IgnoreCase);

        var aliasColRegex = new Regex(
            @"\b([A-Za-z0-9_]+)\.\[([^\]]+)\]",
            RegexOptions.IgnoreCase);

        var aliasMap = new Dictionary<string, (string schema, string table)>();

        // Extract table aliases
        foreach (Match m in tableAliasRegex.Matches(sql))
        {
            aliasMap[m.Groups[3].Value] = (m.Groups[1].Value, m.Groups[2].Value);
        }

        // Check column references
        foreach (Match m in aliasColRegex.Matches(sql))
        {
            var alias = m.Groups[1].Value;
            var columnName = m.Groups[2].Value;

            if (!aliasMap.ContainsKey(alias))
                continue;

            var (schemaName, tableName) = aliasMap[alias];
            var table = schema.Tables.FirstOrDefault(t =>
                string.Equals(t.Schema, schemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));

            if (table == null)
                continue;

            var columnExists = table.Columns.Any(c =>
                string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));

            if (!columnExists)
            {
                invalidColumns.Add($"{alias}.[{columnName}]");
            }
        }

        return invalidColumns;
    }

    /// <summary>
    /// Extracts SQL from LLM response (handles markdown code blocks and plain SQL)
    /// </summary>
    private string ExtractSqlFromResponse(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "";

        // Look for SQL in markdown code blocks
        var codeBlockPattern = @"```(?:sql)?\s*([\s\S]*?)```";
        var codeBlockRegex = new Regex(codeBlockPattern);
        var codeBlockMatch = codeBlockRegex.Match(response);

        if (codeBlockMatch.Success)
        {
            return codeBlockMatch.Groups[1].Value.Trim();
        }

        // If no code block, look for SELECT/WITH statements
        var sqlPattern = @"((?:WITH|SELECT)[\s\S]*?(?:;|$))";
        var sqlRegex = new Regex(sqlPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var sqlMatch = sqlRegex.Match(response);

        if (sqlMatch.Success)
        {
            return sqlMatch.Groups[1].Value.Trim();
        }

        // If still nothing found, return the whole response
        return response.Trim();
    }
}
