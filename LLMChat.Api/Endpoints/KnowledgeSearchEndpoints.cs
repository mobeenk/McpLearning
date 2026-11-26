using LLMChat.Api.Models;
using LLMChat.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace LLMChat.Api.Endpoints;

public static class KnowledgeSearchEndpoints
{
    public static WebApplication MapKnowledgeSearch(this WebApplication app)
    {
        app.MapPost("/knowledge/search", HandleAsync)
            .WithName("SearchKnowledge")
            .WithSummary("Find relevant articles and document passages in the configured knowledge index.");
        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<CyclusKnowledgeMatch>>, BadRequest<ChatErrorResponse>, ProblemHttpResult>> HandleAsync(
        KnowledgeSearchRequest request, KnowledgeVectorStore store, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return TypedResults.BadRequest(new ChatErrorResponse("Question is required."));
        if (request.Question.Length > 24000)
            return TypedResults.BadRequest(new ChatErrorResponse("Question cannot exceed 24000 characters."));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            return TypedResults.Ok(await store.SearchAsync(request.Question, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("KnowledgeSearch").LogWarning("Knowledge search exceeded the 75-second deadline.");
            return TypedResults.Problem(statusCode: 504, title: "The knowledge search timed out.");
        }
        catch (ChatFailureException exception)
        {
            return TypedResults.Problem(statusCode: exception.StatusCode, title: exception.Message);
        }
        catch (NpgsqlException exception)
        {
            loggerFactory.CreateLogger("KnowledgeSearch").LogError(
                "Knowledge database request failed. Type={ErrorType}, SqlState={SqlState}, InnerType={InnerType}.",
                exception.GetType().Name, exception.SqlState, exception.InnerException?.GetType().Name);
            return TypedResults.Problem(statusCode: 503, title: "The knowledge database is unavailable or not indexed.");
        }
        catch (InvalidDataException exception)
        {
            loggerFactory.CreateLogger("KnowledgeSearch").LogWarning("Knowledge search failed validation ({ErrorType}).", exception.GetType().Name);
            return TypedResults.Problem(statusCode: 502, title: "The knowledge search received invalid data.");
        }
    }
}

public sealed record KnowledgeSearchRequest(string? Question);
