using LLMChat.Api.Models;

namespace LLMChat.Api.Services;

public interface IChatOrchestrator
{
    Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken);
}
