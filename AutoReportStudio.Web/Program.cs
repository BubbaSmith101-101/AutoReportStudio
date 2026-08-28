using AutoReportStudio.Web.Services;
using Serilog;
using System.IO;

// Ensure log directory exists
var logDir = @"c:\Logs\AutoReportStudio";
try
{
    if (!Directory.Exists(logDir))
    {
        Directory.CreateDirectory(logDir);
        Console.WriteLine($"Created log directory: {logDir}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error creating log directory: {ex.Message}");
}

try
{
    var builder = WebApplication.CreateBuilder(args);
    Console.WriteLine("Builder created successfully");

    // Configure Kestrel to allow larger request headers
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestHeadersTotalSize = 131072; // 128KB for headers (increased for browser dev tools)
        options.Limits.MaxRequestLineSize = 32768; // 32KB for request line
        Console.WriteLine("Kestrel configured with increased header limits");
    });

    // Configure Serilog
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .CreateLogger();

    Console.WriteLine("Serilog configured");

    builder.Host.UseSerilog();
    Console.WriteLine("Serilog integrated with host");

    builder.Services.AddControllersWithViews();
    builder.Services.AddHttpClient();
    Console.WriteLine("HttpClient service configured");

    builder.Services.AddScoped<ISchemaService, SchemaService>();
    builder.Services.AddScoped<IOllamaService, OllamaService>();
    builder.Services.AddScoped<IReportService, ReportService>();
    builder.Services.AddScoped<IExcelService, ExcelService>();
    Console.WriteLine("Services registered");

    var app = builder.Build();
    Console.WriteLine("Application built successfully");

    if (!app.Environment.IsDevelopment()) 
    { 
        app.UseExceptionHandler("/Report/Index"); 
        app.UseHsts(); 
    }

    app.UseHttpsRedirection(); 
    app.UseStaticFiles(); 
    app.UseRouting();

    app.MapControllerRoute(name: "default", pattern: "{controller=Report}/{action=Index}/{id?}");
    Console.WriteLine("Routes mapped");

    Log.Information("Application starting up");
    Console.WriteLine("Starting application...");
    app.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"FATAL ERROR: {ex.GetType().Name}");
    Console.WriteLine($"Message: {ex.Message}");
    Console.WriteLine($"Stack Trace: {ex.StackTrace}");

    try
    {
        Log.Fatal(ex, "Application terminated unexpectedly");
    }
    catch
    {
        Console.WriteLine("Failed to log fatal error to Serilog");
    }

    Environment.Exit(1);
}
finally
{
    try
    {
        Log.CloseAndFlush();
        Console.WriteLine("Logs flushed");
    }
    catch
    {
        Console.WriteLine("Failed to close/flush logs");
    }
}
