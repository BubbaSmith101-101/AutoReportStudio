using System.Text.Json;
using AutoReportStudio.Web.Models;
using AutoReportStudio.Web.Services;
using Microsoft.AspNetCore.Mvc;
namespace AutoReportStudio.Web.Controllers;

public class ReportController : Controller
{
    readonly ISchemaService schema; 
    readonly IReportService reports; 
    readonly IExcelService excel;
    readonly IOllamaService ollama;
    readonly IConfiguration config;
    readonly ILogger<ReportController> logger;

    public ReportController(ISchemaService s, IReportService r, IExcelService e, IOllamaService o, IConfiguration cfg, ILogger<ReportController> l) 
    { 
        schema = s; 
        reports = r; 
        excel = e;
        ollama = o;
        config = cfg;
        logger = l;
    }

    [HttpGet] 
    public IActionResult Index(int page = 1)
    {
        logger.LogDebug("Entering Index action");
        try
        {
            var vm = new HomeVm();
            vm.CurrentPage = page;
            var defaultConnection = config.GetConnectionString("DefaultConnection");
            if (!string.IsNullOrEmpty(defaultConnection))
            {
                vm.ConnectionString = defaultConnection;
                logger.LogDebug("Loaded default connection string from configuration");
            }
            return View(vm);
        }
        finally
        {
            logger.LogDebug("Exiting Index action");
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetModels(CancellationToken ct)
    {
        logger.LogDebug("Entering GetModels action");
        try
        {
            var models = await ollama.ListModelsWithDetails(ct);
            logger.LogInformation("Retrieved {ModelCount} available models", models.Count);
            return Json(models);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving models: {ErrorMessage}", ex.Message);
            return Json(new List<OllamaModelInfo>());
        }
        finally
        {
            logger.LogDebug("Exiting GetModels action");
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetModelMaxContext(string model, CancellationToken ct)
    {
        logger.LogDebug("Entering GetModelMaxContext action for model: {Model}", model);
        try
        {
            var maxContext = await ollama.GetModelMaxContext(model, ct);
            logger.LogInformation("Retrieved max context length: {MaxContext}", maxContext);
            return Json(maxContext);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving max context length: {ErrorMessage}", ex.Message);
            return Json(0);
        }
        finally
        {
            logger.LogDebug("Exiting GetModelMaxContext action");
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken] 
    public async Task<IActionResult> Discover(HomeVm m, CancellationToken ct)
    {
        logger.LogDebug("Entering Discover action with connection string: {ConnectionString}", m.ConnectionString);
        try 
        { 
            m.Schema = await schema.Discover(m.ConnectionString, ct);
            logger.LogInformation("Schema discovery completed successfully");
        } 
        catch (Exception ex) 
        { 
            m.Error = ex.Message;
            logger.LogError(ex, "Error during schema discovery: {ErrorMessage}", ex.Message);
        } 
        finally
        {
            logger.LogDebug("Exiting Discover action");
        }
        return View("Index", m); 
    }

    [HttpPost]
    [ValidateAntiForgeryToken] 
    public async Task<IActionResult> Generate(HomeVm m, CancellationToken ct)
    {
        logger.LogDebug("Entering Generate action");
        try 
        { 
            // Reset token counter for this report generation
            ollama.ResetTokenCount();

            // Get context length percentage from form (slider value)
            int? contextLengthPercent = null;
            if (!string.IsNullOrEmpty(m.ContextLengthPercent))
            {
                contextLengthPercent = int.Parse(m.ContextLengthPercent);
            }

            // Get timeout minutes from form (slider value)
            int? timeoutMinutes = null;
            if (!string.IsNullOrEmpty(m.TimeoutMinutes))
            {
                timeoutMinutes = int.Parse(m.TimeoutMinutes);
            }

            logger.LogDebug("Generating report with {TableCount} tables, model {SelectedModel}, context length {ContextLengthPercent}%, timeout {TimeoutMinutes} minutes", m.SelectedTables.Count, m.SelectedModel, contextLengthPercent ?? 75, timeoutMinutes ?? 3);
            var r = await reports.Generate(m.ConnectionString, m.SelectedTables, m.Request, m.SelectedModel, ct, contextLengthPercent, timeoutMinutes);
            TempData["Report"] = JsonSerializer.Serialize(r); 
            logger.LogInformation("Report generated successfully");
            return View("Result", r); 
        } 
        catch (Exception ex) 
        { 
            // Build more informative error message with source information
            string errorMessage = ex.Message;
            string errorSource = "Unknown";

            // Determine error source
            if (ex.Message.Contains("Ollama"))
            {
                errorSource = "Ollama LLM Service";
            }
            else if (ex.Message.Contains("connection"))
            {
                errorSource = "Database Connection";
            }
            else if (ex.Message.Contains("JSON"))
            {
                errorSource = "Response Parsing (LLM)";
            }
            else if (ex.InnerException is JsonException)
            {
                errorSource = "LLM Response Format (JSON parsing failed)";
                errorMessage = $"The LLM returned invalid JSON. This typically means the model output couldn't be parsed as expected. Original error: {ex.InnerException.Message}";
            }
            else if (ex.Message.Contains("timeout") || ex.Message.Contains("Timeout"))
            {
                errorSource = "Request Timeout";
            }

            m.Error = $"[{errorSource}] {errorMessage}";
            logger.LogError(ex, "Error during report generation from {ErrorSource}: {ErrorMessage}", errorSource, errorMessage);
            try 
            { 
                m.Schema = await schema.Discover(m.ConnectionString, ct); 
            } 
            catch (Exception discoverEx)
            {
                logger.LogError(discoverEx, "Error during recovery schema discovery");
            } 
            return View("Index", m); 
        }
        finally
        {
            logger.LogDebug("Exiting Generate action");
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken] 
    public IActionResult Export()
    {
        logger.LogDebug("Entering Export action");
        try
        {
            var j = TempData.Peek("Report") as string; 
            if (j == null)
            {
                logger.LogWarning("No report found in TempData for export");
                return RedirectToAction("Index"); 
            }
            var r = JsonSerializer.Deserialize<ReportResult>(j)!; 
            logger.LogInformation("Exporting report to Excel");
            return File(excel.Export(r), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "GeneratedReport.xlsx"); 
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during report export: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            logger.LogDebug("Exiting Export action");
        }
    }
}
