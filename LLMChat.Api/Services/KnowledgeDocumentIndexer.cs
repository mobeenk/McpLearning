using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMChat.Api.Models;
using LLMChat.Api.Repositories;

namespace LLMChat.Api.Services;

internal sealed partial class KnowledgeDocumentIndexer(EmbeddingService embeddings, KnowledgeDocumentRepository repository,
    IHostEnvironment environment, ILogger<KnowledgeDocumentIndexer> logger)
{
    private const string PipelineVersion = "1";
    private const long MaximumFileBytes = 50L * 1024 * 1024;

    public async Task IndexAsync(CyclusKnowledge knowledge, string manifestPath, CancellationToken cancellationToken)
    {
        string root = Path.Combine(environment.ContentRootPath, "Knowledge", "Nphies");
        var sources = KnowledgeSourceManifest.Read(manifestPath, root);
        await using var lockConnection = await repository.OpenAsync(cancellationToken);
        await using var runLock = await lockConnection.BeginTransactionAsync(cancellationToken);
        await KnowledgeDocumentRepository.LockImportAsync(runLock, cancellationToken);
        await using var connection = await repository.OpenAsync(cancellationToken);
        var ids = new List<string>();
        int updated = 0;
        foreach (var article in knowledge.GetArticles())
        {
            string id = "cyclus/" + article.Id;
            var source = new KnowledgeReference(article.Id, article.Title, "Cyclus", null, null, null, null, null);
            string text = CyclusKnowledge.Format(article) + "Keywords: " + string.Join(", ", article.Keywords);
            string hash = Hash(JsonSerializer.Serialize(article));
            if (await UpdateAsync(id, hash, source, article, () => [new(text, "Article")])) updated++;
        }
        foreach (var entry in sources)
        {
            string citation = "ref-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Id)))[..16].ToLowerInvariant();
            var source = new KnowledgeReference(citation, entry.Title, entry.Category, entry.Payer,
                entry.Version, entry.EffectiveFrom, entry.EffectiveTo, null);
            await using var file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaximumFileBytes)
                throw new InvalidDataException($"Knowledge source {citation} exceeds the 50 MB limit.");
            string hash = Hash(JsonSerializer.Serialize(source) + Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)));
            if (await UpdateAsync(entry.Id, hash, source, null, () => KnowledgeDocumentReader.Read(entry.Path, cancellationToken))) updated++;
        }
        int removed = await KnowledgeDocumentRepository.RemoveAbsentAsync(connection, [.. ids], cancellationToken);
        await runLock.CommitAsync(cancellationToken);
        logger.LogInformation("Document index finished: {UpdatedCount} updated, {UnchangedCount} unchanged, {RemovedCount} removed.",
            updated, ids.Count - updated, removed);

        async Task<bool> UpdateAsync(string id, string hash, KnowledgeReference source, CyclusKnowledgeArticle? article,
            Func<IReadOnlyList<KnowledgePassage>> read)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ids.Add(id);
            if (await KnowledgeDocumentRepository.IsCurrentAsync(connection, id, hash, embeddings.Model, cancellationToken)) return false;
            var passages = read();
            if (passages.Count == 0) throw new InvalidDataException($"Knowledge source {source.CitationId} has no searchable passages.");
            var indexed = new List<IndexedKnowledgePassage>(passages.Count);
            foreach (var passage in passages)
            {
                string input = article is not null ? passage.Text : $"{source.Title}\n{passage.Location}\n{passage.Text}";
                var embedding = await embeddings.GenerateAsync(input, cancellationToken);
                KnowledgeVectorStore.ValidateEmbedding(embedding);
                indexed.Add(new(passage.Text, passage.Location, FindCodes(passage.Text), embedding.Vector));
            }
            await KnowledgeDocumentRepository.ReplaceAsync(connection, id, hash, embeddings.Model, source, article, indexed, cancellationToken);
            logger.LogInformation("Indexed knowledge source {CitationId} with {PassageCount} passages.", source.CitationId, indexed.Count);
            return true;
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PipelineVersion + ":" + value)));

    internal static string[] FindCodes(string text) => ErrorCode().Matches(text)
        .Select(match => match.Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_-])[A-Z]{2,6}-[0-9]{3,8}(?![\p{L}\p{N}_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCode();
}
