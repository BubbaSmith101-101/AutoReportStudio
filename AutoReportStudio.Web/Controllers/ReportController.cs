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
    public IActionResult Index()
    {
        logger.LogDebug("Entering Index action");
        try
        {
            var vm = new HomeVm();
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
            var models = await ollama.ListModels(ct);
            logger.LogInformation("Retrieved {ModelCount} available models", models.Count);
            return Json(models);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving models: {ErrorMessage}", ex.Message);
            return Json(new List<string>());
        }
        finally
        {
            logger.LogDebug("Exiting GetModels action");
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

            logger.LogDebug("Generating report with {TableCount} tables and model {SelectedModel}", m.SelectedTables.Count, m.SelectedModel);
            var r = await reports.Generate(m.ConnectionString, m.SelectedTables, m.Request, m.SelectedModel, ct); 
            TempData["Report"] = JsonSerializer.Serialize(r); 
            logger.LogInformation("Report generated successfully");
            return View("Result", r); 
        } 
        catch (Exception ex) 
        { 
            m.Error = ex.Message;
            logger.LogError(ex, "Error during report generation: {ErrorMessage}", ex.Message);
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
