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
    readonly ILogger<ReportService> logger;
    static readonly Regex Bad = new(@"\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|CREATE|TRUNCATE|EXEC|EXECUTE|GRANT|REVOKE|DENY|DBCC|BACKUP|RESTORE|OPENROWSET|OPENDATASOURCE|KILL)\b", RegexOptions.IgnoreCase);

    public ReportService(ISchemaService s, IOllamaService o, ILogger<ReportService> l) 
    { 
        schema = s; 
        ollama = o;
        logger = l;
    }

    public async Task<ReportResult> Generate(string cs, List<string> selected, string request, string? selectedModel = null, CancellationToken ct = default)
    {
        logger.LogDebug("Entering Generate method with {TableCount} selected tables", selected.Count);
        var startTime = DateTime.UtcNow;
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
            var plan = await ollama.Plan(db, request, selectedModel, ct); 
            logger.LogInformation("AI plan generated with {SectionCount} sections", plan.Sections.Count);

            var result = new ReportResult { Title = plan.Title };
            await using var cn = new SqlConnection(cs); 
            await cn.OpenAsync(ct);
            logger.LogDebug("Database connection opened");

            int processedSections = 0;
            foreach (var p in plan.Sections.Take(6))
            {
                logger.LogDebug("Processing section: {SectionHeading}", p.Heading);
                var sec = new ReportSection { Heading = p.Heading, Purpose = p.Purpose, Sql = p.Sql }; 
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
                    await using var cmd = new SqlCommand(p.Sql, cn) { CommandTimeout = 60 }; 
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
                    logger.LogInformation("Query executed successfully for section {SectionHeading} with {RowCount} rows", p.Heading, count);
                }
                catch (Exception ex) 
                { 
                    logger.LogError(ex, "Error executing query for section {SectionHeading}: {ErrorMessage}", p.Heading, ex.Message);
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

            logger.LogInformation("Report generation completed successfully in {ProcessingTimeMs}ms with {TokensGenerated} tokens", result.ProcessingTimeMs, result.TokensGenerated);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during report generation: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            logger.LogDebug("Exiting Generate method");
        }
    }
}
