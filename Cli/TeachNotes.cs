using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Starforge.Cobol.Dialect;

namespace CobolToQuarkusMigration.Cli;

// Names that belong to the estate: folder names, file names, data names and PROGRAM-IDs, plus the site's own
// Config/sensitive-terms.local.txt. Notes meant for the Starforge team are checked against them before they are written.
public sealed partial class EstateNames
{
    public const string SensitiveTermsPath = "Config/sensitive-terms.local.txt";
    private const long MaxFileBytes = 4 * 1024 * 1024;
    private const int MaxFiles = 50_000;

    private readonly HashSet<string> _names;
    private readonly IReadOnlyList<string> _terms;

    private EstateNames(HashSet<string> names, IReadOnlyList<string> terms, bool estateChecked)
    {
        _names = names;
        _terms = terms;
        EstateChecked = estateChecked;
    }

    // False when no estate folder was given: only the sensitive terms could be checked.
    public bool EstateChecked { get; }

    public static EstateNames From(IReadOnlyList<string> roots, string? sensitiveTermsPath = SensitiveTermsPath)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        foreach (var root in roots.Select(Path.GetFullPath).Where(Directory.Exists))
        {
            Add(names, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            var pending = new Stack<string>([root]);
            while (pending.Count > 0 && files < MaxFiles)
            {
                var dir = pending.Pop();
                foreach (var sub in SafeList(() => Directory.GetDirectories(dir)))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    Add(names, name);
                    pending.Push(sub);
                }

                foreach (var file in SafeList(() => Directory.GetFiles(dir)))
                {
                    if (++files > MaxFiles)
                    {
                        break;
                    }

                    Add(names, Path.GetFileNameWithoutExtension(file));
                    AddDefinedNames(names, file);
                }
            }
        }

        var terms = sensitiveTermsPath is not null && File.Exists(sensitiveTermsPath)
            ? File.ReadAllLines(sensitiveTermsPath).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToList()
            : [];
        return new EstateNames(names, terms, roots.Count > 0);
    }

    // Each estate name or sensitive term found in the text, once.
    public IReadOnlyList<string> Hits(string text)
        => Words().Matches(text).Select(m => m.Value).Where(w => _names.Contains(w))
            .Concat(_terms.Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public bool Contains(string word) => Hits(word).Count > 0;

    // Replaces every estate name by NAME1, NAME2, … and every sensitive term by NAME.
    public string Mask(string text, out int masked)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = Words().Replace(text, m => _names.Contains(m.Value)
            ? map.TryGetValue(m.Value, out var p) ? p : map[m.Value] = "NAME" + (map.Count + 1)
            : m.Value);
        var count = map.Count;
        foreach (var term in _terms.Where(t => result.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            result = Regex.Replace(result, Regex.Escape(term), "NAME", RegexOptions.IgnoreCase);
            count++;
        }

        masked = count;
        return result;
    }

    // Common folder and file words that say nothing about a site; the note's own text uses some of them.
    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "FILLER", "COBOL", "COPY", "COPYBOOK", "COPYBOOKS", "COPYLIB", "CPY", "CBL", "CBLS", "SRC", "SOURCE", "SOURCES",
        "JCL", "PROC", "PROCS", "BMS", "MAPS", "SQL", "DDL", "DCL", "DCLGEN", "INCLUDE", "INCLUDES", "LIB", "LIBS",
        "PROGRAMS", "PROGRAM", "DATA", "CICS", "IMS", "DB2", "ASM", "TEST", "TESTS", "MAIN", "APP", "APPS",
        "STARFORGE", "EXAMPLE", "EXAMPLEC", "EX-FIELD", "SITEWORD",
    };

    private static void Add(HashSet<string> names, string? name)
    {
        var upper = name?.Trim().ToUpperInvariant();
        if (upper is { Length: >= 3 } && !Generic.Contains(upper) && !CobolWords.IsKeyword(upper) && !upper.All(char.IsAsciiDigit))
        {
            names.Add(upper);
        }
    }

    private static void AddDefinedNames(HashSet<string> names, string file)
    {
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes)
            {
                return;
            }

            foreach (var line in File.ReadLines(file))
            {
                foreach (Match m in Defined().Matches(line))
                {
                    Add(names, m.Groups["name"].Value);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string[] SafeList(Func<string[]> list)
    {
        try
        {
            return list();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9-]*[A-Za-z0-9]|[A-Za-z0-9]")]
    private static partial Regex Words();

    // Level-number data names (01 CUST-REC, 05 CUST-ID, 77 …, 88 …) and PROGRAM-ID names.
    [GeneratedRegex(@"(?:(?<![\w-])(?:0?[1-9]|[1-4][0-9]|66|77|88)\s+|PROGRAM-ID\.?\s+)(?<name>[A-Za-z0-9][A-Za-z0-9-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Defined();
}

// Notes for the Starforge team (output/teach-parser/notes/<id>.md). Writing one sends nothing; the person decides whether
// to share it, and posting to GitHub happens only on 'share <id> --github' after they confirm.
public sealed partial class TeachNotes(string folder)
{
    public const string DefaultFolder = "output/teach-parser/notes";
    public const string Repository = "Azure-Samples/Legacy-Modernization-Agents";

    public string FolderPath => folder;

    public string PathOf(string id) => Path.Join(folder, id + ".md");

    public bool Exists(string id) => IsId(id) && File.Exists(PathOf(id));

    public IReadOnlyList<string> Ids()
        => Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.md").Select(f => Path.GetFileNameWithoutExtension(f)).Where(IsId).Order(StringComparer.Ordinal).ToList()
            : [];

    public string Write(string id, string text)
    {
        Directory.CreateDirectory(folder);
        var path = PathOf(id);
        File.WriteAllText(path, text);
        return path;
    }

    public string NewGapId(string code)
    {
        var id = "gap-" + TeachParser.Slug(code);
        var taken = Ids().ToHashSet(StringComparer.Ordinal);
        var candidate = id;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }

    public static string Title(string note) => note.Split('\n')[0].TrimStart('#', ' ').Trim();

    public static string RuleNote(DialectRule generic, IEnumerable<string> example)
    {
        var kind = generic.Kind == DialectRuleKind.CopyAlias ? "include word" : "site statement";
        var sb = new StringBuilder();
        sb.AppendLine($"# Starforge dialect: {kind} {generic.Word}");
        sb.AppendLine();
        sb.AppendLine("A site taught Starforge this rule with `teach-parser`. Please consider reading it without a taught rule.");
        sb.AppendLine("The note holds only the generic rule and a made-up example, no code from the estate.");
        sb.AppendLine();
        sb.AppendLine("## Rule");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine(DialectRules.Create([generic]).ToJson().TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Made-up example");
        sb.AppendLine();
        sb.AppendLine("```cobol");
        foreach (var line in example)
        {
            sb.AppendLine(line);
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    public static string GapNote(string meaning, string code, string explanation, string excerpt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Starforge parser gap: {meaning}");
        sb.AppendLine();
        sb.AppendLine($"Starforge could not read a place that looks like standard COBOL (`{code}`).");
        sb.AppendLine("Names chosen by the site are replaced by W1, W2, …; literal contents and comments are removed;");
        sb.AppendLine("names found in the estate are masked as NAME1, NAME2, ….");
        sb.AppendLine();
        sb.AppendLine("## What the AI said");
        sb.AppendLine();
        sb.AppendLine(explanation.Length == 0 ? "(nothing)" : explanation);
        sb.AppendLine();
        sb.AppendLine("## Excerpt (>> marks the place)");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(excerpt);
        sb.AppendLine("```");
        return sb.ToString();
    }

    // Returns null when the gh command is not available.
    public static (int Exit, string Output)? PostToGitHub(string notePath, string title)
    {
        var start = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "issue", "create", "--repo", Repository, "--title", title, "--body-file", notePath })
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, (stdout.Result + stderr).Trim());
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static bool IsId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,80}$")]
    private static partial Regex IdPattern();
}
