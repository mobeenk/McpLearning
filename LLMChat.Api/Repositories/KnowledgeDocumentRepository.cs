using System.Text.Json;
using LLMChat.Api.Models;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace LLMChat.Api.Repositories;

internal sealed record IndexedKnowledgePassage(string Text, string Location, string[] Codes, float[] Vector);

internal sealed class KnowledgeDocumentRepository(NpgsqlDataSource dataSource)
{
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        await dataSource.OpenConnectionAsync(cancellationToken);

    public static async Task LockImportAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(212601117, 2)", transaction.Connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("Another knowledge document import is already running.");
    }

    public static async Task<bool> IsCurrentAsync(NpgsqlConnection connection, string id, string hash,
        string model, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM cyclus_knowledge_documents WHERE id = @id AND content_hash = @hash AND model = @model)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("hash", hash);
        command.Parameters.AddWithValue("model", model);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public static async Task ReplaceAsync(NpgsqlConnection connection, string id, string hash, string model,
        KnowledgeReference source, CyclusKnowledgeArticle? article, IReadOnlyList<IndexedKnowledgePassage> passages,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var document = new NpgsqlCommand("""
            INSERT INTO cyclus_knowledge_documents (id, title, category, metadata, article, content_hash, model)
            VALUES (@id, @title, @category, @metadata, @article, @hash, @model)
            ON CONFLICT (id) DO UPDATE SET title = EXCLUDED.title, category = EXCLUDED.category,
                metadata = EXCLUDED.metadata, article = EXCLUDED.article, content_hash = EXCLUDED.content_hash,
                model = EXCLUDED.model, indexed_at = now();
            DELETE FROM cyclus_knowledge_chunks WHERE document_id = @id;
            """, connection, transaction))
        {
            document.Parameters.AddWithValue("id", id);
            document.Parameters.AddWithValue("title", source.Title);
            document.Parameters.AddWithValue("category", source.Category);
            document.Parameters.AddWithValue("metadata", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(source));
            document.Parameters.AddWithValue("article", NpgsqlDbType.Jsonb,
                article is null ? DBNull.Value : JsonSerializer.Serialize(article));
            document.Parameters.AddWithValue("hash", hash);
            document.Parameters.AddWithValue("model", model);
            await document.ExecuteNonQueryAsync(cancellationToken);
        }

        int ordinal = 0;
        foreach (var group in passages.Chunk(128))
        {
            await using var batch = new NpgsqlBatch(connection, transaction);
            foreach (var passage in group)
            {
                var insert = new NpgsqlBatchCommand("""
                    INSERT INTO cyclus_knowledge_chunks (document_id, ordinal, body, location, codes, embedding)
                    VALUES (@id, @ordinal, @body, @location, @codes, @embedding)
                    """);
                insert.Parameters.AddWithValue("id", id);
                insert.Parameters.AddWithValue("ordinal", ++ordinal);
                insert.Parameters.AddWithValue("body", passage.Text);
                insert.Parameters.AddWithValue("location", passage.Location);
                insert.Parameters.AddWithValue("codes", passage.Codes);
                insert.Parameters.AddWithValue("embedding", new Vector(passage.Vector));
                batch.BatchCommands.Add(insert);
            }
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task<int> RemoveAbsentAsync(NpgsqlConnection connection, string[] ids, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            DELETE FROM cyclus_knowledge_documents WHERE NOT (id = ANY(@ids))
            """, connection);
        command.Parameters.AddWithValue("ids", ids);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CyclusKnowledgeMatch>> SearchAsync(float[] vector, string model, string[] codes,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH scored AS (
                SELECT d.id, d.article::text, d.metadata::text, c.ordinal, c.body, c.location,
                    1 - (c.embedding <=> @embedding) AS similarity, c.codes && @codes AS exact_code
                FROM cyclus_knowledge_chunks c JOIN cyclus_knowledge_documents d ON d.id = c.document_id
                WHERE d.model = @model
            ), ranked AS (
                SELECT *, row_number() OVER (PARTITION BY id ORDER BY exact_code DESC, similarity DESC, ordinal) AS position
                FROM scored
            )
            SELECT article, metadata, ordinal, body, location, similarity, exact_code
            FROM ranked WHERE position <= 2
            ORDER BY exact_code DESC, similarity DESC, id, ordinal LIMIT 5
            """, connection);
        command.Parameters.AddWithValue("embedding", new Vector(vector));
        command.Parameters.AddWithValue("model", model);
        command.Parameters.AddWithValue("codes", codes);
        var matches = new List<CyclusKnowledgeMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                var article = JsonSerializer.Deserialize<CyclusKnowledgeArticle>(reader.GetString(0))
                    ?? throw new InvalidDataException("A stored knowledge article is invalid.");
                matches.Add(new(article, reader.GetDouble(5)) { ExactCode = reader.GetBoolean(6) });
                continue;
            }
            var reference = JsonSerializer.Deserialize<KnowledgeReference>(reader.GetString(1))
                ?? throw new InvalidDataException("Stored knowledge source metadata is invalid.");
            reference = reference with { CitationId = $"{reference.CitationId}-{reader.GetInt32(2)}", Location = reader.GetString(4) };
            var passage = new CyclusKnowledgeArticle(reference.CitationId, reference.Title, reference.Category,
                [], [], [], [], [reader.GetString(3)], []);
            matches.Add(new(passage, reader.GetDouble(5)) { Reference = reference, ExactCode = reader.GetBoolean(6) });
        }
        return matches;
    }
}
