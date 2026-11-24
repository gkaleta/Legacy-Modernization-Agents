using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Starforge.Cobol.Emit;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Cli;

// `starforge diff-rekt --procedure`: compares the M3 procedure structure with REKT's flow_ast per program.
// REKT nodes carry no line numbers, so REKT lines are derived: a header is located in the program file and the
// newlines of a node's originalText give its end line and the offsets of the statements inside it.
internal static partial class ProcedureDiffCommand
{
    public static readonly string[] Categories = { "sections", "paragraphs", "perform", "call", "goto" };

    public static int Run(CommandLine cmd, TextWriter stdout, TextWriter stderr)
    {
        cmd.RejectUnknown("source", "out", "rekt");
        var roots = cmd.All("source").Select(Path.GetFullPath).ToList();
        if (roots.Count == 0)
        {
            throw new UsageException("diff-rekt --procedure needs at least one --source <estate-dir>");
        }

        foreach (var r in roots.Where(r => !Directory.Exists(r)))
        {
            stderr.WriteLine($"error: source directory not found: {r}");
            return 2;
        }

        var baseline = Path.GetFullPath(cmd.Required("rekt"));
        var outDir = Path.GetFullPath(cmd.Required("out"));
        var display = new PathDisplay(roots.Select(r => (r, Path.GetFileName(r))));
        var programs = roots
            .SelectMany(r => CoverageCommand.DiscoverPrograms(r).Select(p => (Display: display.Show(p), Path: p)))
            .OrderBy(p => p.Display, StringComparer.Ordinal)
            .ToList();

        var frontEnd = new CobolFrontEnd(new FrontEndOptions(roots));
        var results = new ProgramDiff[programs.Count];
        Parallel.For(0, programs.Count, i =>
        {
            var (name, path) = programs[i];
            var ast = Path.Combine(baseline, name + ".report", "flow_ast", "flow-ast-" + Path.GetFileName(name) + ".json");
            try
            {
                var sf = Starforge(frontEnd.Process(path));
                var rekt = File.Exists(ast) ? Rekt(ast, File.ReadAllLines(path)) : new Facts();
                results[i] = Compare(name, File.Exists(ast), null, sf, rekt);
            }
            catch (Exception ex)
            {
                results[i] = Compare(name, File.Exists(ast), $"{ex.GetType().Name}: {ex.Message}", new Facts(), new Facts());
            }
        });

        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "procedure-diff.json"), Json(results));
        File.WriteAllText(Path.Combine(outDir, "procedure-diff.md"), Markdown(results), new UTF8Encoding(false));
        var totals = Totals(results);
        stdout.WriteLine($"programs={results.Length} noBaseline={results.Count(r => !r.RektFound)} exceptions={results.Count(r => r.Exception is not null)} " +
            string.Join(' ', Categories.Select(c => $"{c}={totals[c].Matched}/{totals[c].Starforge}:{totals[c].Rekt}~{totals[c].LineMismatches}")));
        stdout.WriteLine($"report: {Path.Combine(outDir, "procedure-diff.md")}");
        return 0;
    }

    // One comparable fact: Key identifies it (multiset semantics), Line/EndLine are compared once keys match.
    internal sealed record Fact(string Key, int? Line, int? EndLine = null, string? Note = null);

    internal sealed class Facts
    {
        private readonly Dictionary<string, List<Fact>> _items = Categories.ToDictionary(c => c, _ => new List<Fact>(), StringComparer.Ordinal);

        public List<Fact> this[string category] => _items[category];
    }

    internal sealed record LineMismatch(string Category, string Key, string Starforge, string Rekt);

    internal sealed record CategoryDiff(int Starforge, int Rekt, IReadOnlyList<Fact> OnlyStarforge, IReadOnlyList<Fact> OnlyRekt, IReadOnlyList<LineMismatch> Lines, IReadOnlyList<Fact> RektLineUnknown, int RektFromText)
    {
        public int Matched => Starforge - OnlyStarforge.Count;
    }

    internal sealed record ProgramDiff(string Program, bool RektFound, string? Exception, IReadOnlyDictionary<string, CategoryDiff> Categories);

    internal sealed record Total(int Starforge, int Rekt, int Matched, int LineMismatches, int RektLineUnknown, int RektFromText);

    internal static Facts Starforge(CompilationResult r)
    {
        var f = new Facts();
        var ts = r.Tokens;
        foreach (var program in r.Syntax.Programs)
        {
            if (program.Procedure is not { } proc)
            {
                continue;
            }

            foreach (var section in proc.AllSections())
            {
                if (section.Name is { } sectionName)
                {
                    f["sections"].Add(new Fact(sectionName.ToUpperInvariant(), section.StartLine, section.EndLine));
                }

                foreach (var p in section.Paragraphs)
                {
                    f["paragraphs"].Add(new Fact(ParagraphKey(section.Name, p.Name), p.StartLine, p.EndLine));
                }

                var sectionFrom = section.Name?.ToUpperInvariant() ?? "(body)";
                AddStatements(f, ts, section.Leading, sectionFrom);
                foreach (var p in section.Paragraphs)
                {
                    AddStatements(f, ts, p.Sentences, p.Name.ToUpperInvariant());
                }
            }
        }

        return f;
    }

    private static void AddStatements(Facts f, TokenStream ts, IEnumerable<Sentence> sentences, string from)
    {
        foreach (var s in sentences.SelectMany(s => s.Statements).SelectMany(Statement.SelfAndNested))
        {
            var line = ts.ProgramLine(s.Span.FirstToken);
            switch (s)
            {
                case PerformStatement { Target: { } performed } perform:
                    f["perform"].Add(new Fact(Edge(from, performed.Name, perform.Thru?.Name), line));
                    break;
                case GoToStatement go:
                    var dep = go.DependingOn is null ? string.Empty : " DEPENDING";
                    if (go.Targets.Count == 0)
                    {
                        f["goto"].Add(new Fact($"{from} -> (altered)", line));
                    }

                    foreach (var t in go.Targets)
                    {
                        f["goto"].Add(new Fact($"{from} -> {t.Name.ToUpperInvariant()}{dep}", line));
                    }

                    break;
                case CallStatement call:
                    var target = call.Program is { } program ? $"'{program}'" : "dynamic " + ProcedureText.Of(call.Target).ToUpperInvariant();
                    f["call"].Add(new Fact($"{from} -> {target}", line));
                    break;
            }
        }
    }

    private static string ParagraphKey(string? section, string paragraph)
        => section is null ? paragraph.ToUpperInvariant() : $"{section.ToUpperInvariant()}/{paragraph.ToUpperInvariant()}";

    private static string Edge(string from, string target, string? thru)
        => thru is null ? $"{from} -> {target.ToUpperInvariant()}" : $"{from} -> {target.ToUpperInvariant()} THRU {thru.ToUpperInvariant()}";

    // REKT originalText is preprocessed text: EXEC blocks become `_DIALECT_ n` placeholders that drop lines and
    // procedure COPY statements are expanded, so lines are found by aligning each text line with the program file.
    private sealed class Anchor
    {
        private readonly string _text;
        private readonly int?[] _map;
        private int _cursor;

        public Anchor(int? start, string text, string[] program, int limit)
        {
            _text = text;
            var lines = text.Split('\n');
            _map = new int?[lines.Length];
            if (start is null)
            {
                return;
            }

            _map[0] = start;
            var at = start.Value;
            for (var i = 1; i < lines.Length; i++)
            {
                var want = Normalize(lines[i]);
                if (want.Length == 0 || lines[i].Contains('\u200b') || want.Contains("_DIALECT_", StringComparison.Ordinal))
                {
                    continue;
                }

                for (var j = at + 1; j <= limit; j++)
                {
                    if (Code(program[j - 1]) is { } code && Normalize(code) == want)
                    {
                        _map[i] = at = j;
                        break;
                    }
                }
            }

            End = EndLine(lines, program, limit);
        }

        public int? End { get; }

        public bool Anchored => _map[0] is not null;

        // A childless node's text is consumed whole so a later identical statement cannot match text nested inside it.
        public int? Locate(string statementText, bool leaf)
        {
            if (_map[0] is null || statementText.Length == 0)
            {
                return null;
            }

            var at = _text.IndexOf(statementText, _cursor, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }

            _cursor = leaf ? at + statementText.Length : at + 1;
            _located = at;
            return LineAt(at);
        }

        private int _located;

        // Offset in the anchor text of position index within the statement text located last.
        public int Offset(string statementText, int index) => _located + index;

        public int? LineAt(int offset) => _map[_text.AsSpan(0, offset).Count('\n')];

        // The last aligned line; when the node ends in EXEC placeholders, the END-EXEC of each remaining block and a
        // period that follows on its own line (REKT folds that period into the placeholder).
        private int? EndLine(string[] lines, string[] program, int limit)
        {
            var last = Array.FindLastIndex(_map, m => m is not null);
            var pending = lines.Skip(last + 1).SelectMany(l => DialectPlaceholder().Matches(l)).Select(m => m.Value).Distinct().Count();
            if (pending == 0)
            {
                return _map[last];
            }

            var end = _map[last]!.Value;
            for (var j = end + 1; pending > 0 && j <= limit; j++)
            {
                if (Code(program[j - 1]) is { } code && code.Contains("END-EXEC", StringComparison.OrdinalIgnoreCase))
                {
                    end = j;
                    pending--;
                }
            }

            if (pending > 0)
            {
                return null;
            }

            if (Code(program[end - 1])!.Contains('.'))
            {
                return end;
            }

            for (var j = end + 1; j <= limit; j++)
            {
                if (Code(program[j - 1]) is { } code && code.Trim() is { Length: > 0 } t)
                {
                    return t.StartsWith('.') ? j : end;
                }
            }

            return end;
        }

        private static string Normalize(string line) => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        private static string? Code(string line)
            => line.Length < 8 || line[6] is '*' or '/' ? null : line.Length > 72 ? line[7..72] : line[7..];
    }

    internal static Facts Rekt(string astPath, string[] programLines)
    {
        var f = new Facts();
        var headers = Headers(programLines);
        var starts = headers.Values.SelectMany(q => q).Order().ToList();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(astPath));
        WalkRekt(doc.RootElement, f, new RektScan(headers, starts, programLines), section: null, from: "(body)", anchor: null);
        return f;
    }

    private sealed record RektScan(Dictionary<string, Queue<int>> Headers, List<int> Starts, string[] Program)
    {
        // A node's text cannot extend past the next header (a section's past the next section header).
        public int Limit(int? start, bool section)
        {
            if (start is null)
            {
                return Program.Length;
            }

            var next = Starts.FirstOrDefault(s => s > start && (!section || Headers.Any(h => h.Key.StartsWith("S:", StringComparison.Ordinal) && h.Value.Contains(s))));
            return next == 0 ? Program.Length : next - 1;
        }
    }

    private static void WalkRekt(JsonElement n, Facts f, RektScan scan, string? section, string from, Anchor? anchor)
    {
        if (n.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = Str(n, "type");
        var text = Str(n, "originalText");
        var name = Str(n, "name").ToUpperInvariant();
        switch (type)
        {
            case "PROCEDURE_DIVISION_BODY":
            {
                var start = BodyStart(scan.Program, text);
                anchor = new Anchor(start, text, scan.Program, start is null ? scan.Program.Length : scan.Starts.FirstOrDefault(s => s > start) is var n0 and > 0 ? n0 - 1 : scan.Program.Length);
                break;
            }

            case "SECTION":
            {
                var start = Take(scan, "S:" + name);
                anchor = new Anchor(start, text, scan.Program, scan.Limit(start, section: true));
                f["sections"].Add(new Fact(name, start, anchor.End, start is null ? "header not in program file" : null));
                section = name;
                from = name;
                break;
            }

            case "PARAGRAPH":
            {
                var start = Take(scan, "P:" + name);
                anchor = new Anchor(start, text, scan.Program, scan.Limit(start, section: false));
                f["paragraphs"].Add(new Fact(section is null ? name : $"{section}/{name}", start, anchor.End,
                    start is null ? "header not in program file" : null));
                from = name;
                break;
            }

            case "PERFORM":
            {
                var line = anchor?.Locate(text, !HasChildren(n));
                if (PerformTarget(text) is { } t)
                {
                    f["perform"].Add(new Fact(Edge(from, t.Target, t.Thru), line, Note: Why(anchor, line, null)));
                }
                else if (!HasChildren(n))
                {
                    ScanNested(f, from, text, anchor, line, skipFirst: true, "inline-perform text");
                }

                break;
            }

            case "GOTO":
            {
                var line = anchor?.Locate(text, leaf: true);
                foreach (var key in GoToKeys(from, text, 0))
                {
                    f["goto"].Add(new Fact(key, line, Note: Why(anchor, line, null)));
                }

                break;
            }

            case "CALL":
            {
                var line = anchor?.Locate(text, !HasChildren(n));
                f["call"].Add(new Fact($"{from} -> {CallTarget(text)}", line, Note: Why(anchor, line, null)));
                ScanNested(f, from, text, anchor, line, skipFirst: true, "call text");
                break;
            }

            // REKT keeps no child nodes for EVALUATE branches or for statements nested in READ/WRITE/... handlers;
            // their originalText is all it has, so nested PERFORM/GO TO/CALL are harvested from it.
            case "EVALUATE" or "GENERIC_STATEMENT" when !HasChildren(n):
                ScanNested(f, from, text, anchor, anchor?.Locate(text, leaf: true), skipFirst: false, type == "EVALUATE" ? "evaluate text" : "generic-statement text");
                break;
        }

        if (n.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in children.EnumerateArray())
            {
                WalkRekt(c, f, scan, section, from, anchor);
            }
        }
    }

    private static string? Why(Anchor? anchor, int? line, string? note)
    {
        if (line is not null)
        {
            return note;
        }

        var why = anchor is null || !anchor.Anchored ? "enclosing header not in program file" : "statement text not aligned";
        return note is null ? why : $"{note}; {why}";
    }

    private static bool HasChildren(JsonElement n)
        => n.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0;

    private static void ScanNested(Facts f, string from, string text, Anchor? anchor, int? line, bool skipFirst, string note)
    {
        // Blank out literals so words inside them are not mistaken for verbs; offsets stay aligned with text.
        var bare = Literal().Replace(text.ToUpperInvariant(), m => new string(' ', m.Length));
        foreach (Match m in Verb().Matches(bare))
        {
            if (skipFirst && m.Index == bare.Length - bare.TrimStart().Length)
            {
                continue;
            }

            var at = line is null ? null : anchor!.LineAt(anchor.Offset(text, m.Index));
            switch (m.Value)
            {
                case "PERFORM" when PerformTarget(bare[m.Index..]) is { } t:
                    f["perform"].Add(new Fact(Edge(from, t.Target, t.Thru), at, Note: Why(anchor, at, note)));
                    break;
                case "GO":
                    foreach (var key in GoToKeys(from, bare, m.Index))
                    {
                        f["goto"].Add(new Fact(key, at, Note: Why(anchor, at, note)));
                    }

                    break;
                case "CALL":
                    f["call"].Add(new Fact($"{from} -> {CallTarget(text[m.Index..])}", at, Note: Why(anchor, at, note)));
                    break;
            }
        }
    }

    // GO [TO] p1 [p2 ... DEPENDING ON x]: without DEPENDING only the first name is a target; what follows it is the
    // next statement.
    private static IEnumerable<string> GoToKeys(string from, string text, int index)
    {
        var words = Words(text[index..]);
        var first = words.Count > 1 && words[1] == "TO" ? 2 : 1;
        var names = words.Skip(first).TakeWhile(w => !StatementVerbs.Contains(w) && w != "DEPENDING").ToList();
        var depending = first + names.Count < words.Count && words[first + names.Count] == "DEPENDING";
        if (names.Count == 0)
        {
            return new[] { $"{from} -> (altered)" };
        }

        return (depending ? names : names.Take(1)).Select(t => $"{from} -> {t}{(depending ? " DEPENDING" : string.Empty)}");
    }

    private static readonly HashSet<string> StatementVerbs = new(StringComparer.Ordinal)
    {
        "ACCEPT", "ADD", "ALTER", "CALL", "CANCEL", "CLOSE", "COMPUTE", "CONTINUE", "DELETE", "DISPLAY", "DIVIDE", "ELSE",
        "END-IF", "END-EVALUATE", "END-PERFORM", "END-READ", "EVALUATE", "EXEC", "EXIT", "GO", "GOBACK", "IF", "INITIALIZE",
        "INSPECT", "MERGE", "MOVE", "MULTIPLY", "NEXT", "NOT", "OPEN", "PERFORM", "READ", "RELEASE", "RETURN", "REWRITE",
        "SEARCH", "SET", "SORT", "START", "STOP", "STRING", "SUBTRACT", "UNSTRING", "WHEN", "WRITE", "_DIALECT_",
    };

    // Out-of-line target of a PERFORM statement text; null for an inline PERFORM.
    internal static (string Target, string? Thru)? PerformTarget(string text)
    {
        var w = Words(text);
        if (w.Count < 2 || w[1] is "UNTIL" or "VARYING" or "WITH" or "TEST" || (w.Count > 2 && w[2] == "TIMES"))
        {
            return null;
        }

        var next = 2;
        if (w.Count > 3 && w[2] is "OF" or "IN")
        {
            next = 4;
        }

        return w.Count > next + 1 && w[next] is "THRU" or "THROUGH" ? (w[1], w[next + 1]) : (w[1], null);
    }

    internal static string CallTarget(string text)
    {
        var rest = text.TrimStart()[4..].TrimStart();
        if (rest.Length > 0 && rest[0] is '\'' or '"')
        {
            var close = rest.IndexOf(rest[0], 1);
            return $"'{(close < 0 ? rest[1..] : rest[1..close])}'";
        }

        var words = Words(text);
        return "dynamic " + (words.Count > 1 ? words[1] : string.Empty);
    }

    private static List<string> Words(string text)
        => WordPattern().Matches(text.ToUpperInvariant()).Select(m => m.Value.TrimEnd('.')).Where(w => w.Length > 0).ToList();

    private static int? Take(RektScan scan, string key)
        => scan.Headers.TryGetValue(key, out var q) && q.Count > 0 ? q.Dequeue() : null;

    // Section and paragraph headers of the program file's PROCEDURE DIVISION, in order: Area A words followed by
    // SECTION or by a period. Copybook-expanded headers are not in the program file and stay unlocated.
    internal static Dictionary<string, Queue<int>> Headers(string[] lines)
    {
        var result = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        var inProcedure = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length < 8 || line[6] is '*' or '/' or '-')
            {
                continue;
            }

            var area = line.Length > 72 ? line[7..72] : line[7..];
            if (!inProcedure)
            {
                inProcedure = ProcedureHeader().IsMatch(area);
                continue;
            }

            if (area.Length == 0 || area.AsSpan(0, Math.Min(4, area.Length)).Trim().IsEmpty)
            {
                continue;
            }

            var m = HeaderPattern().Match(area);
            var name = m.Success ? m.Groups[1].Value : null;
            var isSection = m.Success && m.Groups[2].Success;
            if (!m.Success && BareName().Match(area) is { Success: true } bare && NextCode(lines, i) is { } next)
            {
                // The header's period (or SECTION) may sit on the following line.
                var rest = next.TrimStart();
                isSection = rest.StartsWith("SECTION", StringComparison.OrdinalIgnoreCase);
                name = isSection || rest.StartsWith('.') ? bare.Groups[1].Value : null;
            }

            if (name is null)
            {
                continue;
            }

            var key = (isSection ? "S:" : "P:") + name.ToUpperInvariant();
            if (!result.TryGetValue(key, out var q))
            {
                result[key] = q = new Queue<int>();
            }

            q.Enqueue(i + 1);
        }

        return result;
    }

    private static string Str(JsonElement n, string name)
        => n.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    [GeneratedRegex(@"[A-Z0-9_][A-Z0-9_-]*\.?|'[^']*'|""[^""]*""")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"(?<![A-Z0-9-])(PERFORM|GO|CALL)(?![A-Z0-9-])")]
    private static partial Regex Verb();

    [GeneratedRegex(@"'[^'\n]*'?|""[^""\n]*""?")]
    private static partial Regex Literal();

    [GeneratedRegex(@"_DIALECT_ \d+")]
    private static partial Regex DialectPlaceholder();

    [GeneratedRegex(@"^\s{0,3}([A-Za-z0-9][A-Za-z0-9-]*)\s*$")]
    private static partial Regex BareName();

    private static string? NextCode(string[] lines, int i)
    {
        for (var j = i + 1; j < lines.Length; j++)
        {
            var line = lines[j];
            if (line.Length < 8 || line[6] is '*' or '/')
            {
                continue;
            }

            var area = line.Length > 72 ? line[7..72] : line[7..];
            if (area.Trim().Length > 0)
            {
                return area;
            }
        }

        return null;
    }

    // The program-file line holding the first line of the procedure body text, searched after the division header.
    private static int? BodyStart(string[] lines, string text)
    {
        var want = string.Join(' ', text.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var inProcedure = false;
        for (var i = 0; i < lines.Length && want.Length > 0; i++)
        {
            var line = lines[i];
            if (line.Length < 8 || line[6] is '*' or '/')
            {
                continue;
            }

            var area = line.Length > 72 ? line[7..72] : line[7..];
            if (!inProcedure)
            {
                inProcedure = ProcedureHeader().IsMatch(area);
                continue;
            }

            if (string.Join(' ', area.Split(' ', StringSplitOptions.RemoveEmptyEntries)) == want)
            {
                return i + 1;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\bPROCEDURE\s+DIVISION\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProcedureHeader();

    [GeneratedRegex(@"^\s{0,3}([A-Za-z0-9][A-Za-z0-9-]*)(?:\s+(SECTION)\b[^.]*\.|\s*\.)", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderPattern();

    internal static ProgramDiff Compare(string program, bool rektFound, string? exception, Facts sf, Facts rekt)
    {
        var categories = new Dictionary<string, CategoryDiff>(StringComparer.Ordinal);
        foreach (var c in Categories)
        {
            var pending = rekt[c].Select((x, i) => (Index: i, Fact: x)).GroupBy(x => x.Fact.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new Queue<(int Index, Fact Fact)>(g), StringComparer.Ordinal);
            var onlySf = new List<Fact>();
            var lines = new List<LineMismatch>();
            var unpaired = new List<Fact>();

            // Same-key facts pair on equal lines first so that one unlocated or reordered REKT fact does not shift the rest.
            foreach (var s in sf[c])
            {
                if (pending.TryGetValue(s.Key, out var q) && q.FirstOrDefault(x => x.Fact.Line == s.Line && x.Fact.EndLine == s.EndLine) is { Fact: not null } same)
                {
                    pending[s.Key] = new Queue<(int Index, Fact Fact)>(q.Where(x => x.Index != same.Index));
                }
                else
                {
                    unpaired.Add(s);
                }
            }

            foreach (var s in unpaired)
            {
                if (!pending.TryGetValue(s.Key, out var q) || q.Count == 0)
                {
                    onlySf.Add(s);
                    continue;
                }

                var r = q.Dequeue().Fact;
                if (r.Line is not null && (r.Line != s.Line || (r.EndLine is not null && r.EndLine != s.EndLine)))
                {
                    lines.Add(new LineMismatch(c, s.Key, Lines(s), Lines(r)));
                }
            }

            var onlyRekt = pending.Values.SelectMany(q => q).OrderBy(x => x.Index).Select(x => x.Fact).ToList();
            categories[c] = new CategoryDiff(sf[c].Count, rekt[c].Count, onlySf, onlyRekt, lines, rekt[c].Where(r => r.Line is null).ToList(),
                rekt[c].Count(r => r.Note?.Contains(" text", StringComparison.Ordinal) == true));
        }

        return new ProgramDiff(program, rektFound, exception, categories);
    }

    private static string Lines(Fact f) => f.EndLine is null ? $"{f.Line}" : $"{f.Line}-{f.EndLine}";

    internal static Dictionary<string, Total> Totals(IReadOnlyList<ProgramDiff> results)
        => Categories.ToDictionary(c => c, c => new Total(
            results.Sum(r => r.Categories[c].Starforge),
            results.Sum(r => r.Categories[c].Rekt),
            results.Sum(r => r.Categories[c].Matched),
            results.Sum(r => r.Categories[c].Lines.Count),
            results.Sum(r => r.Categories[c].RektLineUnknown.Count),
            results.Sum(r => r.Categories[c].RektFromText)), StringComparer.Ordinal);

    private static byte[] Json(IReadOnlyList<ProgramDiff> results)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("milestone", "M3");
            w.WriteNumber("programs", results.Count);
            w.WriteNumber("noBaseline", results.Count(r => !r.RektFound));
            w.WriteNumber("exceptions", results.Count(r => r.Exception is not null));
            w.WriteStartObject("totals");
            foreach (var (c, t) in Totals(results))
            {
                w.WriteStartObject(c);
                w.WriteNumber("starforge", t.Starforge);
                w.WriteNumber("rekt", t.Rekt);
                w.WriteNumber("matched", t.Matched);
                w.WriteNumber("onlyStarforge", t.Starforge - t.Matched);
                w.WriteNumber("onlyRekt", t.Rekt - t.Matched);
                w.WriteNumber("lineMismatches", t.LineMismatches);
                w.WriteNumber("rektLineUnknown", t.RektLineUnknown);
                w.WriteNumber("rektFromText", t.RektFromText);
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteStartArray("programs");
            foreach (var r in results)
            {
                w.WriteStartObject();
                w.WriteString("program", r.Program);
                w.WriteBoolean("rektFound", r.RektFound);
                if (r.Exception is not null)
                {
                    w.WriteString("exception", r.Exception);
                }

                foreach (var (c, d) in r.Categories)
                {
                    w.WriteStartObject(c);
                    w.WriteNumber("starforge", d.Starforge);
                    w.WriteNumber("rekt", d.Rekt);
                    w.WriteNumber("matched", d.Matched);
                    WriteFacts(w, "onlyStarforge", d.OnlyStarforge);
                    WriteFacts(w, "onlyRekt", d.OnlyRekt);
                    WriteFacts(w, "rektLineUnknown", d.RektLineUnknown);
                    if (d.Lines.Count > 0)
                    {
                        w.WriteStartArray("lineMismatches");
                        foreach (var l in d.Lines)
                        {
                            w.WriteStartObject();
                            w.WriteString("key", l.Key);
                            w.WriteString("starforge", l.Starforge);
                            w.WriteString("rekt", l.Rekt);
                            w.WriteEndObject();
                        }

                        w.WriteEndArray();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    private static void WriteFacts(Utf8JsonWriter w, string name, IReadOnlyList<Fact> facts)
    {
        if (facts.Count == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (var f in facts)
        {
            w.WriteStartObject();
            w.WriteString("key", f.Key);
            if (f.Line is not null)
            {
                w.WriteString("lines", Lines(f));
            }

            if (f.Note is not null)
            {
                w.WriteString("note", f.Note);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static string Markdown(IReadOnlyList<ProgramDiff> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Starforge M3 procedure diff vs REKT");
        sb.AppendLine();
        sb.AppendLine("Facts per program: sections and paragraphs (name, start-end line), PERFORM edges (enclosing paragraph -> target [THRU]),");
        sb.AppendLine("CALL targets ('literal' = static, `dynamic NAME`) and GO TO targets, each with the statement line.");
        sb.AppendLine("REKT flow_ast has no line numbers: a REKT header is located in the program file and every line of its `originalText` is aligned");
        sb.AppendLine("with the program file (REKT text is preprocessed: EXEC blocks become shorter `_DIALECT_ n` placeholders, procedure COPYs are expanded).");
        sb.AppendLine("`Line mismatches` counts matched facts whose lines differ; `REKT line unknown` counts REKT facts that could not be located (listed with the reason in procedure-diff.json).");
        sb.AppendLine("`REKT from text` counts REKT facts recovered from statement text because REKT keeps no child nodes for EVALUATE branches,");
        sb.AppendLine("statements nested in READ/WRITE/... handlers, or some inline PERFORM/CALL bodies.");
        sb.AppendLine();
        sb.AppendLine($"Programs: {results.Count}, no REKT baseline: {results.Count(r => !r.RektFound)}, exceptions: {results.Count(r => r.Exception is not null)}");
        sb.AppendLine();
        sb.AppendLine("| Category | Starforge | REKT | Matched | Only Starforge | Only REKT | Line mismatches | REKT line unknown | REKT from text |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (c, t) in Totals(results))
        {
            sb.AppendLine($"| {c} | {t.Starforge} | {t.Rekt} | {t.Matched} | {t.Starforge - t.Matched} | {t.Rekt - t.Matched} | {t.LineMismatches} | {t.RektLineUnknown} | {t.RektFromText} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Differences by program");
        foreach (var r in results)
        {
            var rows = r.Categories.SelectMany(kv =>
                    kv.Value.OnlyStarforge.Select(f => $"| {kv.Key} | only Starforge | `{f.Key}` | {Lines(f)} | |")
                        .Concat(kv.Value.OnlyRekt.Select(f => $"| {kv.Key} | only REKT | `{f.Key}` | | {string.Join(' ', new[] { Lines(f), f.Note is null ? string.Empty : $"({f.Note})" }.Where(x => x.Length > 0))} |"))
                        .Concat(kv.Value.Lines.Select(l => $"| {kv.Key} | lines differ | `{l.Key}` | {l.Starforge} | {l.Rekt} |")))
                .ToList();
            if (rows.Count == 0 && r.RektFound && r.Exception is null)
            {
                continue;
            }

            sb.AppendLine();
            sb.AppendLine($"### {r.Program}");
            if (!r.RektFound)
            {
                sb.AppendLine("No REKT flow_ast.");
            }

            if (r.Exception is not null)
            {
                sb.AppendLine($"Exception: {r.Exception}");
            }

            if (rows.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("| Category | Kind | Fact | Starforge lines | REKT lines |");
                sb.AppendLine("|---|---|---|---|---|");
                rows.ForEach(row => sb.AppendLine(row));
            }
        }

        return sb.ToString();
    }
}
