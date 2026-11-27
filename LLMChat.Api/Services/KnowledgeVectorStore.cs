using LLMChat.Api.Models;
using LLMChat.Api.Repositories;

namespace LLMChat.Api.Services;

public sealed class KnowledgeVectorStore(EmbeddingService embeddings,
    KnowledgeVectorRepository repository, ILogger<KnowledgeVectorStore> logger)
{
    private const int Dimensions = 1024;
    private readonly string model = embeddings.Model;
    internal KnowledgeDocumentRepository? Documents { get; init; }

    public async Task IndexAsync(CyclusKnowledge knowledge, CancellationToken cancellationToken)
    {
        var articles = knowledge.GetArticles();
        var indexed = new List<KnowledgeVectorEntry>(articles.Count);
        foreach (var article in articles)
        {
            string text = CyclusKnowledge.Format(article) + "Keywords: " + string.Join(", ", article.Keywords);
            var embedding = await embeddings.GenerateAsync(text, cancellationToken);
            ValidateEmbedding(embedding);
            indexed.Add(new KnowledgeVectorEntry(article, embedding.Vector));
            if (indexed.Count % 20 == 0 || indexed.Count == articles.Count)
                logger.LogInformation("Generated embeddings for {IndexedCount} of {ArticleCount} articles.", indexed.Count, articles.Count);
        }

        await repository.ReplaceAsync(indexed, model, cancellationToken);
        logger.LogInformation("Stored {ArticleCount} knowledge articles in PostgreSQL.", indexed.Count);
    }

    public async Task<IReadOnlyList<CyclusKnowledgeMatch>> SearchAsync(string question, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("A search question is required.", nameof(question));
        var embedding = await embeddings.GenerateAsync(question, cancellationToken);
        ValidateEmbedding(embedding);
        var matches = Documents is null
            ? await repository.SearchAsync(embedding.Vector, model, cancellationToken)
            : await Documents.SearchAsync(embedding.Vector, model, KnowledgeDocumentIndexer.FindCodes(question), cancellationToken);
        if (matches.Count == 0)
        {
            logger.LogWarning("No knowledge embeddings are available for the configured model.");
            throw new ChatFailureException(503, "Knowledge embeddings are not indexed for the configured model.");
        }
        return matches;
    }

    internal static void ValidateEmbedding(EmbeddingResponse embedding)
    {
        if (embedding.Dimensions != Dimensions || embedding.Vector.Length != Dimensions ||
            embedding.Vector.Any(value => !float.IsFinite(value)) || !embedding.Vector.Any(value => value != 0))
            throw new InvalidDataException($"The embedding model must return a finite, nonzero {Dimensions}-dimensional vector.");
    }
}

public sealed record KnowledgeVectorEntry(CyclusKnowledgeArticle Article, float[] Vector);
