using LLMChat.Api.Models;
using OpenAI.Chat;

namespace LLMChat.Api.Tools;

public enum EmailDraftIntent
{
    None,
    NeedsDetails,
    Ready
}

public interface IEmailDraftTool
{
    void Configure(List<ChatMessage> messages, ChatCompletionOptions options, EmailDraftIntent intent);
    ChatResponse ReadResponse(ChatCompletion completion, EmailDraftIntent intent);
}
