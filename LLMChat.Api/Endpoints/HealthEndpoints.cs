using LLMChat.Api.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenAI.Chat;

namespace LLMChat.Api.Endpoints;

public static class HealthEndpoints
{
    public static WebApplication MapHealth(this WebApplication app)
    {
        app.MapGet("/health", (HttpResponse response) =>
        {
            response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new HealthResponse("Healthy"));
        })
            .WithName("ApiHealth")
            .WithSummary("Check API liveness without calling PostgreSQL or the LLM.");

        app.MapGet("/health/llm", CheckLlmAsync)
            .WithName("LlmHealth")
            .WithSummary("Probe the configured chat model without PostgreSQL. Makes a billable model request.");
        return app;
    }

    private static async Task<Results<Ok<HealthResponse>, ProblemHttpResult>> CheckLlmAsync(
        IChatModel model, HttpResponse response, ILoggerFactory loggerFactory, CancellationToken cancellationToken)
    {
        response.Headers.CacheControl = "no-store";
        var logger = loggerFactory.CreateLogger("LlmHealth");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var completion = await model.CompleteAsync(null,
                [new UserChatMessage("Health check: reply with OK only.")],
                new ChatCompletionOptions { MaxOutputTokenCount = 512 }, timeout.Token);
            if (!completion.Content.Any(part => !string.IsNullOrWhiteSpace(part.Text)))
            {
                logger.LogWarning("LLM health probe returned no text.");
                return TypedResults.Problem(statusCode: 503, title: "The LLM returned no text.");
            }
            return TypedResults.Ok(new HealthResponse("Healthy"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            logger.LogWarning("LLM health probe exceeded the 30-second deadline.");
            return TypedResults.Problem(statusCode: 504, title: "The LLM health probe timed out.");
        }
        catch (ChatFailureException exception)
        {
            logger.LogWarning("LLM health probe failed (HTTP {StatusCode}).", exception.StatusCode);
            return TypedResults.Problem(statusCode: 503, title: "The LLM is unavailable.");
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("LLM health probe could not reach the provider.");
            return TypedResults.Problem(statusCode: 503, title: "The LLM is unavailable.");
        }
    }
}

public sealed record HealthResponse(string Status);
