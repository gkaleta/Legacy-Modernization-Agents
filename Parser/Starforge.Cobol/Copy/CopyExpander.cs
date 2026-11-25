using System.Collections.Concurrent;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Copy;

// Thread-safe cache of copybook readings shared across programs of one run.
public sealed class SourceCache
{
    private readonly ConcurrentDictionary<(string Path, bool Debug), Lazy<LogicalSource>> _map = new();

    public LogicalSource GetCopybook(string path, bool debuggingMode)
        => _map.GetOrAdd(
            (path, debuggingMode),
            key => new Lazy<LogicalSource>(() => SourceReader.Read(SourceFile.Load(key.Path), new SourceReaderOptions(key.Debug, IsProgram: false)))).Value;
}

public sealed record CopyExpansionOptions(int MaxDepth = 50, bool DebuggingMode = false, Dialect.DialectRules? Rules = null);

internal sealed class CopyExpander
{
    private readonly CopybookResolver _resolver;
    private readonly SourceCache _cache;
    private readonly TokenAccounting _accounting;
    private readonly List<Diagnostic> _diagnostics;
    private readonly List<CopyInclusion> _inclusions;
    private readonly CopyExpansionOptions _options;
    private readonly List<string> _active = new();
    private string _programPath = string.Empty;

    // Places a taught copy alias (e.g. ++INCLUDE) was expanded.
    public List<Dialect.TaughtUse> TaughtUses { get; } = new();

    public CopyExpander(CopybookResolver resolver, SourceCache cache, TokenAccounting accounting, List<Diagnostic> diagnostics, List<CopyInclusion> inclusions, CopyExpansionOptions options)
    {
        _resolver = resolver;
        _cache = cache;
        _accounting = accounting;
        _diagnostics = diagnostics;
        _inclusions = inclusions;
        _options = options;
    }

    public OriginText ExpandProgram(SourceUnit program)
    {
        _programPath = program.Path;
        var builder = new OriginTextBuilder(program.Source.Text.Length * 2);
        _active.Add(Path.GetFullPath(program.Path));
        ExpandUnit(program, builder);
        _active.RemoveAt(_active.Count - 1);
        return builder.Build();
    }

    private void ExpandUnit(SourceUnit unit, OriginTextBuilder output)
    {
        var source = OriginText.ForUnit(unit.Id, unit.Source.Text);
        var text = source.Text;
        var words = TextWordScanner.Scan(text, 0, text.Length);
        var cursor = 0;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.Kind != TextWordKind.Word)
            {
                continue;
            }

            if (ReplacingParser.Is(text, w, "EXEC") || ReplacingParser.Is(text, w, "EXECUTE"))
            {
                var j = i + 1;
                while (j < words.Count && !ReplacingParser.Is(text, words[j], "END-EXEC"))
                {
                    j++;
                }

                if (j < words.Count && j == i + 4
                    && ReplacingParser.Is(text, words[i + 1], "SQL")
                    && ReplacingParser.Is(text, words[i + 2], "INCLUDE")
                    && words[i + 3].Kind is TextWordKind.Word or TextWordKind.Literal)
                {
                    var endWord = j + 1 < words.Count && words[j + 1].Kind == TextWordKind.Period ? j + 1 : j;
                    var name = text.Substring(words[i + 3].Start, words[i + 3].Length);
                    output.Append(source, cursor, w.Start);
                    Include(unit, output, CopyStatementKind.SqlInclude, w.Start, words[endWord].End, name, null, false, Array.Empty<ReplacingPair>());
                    cursor = words[endWord].End;
                    i = endWord;
                    continue;
                }

                i = Math.Min(j, words.Count - 1);
                continue;
            }

            if (ReplacingParser.Is(text, w, "REPLACE") && ReplaceStage.LooksLikeReplace(text, words, i))
            {
                var stmt = ReplaceStage.Parse(source, words, i);
                i = stmt.EndWord;
                continue;
            }

            if (_options.Rules is { IsEmpty: false } rules && rules.CopyAlias(text.Substring(w.Start, w.Length)) is { } alias
                && i + 1 < words.Count && words[i + 1].Kind is TextWordKind.Word or TextWordKind.Literal
                && unit.Source.RangeOf(words[i + 1].Start, words[i + 1].End).StartLine == unit.Source.RangeOf(w.Start, w.End).StartLine)
            {
                i = ExpandAlias(unit, source, words, i, output, ref cursor, alias);
                continue;
            }

            if (ReplacingParser.Is(text, w, "-COPY") && i + 1 < words.Count && words[i + 1].Kind == TextWordKind.Word)
            {
                i = ExpandDashCopy(unit, source, words, i, output, ref cursor);
                continue;
            }

            if (!ReplacingParser.Is(text, w, "COPY"))
            {
                continue;
            }

            if (IsIdmsCopy(text, words, i))
            {
                i = ExpandIdmsCopy(unit, source, words, i, output, ref cursor);
                continue;
            }

            var copy = ParseCopy(source, words, i);
            output.Append(source, cursor, copy.Start);
            if (copy.Error is { Code: "copy-period-expected" })
            {
                // The compiler would flag this, but the intent is unambiguous, so the copybook is still included.
                _diagnostics.Add(Diagnostic.Create("copy-period-expected", DiagnosticSeverity.Warning, copy.Error.Message, unit.Source.RangeOf(copy.Start, copy.End), string.Empty));
                Include(unit, output, CopyStatementKind.Copy, copy.Start, copy.End, copy.Name, copy.Library, copy.Suppress, copy.Pairs);
            }
            else if (copy.Error is not null)
            {
                var range = unit.Source.RangeOf(copy.Start, copy.End);
                _diagnostics.Add(Diagnostic.Create("malformed-copy-statement", DiagnosticSeverity.Error, $"{copy.Error.Message} ({copy.Error.Code})", range, copy.Error.Code));
                _accounting.Mark(unit.Id, copy.Start, copy.End, CoverageClass.Unsupported, "malformed-copy-statement");
                _inclusions.Add(new CopyInclusion(CopyStatementKind.Copy, copy.Name, copy.Library, copy.Suppress, copy.Pairs.Count, unit.Id, -1, range, CopyStatus.Malformed, null, null, Array.Empty<string>()));
            }
            else
            {
                Include(unit, output, CopyStatementKind.Copy, copy.Start, copy.End, copy.Name, copy.Library, copy.Suppress, copy.Pairs);
            }

            cursor = copy.End;
            i = copy.EndWord;
        }

        output.Append(source, cursor, text.Length);
    }

    // A taught include word: `<word> name [.]` on one line, expanded like a plain COPY name (no OF, SUPPRESS or REPLACING).
    private int ExpandAlias(SourceUnit unit, OriginText source, List<TextWord> words, int i, OriginTextBuilder output, ref int cursor, Dialect.DialectRule alias)
    {
        var text = source.Text;
        var start = words[i].Start;
        var line = unit.Source.RangeOf(start, words[i].End).StartLine;
        var last = i + 1;
        if (last + 1 < words.Count && words[last + 1].Kind == TextWordKind.Period
            && unit.Source.RangeOf(words[last + 1].Start, words[last + 1].End).StartLine == line)
        {
            last++;
        }

        var end = words[last].End;
        output.Append(source, cursor, start);
        cursor = end;
        TaughtUses.Add(new Dialect.TaughtUse(alias.Id, Dialect.DialectRuleKind.CopyAlias, alias.Word!, unit.Source.RangeOf(start, end)));
        Include(unit, output, CopyStatementKind.Copy, start, end, text.Substring(words[i + 1].Start, words[i + 1].Length), null, false, Array.Empty<ReplacingPair>());
        return last;
    }

    // Site precompilers accept `-COPY NAME [-PRE PREFIX-][.]`; plain forms behave like COPY.
    // The -PRE renaming rule is site-specific, so a found copybook is flagged instead of guessed.
    private int ExpandDashCopy(SourceUnit unit, OriginText source, List<TextWord> words, int i, OriginTextBuilder output, ref int cursor)
    {
        var text = source.Text;
        var start = words[i].Start;
        var name = text.Substring(words[i + 1].Start, words[i + 1].Length);
        var last = i + 1;
        string? prefix = null;
        if (last + 2 < words.Count && ReplacingParser.Is(text, words[last + 1], "-PRE") && words[last + 2].Kind == TextWordKind.Word)
        {
            prefix = text.Substring(words[last + 2].Start, words[last + 2].Length);
            last += 2;
        }

        if (last + 1 < words.Count && words[last + 1].Kind == TextWordKind.Period)
        {
            last++;
        }

        var end = words[last].End;
        output.Append(source, cursor, start);
        cursor = end;
        var normalized = CopybookResolver.NormalizeName(name);
        if (prefix is not null && _resolver.Resolve(normalized, null, unit.Path, _programPath) is { Found: true } found)
        {
            var range = unit.Source.RangeOf(start, end);
            _diagnostics.Add(Diagnostic.Create("copy-prefix-unsupported", DiagnosticSeverity.Error,
                $"-COPY {normalized.ToUpperInvariant()} -PRE {prefix} needs this site's prefix rule; the copybook was not expanded", range, normalized.ToUpperInvariant()));
            _accounting.Mark(unit.Id, start, end, CoverageClass.Unsupported, "copy-prefix-unsupported");
            _inclusions.Add(new CopyInclusion(CopyStatementKind.Copy, normalized, null, false, 0, unit.Id, -1, range, CopyStatus.Malformed, found.Path, found.Stage, found.OtherCandidates));
            return last;
        }

        Include(unit, output, CopyStatementKind.Copy, start, end, name, null, false, Array.Empty<ReplacingPair>());
        return last;
    }

    // CA IDMS precompiler directive (Broadcom "DML Reference for COBOL", Precompiler-Directive Statements):
    //   [level] COPY IDMS {member | RECORD rec | FILE f | MODULE m | LR lr | MAP map | MAP-CONTROL map}
    //       [VERSION n] [REDEFINES rec] [SIZE IS n] .
    // The precompiler copies the member from the data dictionary; here the member is an estate copybook (a dictionary
    // punch or precompiler listing). COPY IDMS DEPT. or COPY IDMS OF LIB. stay ordinary COPY of a copybook named IDMS.
    private static readonly HashSet<string> IdmsNamedForms = new(StringComparer.OrdinalIgnoreCase) { "RECORD", "FILE", "MODULE", "LR", "MAP", "MAP-CONTROL" };
    private static readonly HashSet<string> IdmsClauseWords = new(StringComparer.OrdinalIgnoreCase) { "VERSION", "REDEFINES", "SIZE" };

    private static bool IsIdmsCopy(string text, List<TextWord> words, int i)
        => i + 2 < words.Count && ReplacingParser.Is(text, words[i + 1], "IDMS") && words[i + 2].Kind == TextWordKind.Word
            && !ReplacingParser.Is(text, words[i + 2], "OF") && !ReplacingParser.Is(text, words[i + 2], "IN")
            && !ReplacingParser.Is(text, words[i + 2], "SUPPRESS") && !ReplacingParser.Is(text, words[i + 2], "REPLACING");

    private int ExpandIdmsCopy(SourceUnit unit, OriginText source, List<TextWord> words, int i, OriginTextBuilder output, ref int cursor)
    {
        var text = source.Text;
        string Word(int k) => text.Substring(words[k].Start, words[k].Length);
        int? level = null;
        if (i > 0 && words[i - 1].Start >= cursor && words[i - 1].Kind == TextWordKind.Word && words[i - 1].Length <= 2
            && Word(i - 1).All(char.IsAsciiDigit) && int.Parse(Word(i - 1)) is >= 1 and <= 48 and var lv)
        {
            level = lv;
        }

        var j = i + 2;
        var memberWord = j;
        if (IdmsNamedForms.Contains(Word(j)) && j + 1 < words.Count && words[j + 1].Kind == TextWordKind.Word && !IdmsClauseWords.Contains(Word(j + 1)))
        {
            memberWord = ++j;
        }

        j++;
        string? redefines = null;
        while (j + 1 < words.Count && words[j].Kind == TextWordKind.Word && IdmsClauseWords.Contains(Word(j)))
        {
            if (ReplacingParser.Is(text, words[j], "REDEFINES"))
            {
                redefines = Word(j + 1);
            }

            j += ReplacingParser.Is(text, words[j], "SIZE") && ReplacingParser.Is(text, words[j + 1], "IS") ? 3 : 2;
        }

        var period = j < words.Count && words[j].Kind == TextWordKind.Period;
        var last = Math.Min(period ? j : j - 1, words.Count - 1);
        var member = Word(memberWord);
        var header = level is { } l ? RecordHeader(unit, member, l, redefines, unit.Source.RangeOf(words[i].Start, words[last].End)) : null;

        // A synthesized header keeps the level number as real text, so only COPY IDMS ... is the directive.
        var start = level is not null && header is null ? words[i - 1].Start : words[i].Start;
        output.Append(source, cursor, start);
        if (!period)
        {
            _diagnostics.Add(Diagnostic.Create("copy-period-expected", DiagnosticSeverity.Warning, "COPY IDMS statement must end with a separator period", unit.Source.RangeOf(start, words[last].End), string.Empty));
        }

        Include(unit, output, CopyStatementKind.Copy, start, words[last].End, member, null, false, Array.Empty<ReplacingPair>(), idms: true, prefix: header);
        cursor = words[last].End;
        return last;
    }

    // "If you specify a level number, the first level of code is copied to the specified level. All other levels
    // adjust accordingly." Dictionary record descriptions start with the record's own 01 entry. When the estate
    // copybook holds only the record's fields, the record entry is rebuilt from the level and record name written in
    // the COPY IDMS statement itself; level shifting of a copybook that already has its record entry is not done.
    private string? RecordHeader(SourceUnit unit, string member, int level, string? redefines, SourceRange range)
    {
        var resolution = _resolver.Resolve(CopybookResolver.NormalizeName(member), null, unit.Path, _programPath);
        if (!resolution.Found)
        {
            return null;
        }

        string body;
        try
        {
            body = _cache.GetCopybook(resolution.Path!, _options.DebuggingMode).Text;
        }
        catch (IOException)
        {
            return null;
        }

        var head = TextWordScanner.Scan(body, 0, Math.Min(body.Length, 512));
        if (head.Count == 0 || head[0].Kind != TextWordKind.Word || !body.Substring(head[0].Start, head[0].Length).All(char.IsAsciiDigit))
        {
            return null;
        }

        var first = int.Parse(body.Substring(head[0].Start, head[0].Length));
        var hasRecordEntry = head.Count > 1 && string.Equals(body.Substring(head[1].Start, head[1].Length), member, StringComparison.OrdinalIgnoreCase);
        if (!hasRecordEntry && first > level)
        {
            return $" {member.ToUpperInvariant()}{(redefines is null ? string.Empty : $" REDEFINES {redefines.ToUpperInvariant()}")}.";
        }

        if (first != level || redefines is not null)
        {
            _diagnostics.Add(Diagnostic.Create("copy-idms-level-unsupported", DiagnosticSeverity.Warning,
                $"COPY IDMS {member.ToUpperInvariant()} asks for level {level:00}{(redefines is null ? string.Empty : $" REDEFINES {redefines.ToUpperInvariant()}")}; the copybook is included with its own levels", range, member.ToUpperInvariant()));
        }

        return null;
    }

    private void Include(SourceUnit parent, OriginTextBuilder output, CopyStatementKind kind, int start, int end, string rawName, string? library, bool suppress, IReadOnlyList<ReplacingPair> pairs, bool idms = false, string? prefix = null)
    {
        var range = parent.Source.RangeOf(start, end);
        var name = CopybookResolver.NormalizeName(rawName);
        var resolution = _resolver.Resolve(name, library, parent.Path, _programPath);
        var label = name.ToUpperInvariant();

        void Fail(CopyStatus status, string code, string message)
        {
            _diagnostics.Add(Diagnostic.Create(code, DiagnosticSeverity.Error, message, range, label));
            _accounting.Mark(parent.Id, start, end, CoverageClass.Unresolved, $"{code}:{label}");
            _inclusions.Add(new CopyInclusion(kind, name, library, suppress, pairs.Count, parent.Id, -1, range, status, resolution.Path, resolution.Stage, resolution.OtherCandidates, idms));
        }

        if (!resolution.Found)
        {
            Fail(CopyStatus.Missing, "missing-copybook", $"Copybook {name} not found in the program folder, estate folders or .preprocessed");
            return;
        }

        var full = Path.GetFullPath(resolution.Path!);
        if (_active.Contains(full, StringComparer.Ordinal))
        {
            Fail(CopyStatus.Cycle, "copy-cycle", $"Copybook {name} includes itself through {string.Join(" -> ", _active.Select(Path.GetFileName))}");
            return;
        }

        if (parent.Depth + 1 > _options.MaxDepth)
        {
            Fail(CopyStatus.DepthExceeded, "copy-depth-exceeded", $"COPY nesting deeper than {_options.MaxDepth}");
            return;
        }

        LogicalSource childSource;
        try
        {
            childSource = _cache.GetCopybook(resolution.Path!, _options.DebuggingMode);
        }
        catch (IOException ex)
        {
            Fail(CopyStatus.ReadError, "copybook-read-error", $"Copybook {name} could not be read: {ex.Message}");
            return;
        }

        _accounting.Mark(parent.Id, start, end, CoverageClass.Directive, kind == CopyStatementKind.Copy ? "copy-statement" : "sql-include");
        var child = new SourceUnit(_accounting.Units.Count, kind == CopyStatementKind.Copy ? SourceUnitKind.Copybook : SourceUnitKind.SqlInclude, childSource, parent.Id, name, library, range, parent.Depth + 1, pairs.Count > 0);
        _accounting.Register(child);
        _diagnostics.AddRange(childSource.Diagnostics);
        _inclusions.Add(new CopyInclusion(kind, name, library, suppress, pairs.Count, parent.Id, child.Id, range, CopyStatus.Resolved, resolution.Path, resolution.Stage, resolution.OtherCandidates, idms));

        if (prefix is not null)
        {
            output.AppendSynthetic(prefix);
        }

        if (!char.IsWhiteSpace(output.LastChar))
        {
            output.AppendSynthetic(" ");
        }

        _active.Add(full);
        if (pairs.Count == 0)
        {
            ExpandUnit(child, output);
        }
        else
        {
            var temp = new OriginTextBuilder(childSource.Text.Length + 64);
            ExpandUnit(child, temp);
            var copied = temp.Build();
            var replaced = Replacer.Apply(copied, 0, copied.Length, pairs, output, (s, e) => _accounting.MarkExpanded(copied, s, e, CoverageClass.Directive, "replaced-by-copy-replacing"));
            if (replaced == 0)
            {
                _diagnostics.Add(Diagnostic.Create("replacing-no-match", DiagnosticSeverity.Info, $"COPY {name} REPLACING matched nothing in the copybook", range, label));
            }
        }

        _active.RemoveAt(_active.Count - 1);
        if (!char.IsWhiteSpace(output.LastChar))
        {
            output.AppendSynthetic("\n");
        }
    }

    private static CopyStatement ParseCopy(OriginText source, List<TextWord> words, int i)
    {
        var text = source.Text;
        var start = words[i].Start;
        var j = i + 1;
        if (j >= words.Count || words[j].Kind is not (TextWordKind.Word or TextWordKind.Literal) || ReplacingParser.Is(text, words[j], "REPLACING"))
        {
            return new CopyStatement(i, start, words[i].End, string.Empty, null, false, new List<ReplacingPair>(),
                new ParseError("copy-name-expected", "COPY must be followed by a text name", start, words[i].End));
        }

        var name = text.Substring(words[j].Start, words[j].Length);
        j++;
        string? library = null;
        if (j + 1 < words.Count && (ReplacingParser.Is(text, words[j], "OF") || ReplacingParser.Is(text, words[j], "IN"))
            && words[j + 1].Kind is TextWordKind.Word or TextWordKind.Literal)
        {
            library = text.Substring(words[j + 1].Start, words[j + 1].Length);
            j += 2;
        }

        var suppress = false;
        if (j < words.Count && ReplacingParser.Is(text, words[j], "SUPPRESS"))
        {
            suppress = true;
            j++;
        }

        var pairs = new List<ReplacingPair>();
        if (j < words.Count && ReplacingParser.Is(text, words[j], "REPLACING"))
        {
            j++;
            while (j < words.Count && words[j].Kind != TextWordKind.Period)
            {
                var pair = ReplacingParser.ParsePair(source, words, ref j, pseudoTextOnly: false, out var error);
                if (pair is null)
                {
                    var stop = Math.Min(j, words.Count - 1);
                    return new CopyStatement(stop, start, words[stop].End, name, library, suppress, pairs, error);
                }

                pairs.Add(pair);
            }

            if (pairs.Count == 0)
            {
                var stop = Math.Min(j, words.Count - 1);
                return new CopyStatement(stop, start, words[stop].End, name, library, suppress, pairs,
                    new ParseError("replacing-operand-expected", "REPLACING must be followed by at least one operand pair", start, words[stop].End));
            }
        }

        if (j < words.Count && words[j].Kind == TextWordKind.Period)
        {
            return new CopyStatement(j, start, words[j].End, name, library, suppress, pairs, null);
        }

        var last = j - 1;
        return new CopyStatement(last, start, words[last].End, name, library, suppress, pairs,
            new ParseError("copy-period-expected", "COPY statement must end with a separator period", start, words[last].End));
    }

    private sealed record CopyStatement(int EndWord, int Start, int End, string Name, string? Library, bool Suppress, List<ReplacingPair> Pairs, ParseError? Error);
}
