using System.ComponentModel;
using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ModelContextProtocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class NorthwindTools
{
    private const string ConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=Northwind;Integrated Security=True;Connect Timeout=5";

    [McpServerTool(Name = "find_northwind_products", ReadOnly = true)]
    [Description("Look up to five products in the local Northwind database by full or partial name. Returns product names, unit prices, units in stock, and whether each product is discontinued.")]
    public static async Task<string> FindProducts(
        [Description("The product name or part of it, for example Chai.")] string name,
        CancellationToken cancellationToken)
    {
        const int maxNameLength = 100;
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > maxNameLength)
            throw new McpException($"Product name must contain 1 to {maxNameLength} characters.");

        if (!OperatingSystem.IsWindows())
            throw new McpException("Northwind product lookup requires SQL Server LocalDB on Windows.");

        try
        {
            await using var connection = new SqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = """
                SELECT TOP (5) ProductName, UnitPrice, UnitsInStock, Discontinued
                FROM dbo.Products
                WHERE CHARINDEX(@name, ProductName) > 0
                ORDER BY CASE WHEN ProductName = @name THEN 0 ELSE 1 END, ProductName, ProductID;
                """;
            command.Parameters.Add("@name", SqlDbType.NVarChar, maxNameLength).Value = name.Trim();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var products = new List<Product>();
            while (await reader.ReadAsync(cancellationToken))
            {
                products.Add(new Product(reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetDecimal(1),
                    reader.IsDBNull(2) ? null : reader.GetInt16(2),
                    reader.GetBoolean(3)));
            }

            return products.Count == 0
                ? "No matching Northwind products were found."
                : JsonSerializer.Serialize(products, JsonSerializerOptions.Web);
        }
        catch (SqlException ex)
        {
            throw new McpException(
                $"Northwind lookup failed (SQL error {ex.Number}). Check that Northwind is attached to (localdb)\\MSSQLLocalDB and your Windows account can read dbo.Products.",
                ex);
        }
    }

    private sealed record Product(string ProductName, decimal? UnitPrice, short? UnitsInStock, bool Discontinued);
}
