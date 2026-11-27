using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class DatabaseSelfTest
{
    public static async Task RunAsync(McpClient mcp, IList<McpClientTool> tools, CancellationToken cancellationToken)
    {
        var schema = tools.Single(tool => tool.Name == "query_database").JsonSchema;
        Require(schema.GetProperty("properties").TryGetProperty("sql", out _)
            && schema.GetProperty("properties").TryGetProperty("maxRows", out _)
            && schema.GetProperty("required").EnumerateArray().Any(item => item.GetString() == "sql"),
            "Database query schema exposes SQL and the optional row limit.");

        foreach (var sql in new[]
        {
            "", " ", new string('x', 8001), "not SQL", "DELETE FROM dbo.Products WHERE 1=0",
            "UPDATE dbo.Products SET UnitPrice=0 WHERE 1=0", "EXEC sp_help", "DROP TABLE dbo.Products",
            "SELECT 1; SELECT 2", "SELECT 1; REVERT; DELETE FROM dbo.Products",
            "SELECT 1\nGO\nSELECT 2", "SELECT * INTO #copy FROM dbo.Products",
            "WITH p AS (SELECT * FROM dbo.Products) SELECT * INTO #copy FROM p",
            "SELECT @x = 1", "USE master"
        })
        {
            var result = await CallAsync(sql);
            Require(result.IsError == true, "Unsafe or invalid database SQL is rejected before execution.");
        }
        foreach (int limit in new[] { 0, -1, 101 })
            Require((await CallAsync("SELECT 1", limit)).IsError == true, "Invalid database row limit is rejected.");

        if (Environment.GetEnvironmentVariable("MCP_TEST_NORTHWIND") != "1")
        {
            Console.WriteLine("SKIP: Live Northwind checks (set MCP_TEST_NORTHWIND=1 to enable).");
            return;
        }

        var tables = await mcp.CallToolAsync("list_database_tables", cancellationToken: cancellationToken);
        Require(tables.IsError != true, "Northwind schema can be read through MCP.");
        using (var json = Parse(tables))
        {
            var root = json.RootElement;
            Require(!root.GetProperty("truncated").GetBoolean()
                && root.GetProperty("rows").EnumerateArray().Any(row => row[1].GetString() == "Products"
                    && row[2].GetString() == "ProductID" && row[5].GetBoolean()),
                "Schema contains the Products primary key without truncation.");
        }

        using (var json = await QueryAsync("SELECT (SELECT COUNT(*) FROM dbo.Customers) AS customers, (SELECT COUNT(*) FROM dbo.Products) AS products, (SELECT COUNT(*) FROM dbo.Orders) AS orders"))
        {
            var root = json.RootElement;
            var row = root.GetProperty("rows")[0];
            Require(root.GetProperty("database").GetString() == "Northwind"
                && root.GetProperty("rowCount").GetInt32() == 1
                && row[0].GetInt32() == 91 && row[1].GetInt32() == 77 && row[2].GetInt32() == 830,
                "Northwind sample counts are correct through the actual MCP connection.");
        }
        using (var json = await QueryAsync("WITH p AS (SELECT ProductID, ProductName, CategoryID FROM dbo.Products) SELECT p.ProductID, p.ProductName, c.CategoryName FROM p JOIN dbo.Categories c ON c.CategoryID=p.CategoryID ORDER BY p.ProductID", 2))
        {
            var root = json.RootElement;
            Require(root.GetProperty("rowCount").GetInt32() == 2 && root.GetProperty("truncated").GetBoolean()
                && root.GetProperty("rows")[0][1].GetString() == "Chai"
                && root.GetProperty("rows")[0][2].GetString() == "Beverages",
                "CTEs and joins work and the exact row limit is enforced.");
        }
        using (var json = await QueryAsync("SELECT ProductID FROM dbo.Products ORDER BY ProductID"))
            Require(json.RootElement.GetProperty("rowCount").GetInt32() == 20
                && json.RootElement.GetProperty("truncated").GetBoolean(), "Default database row limit is 20.");
        using (var json = await QueryAsync("SELECT ProductID FROM dbo.Products ORDER BY ProductID", 100))
            Require(json.RootElement.GetProperty("rowCount").GetInt32() == 77
                && !json.RootElement.GetProperty("truncated").GetBoolean(), "Complete results are not marked truncated.");
        using (var json = await QueryAsync("SELECT TOP (2) ProductID FROM dbo.Products ORDER BY ProductID", 2))
            Require(!json.RootElement.GetProperty("truncated").GetBoolean(), "An exact-limit complete result is not marked truncated.");
        using (var json = await QueryAsync("SELECT REPLICATE(CAST('x' AS nvarchar(max)), 10000) AS longText, CAST(NULL AS int) AS missing, CAST(0x1234 AS varbinary(max)) AS binaryValue"))
        {
            var root = json.RootElement;
            Require(root.GetProperty("rows")[0][0].GetString()!.Length == 500
                && root.GetProperty("rows")[0][1].ValueKind == JsonValueKind.Null
                && root.GetProperty("rows")[0][2].GetString() == "[binary or unsupported value omitted]"
                && root.GetProperty("valuesTruncated").GetBoolean(), "Long text, NULL and binary output are handled explicitly.");
        }
        using (var json = await QueryAsync("SELECT USER_NAME() AS principal, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'INSERT') AS canInsert, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'UPDATE') AS canUpdate, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'DELETE') AS canDelete, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'EXECUTE') AS canExecute, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER') AS canAlter"))
        {
            var row = json.RootElement.GetProperty("rows")[0];
            Require(row[0].GetString() == "McpNorthwindReader"
                && Enumerable.Range(1, 5).All(i => row[i].GetInt32() == 0),
                "Actual query session is the restricted user with no write, execute or alter permissions.");
        }
        Require((await CallAsync("SELECT * FROM master.sys.databases")).IsError == true,
            "The restricted session cannot read another database.");
        Require((await CallAsync("SELECT " + string.Join(",", Enumerable.Range(1, 33).Select(i => $"{i} AS c{i}")))).IsError == true,
            "Database results reject more than 32 columns.");

        using (var json = await QueryAsync("SELECT REPLICATE('x', 396) AS a FROM dbo.Orders", 100))
        {
            var root = json.RootElement;
            Require(root.GetProperty("truncated").GetBoolean()
                && root.GetProperty("rowCount").GetInt32() == 79
                && root.GetProperty("rows").GetRawText().Length == 31680,
                "The 32000-character budget includes JSON array brackets and row separators.");
        }

        Task<CallToolResult> CallAsync(string sql, int? limit = null)
        {
            var arguments = new Dictionary<string, object?> { ["sql"] = sql };
            if (limit is not null) arguments["maxRows"] = limit.Value;
            return mcp.CallToolAsync("query_database", arguments, cancellationToken: cancellationToken).AsTask();
        }

        async Task<JsonDocument> QueryAsync(string sql, int? limit = null)
        {
            var result = await CallAsync(sql, limit);
            Require(result.IsError != true, "Read-only query succeeds through MCP.");
            return Parse(result);
        }
    }

    private static JsonDocument Parse(CallToolResult result) => JsonDocument.Parse(
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"Database self-test failed: {description}");
        Console.WriteLine($"PASS: {description}");
    }
}
