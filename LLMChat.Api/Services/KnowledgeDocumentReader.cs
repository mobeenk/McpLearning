using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ExcelDataReader;
using HtmlAgilityPack;
using Microsoft.VisualBasic.FileIO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LLMChat.Api.Services;

internal sealed record KnowledgePassage(string Text, string Location);

internal static class KnowledgeDocumentReader
{
    private const int MaximumFileBytes = 32 * 1024 * 1024;
    private const int MaximumMarkupBytes = 16 * 1024 * 1024;
    private const int MaximumExpandedBytes = 64 * 1024 * 1024;
    private const int MaximumOfficeParts = 1024;
    private const int MaximumPdfPages = 500;
    private const int MaximumPdfLettersPerPage = 200_000;
    private const int MaximumRows = 50_000;
    private const int MaximumColumns = 256;
    private const int MaximumCells = 1_000_000;
    private const int MaximumTextCharacters = 32_000_000;
    private const int MaximumChunkCharacters = 2_000;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static IReadOnlyList<KnowledgePassage> Read(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".txt" or ".md" or ".markdown" or ".csv" or ".json" or ".pdf" or ".xlsx" or ".docx" or ".html" or ".htm"))
            throw new NotSupportedException($"Unsupported knowledge document extension: '{extension}'.");

        using var stream = File.OpenRead(path);
        if (stream.Length == 0)
            throw new InvalidDataException("The knowledge document is empty.");
        if (stream.Length > MaximumFileBytes ||
            (extension is ".html" or ".htm" && stream.Length > MaximumMarkupBytes))
            throw new InvalidDataException("The knowledge document exceeds the file size limit.");

        if (extension is ".xlsx" or ".docx")
            CheckOfficePackage(stream, cancellationToken);

        var chunks = new PassageBuilder(cancellationToken);
        switch (extension)
        {
            case ".txt":
            case ".md":
            case ".markdown":
                ReadText(stream, chunks);
                break;
            case ".csv":
                ReadCsv(stream, chunks);
                break;
            case ".json":
                using (var document = JsonDocument.Parse(stream))
                    ValidateReferenceJson(document.RootElement);
                stream.Position = 0;
                ReadText(stream, chunks);
                break;
            case ".pdf":
                ReadPdf(stream, chunks);
                break;
            case ".xlsx":
                ReadWorkbook(stream, chunks);
                break;
            case ".docx":
                ReadWordDocument(stream, chunks);
                break;
            case ".html":
            case ".htm":
                ReadHtml(stream, chunks);
                break;
        }

        return chunks.Complete();
    }

    private static void ValidateReferenceJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("resourceType", out var type) && type.GetString() is
                "Patient" or "Claim" or "ClaimResponse" or "ExplanationOfBenefit" or "Bundle" or "Encounter" or "Coverage")
                throw new InvalidDataException("Patient and claim FHIR payloads cannot be imported into the shared reference index.");
            foreach (var property in element.EnumerateObject()) ValidateReferenceJson(property.Value);
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateReferenceJson(item);
    }

    private static void ReadText(Stream stream, PassageBuilder chunks)
    {
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
        chunks.StartSection("Lines");
        int line = 0;
        while (reader.ReadLine() is { } text)
            chunks.Add(text.AsSpan(), ++line);
    }

    private static void ReadCsv(Stream stream, PassageBuilder chunks)
    {
        using var reader = new TextFieldParser(stream, Utf8, detectEncoding: true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        reader.SetDelimiters(",");
        chunks.StartSection("Rows");
        int row = 0;
        int cells = 0;
        while (!reader.EndOfData)
        {
            chunks.CheckCancellation();
            string[] fields;
            try
            {
                fields = reader.ReadFields()!;
            }
            catch (MalformedLineException)
            {
                // The parser's exception includes the original CSV contents.
                throw new InvalidDataException($"Invalid CSV record at line {reader.ErrorLineNumber}.");
            }

            // Row numbers count logical records, not physical lines inside quoted fields.
            CheckTableLimits(++row, fields.Length, ref cells);
            AddTableRow(fields, row, chunks);
        }
    }

    private static void ReadPdf(Stream stream, PassageBuilder chunks)
    {
        using var document = PdfDocument.Open(stream, new ParsingOptions { MaxStackDepth = 128 });
        if (document.NumberOfPages > MaximumPdfPages)
            throw new InvalidDataException("The PDF exceeds the page limit.");

        for (int pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            chunks.CheckCancellation();
            var page = document.GetPage(pageNumber);
            if (page.Letters.Count > MaximumPdfLettersPerPage)
                throw new InvalidDataException($"PDF page {pageNumber} exceeds the letter limit.");

            chunks.StartSection($"Page {pageNumber}");
            chunks.Add(ContentOrderTextExtractor.GetText(page).AsSpan());
        }
    }

    private static void ReadWorkbook(Stream stream, PassageBuilder chunks)
    {
        // ExcelDataReader initializes a Windows-1252 fallback even for Open XML workbooks.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
        int rows = 0;
        int cells = 0;
        long scannedCells = 0;
        do
        {
            chunks.StartSection($"Sheet \"{reader.Name}\", rows");
            int row = 0;
            int columns = reader.FieldCount;
            if (reader.RowCount > MaximumRows - rows)
                throw new InvalidDataException("The workbook exceeds the row limit.");

            while (reader.Read())
            {
                chunks.CheckCancellation();
                scannedCells += columns;
                if (scannedCells > 100_000_000)
                    throw new InvalidDataException("The workbook exceeds the scanned cell limit.");
                var fields = new List<string>();
                string? previous = null;
                int firstColumn = 0;
                int lastColumn = 0;
                void FlushRun()
                {
                    if (previous is null) return;
                    if (fields.Count == MaximumColumns)
                        throw new InvalidDataException("The workbook exceeds the populated column runs per row limit.");
                    string location = firstColumn == lastColumn ? $"Column {firstColumn}" : $"Columns {firstColumn}-{lastColumn}";
                    fields.Add($"{location}: {previous}");
                    previous = null;
                }
                for (int column = 0; column < columns; column++)
                {
                    string? value = reader.GetValue(column) switch
                    {
                        DateTime date => date.ToString("s", CultureInfo.InvariantCulture),
                        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
                        object cell => cell.ToString(),
                        null => null
                    };
                    if (string.IsNullOrWhiteSpace(value)) { FlushRun(); continue; }
                    if (previous is not null && string.Equals(previous, value, StringComparison.Ordinal))
                    {
                        lastColumn = column + 1;
                        continue;
                    }
                    FlushRun();
                    previous = value;
                    firstColumn = lastColumn = column + 1;
                }
                FlushRun();
                CheckTableLimits(++rows, fields.Count, ref cells);
                AddTableRow([.. fields], ++row, chunks);
            }
        } while (reader.NextResult());
    }

    private static void CheckTableLimits(int rows, int columns, ref int cells)
    {
        cells += columns;
        if (rows > MaximumRows || columns > MaximumColumns || cells > MaximumCells)
            throw new InvalidDataException("The knowledge document exceeds the table size limit.");
    }

    private static void AddTableRow(string?[] fields, int row, PassageBuilder chunks)
    {
        int width = fields.Length;
        while (width > 0 && string.IsNullOrWhiteSpace(fields[width - 1]))
            width--;
        if (width == 0)
            return;

        var text = new StringBuilder();
        for (int i = 0; i < width; i++)
        {
            if (i > 0)
                text.Append(" | ");
            AppendText(text, fields[i]);
        }
        chunks.Add(text.ToString().AsSpan(), row);
    }

    private static void ReadWordDocument(Stream stream, PassageBuilder chunks)
    {
        using var document = WordprocessingDocument.Open(stream, false,
            new OpenSettings { MaxCharactersInPart = MaximumMarkupBytes });
        if (document.MainDocumentPart?.Document.Body is not { } body)
            throw new InvalidDataException("The Word document does not contain a document body.");

        chunks.StartSection("Paragraphs");
        int paragraphNumber = 0;
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            chunks.CheckCancellation();
            var text = new StringBuilder();
            foreach (var child in paragraph.Descendants())
            {
                if (child is (Text or TabChar or Break or CarriageReturn) &&
                    child.Ancestors<Paragraph>().FirstOrDefault() == paragraph)
                {
                    AppendText(text, child switch
                    {
                        Text runText => runText.Text,
                        TabChar => "\t",
                        _ => "\n"
                    });
                }
            }
            chunks.Add(text.ToString().AsSpan(), ++paragraphNumber);
        }
    }

    private static void ReadHtml(Stream stream, PassageBuilder chunks)
    {
        var document = new HtmlDocument { OptionMaxNestedChildNodes = 128 };
        document.Load(stream, detectEncodingFromByteOrderMarks: true);
        var root = document.DocumentNode.SelectSingleNode("//body") ?? document.DocumentNode;
        chunks.StartSection("HTML");
        var text = new StringBuilder();
        Visit(root);
        chunks.Add(text.ToString().AsSpan());

        void Visit(HtmlNode node)
        {
            chunks.CheckCancellation();
            if (node.NodeType == HtmlNodeType.Text)
            {
                AppendText(text, HtmlEntity.DeEntitize(((HtmlTextNode)node).Text));
                return;
            }
            if (node.NodeType == HtmlNodeType.Comment || node.Attributes.Contains("hidden") ||
                node.Name is "script" or "style" or "head" or "template" or "noscript")
                return;

            bool block = node.Name is "article" or "blockquote" or "br" or "div" or "dl" or "dt" or "dd" or
                "footer" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "header" or "hr" or "li" or
                "main" or "p" or "pre" or "section" or "table" or "tr" or "ul" or "ol";
            if (block)
                Separate('\n');
            else if (node.Name is "td" or "th")
                Separate(' ');
            foreach (var child in node.ChildNodes)
                Visit(child);
            if (block)
                Separate('\n');
        }

        void Separate(char separator)
        {
            if (text.Length > 0 && text[^1] != '\n' && text[^1] != separator)
                AppendText(text, separator == '\n' ? "\n" : " ");
        }
    }

    private static void AppendText(StringBuilder text, string? value)
    {
        if (value is null)
            return;
        CheckTextSize((long)text.Length + value.Length);
        text.Append(value);
    }

    private static void CheckTextSize(long characters)
    {
        if (characters > MaximumTextCharacters)
            throw new InvalidDataException("The knowledge document exceeds the extracted text limit.");
    }

    private static void CheckOfficePackage(Stream stream, CancellationToken cancellationToken)
    {
        // Preflight ZIP expansion for the two Office formats only; embedded files are not indexed.
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (archive.Entries.Count > MaximumOfficeParts)
                throw new InvalidDataException("The Office document exceeds the package part limit.");

            var buffer = new byte[32 * 1024];
            long expandedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length > MaximumMarkupBytes || entry.Length > MaximumExpandedBytes - expandedBytes)
                    throw new InvalidDataException("The Office document exceeds the expanded size limit.");

                using var part = entry.Open();
                long actualLength = 0;
                int bytesRead;
                while ((bytesRead = part.Read(buffer)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    actualLength += bytesRead;
                    if (actualLength > entry.Length)
                        throw new InvalidDataException("The Office document contains a part with an invalid size.");
                }
                if (actualLength != entry.Length)
                    throw new InvalidDataException("The Office document contains a part with an invalid size.");
                expandedBytes += actualLength;
            }
        }
        stream.Position = 0;
    }

    private sealed class PassageBuilder(CancellationToken cancellationToken)
    {
        private readonly List<KnowledgePassage> passages = [];
        private readonly StringBuilder pending = new(MaximumChunkCharacters);
        private string section = string.Empty;
        private int firstUnit;
        private int lastUnit;
        private long extractedCharacters;

        internal void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();

        internal void StartSection(string location)
        {
            CheckCancellation();
            Flush();
            section = location;
        }

        internal void Add(ReadOnlySpan<char> text, int unit = 0)
        {
            CheckCancellation();
            extractedCharacters += text.Length;
            CheckTextSize(extractedCharacters);
            if (text.Contains('\0'))
                throw new InvalidDataException("The knowledge document contains invalid text.");

            text = text.Trim();
            while (!text.IsEmpty)
            {
                int separatorLength = pending.Length == 0 ? 0 : 1;
                int available = MaximumChunkCharacters - pending.Length - separatorLength;
                if (text.Length > available && pending.Length > 0)
                {
                    Flush();
                    continue;
                }

                int length = Math.Min(available, text.Length);
                if (length < text.Length)
                {
                    int boundary = length;
                    while (boundary > length / 2 && !char.IsWhiteSpace(text[boundary]))
                        boundary--;
                    if (boundary > length / 2)
                        length = boundary;
                    else if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length]))
                        length--;
                }

                if (separatorLength == 0)
                    firstUnit = unit;
                else
                    pending.Append('\n');
                pending.Append(text[..length].TrimEnd());
                lastUnit = unit;
                text = text[length..].TrimStart();
                if (!text.IsEmpty)
                    Flush();
            }
        }

        internal IReadOnlyList<KnowledgePassage> Complete()
        {
            CheckCancellation();
            Flush();
            if (passages.Count == 0)
                throw new InvalidDataException("The knowledge document contains no extractable text. Scanned pages are not OCR-processed.");
            return passages.AsReadOnly();
        }

        private void Flush()
        {
            if (pending.Length == 0)
                return;
            string location = firstUnit == 0 ? section : firstUnit == lastUnit
                ? $"{section} {firstUnit}"
                : $"{section} {firstUnit}-{lastUnit}";
            passages.Add(new KnowledgePassage(pending.ToString(), location));
            pending.Clear();
        }
    }
}
