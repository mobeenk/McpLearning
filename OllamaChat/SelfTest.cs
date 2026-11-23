using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class SelfTest
{
    public static async Task RunAsync(McpClient mcp, IList<McpClientTool> tools, CancellationToken cancellationToken)
    {
        Require(tools.Select(tool => tool.Name).Order().SequenceEqual(
            new[] { "add_numbers", "find_northwind_products", "get_store_hours", "search_web" }), "MCP discovers all expected tools.");
        var schema = tools.Single(tool => tool.Name == "add_numbers").JsonSchema;
        Require(schema.GetProperty("properties").TryGetProperty("a", out _)
            && schema.GetProperty("properties").TryGetProperty("b", out _),
            "The discovered addition schema describes both arguments.");
        var searchSchema = tools.Single(tool => tool.Name == "search_web").JsonSchema;
        Require(searchSchema.GetProperty("properties").TryGetProperty("query", out _),
            "The discovered web search schema describes the query.");
        Require(OllamaConversation.ExplicitWebQuery("yes search the web for 2026 gold price") == "gold price 2026"
            && OllamaConversation.ExplicitWebQuery("Yes, please search the web for: gold prices") == "gold prices",
            "Explicit web search extracts the query after a confirmation and puts a leading year after the topic.");
        Require(OllamaConversation.ExplicitWebQuery("Don't search the web for gold prices") is null
            && OllamaConversation.ExplicitWebQuery("Search the web for ") is null,
            "A negated or incomplete request does not trigger a direct search.");

        foreach (var (a, b, expected) in new[]
        {
            (17m, 25m, "42"), (-2.5m, 0.5m, "-2.0"), (0.1m, 0.2m, "0.3")
        })
        {
            var result = await mcp.CallToolAsync("add_numbers",
                new Dictionary<string, object?> { ["a"] = a, ["b"] = b },
                cancellationToken: cancellationToken);
            var text = Text(result);
            Require(result.IsError != true && decimal.Parse(text.Trim('"'),
                System.Globalization.CultureInfo.InvariantCulture) == decimal.Parse(expected,
                System.Globalization.CultureInfo.InvariantCulture),
                $"Addition through MCP: {a} + {b} = {expected}.");
        }

        var hours = await mcp.CallToolAsync("get_store_hours", cancellationToken: cancellationToken);
        Require(hours.IsError != true && Text(hours).Contains("10:15-18:45")
            && Text(hours).Contains("closed Sunday"), "Store hours arrive through MCP.");

        var invalid = await mcp.CallToolAsync("add_numbers",
            new Dictionary<string, object?> { ["a"] = "not-a-number", ["b"] = 1 },
            cancellationToken: cancellationToken);
        Require(invalid.IsError == true, "Invalid tool arguments return an explicit MCP error.");
        var emptySearch = await mcp.CallToolAsync("search_web",
            new Dictionary<string, object?> { ["query"] = " " },
            cancellationToken: cancellationToken);
        Require(emptySearch.IsError == true, "An empty web search query returns an explicit MCP error.");
        var longSearch = await mcp.CallToolAsync("search_web",
            new Dictionary<string, object?> { ["query"] = new string('a', 201) },
            cancellationToken: cancellationToken);
        Require(longSearch.IsError == true, "An excessively long web search query returns an explicit MCP error.");
        foreach (var name in new[] { " ", new string('a', 101) })
        {
            var invalidProduct = await mcp.CallToolAsync("find_northwind_products",
                new Dictionary<string, object?> { ["name"] = name },
                cancellationToken: cancellationToken);
            Require(invalidProduct.IsError == true && Text(invalidProduct).Contains("1 to 100 characters"),
                "An invalid Northwind product name is rejected without querying the database.");
        }
        Console.WriteLine("\nPASS: MCP discovery, schemas, tool execution, and invalid-input handling. Ollama was not needed.");
    }

    private static string Text(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {description}");
        Console.WriteLine($"PASS: {description}");
    }
}
