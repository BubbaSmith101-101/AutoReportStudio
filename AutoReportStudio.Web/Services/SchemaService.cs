using AutoReportStudio.Web.Models;
using Microsoft.Data.SqlClient;
namespace AutoReportStudio.Web.Services;

public interface ISchemaService 
{ 
    Task<DbSchema> Discover(string cs, CancellationToken ct = default); 

    /// <summary>
    /// Fetches the actual distinct values present in a column, so callers (e.g. the LLM
    /// correction pipeline) can verify a string literal used in a WHERE/HAVING comparison
    /// actually exists in the data instead of guessing a plausible-sounding value.
    /// </summary>
    Task<List<string>> GetDistinctValues(string cs, string schemaName, string tableName, string columnName, int maxValues = 50, CancellationToken ct = default);
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

            await PopulateSampleValues(result, cn, logger, ct);

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

    /// <summary>
    /// For small "lookup"-style tables (fewer than a threshold number of rows) with string
    /// columns, fetches the actual distinct values present in the database and stores them on
    /// the DbColumn. This lets the LLM use real values (e.g. actual StatusName strings) in
    /// WHERE clauses instead of guessing plausible-sounding literals that don't exist in the
    /// data, which was causing queries to silently return zero rows.
    /// </summary>
    private static async Task PopulateSampleValues(DbSchema schema, SqlConnection cn, ILogger logger, CancellationToken ct)
    {
        const int MaxTableRowsForSampling = 500;
        const int MaxDistinctValuesPerColumn = 25;

        foreach (var table in schema.Tables)
        {
            var stringColumns = table.Columns
                .Where(c => c.Type is "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext")
                .ToList();

            if (stringColumns.Count == 0) continue;

            long rowCount;
            try
            {
                using var countCmd = new SqlCommand($"SELECT COUNT(*) FROM [{table.Schema}].[{table.Name}]", cn) { CommandTimeout = 15 };
                rowCount = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct) ?? 0L);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skipping sample value discovery for {Schema}.{Table}: could not get row count", table.Schema, table.Name);
                continue;
            }

            // Only sample small "lookup"-style tables to avoid scanning large fact tables
            if (rowCount == 0 || rowCount > MaxTableRowsForSampling) continue;

            foreach (var col in stringColumns)
            {
                try
                {
                    var sql = $"SELECT DISTINCT TOP ({MaxDistinctValuesPerColumn}) [{col.Name}] FROM [{table.Schema}].[{table.Name}] WHERE [{col.Name}] IS NOT NULL ORDER BY [{col.Name}]";
                    using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 15 };
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    var values = new List<string>();
                    while (await r.ReadAsync(ct))
                    {
                        if (!r.IsDBNull(0))
                            values.Add(r.GetString(0));
                    }
                    col.SampleValues = values;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not fetch sample values for {Schema}.{Table}.{Column}", table.Schema, table.Name, col.Name);
                }
            }
        }
    }

    /// <summary>
    /// Fetches the actual distinct values present in a column directly from the database.
    /// Used by the LLM correction pipeline to verify that a string literal referenced in a
    /// WHERE/HAVING comparison (e.g. [StatusName] = 'Absent') actually exists in the data,
    /// rather than being a plausible-sounding guess.
    /// </summary>
    public async Task<List<string>> GetDistinctValues(string cs, string schemaName, string tableName, string columnName, int maxValues = 50, CancellationToken ct = default)
    {
        logger.LogDebug("Fetching distinct values for {Schema}.{Table}.{Column}", schemaName, tableName, columnName);
        var values = new List<string>();

        try
        {
            await using var cn = new SqlConnection(cs);
            await cn.OpenAsync(ct);

            // Validate the schema/table/column actually exist before building dynamic SQL,
            // to avoid SQL injection via the identifier names.
            const string validateSql = @"SELECT COUNT(*) FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schema AND t.name = @table AND c.name = @column";

            await using (var validateCmd = new SqlCommand(validateSql, cn))
            {
                validateCmd.Parameters.AddWithValue("@schema", schemaName);
                validateCmd.Parameters.AddWithValue("@table", tableName);
                validateCmd.Parameters.AddWithValue("@column", columnName);
                var exists = Convert.ToInt32(await validateCmd.ExecuteScalarAsync(ct) ?? 0) > 0;
                if (!exists)
                {
                    logger.LogWarning("GetDistinctValues: column {Schema}.{Table}.{Column} not found in schema", schemaName, tableName, columnName);
                    return values;
                }
            }

            var sql = $"SELECT DISTINCT TOP ({maxValues}) [{columnName}] FROM [{schemaName}].[{tableName}] WHERE [{columnName}] IS NOT NULL ORDER BY [{columnName}]";
            await using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 15 };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!r.IsDBNull(0))
                    values.Add(Convert.ToString(r.GetValue(0)) ?? "");
            }

            logger.LogInformation("Fetched {Count} distinct value(s) for {Schema}.{Table}.{Column}", values.Count, schemaName, tableName, columnName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch distinct values for {Schema}.{Table}.{Column}", schemaName, tableName, columnName);
        }

        return values;
    }
}
