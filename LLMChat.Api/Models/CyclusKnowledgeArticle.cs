namespace LLMChat.Api.Models;

public sealed record CyclusKnowledgeArticle(
    string Id,
    string Title,
    string Area,
    string[] Routes,
    string[] Keywords,
    string[] Prerequisites,
    string[] Steps,
    string[] Notes,
    string[] Sources);

public sealed record CyclusKnowledgeMatch(CyclusKnowledgeArticle Article, double Score)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public KnowledgeReference? Reference { get; init; }
    internal bool ExactCode { get; init; }
}

public sealed record KnowledgeReference(string CitationId, string Title, string Category, string? Payer,
    string? Version, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string? Location);
