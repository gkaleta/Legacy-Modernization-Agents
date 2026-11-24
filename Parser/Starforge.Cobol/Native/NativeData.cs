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
