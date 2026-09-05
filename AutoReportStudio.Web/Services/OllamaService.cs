using System.Net.Http.Json;
using System.Text.Json;
using AutoReportStudio.Web.Models;
namespace AutoReportStudio.Web.Services;

// Container for Ollama generation result with token tracking
public class OllamaGenerationResult
{
    public string Text { get; set; } = "";
    public long TokensGenerated { get; set; } = 0;
}

// Response shape returned by the Ollama /api/generate endpoint
public class OllamaResponse
{
    public string? response { get; set; }
    public bool? done { get; set; }
    public long eval_count { get; set; } = 0;
    public long prompt_eval_count { get; set; } = 0;
}

public interface IOllamaService 
{ 
    Task<List<string>> ListModels(CancellationToken ct = default);
    Task<ReportPlan> Plan(DbSchema schema, string request, string? selectedModel = null, CancellationToken ct = default); 
    Task<string> Summarize(ReportResult report, string? selectedModel = null, CancellationToken ct = default);
    long GetTotalTokensGenerated();
    void ResetTokenCount();
}

public class OllamaService : IOllamaService
{
    readonly IHttpClientFactory f; 
    readonly IConfiguration cfg;
    readonly ILogger<OllamaService> logger;
    private long totalTokensGenerated = 0;

    public OllamaService(IHttpClientFactory f, IConfiguration cfg, ILogger<OllamaService> l) 
    { 
        this.f = f; 
        this.cfg = cfg;
        logger = l;
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

    public async Task<ReportPlan> Plan(DbSchema schema, string request, string? selectedModel = null, CancellationToken ct = default)
    {
        logger.LogDebug("Entering Plan method");
        try
        {
            logger.LogInformation("Generating report plan with request: {Request}", request);

            var prompt = $@"You are a SQL Server report and visualization planner.

Your job is to analyze the user's reporting request and create a report plan using ONLY the supplied database schema.

GENERAL RULES

* Return valid JSON only.
* Do not include Markdown.
* Do not include ```json code fences.
* Do not include explanations before or after the JSON.
* Use ONLY tables and columns that exist in the supplied schema.
* Never invent tables, columns, relationships, or data.
* Use fully qualified bracketed SQL Server table names, for example [dbo].[Employees].
* Every SQL query must be read-only.
* The work week runs from Sunday through Saturday.
* Every query must be either:

  1. A single SELECT statement, or
  2. A SELECT-based CTE followed by a SELECT.
* Never generate INSERT, UPDATE, DELETE, MERGE, DROP, ALTER, CREATE, TRUNCATE, EXEC, EXECUTE, or other data-changing statements.
* For detail/tabular queries that could return many rows, use TOP (200).
* Aggregated queries used for charts do not require TOP (200) unless the result could reasonably contain more than 200 groups.
* Create between 2 and 6 useful report sections.

CHART RULES

* If the user explicitly asks for a chart, graph, visualization, trend, comparison, distribution, breakdown, or similar visual representation, include one or more appropriate chart sections.
* If the user asks for charts, do not return only tables.
* Charts must be based entirely on the SQL query contained in that section.
* Prefer aggregated SQL suitable for visualization.
* Choose the chart type that best represents the requested information.
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
""yAxisTitle"": null
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
""yAxisTitle"": ""Total""
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

USER REQUEST:
{request}

SUPPLIED SCHEMA:
{JsonSerializer.Serialize(schema)}
";

            var txt = await Generate(prompt, selectedModel, ct);
            logger.LogDebug("AI plan response received, deserializing");

            var plan = JsonSerializer.Deserialize<ReportPlan>(txt, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new Exception("Invalid Ollama report plan.");
            logger.LogInformation("Report plan generated successfully with {SectionCount} sections", plan.Sections.Count);
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
                using var doc = JsonDocument.Parse(text); 
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
