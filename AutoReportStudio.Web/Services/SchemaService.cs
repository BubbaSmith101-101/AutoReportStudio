using AutoReportStudio.Web.Models;
using Microsoft.Data.SqlClient;
namespace AutoReportStudio.Web.Services;

public interface ISchemaService 
{ 
    Task<DbSchema> Discover(string cs, CancellationToken ct = default); 
}

public class SchemaService : ISchemaService
{
    readonly ILogger<SchemaService> logger;

    public SchemaService(ILogger<SchemaService> l)
    {
        logger = l;
    }

    public async Task<DbSchema> Discover(string cs, CancellationToken ct = default)
    {
        logger.LogDebug("Entering Discover method");
        try
        {
            var result = new DbSchema(); 
            await using var cn = new SqlConnection(cs); 
            await cn.OpenAsync(ct);
            logger.LogInformation("Database connection opened, database: {DatabaseName}", cn.Database);
            result.DatabaseName = cn.Database;

            var sql = @"SELECT s.name,t.name,c.name,ty.name,c.is_nullable,
 CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END
 FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
 JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
 LEFT JOIN (SELECT ic.object_id,ic.column_id FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id WHERE i.is_primary_key=1) pk
 ON pk.object_id=c.object_id AND pk.column_id=c.column_id WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name,c.column_id";

            logger.LogDebug("Executing schema discovery query for tables and columns");
            await using (var cmd = new SqlCommand(sql, cn)) 
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                int tableCount = 0;
                int columnCount = 0;
                while (await r.ReadAsync(ct))
                {
                    var s = r.GetString(0); 
                    var n = r.GetString(1); 
                    var t = result.Tables.FirstOrDefault(x => x.Schema == s && x.Name == n);

                    if (t == null) 
                    { 
                        t = new DbTable { Schema = s, Name = n }; 
                        result.Tables.Add(t);
                        tableCount++;
                    }

                    t.Columns.Add(new DbColumn { Name = r.GetString(2), Type = r.GetString(3), Nullable = r.GetBoolean(4), PrimaryKey = Convert.ToInt32(r.GetValue(5)) == 1 });
                    columnCount++;
                }
                logger.LogInformation("Schema discovery completed: {TableCount} tables, {ColumnCount} columns discovered", tableCount, columnCount);
            }

            var fk = @"SELECT s.name,t.name,c.name,rs.name,rt.name,rc.name FROM sys.foreign_key_columns f
 JOIN sys.tables t ON t.object_id=f.parent_object_id JOIN sys.schemas s ON s.schema_id=t.schema_id
 JOIN sys.columns c ON c.object_id=t.object_id AND c.column_id=f.parent_column_id
 JOIN sys.tables rt ON rt.object_id=f.referenced_object_id JOIN sys.schemas rs ON rs.schema_id=rt.schema_id
 JOIN sys.columns rc ON rc.object_id=rt.object_id AND rc.column_id=f.referenced_column_id";

            logger.LogDebug("Executing foreign key discovery query");
            await using (var cmd = new SqlCommand(fk, cn)) 
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                int fkCount = 0;
                while (await r.ReadAsync(ct))
                {
                    var t = result.Tables.First(x => x.Schema == r.GetString(0) && x.Name == r.GetString(1));
                    t.ForeignKeys.Add(new DbForeignKey { Column = r.GetString(2), RefSchema = r.GetString(3), RefTable = r.GetString(4), RefColumn = r.GetString(5) });
                    fkCount++;
                }
                logger.LogInformation("Foreign key discovery completed: {ForeignKeyCount} foreign keys discovered", fkCount);
            }

            logger.LogInformation("Schema discovery process completed successfully");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during schema discovery: {ErrorMessage}", ex.Message);
            throw;
        }
        finally
        {
            logger.LogDebug("Exiting Discover method");
        }
    }
}
