namespace LLMChat.Api.Models;

/// <summary>Chat input with optional history, retrieval context, and generation settings.</summary>
public sealed record ChatRequest(
    string Message,
    string? ModelName = null,
    string? SystemPrompt = null,
    List<string>? RagContext = null,
    List<ChatTurn>? History = null,
    int? MaxOutputTokens = null,
    bool? IncludeLogProbabilities = null,
    int? TopLogProbabilityCount = null,
    List<string>? StopSequences = null,
    bool EnableEmailDrafts = false,
    bool AutoDetectEmailDrafts = false,
    List<string>? AvailableTools = null);

/// <summary>A previous system, user, or assistant message.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>The assistant text, optional validated email draft, or trusted application action request.</summary>
public sealed record ChatResponse(string Response, EmailDraft? EmailDraft = null, ChatToolRequest? ToolRequest = null);

/// <summary>A validated action request for the trusted application backend to execute.</summary>
public sealed record ChatToolRequest(string Name, IReadOnlyDictionary<string, string> Arguments);

/// <summary>A single plain-text email for user review, not automatic sending.</summary>
public sealed record EmailDraft(string Recipient, string Subject, string Body);

/// <summary>A validation error returned by the chat endpoint.</summary>
public sealed record ChatErrorResponse(string Error);
