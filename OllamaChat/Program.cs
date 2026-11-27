using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    var options = Options.Parse(args);
    var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
    var serverDll = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "McpServer", "bin", configuration, "net10.0", "McpServer.dll"));

    if (!File.Exists(serverDll))
        throw new FileNotFoundException("Build the solution first. MCP server DLL is missing.", serverDll);

    Console.WriteLine("[MCP] Starting a local MCP server subprocess over stdio...");
    await using var mcp = await McpClient.CreateAsync(new StdioClientTransport(
        new StdioClientTransportOptions
        {
            Name = "Learning tools",
            Command = "dotnet",
            Arguments = [serverDll]
        }), cancellationToken: cancellation.Token);

    var tools = await mcp.ListToolsAsync(cancellationToken: cancellation.Token);
    Console.WriteLine("[MCP] Discovered tools (tools/list):");
    foreach (var tool in tools)
        Console.WriteLine($"  {tool.Name}: {tool.Description}");

    if (options.SelfTest)
    {
        await SelfTest.RunAsync(mcp, tools, cancellation.Token);
        return;
    }

    var settings = OllamaSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
    var model = options.Model ?? settings.Model;
    using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = settings.Provider == "Ollama" })
    {
        BaseAddress = settings.Endpoint,
        Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
    };
    var chat = new OllamaConversation(http, mcp, tools, model, settings);
    await chat.CheckModelAsync(cancellation.Token);
    Console.WriteLine($"\nProvider: {settings.Provider}. Model: {model}.");

    if (options.Prompt is not null)
    {
        await chat.AskAsync(options.Prompt, cancellation.Token);
        return;
    }

    Console.WriteLine("Try: What is 17 plus 25? | When does Learning Bookshop open? | Say hello.");
    Console.WriteLine("Commands: /tools (show schemas), /reset (clear chat), /exit. Ctrl+C cancels.\n");
    while (!cancellation.IsCancellationRequested)
    {
        Console.Write("You> ");
        var prompt = Console.ReadLine();
        if (prompt is null || prompt.Trim() == "/exit")
            break;
        if (string.IsNullOrWhiteSpace(prompt))
            continue;
        if (prompt.Trim() == "/reset")
        {
            chat.Reset();
            Console.WriteLine("Conversation cleared.");
            continue;
        }
        if (prompt.Trim() == "/tools")
        {
            foreach (var tool in tools)
                Console.WriteLine($"{tool.Name}\n{tool.JsonSchema}\n");
            continue;
        }

        await chat.AskAsync(prompt, cancellation.Token);
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    Environment.ExitCode = 1;
}
catch (Exception ex) when (ex is HttpRequestException or IOException
    or ArgumentException or InvalidOperationException or System.Text.Json.JsonException
    or OperationCanceledException or ModelContextProtocol.McpException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Console.Error.WriteLine("Check the provider settings, service availability, model tool support, and solution build.");
    Environment.ExitCode = 1;
}

internal sealed record Options(string? Model, string? Prompt, bool SelfTest)
{
    public static Options Parse(string[] args)
    {
        string? model = null;
        string? prompt = null;
        var selfTest = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--prompt" when i + 1 < args.Length:
                    prompt = args[++i];
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete option: {args[i]}. Use --model NAME, --prompt TEXT, or --self-test.");
            }
        }
        if ((model is not null && string.IsNullOrWhiteSpace(model)) || (prompt is not null && string.IsNullOrWhiteSpace(prompt)))
            throw new ArgumentException("Model and prompt must not be empty.");
        if (selfTest && prompt is not null)
            throw new ArgumentException("--self-test cannot be combined with --prompt.");
        return new Options(model, prompt, selfTest);
    }
}
