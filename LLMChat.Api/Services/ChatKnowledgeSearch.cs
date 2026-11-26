using System.Text.RegularExpressions;
using LLMChat.Api.Models;

namespace LLMChat.Api.Services;

public sealed class ChatKnowledgeSearch(KnowledgeVectorStore store, IConfiguration configuration, ILogger<ChatKnowledgeSearch> logger)
{
    private const int MaximumContextCharacters = 24000;
    private static readonly Regex FollowUp = new(@"\b(it|its|that|this|those|these|there|them|same)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly double minimumSimilarity = ReadMinimumSimilarity(configuration);

    public async Task<IReadOnlyList<CyclusKnowledgeMatch>> SearchAsync(string question, IReadOnlyList<ChatTurn>? history,
        CancellationToken cancellationToken)
    {
        string query = question;
        if (FollowUp.IsMatch(question))
        {
            var previous = history?.LastOrDefault(turn => string.Equals(turn.Role?.Trim(), "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(turn.Content));
            if (previous != null)
            {
                int available = Math.Min(2000, Math.Max(0, MaximumContextCharacters - question.Length - 1));
                if (available > 0)
                    query = previous.Content[..Math.Min(available, previous.Content.Length)] + "\n" + question;
            }
        }

        var candidates = await store.SearchAsync(query, cancellationToken);
        var matches = new List<CyclusKnowledgeMatch>();
        int remaining = MaximumContextCharacters;
        foreach (var candidate in candidates)
        {
            if (!double.IsFinite(candidate.Score) || (!candidate.ExactCode && candidate.Score < minimumSimilarity)) continue;
            int length = RetrievalContext.Format(candidate).Length;
            if (length > remaining) continue;
            matches.Add(candidate);
            remaining -= length;
        }
        logger.LogInformation("Knowledge search selected {MatchCount} passages.", matches.Count);
        return matches;
    }

    private static double ReadMinimumSimilarity(IConfiguration configuration)
    {
        double value = configuration.GetValue<double?>("KnowledgeSearch:MinimumSimilarity") ?? 0.6;
        if (!double.IsFinite(value) || value < -1 || value > 1)
            throw new InvalidOperationException("KnowledgeSearch:MinimumSimilarity must be between -1 and 1.");
        return value;
    }
}
