using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WBDropp.Services;

public static partial class ArticleParser
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".doc", ".docx"
    };

    static ArticleParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match match in DigitsRegex().Matches(text))
        {
            var value = match.Value.TrimStart('0');
            if (value.Length == 0) value = "0";
            if (seen.Add(value)) result.Add(value);
        }
        return result;
    }

    public static string ReadImportFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".txt" => ReadText(path),
            ".docx" => ReadDocx(path),
            ".doc" => ReadLegacyDoc(path),
            _ => throw new NotSupportedException("Поддерживаются файлы .txt, .doc и .docx.")
        };
    }

    public static bool IsSupportedFile(string path) =>
        File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path));

    public static IReadOnlyList<string> ParseFiles(IEnumerable<string> paths) =>
        Parse(string.Join(Environment.NewLine, paths.Select(ReadImportFile)));

    private static string ReadText(string path)
    {
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.GetEncoding(1251) })
        {
            try { return File.ReadAllText(path, encoding); }
            catch (DecoderFallbackException) { }
        }
        return File.ReadAllText(path);
    }

    private static string ReadDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("В DOCX не найден документ.");
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        return string.Join(" ", document.Descendants().Where(node => node.Name.LocalName == "t").Select(node => node.Value));
    }

    private static string ReadLegacyDoc(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var utf8 = Encoding.UTF8.GetString(bytes);
        var cp1251 = Encoding.GetEncoding(1251).GetString(bytes);
        return $"{utf8}\n{cp1251}";
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();
}
