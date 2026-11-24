using System.Text;
using System.Text.RegularExpressions;

namespace Starforge.Cobol.Native;

// One CICS resource definition (FILE, PROGRAM, TRANSACTION, TDQUEUE, ...) read from a DFHCSDUP extract or a
// resourceDefinitions YAML catalog. Attribute keys are upper case; values keep their spelling.
internal sealed record CicsDefinition(string Type, string Name, IReadOnlyDictionary<string, string> Attributes, string Path, string Sha256)
{
    public string? this[string key] => Attributes.TryGetValue(key, out var value) ? value : null;
}

// The estate's CICS resource definitions. A name defined more than once with different attributes is
// ambiguous: the exporter cannot know which one the region installs, so it never binds.
internal sealed partial class CicsCatalog
{
    public static readonly CicsCatalog Empty = new([]);

    private readonly Dictionary<(string Type, string Name), List<CicsDefinition>> _definitions = new();

    private CicsCatalog(IEnumerable<CicsDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            var key = (definition.Type, definition.Name);
            if (!_definitions.TryGetValue(key, out var list))
            {
                _definitions[key] = list = new List<CicsDefinition>();
            }

            list.Add(definition);
        }
    }

    public CicsDefinition? Find(string type, string name)
    {
        if (!_definitions.TryGetValue((type, name.Trim().ToUpperInvariant()), out var list))
        {
            return null;
        }

        var first = list[0];
        return list.All(d => d.Attributes.Count == first.Attributes.Count
            && d.Attributes.All(a => first[a.Key] == a.Value)) ? first : null;
    }

    public static CicsCatalog Load(IEnumerable<string> files)
    {
        var definitions = new List<CicsDefinition>();
        foreach (var file in files)
        {
            var path = System.IO.Path.GetFullPath(file);
            var text = File.ReadAllText(path, Encoding.Latin1);
            var sha = NativeSyntax.FileSha256(path);
            var parsed = System.IO.Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml" ? Yaml(text) : Csd(text);
            definitions.AddRange(parsed.Select(p => new CicsDefinition(p.Type, p.Name, p.Attributes, path, sha)));
        }

        return new CicsCatalog(definitions);
    }

    private static IEnumerable<(string Type, string Name, Dictionary<string, string> Attributes)> Csd(string text)
    {
        var starts = DefineR().Matches(text).Select(m => m.Index).Append(text.Length).ToList();
        for (var i = 0; i + 1 < starts.Count; i++)
        {
            var body = text[starts[i]..starts[i + 1]];
            var options = Options(body, 0);
            if (options.Count < 2 || !options[0].Key.Equals("DEFINE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var (type, name) = options[1];
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in options.Skip(2))
            {
                attributes.TryAdd(key.ToUpperInvariant(), value ?? string.Empty);
            }

            if (name is { Length: > 0 })
            {
                yield return (type.ToUpperInvariant(), name.Trim().ToUpperInvariant(), attributes);
            }
        }
    }

    // KEYWORD and KEYWORD(value) items; a value keeps nested parentheses and inner spaces.
    public static List<(string Key, string? Value)> Options(string text, int from)
    {
        var items = new List<(string, string?)>();
        var i = from;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == ',')
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('(' or ')' or ','))
            {
                i++;
            }

            var key = text[start..i];
            var probe = i;
            while (probe < text.Length && text[probe] == ' ')
            {
                probe++;
            }

            if (probe < text.Length && text[probe] == '(' && key.Length > 0)
            {
                var depth = 0;
                var open = probe;
                for (i = probe; i < text.Length; i++)
                {
                    depth += text[i] == '(' ? 1 : text[i] == ')' ? -1 : 0;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                items.Add((key, text[(open + 1)..Math.Min(i, text.Length)]));
                i++;
            }
            else if (key.Length > 0)
            {
                items.Add((key, null));
            }
            else
            {
                i++;
            }
        }

        return items;
    }

    // Two-level "- type:" items with scalar "key: value" members; nested maps and lists are ignored.
    private static IEnumerable<(string Type, string Name, Dictionary<string, string> Attributes)> Yaml(string text)
    {
        string? type = null;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        var itemIndent = -1;
        var memberIndent = -1;
        var inCatalog = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var content = line.TrimStart();
            if (content.Length == 0 || content.StartsWith('#'))
            {
                continue;
            }

            var depth = line.Length - content.Length;
            if (depth == 0)
            {
                if (type is not null && attributes.TryGetValue("NAME", out var last))
                {
                    yield return (type, last.ToUpperInvariant(), attributes);
                }

                type = null;
                inCatalog = content == "resourceDefinitions:";
                continue;
            }

            if (!inCatalog)
            {
                continue;
            }

            if (content.StartsWith("- ", StringComparison.Ordinal) && (itemIndent < 0 || depth <= itemIndent))
            {
                if (type is not null && attributes.TryGetValue("NAME", out var name))
                {
                    yield return (type, name.ToUpperInvariant(), attributes);
                }

                itemIndent = depth;
                memberIndent = -1;
                var header = content[2..].Trim();
                type = header.EndsWith(':') && header.Length > 1 ? header[..^1].Trim().ToUpperInvariant() : null;
                attributes = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            if (type is null || depth <= itemIndent)
            {
                continue;
            }

            if (memberIndent < 0)
            {
                memberIndent = depth;
            }

            var colon = content.IndexOf(':');
            if (depth != memberIndent || colon <= 0)
            {
                continue;
            }

            var key = content[..colon].Trim().ToUpperInvariant();
            var value = Scalar(content[(colon + 1)..]);
            if (value is not null)
            {
                attributes.TryAdd(key, value);
            }
        }

        if (type is not null && attributes.TryGetValue("NAME", out var final))
        {
            yield return (type, final.ToUpperInvariant(), attributes);
        }
    }

    private static string? Scalar(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] is '"' or '\'')
        {
            var end = value.IndexOf(value[0], 1);
            return end > 0 ? value[1..end] : null;
        }

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    [GeneratedRegex(@"\bDEFINE\s+[A-Z]+\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex DefineR();
}

// IDCAMS DEFINE CLUSTER / ALTERNATEINDEX / PATH commands from in-stream SYSIN data of PGM=IDCAMS steps.
internal sealed record VsamDefinition(string Type, string Name, string Command, IReadOnlyDictionary<string, string?> Parameters, string Path, string Sha256)
{
    public string? this[string key] => Parameters.TryGetValue(key, out var value) ? value : null;

    public bool Has(string key) => Parameters.ContainsKey(key);
}

internal sealed class VsamCatalog
{
    public static readonly VsamCatalog Empty = new([]);

    private readonly List<VsamDefinition> _definitions;

    private VsamCatalog(List<VsamDefinition> definitions) => _definitions = definitions;

    // The single DEFINE of that type and name; repeated identical commands are one definition.
    public VsamDefinition? Find(string type, string name)
    {
        var matches = _definitions.Where(d => d.Type == type && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count > 0 && matches.All(m => m.Command == matches[0].Command && m.Path == matches[0].Path) ? matches[0] : null;
    }

    // Every distinct DEFINE of that type and name in source path order; one cluster may be defined by several jobs.
    public List<VsamDefinition> FindAll(string type, string name) =>
        _definitions.Where(d => d.Type == type && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => (d.Command, d.Path)).Select(g => g.First())
            .OrderBy(d => d.Path, StringComparer.Ordinal).ToList();

    public static VsamCatalog Load(IEnumerable<string> files)
    {
        var definitions = new List<VsamDefinition>();
        foreach (var file in files)
        {
            var path = System.IO.Path.GetFullPath(file);
            var sha = NativeSyntax.FileSha256(path);
            foreach (var command in Commands(File.ReadAllLines(path, Encoding.Latin1)))
            {
                if (Definition(command, path, sha) is { } definition)
                {
                    definitions.Add(definition);
                }
            }
        }

        return new VsamCatalog(definitions);
    }

    // Commands of every in-stream SYSIN of an IDCAMS step, continuation dashes removed and blanks collapsed.
    private static IEnumerable<string> Commands(string[] lines)
    {
        var idcams = false;
        var inStream = false;
        var command = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw.Length > 72 ? raw[..72] : raw;
            if (inStream)
            {
                if (line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal))
                {
                    if (command.Length > 0)
                    {
                        yield return Collapse(command.ToString());
                    }

                    command.Clear();
                    inStream = false;
                    if (line.StartsWith("/*", StringComparison.Ordinal))
                    {
                        continue;
                    }
                }
                else
                {
                    var body = line.TrimEnd();
                    var continued = body.EndsWith('-');
                    command.Append(' ').Append(continued ? body[..^1] : body);
                    if (!continued)
                    {
                        yield return Collapse(command.ToString());
                        command.Clear();
                    }

                    continue;
                }
            }

            if (!line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("//*", StringComparison.Ordinal))
            {
                continue;
            }

            if (Regex.IsMatch(line, @"^//\S*\s+EXEC\s", RegexOptions.IgnoreCase))
            {
                idcams = Regex.IsMatch(line, @"\bPGM=IDCAMS\b", RegexOptions.IgnoreCase);
            }

            if (idcams && Regex.IsMatch(line, @"^//SYSIN\s+DD\s+(\*|DATA)", RegexOptions.IgnoreCase))
            {
                inStream = true;
            }
        }

        if (inStream && command.Length > 0)
        {
            yield return Collapse(command.ToString());
        }
    }

    private static string Collapse(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private static VsamDefinition? Definition(string command, string path, string sha)
    {
        var match = Regex.Match(command, @"^DEFINE\s+(CLUSTER|ALTERNATEINDEX|AIX|PATH)\s*\(", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var type = match.Groups[1].Value.ToUpperInvariant() == "AIX" ? "ALTERNATEINDEX" : match.Groups[1].Value.ToUpperInvariant();
        var open = match.Index + match.Length - 1;
        var depth = 0;
        var close = open;
        for (; close < command.Length; close++)
        {
            depth += command[close] == '(' ? 1 : command[close] == ')' ? -1 : 0;
            if (depth == 0)
            {
                break;
            }
        }

        if (close >= command.Length)
        {
            return null;
        }

        var main = command[(open + 1)..close];

        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in CicsCatalog.Options(main, 0))
        {
            parameters.TryAdd(key.ToUpperInvariant(), value?.Trim());
        }

        return parameters.TryGetValue("NAME", out var name) && name is { Length: > 0 }
            ? new VsamDefinition(type, name.ToUpperInvariant(), command, parameters, path, sha)
            : null;
    }
}
