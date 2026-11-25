using System.Collections.Concurrent;
using System.Text;
using Starforge.Cobol.Emit;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Dialect;

// One place Starforge could not read. Program and File are display paths (<estate-folder>/<path>).
public sealed record TeachSite(string Program, string File, int Line, int Column, string Code, string Word);

public sealed record TeachUse(string RuleId, string File, int Line);

public sealed record TeachProgram(
    string Program,
    IReadOnlyList<TeachSite> Sites,
    IReadOnlyList<string> MissingCopybooks,
    IReadOnlyList<TeachUse> TaughtUses);

// Places that fail the same way on the same word; one rule can only ever fix one group.
public sealed record TeachGroup(string Code, string Word, IReadOnlyList<TeachSite> Sites)
{
    public string Key => Code + "|" + Word;

    public IReadOnlyList<string> Programs => Sites.Select(s => s.Program).Distinct(StringComparer.Ordinal).ToList();

    public int Places => Sites.Select(s => (s.File, s.Line)).Distinct().Count();

    // The whole member failed at its first line: a candidate for "this file is not a program".
    public bool WholeFile => Code == "expected-division-header";
}

public sealed record TeachVerification(
    int Fixed,
    IReadOnlyList<TeachSite> Remaining,
    IReadOnlyList<TeachSite> NewSites,
    IReadOnlyList<TeachUse> UsesElsewhere,
    int Excluded)
{
    // A rule is only accepted when it fixes every place in its group and changes nothing that was already read.
    public bool Passed => Remaining.Count == 0 && NewSites.Count == 0 && UsesElsewhere.Count == 0;
}

// Reads a whole estate with the site's rules and reports what Starforge still cannot read.
// Excerpts and verification reread single programs, so nothing but the summary stays in memory.
public sealed class TeachScan
{
    private readonly IReadOnlyList<string> _roots;
    private readonly PathDisplay _display;
    private readonly Dictionary<string, string> _programPaths;

    private TeachScan(IReadOnlyList<string> roots, DialectRules rules, PathDisplay display,
        IReadOnlyList<TeachProgram> programs, IReadOnlyList<string> excluded, Dictionary<string, string> programPaths)
    {
        _roots = roots;
        _display = display;
        _programPaths = programPaths;
        Rules = rules;
        Programs = programs;
        Excluded = excluded;
        Groups = programs
            .SelectMany(p => p.Sites)
            .GroupBy(s => (s.Code, s.Word))
            .Select(g => new TeachGroup(g.Key.Code, g.Key.Word, g.OrderBy(s => s.Program, StringComparer.Ordinal).ThenBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ToList()))
            .OrderByDescending(g => g.Places)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
    }

    public DialectRules Rules { get; }

    public IReadOnlyList<TeachProgram> Programs { get; }

    public IReadOnlyList<string> Excluded { get; }

    public IReadOnlyList<TeachGroup> Groups { get; }

    public static TeachScan Run(IReadOnlyList<string> roots, DialectRules rules)
    {
        var full = roots.Select(Path.GetFullPath).ToList();
        var display = new PathDisplay(full.Select(r => (r, Path.GetFileName(r.TrimEnd(Path.DirectorySeparatorChar)))));
        var all = full.SelectMany(EstateFiles.DiscoverPrograms).ToDictionary(display.Show, p => p, StringComparer.Ordinal);
        var excluded = all.Keys.Where(k => rules.NotProgram(k) is not null).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var included = all.Where(kv => rules.NotProgram(kv.Key) is null).ToList();

        var frontEnd = new CobolFrontEnd(new FrontEndOptions(full, Rules: rules));
        var results = new ConcurrentBag<TeachProgram>();
        Parallel.ForEach(included, kv => results.Add(Summarise(kv.Key, frontEnd.Process(kv.Value), display)));
        var programs = results.OrderBy(p => p.Program, StringComparer.Ordinal).ToList();
        return new TeachScan(full, rules, display, programs, excluded, all);
    }

    public TeachVerification Verify(TeachGroup group, DialectRules candidate)
    {
        var after = Run(_roots, candidate);
        var before = Programs.SelectMany(p => p.Sites).ToList();
        var beforePlaces = before.Select(s => (s.File, s.Line)).ToHashSet();
        var beforeSites = before.Select(s => (s.Program, s.File, s.Line, s.Code)).ToHashSet();
        var afterSites = after.Programs.SelectMany(p => p.Sites).ToList();
        var afterPlaces = afterSites.Select(s => (s.File, s.Line)).ToHashSet();

        var remaining = group.Sites.Where(s => afterPlaces.Contains((s.File, s.Line)) && !after.Excluded.Contains(s.Program)).ToList();
        var newSites = afterSites.Where(s => !beforeSites.Contains((s.Program, s.File, s.Line, s.Code))).ToList();
        var oldRuleIds = Rules.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var usesElsewhere = after.Programs
            .SelectMany(p => p.TaughtUses)
            .Where(u => !oldRuleIds.Contains(u.RuleId) && !beforePlaces.Contains((u.File, u.Line)))
            .Distinct()
            .ToList();
        return new TeachVerification(group.Places - remaining.Select(s => (s.File, s.Line)).Distinct().Count(), remaining, newSites, usesElsewhere,
            after.Excluded.Count - Excluded.Count);
    }

    // The lines around one place, with every name the site chose replaced by W1, W2, …, literal contents and
    // comments removed. COBOL words, numbers, picture strings and the unknown word itself are kept.
    public string Excerpt(TeachSite site, int context = 3)
    {
        var result = new CobolFrontEnd(new FrontEndOptions(_roots, Rules: Rules)).Process(_programPaths[site.Program]);
        var unit = result.Units.FirstOrDefault(u => _display.Show(u.Source.Path) == site.File);
        if (unit is null)
        {
            return string.Empty;
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var line in unit.Source.Lines.Where(l => l.Number >= site.Line - context && l.Number <= site.Line + context))
        {
            sb.Append(line.Number == site.Line ? ">> " : "   ");
            sb.Append(line.Number.ToString().PadLeft(5)).Append(" | ");
            sb.AppendLine(line.Kind switch
            {
                SourceLineKind.Blank => string.Empty,
                SourceLineKind.Comment => "(comment removed)",
                _ => (line.Format == SourceFormat.Fixed && line.Indicator is '-' or 'D' or 'd' ? line.Indicator.ToString() : " ")
                    + Pseudonymise(line.Format == SourceFormat.Fixed ? line.ProgramArea : line.Text, site.Word, names),
            });
        }

        return sb.ToString().TrimEnd();
    }

    internal static string Pseudonymise(string text, string keep, Dictionary<string, string> names)
    {
        var sb = new StringBuilder(text.Length);
        var afterPicture = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (keep.Length > 0 && string.Compare(text, i, keep, 0, keep.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (i == 0 || !IsWordChar(text[i - 1])) && (i + keep.Length == text.Length || !IsWordChar(text[i + keep.Length])))
            {
                sb.Append(keep);
                i += keep.Length;
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '>')
            {
                break;
            }
            else if (c is '\'' or '"')
            {
                var close = text.IndexOf(c, i + 1);
                sb.Append(c).Append("...").Append(c);
                i = close < 0 ? text.Length : close + 1;
            }
            else if (afterPicture && !char.IsWhiteSpace(c))
            {
                var end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && !(text[end] == '.' && (end + 1 == text.Length || char.IsWhiteSpace(text[end + 1]))))
                {
                    end++;
                }

                var picture = text[i..end];
                sb.Append(picture.Equals("IS", StringComparison.OrdinalIgnoreCase) ? "IS" : picture);
                afterPicture = picture.Equals("IS", StringComparison.OrdinalIgnoreCase);
                i = end;
            }
            else if (IsWordChar(c))
            {
                var end = i;
                while (end < text.Length && IsWordChar(text[end]))
                {
                    end++;
                }

                var word = text[i..end];
                var upper = word.ToUpperInvariant();
                var literalPrefix = end < text.Length && text[end] is '\'' or '"' && upper is "X" or "N" or "G" or "Z" or "U" or "B" or "NX" or "BX";
                sb.Append(literalPrefix || word.All(ch => char.IsAsciiDigit(ch)) || CobolWords.IsKeyword(upper) ? upper : Name(word, names));
                afterPicture = upper is "PIC" or "PICTURE";
                i = end;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Name(string word, Dictionary<string, string> names)
    {
        if (!names.TryGetValue(word, out var placeholder))
        {
            placeholder = "W" + (names.Count + 1);
            names[word] = placeholder;
        }

        return placeholder;
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '$' or '#' or '@';

    private static TeachProgram Summarise(string program, CompilationResult r, PathDisplay display)
    {
        var lines = r.Units.GroupBy(u => u.Source.Path).ToDictionary(g => g.Key, g => g.First().Source.Lines);
        var sites = r.UnreadableSource()
            .Select(d => (d.Code, Range: d.Range!.Value))
            .Select(d => new TeachSite(program, display.Show(d.Range.File), d.Range.StartLine, d.Range.StartColumn,
                d.Code, WordAt(lines.GetValueOrDefault(d.Range.File), d.Range.StartLine, d.Range.StartColumn)))
            .ToList();
        var missing = JsonReport.UnresolvedNames(r)
            .Where(n => n.StartsWith("missing-copybook:", StringComparison.Ordinal))
            .Select(n => n["missing-copybook:".Length..])
            .ToList();
        var uses = r.TaughtUses.Select(u => new TeachUse(u.RuleId, display.Show(u.Range.File), u.Range.StartLine)).ToList();
        return new TeachProgram(program, sites, missing, uses);
    }

    // The source text at a place up to the next blank, upper-cased, without a closing period: the word a rule would name.
    private static string WordAt(IReadOnlyList<SourceLine>? lines, int lineNumber, int column)
    {
        var line = lines?.FirstOrDefault(l => l.Number == lineNumber);
        if (line is null)
        {
            return string.Empty;
        }

        var text = line.Text;
        var i = Math.Clamp(column - 1, 0, text.Length);
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        var end = i;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && (line.Format != SourceFormat.Fixed || end < SourceLine.ProgramAreaEnd))
        {
            end++;
        }

        return text[i..end].TrimEnd('.').ToUpperInvariant();
    }
}

public static class CobolWords
{
    private static readonly HashSet<string> DataAndStructureWords = new(StringComparer.Ordinal)
    {
        "IDENTIFICATION", "ID", "ENVIRONMENT", "CONFIGURATION", "SOURCE-COMPUTER", "OBJECT-COMPUTER", "SPECIAL-NAMES",
        "INPUT-OUTPUT", "FILE-CONTROL", "I-O-CONTROL", "SELECT", "ORGANIZATION", "SEQUENTIAL", "INDEXED", "RELATIVE",
        "DYNAMIC", "RANDOM", "WORKING-STORAGE", "LOCAL-STORAGE", "LINKAGE", "FD", "SD", "BLOCK", "CONTAINS", "RECORDING",
        "LABEL", "STANDARD", "OMITTED", "PROGRAM-ID", "AUTHOR", "INSTALLATION", "DATE-WRITTEN", "DATE-COMPILED", "SECURITY",
        "PIC", "PICTURE", "OCCURS", "REDEFINES", "RENAMES", "COMP", "COMP-1", "COMP-2", "COMP-3", "COMP-4", "COMP-5",
        "COMPUTATIONAL", "COMPUTATIONAL-1", "COMPUTATIONAL-2", "COMPUTATIONAL-3", "COMPUTATIONAL-4", "COMPUTATIONAL-5",
        "PACKED-DECIMAL", "DISPLAY-1", "INDEX", "JUSTIFIED", "JUST", "SIGN", "SEPARATE", "SYNC", "SYNCHRONIZED", "BLANK",
        "ZERO", "ZEROS", "ZEROES", "SPACE", "SPACES", "HIGH-VALUE", "HIGH-VALUES", "LOW-VALUE", "LOW-VALUES", "QUOTE",
        "QUOTES", "NULL", "NULLS", "EXTERNAL", "GLOBAL", "BASED", "POINTER", "PROCEDURE-POINTER", "FUNCTION-POINTER",
        "OBJECT", "EXEC", "EXECUTE", "END-EXEC", "SQL", "CICS", "DLI", "INCLUDE", "COPY", "REPLACE", "REPLACING", "LEADING",
        "TRAILING", "PROGRAM", "END-PROGRAM", "PROCESS", "CBL", "SKIP1", "SKIP2", "SKIP3", "EJECT", "TITLE", "BASIS",
        "INSERT", "DELETE", "READY", "RESET", "TRACE", "SERVICE", "DECLARE", "CURSOR", "WHERE", "ORDER", "VALUES", "SET",
        "UPDATE", "FETCH", "OPEN", "CLOSE", "COMMIT", "ROLLBACK", "SECTION", "DIVISION", "CURRENCY", "DECIMAL-POINT",
        "CONSOLE", "SYSIN", "SYSOUT", "SYSIPT", "SYSLST", "C01", "TOP", "ASSIGN",
    };

    public static bool IsKeyword(string upper)
        => Syntax.Parser.IsReservedWord(upper) || Syntax.Parser.IsKnownVerb(upper) || DataAndStructureWords.Contains(upper);
}
