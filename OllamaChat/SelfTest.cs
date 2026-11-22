using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class SelfTest
{
    public static async Task RunAsync(McpClient mcp, IList<McpClientTool> tools, CancellationToken cancellationToken)
    {
        Require(tools.Select(tool => tool.Name).Order().SequenceEqual(
            new[] { "add_numbers", "get_store_hours", "search_web" }), "MCP discovers all expected tools.");
        var schema = tools.Single(tool => tool.Name == "add_numbers").JsonSchema;
        Require(schema.GetProperty("properties").TryGetProperty("a", out _)
            && schema.GetProperty("properties").TryGetProperty("b", out _),
            "The discovered addition schema describes both arguments.");
        var searchSchema = tools.Single(tool => tool.Name == "search_web").JsonSchema;
        Require(searchSchema.GetProperty("properties").TryGetProperty("query", out _),
            "The discovered web search schema describes the query.");

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
