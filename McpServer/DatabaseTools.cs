using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using ModelContextProtocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class DatabaseTools
{
    private const string ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Database=Northwind;Integrated Security=True;TrustServerCertificate=True;Pooling=False;Connect Timeout=5;Application Name=McpNorthwind";

    [McpServerTool(Name = "list_database_tables")]
    [Description("List Northwind LocalDB tables and their columns, SQL types, nullability and primary keys. Call this before writing a database query; never invent table or column names.")]
    public static Task<string> ListTables(CancellationToken cancellationToken) => ExecuteAsync(
        """
        SELECT s.name AS [schema], t.name AS [table], c.name AS [column], ty.name AS [type],
            c.is_nullable AS nullable,
            CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.index_columns ic
                JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                WHERE ic.object_id = t.object_id AND ic.column_id = c.column_id AND i.is_primary_key = 1
            ) THEN 1 ELSE 0 END AS bit) AS primaryKey
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.columns c ON c.object_id = t.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        WHERE s.name = 'dbo' AND t.is_ms_shipped = 0
        ORDER BY s.name, t.name, c.column_id
        """, 500, cancellationToken);

    [McpServerTool(Name = "query_database")]
    [Description("Run one read-only T-SQL SELECT against Northwind LocalDB. Supports joins, aggregates and CTEs. Inspect list_database_tables first. No writes, SELECT INTO, procedures or multiple statements. Returns JSON columns and row arrays with truncation indicators. Use ORDER BY for deterministic results. Never claim truncated output is complete.")]
    public static Task<string> Query(
        [Description("One T-SQL SELECT statement using the Northwind schema. Maximum 8000 characters.")] string sql,
        CancellationToken cancellationToken,
        [Description("Maximum returned rows, from 1 to 100. Default 20.")] int maxRows = 20)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > 8000)
            throw new McpException("SQL must contain 1 to 8000 characters.");
        if (maxRows is < 1 or > 100)
            throw new McpException("maxRows must be between 1 and 100.");

        using var input = new StringReader(sql);
        var fragment = new TSql170Parser(initialQuotedIdentifiers: true).Parse(input, out var errors);
        if (errors.Count != 0 || fragment is not TSqlScript script || script.Batches.Count != 1
            || script.Batches[0].Statements.Count != 1 || script.Batches[0].Statements[0] is not SelectStatement select)
            throw new McpException("Only one valid SELECT statement is allowed; batches and other SQL commands are rejected.");
        var guard = new SelectGuard();
        select.Accept(guard);
        if (!guard.Allowed)
            throw new McpException("SELECT INTO and variable assignment are not allowed.");

        return ExecuteAsync(sql, maxRows, cancellationToken);
    }

    private static async Task<string> ExecuteAsync(string sql, int maxRows, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            // A non-pooled session cannot revert to the owner's permissions, even from submitted SQL.
            await using (var restrict = new SqlCommand(
                "EXECUTE AS USER = 'McpNorthwindReader' WITH NO REVERT;", connection) { CommandTimeout = 5 })
                await restrict.ExecuteNonQueryAsync(cancellationToken);

            await using var command = new SqlCommand($"SET ROWCOUNT {maxRows + 1};\n{sql}", connection)
            {
                CommandTimeout = 15
            };
            await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken);
            if (reader.FieldCount > 32)
                throw new McpException("Queries may return at most 32 columns. Select fewer columns.");
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            var rows = new List<object?[]>();
            bool truncated = false;
            bool valuesTruncated = false;
            int characters = 2;
            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count == maxRows)
                {
                    truncated = true;
                    break;
                }
                var row = new object?[reader.FieldCount];
                for (int i = 0; i < row.Length; i++)
                {
                    if (await reader.IsDBNullAsync(i, cancellationToken))
                        continue;
                    var type = reader.GetFieldType(i);
                    if (type == typeof(string))
                    {
                        var buffer = new char[501];
                        int length = (int)reader.GetChars(i, 0, buffer, 0, buffer.Length);
                        valuesTruncated |= length > 500;
                        row[i] = new string(buffer, 0, Math.Min(length, 500));
                    }
                    else if (type == typeof(byte[]) || type == typeof(object)
                        || reader.GetDataTypeName(i) == "xml")
                    {
                        row[i] = "[binary or unsupported value omitted]";
                        valuesTruncated = true;
                    }
                    else
                    {
                        var value = reader.GetValue(i);
                        row[i] = value is DateTime date ? date.ToString("O", CultureInfo.InvariantCulture) : value;
                    }
                }
                characters += JsonSerializer.Serialize(row).Length + (rows.Count > 0 ? 1 : 0);
                if (characters > 32000)
                {
                    truncated = true;
                    break;
                }
                rows.Add(row);
            }
            return JsonSerializer.Serialize(new { database = "Northwind", columns, rows, rowCount = rows.Count, truncated, valuesTruncated });
        }
        catch (SqlException ex)
        {
            Console.Error.WriteLine($"Northwind SQL operation failed ({ex.Number}): {ex.Message}");
            throw new McpException($"Northwind database operation failed (SQL {ex.Number}). Check that LocalDB is running, Northwind and McpNorthwindReader are installed, and the SELECT uses valid table/column names. See the README.", ex);
        }
    }

    private sealed class SelectGuard : TSqlFragmentVisitor
    {
        public bool Allowed { get; private set; } = true;
        public override void ExplicitVisit(SelectStatement node)
        {
            Allowed &= node.Into is null;
            base.ExplicitVisit(node);
        }
        public override void ExplicitVisit(SelectSetVariable node) => Allowed = false;
    }
}
