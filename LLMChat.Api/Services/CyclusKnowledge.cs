using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LLMChat.Api.Models;

namespace LLMChat.Api.Services;

public interface ICyclusKnowledge
{
    IReadOnlyList<CyclusKnowledgeMatch> Search(string question, IReadOnlyList<ChatTurn>? history = null);
}

public sealed class CyclusKnowledge : ICyclusKnowledge
{
    private const int MaximumMatches = 5;
    private const int MaximumContextCharacters = 24000;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> StopWords = new(
        ("a an and are as at be been but by can could cyclus do does for from get give go have help " +
         "how i if in into is it its me my of on or our please show so some tell than that the their them " +
         "there these they this those to use user users using want was we what when where which who why " +
         "will with would you your also about through need should step steps").Split(' '), StringComparer.Ordinal);
    private static readonly Regex Words = new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FollowUp = new(@"\b(it|its|that|this|those|these|there|them|same)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly IndexedArticle[] articles;
    private readonly Dictionary<string, int> documentFrequency = new(StringComparer.Ordinal);
    private readonly double averageLength;

    public CyclusKnowledge(IHostEnvironment environment, ILogger<CyclusKnowledge> logger)
    {
        string directory = Path.Combine(environment.ContentRootPath, "Knowledge", "Cyclus");
        if (!Directory.Exists(directory))
            throw new InvalidDataException("Cyclus application knowledge is missing. Deploy Knowledge/Cyclus with the API.");
        string[] files = Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("Cyclus application knowledge contains no article files.");

        var loaded = new List<CyclusKnowledgeArticle>();
        foreach (string file in files)
        {
            var batch = JsonSerializer.Deserialize<CyclusKnowledgeArticle[]>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException($"Knowledge file {Path.GetFileName(file)} is empty.");
            foreach (var article in batch)
            {
                Validate(article, file);
                loaded.Add(article);
            }
        }
        if (loaded.Count == 0 || loaded.Select(article => article.Id).Distinct(StringComparer.Ordinal).Count() != loaded.Count)
            throw new InvalidDataException("Cyclus knowledge must contain articles with unique IDs.");

        articles = loaded.Select(Index).ToArray();
        averageLength = articles.Average(article => article.Length);
        foreach (var article in articles)
            foreach (string word in article.Frequencies.Keys)
                documentFrequency[word] = documentFrequency.GetValueOrDefault(word) + 1;
        logger.LogInformation("Loaded {ArticleCount} source-verified Cyclus how-to articles from {FileCount} files.",
            articles.Length, files.Length);
    }

    public IReadOnlyList<CyclusKnowledgeArticle> GetArticles() => articles.Select(article => article.Article).ToArray();

    public IReadOnlyList<CyclusKnowledgeMatch> Search(string question, IReadOnlyList<ChatTurn>? history = null)
    {
        var query = Tokenize(question).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        if (query.Length == 0 && question.Contains("cyclus", StringComparison.OrdinalIgnoreCase))
            query = ["overview"];
        var contextualTerms = new HashSet<string>(StringComparer.Ordinal);
        if (FollowUp.IsMatch(question))
        {
            var previous = history?.LastOrDefault(turn => string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(turn.Content));
            if (previous != null)
                contextualTerms.UnionWith(Tokenize(previous.Content).Take(64));
        }
        contextualTerms.ExceptWith(query);
        if (query.Length == 0 && contextualTerms.Count == 0) return [];
        var ranked = articles.Select(article => new CyclusKnowledgeMatch(article.Article,
                Score(article, query) + Score(article, contextualTerms) * .35))
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Article.Id, StringComparer.Ordinal)
            .ToArray();
        if (ranked.Length == 0) return [];

        int remaining = MaximumContextCharacters;
        var results = new List<CyclusKnowledgeMatch>();
        foreach (var match in ranked.Where(match => match.Score >= ranked[0].Score * .3))
        {
            int length = Format(match.Article).Length;
            if (length > remaining) continue;
            results.Add(match);
            remaining -= length;
            if (results.Count == MaximumMatches) break;
        }
        return results;
    }

    public static string Format(CyclusKnowledgeArticle article)
    {
        var text = new StringBuilder();
        text.AppendLine($"[Cyclus:{article.Id}] {article.Title}");
        text.AppendLine($"Area: {article.Area}");
        text.AppendLine("Evidence: source-code verified; not verified against a running environment.");
        text.AppendLine("Application routes (hash navigation): " + string.Join(", ", article.Routes.Select(route => "#" + route)));
        foreach (string prerequisite in article.Prerequisites) text.AppendLine("Prerequisite: " + prerequisite);
        for (int i = 0; i < article.Steps.Length; i++) text.AppendLine($"{i + 1}. {article.Steps[i]}");
        foreach (string note in article.Notes) text.AppendLine("Limitation/condition: " + note);
        text.AppendLine("Source files: " + string.Join("; ", article.Sources));
        return text.ToString();
    }

    private double Score(IndexedArticle article, IEnumerable<string> terms)
    {
        double score = 0;
        int matched = 0;
        int count = 0;
        foreach (string term in terms)
        {
            count++;
            if (!article.Frequencies.TryGetValue(term, out double frequency)) continue;
            double idf = Math.Log(1 + (articles.Length - documentFrequency[term] + .5) / (documentFrequency[term] + .5));
            score += idf * frequency * 2.2 / (frequency + 1.2 * (.25 + .75 * article.Length / averageLength));
            matched++;
        }
        return count == 0 ? 0 : score * matched / count;
    }

    private static IndexedArticle Index(CyclusKnowledgeArticle article)
    {
        var frequencies = new Dictionary<string, double>(StringComparer.Ordinal);
        Add(article.Title, 6);
        Add(article.Area, 3);
        Add(string.Join(" ", article.Keywords), 5);
        Add(string.Join(" ", article.Routes), 3);
        Add(string.Join(" ", article.Prerequisites), 1);
        Add(string.Join(" ", article.Steps), 1);
        Add(string.Join(" ", article.Notes), .5);
        return new IndexedArticle(article, frequencies, Math.Max(1, frequencies.Values.Sum()));

        void Add(string text, double weight)
        {
            foreach (string word in Tokenize(text))
                frequencies[word] = frequencies.GetValueOrDefault(word) + weight;
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (Match match in Words.Matches(text.ToLowerInvariant()))
        {
            string word = match.Value;
            if (word.Length < 2 || StopWords.Contains(word)) continue;
            word = word switch
            {
                "reconciliation" or "reconciling" or "reconciled" => "reconcile",
                "denied" or "denials" => "denial",
                "logon" or "signin" => "login",
                "authorisation" => "authorization",
                _ => word
            };
            if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal)) word = word[..^3] + "y";
            else if (word.Length > 4 && word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal))
                word = word[..^1];
            yield return word;
        }
    }

    private static void Validate(CyclusKnowledgeArticle? article, string file)
    {
        if (article == null || string.IsNullOrWhiteSpace(article.Id)
            || !Regex.IsMatch(article.Id, "^[a-z0-9]+(?:-[a-z0-9]+)*$")
            || string.IsNullOrWhiteSpace(article.Title) || string.IsNullOrWhiteSpace(article.Area)
            || !Valid(article.Routes, true) || article.Routes.Any(route => !route.StartsWith('/'))
            || !Valid(article.Keywords, false) || !Valid(article.Prerequisites, false)
            || !Valid(article.Steps, true) || !Valid(article.Notes, false) || !Valid(article.Sources, true)
            || article.Sources.Any(source => source.StartsWith('/') || source.Contains('\\') || source.Contains("..")
                || !Regex.IsMatch(source, @"^(cyclus-claim-ui|Cyclus\.Claim)/")))
            throw new InvalidDataException($"Invalid Cyclus knowledge article in {Path.GetFileName(file)}.");
        if (Format(article).Length > MaximumContextCharacters)
            throw new InvalidDataException($"Cyclus knowledge article {article.Id} exceeds the retrieval context budget.");

        static bool Valid(string[]? values, bool required) =>
            values != null && (!required || values.Length > 0) && values.All(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record IndexedArticle(CyclusKnowledgeArticle Article, Dictionary<string, double> Frequencies, double Length);
}
