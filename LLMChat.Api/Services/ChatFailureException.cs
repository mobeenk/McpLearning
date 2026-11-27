namespace LLMChat.Api.Services;

internal sealed class ChatFailureException(int statusCode, string title, string? detail = null)
    : Exception(title)
{
    public int StatusCode { get; } = statusCode;
    public string? Detail { get; } = detail;
}
