namespace AutoReportStudio.Web.Models;

public class DbSchema { public string DatabaseName { get; set; } = ""; public List<DbTable> Tables { get; set; } = new(); }
public class DbTable { public string Schema { get; set; } = ""; public string Name { get; set; } = ""; public string FullName => $"[{Schema}].[{Name}]"; public List<DbColumn> Columns { get; set; } = new(); public List<DbForeignKey> ForeignKeys { get; set; } = new(); }
public class DbColumn { public string Name { get; set; } = ""; public string Type { get; set; } = ""; public bool Nullable { get; set; } public bool PrimaryKey { get; set; } public List<string> SampleValues { get; set; } = new(); }
public class DbForeignKey { public string Column { get; set; } = ""; public string RefSchema { get; set; } = ""; public string RefTable { get; set; } = ""; public string RefColumn { get; set; } = ""; }
public class HomeVm
{
    public string ConnectionString { get; set; } = "";
    public DbSchema? Schema { get; set; }
    public List<string> SelectedTables { get; set; } = new();
    public string Request { get; set; } = "Create a useful management report from these tables.";
    public string? Error { get; set; }
    public List<string> AvailableModels { get; set; } = new();
    public string SelectedModel { get; set; } = "";

    // Pagination properties
    public int PageSize { get; set; } = 20;
    public int CurrentPage { get; set; } = 1;
    public int TotalPages => Schema?.Tables.Count > 0 ? (int)Math.Ceiling((double)Schema.Tables.Count / PageSize) : 1;
    public int TotalItems => Schema?.Tables.Count ?? 0;
}
public class ReportPlan { public string Title { get; set; } = "Generated Report"; public List<SectionPlan> Sections { get; set; } = new(); }
public class SectionPlan { public string Heading { get; set; } = ""; public string Purpose { get; set; } = ""; public string Sql { get; set; } = ""; public string Type { get; set; } = "table"; public string? ChartType { get; set; } public string? XAxis { get; set; } public string? YAxis { get; set; } public string? XAxisTitle { get; set; } public string? YAxisTitle { get; set; } public double Confidence { get; set; } = 100; }
public class ReportResult { public string Title { get; set; } = "Generated Report"; public string Summary { get; set; } = ""; public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow; public List<ReportSection> Sections { get; set; } = new(); public long ProcessingTimeMs { get; set; } = 0; public long TokensGenerated { get; set; } = 0; public long TimeToFirstTokenMs { get; set; } = 0; public string ModelUsed { get; set; } = ""; public string UserRequest { get; set; } = ""; public int ModelMaxContextLength { get; set; } = 0; public int ConfiguredContextLength { get; set; } = 0; }
public class ReportSection { public string Heading { get; set; } = ""; public string Purpose { get; set; } = ""; public string Sql { get; set; } = ""; public List<string> Columns { get; set; } = new(); public List<List<string?>> Rows { get; set; } = new(); public string Type { get; set; } = "table"; public string? ChartType { get; set; } public string? XAxis { get; set; } public string? YAxis { get; set; } public string? XAxisTitle { get; set; } public string? YAxisTitle { get; set; } public int PageSize { get; set; } = 50; public int CurrentPage { get; set; } = 1; public int TotalPages => Rows.Count > 0 ? (int)Math.Ceiling((double)Rows.Count / PageSize) : 1; public int TotalItems => Rows.Count; public double Confidence { get; set; } = 100; }
