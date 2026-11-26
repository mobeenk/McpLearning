using OpenAI.Chat;

namespace LLMChat.Api.Services;

public interface IChatModel
{
    Task<ChatCompletion> CompleteAsync(string? modelName, IEnumerable<ChatMessage> messages,
        ChatCompletionOptions options, CancellationToken cancellationToken);
}
