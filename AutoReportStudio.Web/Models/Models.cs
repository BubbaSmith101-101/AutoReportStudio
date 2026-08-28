namespace AutoReportStudio.Web.Models;

public class DbSchema { public string DatabaseName { get; set; } = ""; public List<DbTable> Tables { get; set; } = new(); }
public class DbTable { public string Schema { get; set; } = ""; public string Name { get; set; } = ""; public string FullName => $"[{Schema}].[{Name}]"; public List<DbColumn> Columns { get; set; } = new(); public List<DbForeignKey> ForeignKeys { get; set; } = new(); }
public class DbColumn { public string Name { get; set; } = ""; public string Type { get; set; } = ""; public bool Nullable { get; set; } public bool PrimaryKey { get; set; } }
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
}
public class ReportPlan { public string Title { get; set; } = "Generated Report"; public List<SectionPlan> Sections { get; set; } = new(); }
public class SectionPlan { public string Heading { get; set; } = ""; public string Purpose { get; set; } = ""; public string Sql { get; set; } = ""; }
public class ReportResult { public string Title { get; set; } = "Generated Report"; public string Summary { get; set; } = ""; public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow; public List<ReportSection> Sections { get; set; } = new(); public long ProcessingTimeMs { get; set; } = 0; public long TokensGenerated { get; set; } = 0; }
public class ReportSection { public string Heading { get; set; } = ""; public string Purpose { get; set; } = ""; public string Sql { get; set; } = ""; public List<string> Columns { get; set; } = new(); public List<List<string?>> Rows { get; set; } = new(); }
