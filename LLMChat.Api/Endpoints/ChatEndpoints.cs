using LLMChat.Api.Models;
using LLMChat.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace LLMChat.Api.Endpoints;

public static class ChatEndpoints
{
    public static WebApplication MapChat(this WebApplication app)
    {
        app.MapPost("/chat", HandleAsync)
            .WithName("Chat")
            .WithSummary("Generate a contextual chat reply with an optional Outlook draft or claim status lookup request.");
        return app;
    }

    private static async Task<Results<Ok<ChatResponse>, BadRequest<ChatErrorResponse>, ProblemHttpResult>> HandleAsync(
        ChatRequest request, IChatOrchestrator orchestrator, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            return TypedResults.Ok(await orchestrator.ChatAsync(request, timeout.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            loggerFactory.CreateLogger("Chat").LogWarning("Chat request exceeded the 75-second model deadline.");
            return TypedResults.Problem(statusCode: 504, title: "The AI request timed out.");
        }
        catch (ChatFailureException exception)
        {
            if (exception.StatusCode == 400)
                return TypedResults.BadRequest(new ChatErrorResponse(exception.Message));

            loggerFactory.CreateLogger("Chat").LogWarning("Chat action failed: {Title}", exception.Message);
            return TypedResults.Problem(statusCode: exception.StatusCode, title: exception.Message, detail: exception.Detail);
        }
        catch (NpgsqlException exception)
        {
            loggerFactory.CreateLogger("Chat").LogError(
                "Knowledge database request failed. Type={ErrorType}, SqlState={SqlState}, InnerType={InnerType}.",
                exception.GetType().Name, exception.SqlState, exception.InnerException?.GetType().Name);
            return TypedResults.Problem(statusCode: 503, title: "The knowledge database is unavailable or not indexed.");
        }
        catch (InvalidDataException exception)
        {
            loggerFactory.CreateLogger("Chat").LogWarning("Knowledge search failed validation ({ErrorType}).", exception.GetType().Name);
            return TypedResults.Problem(statusCode: 502, title: "The knowledge search received invalid data.");
        }
    }
}
