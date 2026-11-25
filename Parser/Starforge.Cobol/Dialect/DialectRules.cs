using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Dialect;

// statement: a site verb Starforge reads past as one opaque statement (it does not know what it does);
// copyAlias: a site include word that works like COPY (e.g. ++INCLUDE);
// notProgram: exact estate paths that are not COBOL programs.
public enum DialectRuleKind
{
    Statement,
    CopyAlias,
    NotProgram,
}

// None only when a person confirmed the statement changes no data and no control flow.
public enum StatementEffects
{
    Unknown,
    None,
}

public sealed record DialectRule(
    string Id,
    DialectRuleKind Kind,
    string? Word = null,
    string? End = null,
    StatementEffects Effects = StatementEffects.Unknown,
    IReadOnlyList<string>? Paths = null,
    string? Reason = null)
{
    public const string PeriodEnd = "period";

    public IReadOnlyList<string> PathList => Paths ?? Array.Empty<string>();
}

// One place a rule was applied while reading a program.
public sealed record TaughtUse(string RuleId, DialectRuleKind Kind, string Word, SourceRange Range);

// The site's taught rules (Config/starforge-dialect.local.json). Rules only add readings where Starforge has none:
// a statement rule cannot redefine a verb Starforge already parses, and a copy alias cannot be COPY itself.
public sealed partial class DialectRules
{
    public const int Version = 1;

    private static readonly HashSet<string> ReservedStatementWords = new(StringComparer.Ordinal)
    {
        "ELSE", "WHEN", "THEN", "NOT", "ON", "AT", "INVALID", "SIZE", "OVERFLOW", "EXCEPTION", "NEXT", "SENTENCE",
        "SECTION", "DIVISION", "COPY", "REPLACE", "EXEC", "EXECUTE", "END-EXEC",
    };

    private readonly Dictionary<string, DialectRule> _statements;
    private readonly Dictionary<string, DialectRule> _aliases;
    private readonly Dictionary<string, DialectRule> _notPrograms;

    private DialectRules(IReadOnlyList<DialectRule> rules, string sha256)
    {
        Rules = rules;
        Sha256 = sha256;
        _statements = rules.Where(r => r.Kind == DialectRuleKind.Statement).ToDictionary(r => r.Word!, StringComparer.OrdinalIgnoreCase);
        _aliases = rules.Where(r => r.Kind == DialectRuleKind.CopyAlias).ToDictionary(r => r.Word!, StringComparer.OrdinalIgnoreCase);
        _notPrograms = rules.Where(r => r.Kind == DialectRuleKind.NotProgram)
            .SelectMany(r => r.PathList.Select(p => (p, r)))
            .ToDictionary(x => x.p, x => x.r, StringComparer.Ordinal);
    }

    public static DialectRules Empty { get; } = new(Array.Empty<DialectRule>(), string.Empty);

    public IReadOnlyList<DialectRule> Rules { get; }

    // SHA-256 of the canonical JSON, so a result records exactly which ruleset read it; empty when there are no rules.
    public string Sha256 { get; }

    public bool IsEmpty => Rules.Count == 0;

    public DialectRule? Statement(string word) => _statements.GetValueOrDefault(word);

    public DialectRule? CopyAlias(string word) => _aliases.GetValueOrDefault(word);

    // relativePath as Starforge shows it: <estate-folder>/<path inside the estate>, forward slashes.
    public DialectRule? NotProgram(string relativePath) => _notPrograms.GetValueOrDefault(relativePath.Replace('\\', '/'));

    public static DialectRules Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"dialect rules file not found: {path}", path);
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static DialectRules Parse(string json, string sourceName = "rules")
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{sourceName}: not valid JSON ({ex.Message})");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{sourceName}: must be a JSON object with \"version\" and \"rules\"");
            }

            if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Version)
            {
                throw new InvalidDataException($"{sourceName}: \"version\" must be {Version}");
            }

            var rules = new List<DialectRule>();
            if (root.TryGetProperty("rules", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"{sourceName}: \"rules\" must be an array");
                }

                foreach (var item in list.EnumerateArray())
                {
                    rules.Add(ReadRule(item, sourceName));
                }
            }

            return Create(rules, sourceName);
        }
    }

    // Validates the rules as a set and returns them in a canonical order.
    public static DialectRules Create(IEnumerable<DialectRule> rules, string sourceName = "rules")
    {
        var list = rules.Select(r => Normalize(r, sourceName)).ToList();
        var errors = new List<string>();
        foreach (var dup in list.GroupBy(r => r.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"rule id '{dup.Key}' is used more than once");
        }

        foreach (var dup in list.Where(r => r.Word is not null).GroupBy(r => r.Word!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"word '{dup.Key}' has more than one rule ({string.Join(", ", dup.Select(r => r.Id))})");
        }

        foreach (var dup in list.SelectMany(r => r.PathList.Select(p => (p, r.Id))).GroupBy(x => x.p, StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            errors.Add($"path '{dup.Key}' is listed by more than one rule");
        }

        if (errors.Count > 0)
        {
            throw new InvalidDataException($"{sourceName}: {string.Join("; ", errors)}");
        }

        if (list.Count == 0)
        {
            return Empty;
        }

        list = list.OrderBy(r => r.Kind).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var canonical = ToJson(list);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new DialectRules(list, hash);
    }

    public DialectRules With(DialectRule rule) => Create(Rules.Append(rule));

    public DialectRules Without(string id) => Create(Rules.Where(r => r.Id != id));

    public string ToJson() => ToJson(Rules);

    private static string ToJson(IReadOnlyList<DialectRule> rules)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteStartArray("rules");
            foreach (var r in rules)
            {
                w.WriteStartObject();
                w.WriteString("id", r.Id);
                w.WriteString("kind", KindName(r.Kind));
                if (r.Word is not null)
                {
                    w.WriteString("word", r.Word);
                }

                if (r.Kind == DialectRuleKind.Statement)
                {
                    w.WriteString("end", r.End);
                    w.WriteString("effects", r.Effects == StatementEffects.None ? "none" : "unknown");
                }

                if (r.Kind == DialectRuleKind.NotProgram)
                {
                    w.WriteStartArray("paths");
                    foreach (var p in r.PathList)
                    {
                        w.WriteStringValue(p);
                    }

                    w.WriteEndArray();
                }

                if (!string.IsNullOrWhiteSpace(r.Reason))
                {
                    w.WriteString("reason", r.Reason);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    public static string KindName(DialectRuleKind kind) => kind switch
    {
        DialectRuleKind.Statement => "statement",
        DialectRuleKind.CopyAlias => "copyAlias",
        _ => "notProgram",
    };

    private static DialectRule ReadRule(JsonElement item, string sourceName)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{sourceName}: each rule must be a JSON object");
        }

        string? Str(string name)
        {
            if (!item.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return p.ValueKind == JsonValueKind.String ? p.GetString() : throw new InvalidDataException($"{sourceName}: rule property \"{name}\" must be a string");
        }

        var id = Str("id") ?? throw new InvalidDataException($"{sourceName}: a rule has no \"id\"");
        var kind = Str("kind") switch
        {
            "statement" => DialectRuleKind.Statement,
            "copyAlias" => DialectRuleKind.CopyAlias,
            "notProgram" => DialectRuleKind.NotProgram,
            var k => throw new InvalidDataException($"{sourceName}: rule '{id}' has unknown kind '{k}' (use statement, copyAlias or notProgram)"),
        };
        var effects = Str("effects") switch
        {
            null or "unknown" => StatementEffects.Unknown,
            "none" => StatementEffects.None,
            var e => throw new InvalidDataException($"{sourceName}: rule '{id}' has effects '{e}' (use none or unknown)"),
        };
        List<string>? paths = null;
        if (item.TryGetProperty("paths", out var ps))
        {
            if (ps.ValueKind != JsonValueKind.Array || ps.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
            {
                throw new InvalidDataException($"{sourceName}: rule '{id}' \"paths\" must be an array of strings");
            }

            paths = ps.EnumerateArray().Select(p => p.GetString()!).ToList();
        }

        return new DialectRule(id, kind, Str("word"), Str("end"), effects, paths, Str("reason"));
    }

    private static DialectRule Normalize(DialectRule r, string sourceName)
    {
        string Fail(string message) => throw new InvalidDataException($"{sourceName}: rule '{r.Id}': {message}");

        if (!RuleId().IsMatch(r.Id))
        {
            Fail("id must be 1-40 lower-case letters, digits or hyphens");
        }

        switch (r.Kind)
        {
            case DialectRuleKind.Statement:
            {
                var word = r.Word?.Trim().ToUpperInvariant() ?? Fail("a statement rule needs \"word\"");
                if (!CobolWord().IsMatch(word))
                {
                    Fail($"'{word}' is not a COBOL word");
                }

                if (Syntax.Parser.IsKnownVerb(word) || ReservedStatementWords.Contains(word) || word.StartsWith("END-", StringComparison.Ordinal))
                {
                    Fail($"{word} is a word Starforge already reads; rules may only add new statements");
                }

                var end = r.End?.Trim() ?? Fail("a statement rule needs \"end\": \"period\" or an END- word");
                end = end.Equals(DialectRule.PeriodEnd, StringComparison.OrdinalIgnoreCase) ? DialectRule.PeriodEnd : end.ToUpperInvariant();
                if (end != DialectRule.PeriodEnd && !(end.StartsWith("END-", StringComparison.Ordinal) && CobolWord().IsMatch(end)))
                {
                    Fail("\"end\" must be \"period\" or an END- word such as END-" + word);
                }

                if (r.PathList.Count > 0)
                {
                    Fail("\"paths\" belongs to notProgram rules");
                }

                return r with { Word = word, End = end, Paths = null, Reason = r.Reason?.Trim() };
            }

            case DialectRuleKind.CopyAlias:
            {
                var word = r.Word?.Trim().ToUpperInvariant() ?? Fail("a copyAlias rule needs \"word\"");
                if (word.Length is 0 or > 30 || word.Any(c => char.IsWhiteSpace(c) || c is '.' or '\'' or '"' or '(' or ')' or ':' or ',' or ';'))
                {
                    Fail($"'{word}' is not a single include word");
                }

                if (word is "COPY" or "-COPY" or "INCLUDE" or "REPLACE" || Syntax.Parser.IsKnownVerb(word))
                {
                    Fail($"{word} is a word Starforge already reads");
                }

                if (r.End is not null || r.PathList.Count > 0)
                {
                    Fail("a copyAlias rule has only \"word\"");
                }

                return r with { Word = word, Effects = StatementEffects.Unknown, Reason = r.Reason?.Trim() };
            }

            default:
            {
                if (r.Word is not null || r.End is not null)
                {
                    Fail("a notProgram rule has only \"paths\"");
                }

                if (r.PathList.Count == 0)
                {
                    Fail("a notProgram rule needs at least one path");
                }

                var paths = new List<string>();
                foreach (var raw in r.PathList)
                {
                    var p = raw.Trim().Replace('\\', '/');
                    if (p.Length == 0 || p.StartsWith('/') || p.Contains(':') || p.IndexOfAny(['*', '?', '[', ']']) >= 0
                        || p.Split('/').Any(s => s is "" or "." or ".."))
                    {
                        Fail($"'{raw}' must be an exact path such as <estate>/<folder>/<file> (no wildcards, no ..)");
                    }

                    paths.Add(p);
                }

                return r with { Paths = paths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList(), Effects = StatementEffects.Unknown, Reason = r.Reason?.Trim() };
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex RuleId();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,29}$")]
    private static partial Regex CobolWord();
}
