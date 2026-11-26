using System.ClientModel;
using OpenAI.Embeddings;

namespace LLMChat.Api.Services;

public sealed class EmbeddingService(EmbeddingClient client, IConfiguration configuration, ILogger<EmbeddingService> logger)
{
    private readonly string model = configuration["EmbeddingModelName"]
        ?? throw new InvalidOperationException("EmbeddingModelName is missing.");

    public string Model => model;

    public async Task<EmbeddingResponse> GenerateAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            OpenAIEmbedding embedding = await client.GenerateEmbeddingAsync(text, cancellationToken: cancellationToken);
            float[] vector = embedding.ToFloats().ToArray();
            if (vector.Length == 0 || vector.Any(value => !float.IsFinite(value)))
            {
                logger.LogWarning("Embedding provider returned an invalid vector.");
                throw new ChatFailureException(502, "The embedding provider returned an invalid vector.");
            }

            return new EmbeddingResponse(model, vector.Length, vector);
        }
        catch (ClientResultException exception)
        {
            logger.LogWarning("Embedding provider request failed (HTTP {Status}).", exception.Status);
            throw new ChatFailureException(502, "The embedding provider rejected or failed the request.");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Embedding provider could not be reached.");
            throw new ChatFailureException(502, "The embedding provider could not be reached.");
        }
    }
}

public sealed record EmbeddingResponse(string Model, int Dimensions, float[] Vector);
