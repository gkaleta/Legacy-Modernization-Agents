using System.Text.RegularExpressions;

namespace Starforge.Cobol.Deps;

// TRANSACTION -> PROGRAM bindings from an estate's CICS resource definitions: DFHCSDUP extracts (*.csd) and
// resourceDefinitions YAML catalogs. A transaction defined twice with different programs is ambiguous and never resolves.
public sealed partial class CicsDefinitions
{
    public static readonly CicsDefinitions Empty = new(new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, Binding> _transactions;

    private CicsDefinitions(Dictionary<string, Binding> transactions) => _transactions = transactions;

    public sealed record Binding(string Program, string SourcePath);

    public int Count => _transactions.Count;

    public Binding? Transaction(string name) =>
        _transactions.TryGetValue(name.Trim(), out var binding) && binding.Program.Length > 0 ? binding : null;

    // Every *.csd under the estate, and YAML files that declare resourceDefinitions; dot folders hold generated copies.
    public static CicsDefinitions Discover(string estateRoot)
    {
        if (!Directory.Exists(estateRoot))
        {
            return Empty;
        }

        var files = Directory.EnumerateFiles(estateRoot, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(estateRoot, f).Split(Path.DirectorySeparatorChar).Any(s => s.StartsWith('.')))
            .Where(f => Path.GetExtension(f).ToLowerInvariant() switch
            {
                ".csd" => true,
                ".yaml" or ".yml" => File.ReadLines(f).Any(l => l.StartsWith("resourceDefinitions:", StringComparison.Ordinal)),
                _ => false,
            })
            .Order(StringComparer.Ordinal);
        return Load(files);
    }

    public static CicsDefinitions Load(IEnumerable<string> files)
    {
        var transactions = new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var path = Path.GetFullPath(file);
            var text = File.ReadAllText(path);
            var pairs = Path.GetExtension(path).ToLowerInvariant() is ".yaml" or ".yml" ? Yaml(text) : Csd(text);
            foreach (var (transaction, program) in pairs)
            {
                if (transactions.TryGetValue(transaction, out var existing) && !existing.Program.Equals(program, StringComparison.OrdinalIgnoreCase))
                {
                    transactions[transaction] = existing with { Program = "" };
                    continue;
                }

                transactions.TryAdd(transaction, new Binding(program.ToUpperInvariant(), path));
            }
        }

        return new CicsDefinitions(transactions);
    }

    private static IEnumerable<(string Transaction, string Program)> Csd(string text)
    {
        var definitions = DefineR().Matches(text).Select(m => m.Index).Append(text.Length).ToList();
        for (var i = 0; i + 1 < definitions.Count; i++)
        {
            var body = text[definitions[i]..definitions[i + 1]];
            var header = TransactionR().Match(body);
            var program = ProgramR().Match(body);
            if (header.Success && program.Success)
            {
                yield return (header.Groups[1].Value.Trim(), program.Groups[1].Value.Trim());
            }
        }
    }

    // Only the two-level shape "- transaction:" with scalar "name:"/"program:" members is read; anything else is ignored.
    private static IEnumerable<(string Transaction, string Program)> Yaml(string text)
    {
        string? name = null, program = null;
        var inTransaction = false;
        var indent = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var content = line.TrimStart();
            if (content.Length == 0 || content.StartsWith('#'))
            {
                continue;
            }

            var depth = line.Length - content.Length;
            if ((content.StartsWith("- ", StringComparison.Ordinal) && (!inTransaction || depth <= indent)) || (inTransaction && depth <= indent))
            {
                if (inTransaction && name is not null && program is not null)
                {
                    yield return (name, program);
                }

                inTransaction = content == "- transaction:";
                indent = depth;
                name = program = null;
                continue;
            }

            if (!inTransaction)
            {
                continue;
            }

            var colon = content.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = content[..colon].Trim();
            var value = content[(colon + 1)..].Trim().Trim('"', '\'');
            if (key == "name")
            {
                name = value;
            }
            else if (key == "program")
            {
                program = value;
            }
        }

        if (inTransaction && name is not null && program is not null)
        {
            yield return (name, program);
        }
    }

    [GeneratedRegex(@"\bDEFINE\s+[A-Z]+\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex DefineR();

    [GeneratedRegex(@"^\s*DEFINE\s+TRANSACTION\s*\(\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex TransactionR();

    [GeneratedRegex(@"\bPROGRAM\s*\(\s*([^)\s]+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramR();
}
