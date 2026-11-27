using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.Diagnostics;

namespace LLMChat.Api.Services;

public sealed class OpenAiChatModel(OpenAIClient client, IConfiguration configuration,
    ILogger<OpenAiChatModel> logger) : IChatModel
{
    private readonly string defaultModel = configuration["ModelName"] ?? "gpt-oss-120b";

    public async Task<ChatCompletion> CompleteAsync(string? modelName, IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        var chatClient = client.GetChatClient(string.IsNullOrWhiteSpace(modelName) ? defaultModel : modelName);
        var timer = Stopwatch.StartNew();
        logger.LogInformation("AI model request started (Tools: {ToolCount}).", options.Tools.Count);
        try
        {
            ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
            logger.LogInformation("AI model request finished in {ElapsedMs} ms ({FinishReason}).",
                timer.ElapsedMilliseconds, completion.FinishReason);
            return completion;
        }
        catch (ClientResultException exception)
        {
            logger.LogWarning("AI provider request failed in {ElapsedMs} ms (HTTP {Status}).",
                timer.ElapsedMilliseconds, exception.Status);
            throw new ChatFailureException(502, "The AI provider rejected or failed the model request.");
        }
    }
}
