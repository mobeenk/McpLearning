using System.ClientModel;
using System.Text.Json;
using LLMChat.Api.Endpoints;
using LLMChat.Api.Repositories;
using LLMChat.Api.Services;
using LLMChat.Api.Tools;
using Npgsql;
using OpenAI;

bool indexKnowledge = args.Length == 1 && args[0] == "--index-knowledge";
bool searchKnowledge = args.Length == 2 && args[0] == "--search-knowledge";
bool indexDocuments = args.FirstOrDefault() == "--index-documents";
bool validateDocuments = args.FirstOrDefault() == "--validate-documents";
if ((indexDocuments || validateDocuments) && args.Length != 2)
{
    Console.Error.WriteLine($"Usage: {args[0]} <reviewed-manifest.csv>");
    Environment.ExitCode = 2;
    return;
}
var builder = WebApplication.CreateBuilder(indexKnowledge || searchKnowledge || indexDocuments || validateDocuments ? [] : args);
if (validateDocuments)
{
    string root = Path.Combine(builder.Environment.ContentRootPath, "Knowledge", "Nphies");
    var sources = KnowledgeSourceManifest.Read(args[1], root);
    int passages = 0;
    int failures = 0;
    foreach (var source in sources)
    {
        try { passages += KnowledgeDocumentReader.Read(source.Path, CancellationToken.None).Count; }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or JsonException)
        {
            failures++;
            Console.Error.WriteLine($"Validation failed for {source.Id}: {exception.Message}");
        }
    }
    Environment.ExitCode = failures == 0 ? 0 : 1;
    Console.WriteLine($"Validated {sources.Count - failures} of {sources.Count} sources and {passages} searchable passages; {failures} failures. No database or embedding calls were made.");
    return;
}
bool useDocuments = builder.Configuration.GetValue<bool>("KnowledgeSearch:UseDocumentIndex");

string apiKey = builder.Configuration["ApiKey"]
    ?? throw new InvalidOperationException("ApiKey is missing in appsettings.json.");
string endpoint = builder.Configuration["Endpoint"] ?? "https://llm.cloudsolutions.com.sa/";

builder.Services.AddSingleton(new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(endpoint) }));
builder.Services.AddSingleton(services =>
{
    string model = services.GetRequiredService<IConfiguration>()["EmbeddingModelName"]
        ?? throw new InvalidOperationException("EmbeddingModelName is missing.");
    var client = new OpenAIClient(new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(new Uri(endpoint), "/v1/") });
    return client.GetEmbeddingClient(model);
});
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<NpgsqlDataSource>(services =>
{
    string connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
    dataSourceBuilder.UseVector();
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton<IChatModel, OpenAiChatModel>();
builder.Services.AddScoped<IEmailDraftTool, EmailDraftTool>();
builder.Services.AddScoped<IChatIntentRouter, ChatIntentRouter>();
builder.Services.AddScoped<IClaimStatusTool, ClaimStatusTool>();
builder.Services.AddSingleton<CyclusKnowledge>();
builder.Services.AddSingleton<ICyclusKnowledge>(services => services.GetRequiredService<CyclusKnowledge>());
builder.Services.AddSingleton<KnowledgeVectorRepository>();
builder.Services.AddSingleton<KnowledgeDocumentRepository>();
builder.Services.AddSingleton<KnowledgeDocumentIndexer>();
builder.Services.AddSingleton(services => new KnowledgeVectorStore(services.GetRequiredService<EmbeddingService>(),
    services.GetRequiredService<KnowledgeVectorRepository>(), services.GetRequiredService<ILogger<KnowledgeVectorStore>>())
{
    Documents = useDocuments ? services.GetRequiredService<KnowledgeDocumentRepository>() : null
});
builder.Services.AddSingleton<ChatKnowledgeSearch>();
builder.Services.AddScoped<IChatOrchestrator, ChatOrchestrator>();

var app = builder.Build();
if (indexKnowledge || searchKnowledge || indexDocuments)
{
    await using (app)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            if (indexDocuments)
                await app.Services.GetRequiredService<KnowledgeDocumentIndexer>().IndexAsync(
                    app.Services.GetRequiredService<CyclusKnowledge>(), args[1], cancellation.Token);
            else if (indexKnowledge)
                await app.Services.GetRequiredService<KnowledgeVectorStore>().IndexAsync(
                    app.Services.GetRequiredService<CyclusKnowledge>(), cancellation.Token);
            else
            {
                var matches = await app.Services.GetRequiredService<KnowledgeVectorStore>().SearchAsync(args[1], cancellation.Token);
                Console.WriteLine(JsonSerializer.Serialize(matches, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Knowledge operation canceled.");
            Environment.ExitCode = 130;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
    return;
}

if (!useDocuments) app.Services.GetRequiredService<ICyclusKnowledge>();
app.UseHttpsRedirection();
app.MapHealth();
app.MapChat();
app.MapEmbeddings();
app.MapKnowledgeSearch();
app.Run();
