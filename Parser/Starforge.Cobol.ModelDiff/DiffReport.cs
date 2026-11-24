using System.Text;
using System.Text.Json;

namespace Starforge.Cobol.ModelDiff;

internal enum DiffKind
{
    OnlyStarforge,
    OnlyRekt,
    Mismatch,
}

internal sealed class ClassTally
{
    public int Count { get; set; }

    public List<string> Examples { get; } = new();
}

// Counts for one compared field: matched values, values only one side has, and values both have but disagree on.
internal sealed class Tally(string category)
{
    private const int MaxExamples = 6;

    public string Category { get; } = category;

    public int Matched { get; private set; }

    public int OnlyStarforge { get; private set; }

    public int OnlyRekt { get; private set; }

    public int Mismatch { get; private set; }

    public SortedDictionary<string, ClassTally> Classes { get; } = new(StringComparer.Ordinal);

    public void Match(int n = 1) => Matched += n;

    public void Add(DiffKind kind, string differenceClass, string example)
    {
        switch (kind)
        {
            case DiffKind.OnlyStarforge:
                OnlyStarforge++;
                break;
            case DiffKind.OnlyRekt:
                OnlyRekt++;
                break;
            default:
                Mismatch++;
                break;
        }

        var key = $"{kind}: {differenceClass}";
        if (!Classes.TryGetValue(key, out var c))
        {
            Classes[key] = c = new ClassTally();
        }

        c.Count++;
        if (c.Examples.Count < MaxExamples)
        {
            c.Examples.Add(example);
        }
    }
}

internal sealed class DiffReport
{
    private readonly SortedDictionary<string, Tally> _tallies = new(StringComparer.Ordinal);

    private readonly List<string> _rows = new();

    public int Programs { get; set; }

    public void Record(string category, DiffKind kind, string differenceClass, string program, string key, string? rekt, string? starforge) =>
        _rows.Add(string.Join('\t', category, kind, differenceClass, program, key, Clean(rekt), Clean(starforge)));

    // Every difference, one per line, so a class can be audited beyond its examples.
    public void WriteRows(string path) =>
        File.WriteAllLines(path, ["category\tkind\tclass\tprogram\tkey\trekt\tstarforge", .. _rows]);

    private static string Clean(string? value) => value is null ? "" : value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    public Tally this[string category]
    {
        get
        {
            if (!_tallies.TryGetValue(category, out var t))
            {
                _tallies[category] = t = new Tally(category);
            }

            return t;
        }
    }

    public IEnumerable<Tally> Tallies => _tallies.Values;

    public int Unexplained => Tallies.SelectMany(t => t.Classes).Where(c => c.Key.Contains(Classifier.Unexplained, StringComparison.Ordinal)).Sum(c => c.Value.Count);

    public void WriteJson(string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        w.WriteStartObject();
        w.WriteNumber("programs", Programs);
        w.WriteNumber("unexplained", Unexplained);
        w.WriteStartArray("categories");
        foreach (var t in Tallies)
        {
            w.WriteStartObject();
            w.WriteString("category", t.Category);
            w.WriteNumber("matched", t.Matched);
            w.WriteNumber("onlyStarforge", t.OnlyStarforge);
            w.WriteNumber("onlyRekt", t.OnlyRekt);
            w.WriteNumber("valueMismatch", t.Mismatch);
            w.WriteStartArray("classes");
            foreach (var (name, c) in t.Classes)
            {
                w.WriteStartObject();
                w.WriteString("class", name);
                w.WriteNumber("count", c.Count);
                w.WriteStartArray("examples");
                foreach (var e in c.Examples)
                {
                    w.WriteStringValue(e);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Model diff: REKT vs Starforge through the application's readers").AppendLine();
        sb.AppendLine($"Programs: {Programs}. Unexplained differences: **{Unexplained}**.").AppendLine();
        sb.AppendLine("| Category | Matched | Only Starforge | Only REKT | Value mismatch |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var t in Tallies)
        {
            sb.AppendLine($"| {t.Category} | {t.Matched} | {t.OnlyStarforge} | {t.OnlyRekt} | {t.Mismatch} |");
        }

        sb.AppendLine().AppendLine("## Difference classes").AppendLine();
        foreach (var t in Tallies.Where(t => t.Classes.Count > 0))
        {
            sb.AppendLine($"### {t.Category}").AppendLine();
            foreach (var (name, c) in t.Classes)
            {
                sb.AppendLine($"- **{name}** — {c.Count}");
                foreach (var e in c.Examples)
                {
                    sb.AppendLine($"  - `{e.Replace('`', '\'').Replace('\n', ' ')}`");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
