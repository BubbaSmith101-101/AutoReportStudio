using AutoReportStudio.Web.Models;
using ClosedXML.Excel;
namespace AutoReportStudio.Web.Services;

public interface IExcelService 
{ 
    byte[] Export(ReportResult r); 
}

public class ExcelService : IExcelService
{
    readonly ILogger<ExcelService> logger;

    public ExcelService(ILogger<ExcelService> l)
    {
        logger = l;
    }

    public byte[] Export(ReportResult r)
    {
        logger.LogDebug("Entering Export method");
        try
        {
            logger.LogInformation("Exporting report to Excel: {ReportTitle}", r.Title);
            using var wb = new XLWorkbook(); 
            var s = wb.AddWorksheet("Summary"); 
            s.Cell(1, 1).Value = r.Title; 
            s.Cell(1, 1).Style.Font.Bold = true; 
            s.Cell(3, 1).Value = r.Summary; 
            s.Column(1).Width = 100;
            logger.LogDebug("Summary sheet created");

            int n = 1; 
            foreach (var sec in r.Sections)
            {
                logger.LogDebug("Processing section {SectionNumber}: {SectionHeading}", n, sec.Heading);
                var ws = wb.AddWorksheet("Section " + n++); 
                ws.Cell(1, 1).Value = sec.Heading; 
                ws.Cell(2, 1).Value = sec.Purpose;

                for (int c = 0; c < sec.Columns.Count; c++) 
                { 
                    ws.Cell(4, c + 1).Value = sec.Columns[c]; 
                    ws.Cell(4, c + 1).Style.Font.Bold = true; 
                }

                for (int y = 0; y < sec.Rows.Count; y++) 
                    for (int x = 0; x < sec.Rows[y].Count; x++) 
                        ws.Cell(y + 5, x + 1).Value = sec.Rows[y][x] ?? "";

                ws.Columns().AdjustToContents(1, 50);
                logger.LogInformation("Section exported with {RowCount} rows and {ColumnCount} columns", sec.Rows.Count, sec.Columns.Count);
            }

            using var ms = new MemoryStream(); 
            wb.SaveAs(ms);
            logger.LogInformation("Excel file generated successfully, size: {FileSize} bytes", ms.ToArray().Length);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during Excel export: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            logger.LogDebug("Exiting Export method");
        }
    }
}
