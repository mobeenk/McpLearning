using System.Text.Json;

internal sealed record OllamaSettings(Uri Endpoint, string Model, int TimeoutSeconds, double Temperature, int NumPredict,
    string Provider, string? ApiKey)
{
    public static OllamaSettings Load(string path) => Parse(File.ReadAllText(path));

    internal static OllamaSettings Parse(string json)
    {
        var configuration = JsonSerializer.Deserialize<Configuration>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("appsettings.json must contain a configuration object.");
        var provider = configuration.Provider ?? "Ollama";
        var isOpenAi = provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase);
        if (!isOpenAi && !provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Provider must be Ollama or OpenAI.");
        var selected = (isOpenAi ? configuration.OpenAI : configuration.Ollama)
            ?? throw new InvalidOperationException($"appsettings.json must contain a {provider} section.");

        if (string.IsNullOrWhiteSpace(selected.Endpoint)
            || !Uri.TryCreate(selected.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException($"{provider} endpoint must be an absolute HTTP or HTTPS URL without credentials, a query, or a fragment.");
        if (isOpenAi && endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("OpenAI endpoint must use HTTPS to protect the API key.");
        if (isOpenAi && string.IsNullOrWhiteSpace(selected.ApiKey))
            throw new InvalidOperationException("OpenAI apiKey must not be empty.");
        if (string.IsNullOrWhiteSpace(selected.Model))
            throw new InvalidOperationException($"{provider} model must not be empty.");
        if (selected.TimeoutSeconds is not (> 0 and <= 2147483))
            throw new InvalidOperationException($"{provider} timeoutSeconds must be between 1 and 2147483.");
        if (selected.Temperature is null || !double.IsFinite(selected.Temperature.Value) || selected.Temperature < 0)
            throw new InvalidOperationException($"{provider} temperature must be a non-negative number.");
        var tokens = isOpenAi ? selected.MaxTokens : selected.NumPredict;
        if (tokens is null || (isOpenAi && tokens <= 0))
            throw new InvalidOperationException(isOpenAi ? "OpenAI maxTokens must be positive." : "Ollama numPredict is required.");

        if (!isOpenAi && !endpoint.AbsolutePath.EndsWith('/'))
            endpoint = new Uri(endpoint.AbsoluteUri + "/");

        return new OllamaSettings(endpoint, selected.Model, selected.TimeoutSeconds.Value,
            selected.Temperature.Value, tokens.Value, isOpenAi ? "OpenAI" : "Ollama", selected.ApiKey);
    }

    private sealed record Configuration(string? Provider, ProviderConfiguration? Ollama, ProviderConfiguration? OpenAI);
    private sealed record ProviderConfiguration(string? Endpoint, string? Model, int? TimeoutSeconds,
        double? Temperature, int? NumPredict, int? MaxTokens, string? ApiKey);
}
