using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace LLMChat.Api.Services;

internal sealed record KnowledgeSource(string Id, string Path, string Title, string Category,
    string? Payer, string? Version, DateOnly? EffectiveFrom, DateOnly? EffectiveTo);

internal static class KnowledgeSourceManifest
{
    private static readonly string[] Columns =
        ["path", "category", "payer", "title", "publisher_version", "effective_from", "effective_to", "notes"];

    public static IReadOnlyList<KnowledgeSource> Read(string manifestPath, string sourceRoot)
    {
        string root = Path.GetFullPath(sourceRoot);
        string manifest = Path.GetFullPath(manifestPath);
        using var parser = new TextFieldParser(manifest) { Delimiters = [","], TrimWhiteSpace = true };
        if (parser.ReadFields() is not { } header || !header.SequenceEqual(Columns, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid knowledge source manifest header.");

        var sources = new List<KnowledgeSource>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!parser.EndOfData)
        {
            long line = parser.LineNumber;
            var fields = parser.ReadFields();
            if (fields is not { Length: 8 } || string.IsNullOrWhiteSpace(fields[0]) ||
                string.IsNullOrWhiteSpace(fields[3]) || Path.IsPathRooted(fields[0]))
                throw new InvalidDataException($"Invalid knowledge source at manifest line {line}.");
            if (fields[1] is not ("NphiesGuidance" or "RegulatorPolicy" or "PayerPolicy" or "InternalGuidance") ||
                (fields[1] == "PayerPolicy" && string.IsNullOrWhiteSpace(fields[2])))
                throw new InvalidDataException($"Invalid knowledge category or payer at manifest line {line}.");

            string path = Path.GetFullPath(fields[0], Path.GetDirectoryName(manifest)!);
            string relative = Path.GetRelativePath(root, path);
            var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || parts.Any(part => part is ".." or "." || part.Contains(':') ||
                part.Equals("claims-request-reponses", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("claims-request-responses", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith("DRG Release JSON Samples", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("licensed", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("archives", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Source is outside the approved corpus at manifest line {line}.");
            if (!paths.Add(relative))
                throw new InvalidDataException($"Duplicate knowledge source at manifest line {line}.");
            if (!File.Exists(path))
                throw new InvalidDataException($"Source file does not exist at manifest line {line}.");

            string current = root;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The knowledge root cannot be a symbolic link or junction.");
            foreach (string part in parts)
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Symbolic links and junctions are not allowed at manifest line {line}.");
            }

            DateOnly? from = ReadDate(fields[5], line);
            DateOnly? to = ReadDate(fields[6], line);
            if (to < from) throw new InvalidDataException($"Invalid effective date range at manifest line {line}.");
            string id = "nphies/" + relative.Replace('\\', '/').ToLowerInvariant();
            sources.Add(new(id, path, fields[3], fields[1], Optional(fields[2]), Optional(fields[4]), from, to));
        }
        if (sources.Count == 0) throw new InvalidDataException("The knowledge manifest contains no approved sources.");
        return sources;
    }

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateOnly? ReadDate(string value, long line) => string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : throw new InvalidDataException($"Invalid effective date at manifest line {line}.");
}
