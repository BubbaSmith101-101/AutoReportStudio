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

public interface IOllamaService 
{ 
    Task<List<string>> ListModels(CancellationToken ct = default);
    Task<ReportPlan> Plan(DbSchema schema, string request, string? selectedModel = null, CancellationToken ct = default); 
    Task<string> Summarize(ReportResult report, string? selectedModel = null, CancellationToken ct = default);
    long GetTotalTokensGenerated();
    void ResetTokenCount();
}

public class OllamaService : IollamaMarker, IOllamaService
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
            var prompt = $@"You are a SQL Server report planner. Use ONLY the supplied schema. Return JSON only.
Every query must be a single read-only SELECT or SELECT-based CTE. Never invent columns. Use fully qualified bracketed table names.
Limit detail output with TOP (200). Create 2-6 useful sections.
JSON shape: {{""title"":""..."",""sections"":[{{""heading"":""..."",""purpose"":""..."",""sql"":""SELECT ...""}}]}}
User request: {request}
Schema: {JsonSerializer.Serialize(schema)}";

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

            var res = await client.PostAsJsonAsync(url + "/api/generate", new { model, prompt, stream = false, format = "json", options = new { temperature = .1 } }, ct);
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
            string errorMessage = $"Unable to select the AI language model. Failed to connect to Ollama at the configured URL. Please verify that Ollama is running and accessible. Details: {ex.Message}";
            logger.LogError(ex, "Network error communicating with Ollama: {ErrorMessage}", errorMessage);
            throw new Exception(errorMessage);
        }
        catch (OperationCanceledException ex)
        {
            string errorMessage = "Request to the AI language model timed out. Ollama is taking longer than expected (over 5 minutes). Please verify that Ollama is responsive and not processing other requests. Your model may be too large for this hardware.";
            logger.LogError(ex, "Timeout error communicating with Ollama: {ErrorMessage}", errorMessage);
            throw new Exception(errorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in Generate method: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            logger.LogDebug("Exiting Generate method");
        }
    }

    class OllamaResponse 
    { 
        public string response { get; set; } = "";
        public long eval_count { get; set; } = 0;
        public long prompt_eval_count { get; set; } = 0;
    }
}
public interface IollamaMarker { }
