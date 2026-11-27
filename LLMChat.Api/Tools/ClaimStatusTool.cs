using System.Globalization;
using System.Text.Json;
using LLMChat.Api.Models;
using LLMChat.Api.Services;
using OpenAI.Chat;

namespace LLMChat.Api.Tools;

public interface IClaimStatusTool
{
    void Configure(List<ChatMessage> messages, ChatCompletionOptions options);
    ChatResponse ReadResponse(ChatCompletion completion, IEnumerable<ChatMessage> messages);
}

public sealed class ClaimStatusTool : IClaimStatusTool
{
    public const string Name = "get_claim_status";

    public void Configure(List<ChatMessage> messages, ChatCompletionOptions options)
    {
        messages.Insert(1, new SystemChatMessage(
            "Request a read-only claim status lookup using exactly one explicit user-supplied Claim ID. " +
            "Never invent an ID or a status. The application executes the lookup under the signed-in user's " +
            "permissions; you do not have database access. Return only the tool call, not a guessed status."));
        options.AllowParallelToolCalls = false;
        options.Tools.Add(ChatTool.CreateFunctionTool(Name,
            "Request the current status for a single explicit user-supplied positive Claim ID.",
            BinaryData.FromString("""
                {
                  "type": "object",
                  "properties": {
                    "claimId": { "type": "string", "description": "Explicit positive integer Claim ID, digits only, up to 9223372036854775807. Never invent it." }
                  },
                  "required": ["claimId"],
                  "additionalProperties": false
                }
                """)));
        options.ToolChoice = ChatToolChoice.CreateFunctionChoice(Name);
    }

    public ChatResponse ReadResponse(ChatCompletion completion, IEnumerable<ChatMessage> messages)
    {
        if (completion.FinishReason != ChatFinishReason.ToolCalls || completion.ToolCalls.Count != 1
            || completion.ToolCalls[0].FunctionName != Name)
            throw new ChatFailureException(502, "The assistant did not return the required claim status action.");

        JsonDocument arguments;
        try { arguments = JsonDocument.Parse(completion.ToolCalls[0].FunctionArguments.ToString()); }
        catch (JsonException) { throw new ChatFailureException(502, "The assistant returned an invalid claim status action."); }
        using (arguments)
        {
            if (arguments.RootElement.ValueKind != JsonValueKind.Object
                || !arguments.RootElement.TryGetProperty("claimId", out var field)
                || field.ValueKind != JsonValueKind.String)
                throw new ChatFailureException(502, "The assistant returned an invalid Claim ID.");
            string id = field.GetString()!;
            if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out long claimId) || claimId <= 0
                || !messages.OfType<UserChatMessage>().Any(message => ContainsId(message, id)))
                throw new ChatFailureException(502, "The assistant returned a Claim ID not supplied by the user.");

            return new ChatResponse("Checking the claim status.", ToolRequest: new ChatToolRequest(Name,
                new Dictionary<string, string> { ["claimId"] = id }));
        }
    }

    private static bool ContainsId(UserChatMessage message, string id) =>
        message.Content.Any(part => part.Text is string text
            && System.Text.RegularExpressions.Regex.IsMatch(text,
                @"(?<![A-Za-z0-9])" + System.Text.RegularExpressions.Regex.Escape(id) + @"(?![A-Za-z0-9])"));
}
