using System.Text.Json.Nodes;
using Starforge.Cobol.Deps;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// Data description entries and procedure names as rooted trees. A DataEntry node holds the entry's own tokens
// through its period, with the name, PICTURE and OCCURS phrases wrapped so the gate can find them.
internal sealed class NativeData
{
    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "PIC", "PICTURE", "VALUE", "VALUES", "USAGE", "COMP", "COMP-1", "COMP-2", "COMP-3", "COMP-4", "COMP-5",
        "COMPUTATIONAL", "COMPUTATIONAL-1", "COMPUTATIONAL-2", "COMPUTATIONAL-3", "COMPUTATIONAL-4", "COMPUTATIONAL-5",
        "BINARY", "DISPLAY", "PACKED-DECIMAL", "INDEX", "POINTER", "REDEFINES", "SYNC", "SYNCHRONIZED", "JUST",
        "JUSTIFIED", "BLANK", "SIGN", "LEADING", "TRAILING", "EXTERNAL", "GLOBAL",
    };

    private readonly CompilationResult _result;
    private readonly SyntaxRoots _roots;

    public NativeData(CompilationResult result, SyntaxRoots roots)
    {
        _result = result;
        _roots = roots;
    }

    public SyntaxRoots Roots => _roots;

    public ProgramUnit? UnitAt(int token) =>
        _result.Syntax.AllPrograms().Where(u => u.Span.FirstToken <= token && token < u.Span.EndToken)
            .OrderBy(u => u.Span.EndToken - u.Span.FirstToken).FirstOrDefault();

    public ProgramData? DataOf(ProgramUnit? unit)
    {
        var index = _result.Syntax.AllPrograms().ToList().FindIndex(u => ReferenceEquals(u, unit));
        return index >= 0 && index < _result.Programs.Count ? _result.Programs[index] : null;
    }

    public JsonObject? Entry(DataItem item)
    {
        var entry = item.Entry;
        var span = entry.HeaderSpan;
        if (span.EndToken <= span.FirstToken)
        {
            return null;
        }

        var tokens = _roots.Tokens;
        var children = new List<JsonNode>();
        var named = entry.Name is null;
        var i = span.FirstToken;
        while (i < span.EndToken)
        {
            var text = tokens.Text(i);
            if (!named && i > span.FirstToken && text.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
            {
                children.Add(NativeSyntax.Node("DataName", [NativeSyntax.Token(text)]));
                named = true;
                i++;
            }
            else if (text.Equals("PIC", StringComparison.OrdinalIgnoreCase) || text.Equals("PICTURE", StringComparison.OrdinalIgnoreCase))
            {
                var end = i + 1;
                while (end < span.EndToken && tokens[end].Kind != TokenKind.PictureString)
                {
                    end++;
                }

                end = Math.Min(end + 1, span.EndToken);
                children.Add(NativeSyntax.Node("PictureClause", Range(i, end)));
                i = end;
            }
            else if (text.Equals("OCCURS", StringComparison.OrdinalIgnoreCase))
            {
                var end = i + 1;
                while (end < span.EndToken && tokens[end].Kind != TokenKind.Period && !ClauseWords.Contains(tokens.Text(end)))
                {
                    end++;
                }

                children.Add(NativeSyntax.Node("OccursClause", Range(i, end)));
                i = end;
            }
            else
            {
                children.Add(NativeSyntax.Token(text));
                i++;
            }
        }

        return _roots.RootTokens(NativeSyntax.Node("DataEntry", children), span.FirstToken, span.EndToken, "data:" + item.DisplayName);
    }

    // The entry's own tokens through its period, concatenated as the gate reads a DataEntry tree, without
    // building a syntax root (roots report text a COPY REPLACING produced).
    public string? EntryText(DataItem item)
    {
        var span = item.Entry.HeaderSpan;
        return span.EndToken <= span.FirstToken ? null : Text(span.FirstToken, span.EndToken);
    }

    // A statement's tokens without the separator period, as the gate reads its syntax tree.
    public List<string> StatementTokens(Statement statement) =>
        Enumerable.Range(statement.Span.FirstToken, StatementEnd(statement) - statement.Span.FirstToken).Select(_roots.Tokens.Text).ToList();

    public IReadOnlyList<ProgramUnit> Units() => _result.Syntax.AllPrograms().ToList();

    public string Text(int first, int end)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = first; i < end; i++)
        {
            builder.Append(_roots.Tokens.Text(i));
        }

        return builder.ToString();
    }

    // The entries from the 01 record down to the item, outermost first.
    public JsonArray? Path(DataItem item)
    {
        var entries = item.Ancestors().Reverse().Append(item).Select(Entry).ToList();
        return entries.Any(e => e is null) ? null : new JsonArray(entries.Select(e => (JsonNode)e!).ToArray());
    }

    public JsonObject? Procedure(ProgramUnit unit, string name)
    {
        var procedure = unit.Procedure;
        var paragraphs = procedure?.AllParagraphs().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var sections = procedure?.AllSections().Where(s => s.Name?.Equals(name, StringComparison.OrdinalIgnoreCase) == true).ToList() ?? [];
        if (paragraphs.Count + sections.Count != 1)
        {
            return null;
        }

        var (kind, first) = paragraphs.Count == 1 ? ("ParagraphName", paragraphs[0].Span.FirstToken) : ("SectionName", sections[0].Span.FirstToken);
        var node = NativeSyntax.Node(kind, [NativeSyntax.Token(_roots.Tokens.Text(first))]);
        return _roots.RootTokens(node, first, first + 1, "procedure:" + name);
    }

    // A constant VALUE proved read-only: readOnly names the EXEC options that only read their operand,
    // callTargets says whether CALL program operands count as reads too, and readStatements adds every
    // procedure reference NativeReads proves read-only.
    public (string Value, DataItem Item)? Constant(ProgramUnit unit, string name, bool callTargets, Func<CicsCommand, ExecOption, bool>? readOnly, bool readStatements)
    {
        var index = _result.Syntax.AllPrograms().ToList().FindIndex(u => ReferenceEquals(u, unit));
        if (index < 0 || index >= _result.Programs.Count || unit.Procedure is not { } procedure
            || _result.Programs[index].Symbols.Resolve(name).Item is not { } item)
        {
            return null;
        }

        var statements = procedure.AllStatements().ToList();
        var resolver = new ConstantResolver(
            _result.Programs[index],
            index < _result.Bindings.Count ? _result.Bindings[index] : ProcedureBindings.Empty,
            procedure,
            statements,
            Execs(unit),
            readOnly,
            callTargets,
            readStatements ? NativeReads.Of(statements) : null);
        return resolver.ValueOf(item) is { } value ? (value, item) : null;
    }

    // True when no procedure statement or EXEC operand outside the given read-only positions can write the item.
    public bool ReadOnly(ProgramUnit unit, DataItem item, Func<CicsCommand, ExecOption, bool> readOnly)
    {
        var index = _result.Syntax.AllPrograms().ToList().FindIndex(u => ReferenceEquals(u, unit));
        if (index < 0 || index >= _result.Programs.Count || unit.Procedure is not { } procedure)
        {
            return false;
        }

        var statements = procedure.AllStatements().ToList();
        var resolver = new ConstantResolver(
            _result.Programs[index],
            index < _result.Bindings.Count ? _result.Bindings[index] : ProcedureBindings.Empty,
            procedure,
            statements,
            Execs(unit),
            readOnly,
            callTargetsRead: true,
            NativeReads.Of(statements));
        return resolver.Untouched(item);
    }

    public ProcedureBindings BindingsOf(ProgramUnit unit)
    {
        var index = _result.Syntax.AllPrograms().ToList().FindIndex(u => ReferenceEquals(u, unit));
        return index >= 0 && index < _result.Bindings.Count ? _result.Bindings[index] : ProcedureBindings.Empty;
    }

    // A procedure statement as a rooted tree of its own tokens; a separator period ending the sentence is not part of it.
    public JsonObject Statement(Statement statement, string kind)
    {
        var end = StatementEnd(statement);
        return _roots.RootTokens(NativeSyntax.Node(kind, Range(statement.Span.FirstToken, end)), statement.Span.FirstToken, end, "statement:" + kind);
    }

    // A phrase inside a statement (e.g. a PERFORM VARYING header) as a rooted tree of its tokens.
    public JsonObject Phrase(int first, int end, string kind) =>
        _roots.RootTokens(NativeSyntax.Node(kind, Range(first, end)), first, end, "phrase:" + kind);

    private int StatementEnd(Statement statement)
    {
        var tokens = _roots.Tokens;
        var end = statement.Span.EndToken;
        while (end > statement.Span.FirstToken + 1 && tokens[end - 1].Kind == TokenKind.Period)
        {
            end--;
        }

        return end;
    }

    // Storage two data items may share: the same record (through record REDEFINES) and overlapping bytes, an
    // OCCURS item counting as its whole table.
    public static bool Overlap(DataItem a, DataItem b)
    {
        static (DataItem Root, int Start, int End) Of(DataItem item) =>
            ConstantResolver.Extent(item.Ancestors().Prepend(item).LastOrDefault(x => x.Occurs is not null) ?? item);
        var (x, y) = (Of(a), Of(b));
        return ReferenceEquals(x.Root, y.Root) && x.Start < y.End && y.Start < x.End;
    }

    // Every EXEC block inside the unit (its data division included).
    public IEnumerable<Exec.ExecStatement> Execs(ProgramUnit unit) => ExecBlocks(unit).Select(b => b.Statement);

    public IEnumerable<(int Token, Exec.ExecStatement Statement)> ExecBlocks(ProgramUnit unit)
    {
        var tokens = _result.Tokens;
        for (var i = unit.Span.FirstToken; i < unit.Span.EndToken && i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.ExecBlock && ReferenceEquals(UnitAt(i), unit))
            {
                yield return (i, _result.ParsedExecs.TryGetValue(i, out var parsed) ? parsed : ExecParser.Parse(tokens, i));
            }
        }
    }

    private List<JsonNode> Range(int start, int end) =>
        Enumerable.Range(start, end - start).Select(t => (JsonNode)NativeSyntax.Token(_roots.Tokens.Text(t))).ToList();
}
