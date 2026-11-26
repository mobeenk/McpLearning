using LLMChat.Api.Models;
using LLMChat.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace LLMChat.Api.Endpoints;

public static class EmbeddingEndpoints
{
    public static WebApplication MapEmbeddings(this WebApplication app)
    {
        app.MapPost("/embeddings", HandleAsync)
            .WithName("GenerateEmbedding")
            .WithSummary("Generate an embedding for a single text using the configured embedding model.");
        return app;
    }

    private static async Task<Results<Ok<EmbeddingResponse>, BadRequest<ChatErrorResponse>, ProblemHttpResult>> HandleAsync(
        EmbeddingRequest request, EmbeddingService service, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return TypedResults.BadRequest(new ChatErrorResponse("Text is required."));
        if (request.Text.Length > 24000)
            return TypedResults.BadRequest(new ChatErrorResponse("Text cannot exceed 24000 characters."));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            return TypedResults.Ok(await service.GenerateAsync(request.Text, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("Embeddings").LogWarning("Embedding request exceeded the 75-second model deadline.");
            return TypedResults.Problem(statusCode: 504, title: "The embedding request timed out.");
        }
        catch (ChatFailureException exception)
        {
            return TypedResults.Problem(statusCode: exception.StatusCode, title: exception.Message);
        }
    }
}

public sealed record EmbeddingRequest(string? Text);
