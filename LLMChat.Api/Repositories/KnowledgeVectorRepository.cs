using System.Text.Json;
using LLMChat.Api.Models;
using LLMChat.Api.Services;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace LLMChat.Api.Repositories;

public sealed class KnowledgeVectorRepository(NpgsqlDataSource dataSource)
{
    public async Task ReplaceAsync(IReadOnlyList<KnowledgeVectorEntry> entries, string model, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var setup = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(212601117, 1);
            CREATE TABLE IF NOT EXISTS cyclus_knowledge_embeddings (
                id text PRIMARY KEY,
                article jsonb NOT NULL,
                embedding vector(1024) NOT NULL,
                model text NOT NULL
            );
            DELETE FROM cyclus_knowledge_embeddings;
            """, connection, transaction))
            await setup.ExecuteNonQueryAsync(cancellationToken);

        await using var insert = new NpgsqlCommand("""
            INSERT INTO cyclus_knowledge_embeddings (id, article, embedding, model)
            VALUES (@id, @article, @embedding, @model)
            """, connection, transaction);
        insert.Parameters.Add("id", NpgsqlDbType.Text);
        insert.Parameters.Add("article", NpgsqlDbType.Jsonb);
        insert.Parameters.Add(new NpgsqlParameter("embedding", new Vector(entries[0].Vector)));
        insert.Parameters.Add("model", NpgsqlDbType.Text).Value = model;
        foreach (var entry in entries)
        {
            insert.Parameters["id"].Value = entry.Article.Id;
            insert.Parameters["article"].Value = JsonSerializer.Serialize(entry.Article);
            insert.Parameters["embedding"].Value = new Vector(entry.Vector);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CyclusKnowledgeMatch>> SearchAsync(float[] vector, string model, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT article::text, 1 - (embedding <=> @embedding) AS similarity
            FROM cyclus_knowledge_embeddings
            WHERE model = @model
            ORDER BY embedding <=> @embedding, id
            LIMIT 5
            """, connection);
        command.Parameters.AddWithValue("embedding", new Vector(vector));
        command.Parameters.AddWithValue("model", model);
        var matches = new List<CyclusKnowledgeMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var article = JsonSerializer.Deserialize<CyclusKnowledgeArticle>(reader.GetString(0))
                ?? throw new InvalidDataException("A stored knowledge article is invalid.");
            matches.Add(new CyclusKnowledgeMatch(article, reader.GetDouble(1)));
        }
        return matches;
    }
}
