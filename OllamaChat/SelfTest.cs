using System.Net;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class SelfTest
{
    public static async Task RunAsync(McpClient mcp, IList<McpClientTool> tools, CancellationToken cancellationToken)
    {
        var settings = OllamaSettings.Parse("""
            { "Ollama": { "endpoint": "http://localhost:12345/prefix", "model": "test-model",
              "timeoutSeconds": 42, "temperature": 0.7, "numPredict": 123 } }
            """);
        Require(settings.Endpoint.AbsoluteUri == "http://localhost:12345/prefix/"
            && settings.Model == "test-model" && settings.TimeoutSeconds == 42
            && settings.Temperature == 0.7 && settings.NumPredict == 123,
            "Ollama settings are parsed and the endpoint path is preserved.");
        Require(Options.Parse([]).Model is null && Options.Parse(["--model", "override"]).Model == "override",
            "The model option is an optional settings override.");
        RequireInvalidSettings("{}");
        RequireInvalidSettings("""
            { "Ollama": { "endpoint": "ftp://localhost", "model": "test",
              "timeoutSeconds": 10, "temperature": 0, "numPredict": 1 } }
            """);
        RequireInvalidSettings("""
            { "Ollama": { "endpoint": "http://localhost", "model": "test",
              "timeoutSeconds": 0, "temperature": 0, "numPredict": 1 } }
            """);
        RequireInvalidSettings("""
            { "Ollama": { "endpoint": "http://localhost", "model": "test",
              "timeoutSeconds": 10, "temperature": -1, "numPredict": 1 } }
            """);

        using (var handler = new OllamaTestHandler())
        using (var http = new HttpClient(handler) { BaseAddress = settings.Endpoint, Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) })
        {
            var chat = new OllamaConversation(http, mcp, tools, "override", settings);
            await chat.CheckModelAsync(cancellationToken);
            await chat.AskAsync("Hello", cancellationToken);
            Require(handler.ShowChecked && handler.ChatChecked, "Configured endpoint, model, and generation settings reach Ollama requests.");
        }

        var openAiSettings = OllamaSettings.Parse("""
            { "provider": "OpenAI", "OpenAI": { "endpoint": "https://example.test/v1/chat/completions",
              "model": "test-model", "apiKey": "test-key", "timeoutSeconds": 42,
              "temperature": 0.7, "maxTokens": 123 } }
            """);
        using (var handler = new OpenAiTestHandler())
        using (var http = new HttpClient(handler) { BaseAddress = openAiSettings.Endpoint })
        {
            var chat = new OllamaConversation(http, mcp, tools, "test-model", openAiSettings);
            await chat.CheckModelAsync(cancellationToken);
            await chat.AskAsync("What is 17 plus 25?", cancellationToken);
            Require(handler.RequestCount == 2, "OpenAI tool call completes through MCP.");
        }
        RequireInvalidSettings("""
            { "provider": "OpenAI", "OpenAI": { "endpoint": "https://example.test/v1/chat/completions",
              "model": "test", "apiKey": "", "timeoutSeconds": 10, "temperature": 0, "maxTokens": 1 } }
            """);
        RequireInvalidSettings("""
            { "provider": "OpenAI", "OpenAI": { "endpoint": "http://example.test/v1/chat/completions",
              "model": "test", "apiKey": "test-key", "timeoutSeconds": 10, "temperature": 0, "maxTokens": 1 } }
            """);

        Require(tools.Select(tool => tool.Name).Order().SequenceEqual(
            new[] { "add_numbers", "get_store_hours", "list_database_tables", "query_database", "search_web" }), "MCP discovers all expected tools.");
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
        await DatabaseSelfTest.RunAsync(mcp, tools, cancellationToken);
        Console.WriteLine("\nPASS: MCP discovery, schemas, tool execution, and invalid-input handling. Ollama was not needed.");
    }

    private static string Text(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static void RequireInvalidSettings(string json)
    {
        try
        {
            OllamaSettings.Parse(json);
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("PASS: Invalid Ollama settings are rejected.");
            return;
        }

        throw new InvalidOperationException("Self-test failed: invalid Ollama settings were accepted.");
    }

    private static void Require(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Self-test failed: {description}");
        Console.WriteLine($"PASS: {description}");
    }

    private sealed class OpenAiTestHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Require(request.Method == HttpMethod.Post
                && request.RequestUri!.AbsoluteUri == "https://example.test/v1/chat/completions"
                && request.Headers.Authorization?.Scheme == "Bearer"
                && request.Headers.Authorization.Parameter == "test-key",
                "OpenAI request uses the configured endpoint and API key.");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            Require(root.GetProperty("model").GetString() == "test-model"
                && root.GetProperty("temperature").GetDouble() == 0.7
                && root.GetProperty("max_tokens").GetInt32() == 123
                && root.GetProperty("tools").GetArrayLength() > 0,
                "OpenAI request contains configured generation settings and tools.");
            if (RequestCount == 1)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        { "choices": [{ "message": { "role": "assistant", "content": null,
                          "tool_calls": [{ "id": "call-1", "type": "function", "function": {
                            "name": "add_numbers", "arguments": "{\"a\":17,\"b\":25}" } }] } }] }
                        """)
                };

            var messages = root.GetProperty("messages");
            var assistant = messages[messages.GetArrayLength() - 2];
            var tool = messages[messages.GetArrayLength() - 1];
            Require(assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString() == "call-1"
                && JsonDocument.Parse(assistant.GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetString()!).RootElement.GetProperty("a").GetInt32() == 17
                && tool.GetProperty("role").GetString() == "tool"
                && tool.GetProperty("tool_call_id").GetString() == "call-1"
                && tool.GetProperty("content").GetString()!.Contains("42"),
                "OpenAI history includes the tool call ID, JSON arguments, and MCP result.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"42\"}}]}")
            };
        }
    }

    private sealed class OllamaTestHandler : HttpMessageHandler
    {
        public bool ShowChecked { get; private set; }
        public bool ChatChecked { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            Require(request.Method == HttpMethod.Post && root.GetProperty("model").GetString() == "override",
                "Ollama request uses the model override.");
            if (request.RequestUri!.AbsoluteUri == "http://localhost:12345/prefix/api/show")
            {
                ShowChecked = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"capabilities\":[\"tools\"]}") };
            }

            Require(request.RequestUri.AbsoluteUri == "http://localhost:12345/prefix/api/chat",
                "Chat request uses the configured endpoint.");
            var options = root.GetProperty("options");
            Require(options.GetProperty("temperature").GetDouble() == 0.7
                && options.GetProperty("num_predict").GetInt32() == 123,
                "Chat request uses the configured generation settings.");
            ChatChecked = true;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"message\":{\"role\":\"assistant\",\"content\":\"Hello\"}}")
            };
        }
    }
}
