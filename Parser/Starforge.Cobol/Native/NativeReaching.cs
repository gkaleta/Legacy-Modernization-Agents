using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// Reaching definitions of one data item at one statement over the structured procedure: IF/EVALUATE branches join,
// a PERFORM runs its paragraph range and returns, loops iterate to a fixpoint and paragraphs fall through. GO TO and
// EXIT PARAGRAPH/SECTION may only jump forward inside a running range, abandoning the PERFORMs nested in it, as long as
// no other flow later falls into an abandoned range's end. A write the analysis cannot name (group move, INTO target,
// BY REFERENCE argument, ...) is an Unknown definition; any other jump, ALTER, CICS HANDLE, SQL WHENEVER,
// declaratives or a recursive PERFORM fail the whole analysis instead of a guess.
internal sealed class ReachingDefinitions
{
    public const int Initial = -1;
    public const int Unknown = -2;

    private const int Budget = 200_000;

    private readonly ProcedureDivision _procedure;
    private readonly ProcedureBindings _bindings;
    private readonly TokenStream _tokens;
    private readonly DataItem _item;
    private readonly Statement _at;
    private readonly List<(string? Section, string? Paragraph, IReadOnlyList<Statement> Body)> _units = new();
    private readonly List<(string? Name, int First, int End)> _sections = new();
    private readonly Dictionary<Statement, int> _definitions = new(ReferenceEqualityComparer.Instance);
    private readonly List<Statement> _assignments = new();
    private readonly Dictionary<(int, int, string), int[]?> _memo = new();
    private readonly HashSet<(int, int)> _active = new();
    private readonly Stack<Frame> _frames = new();
    private readonly HashSet<(int First, int End)> _abandoned = new();
    private readonly HashSet<(int Unit, int First, int End)> _visits = new();
    private readonly NativeWrites _writes;
    private int[]? _reaching;
    private int _steps;

    public sealed class Unsupported(string reason) : Exception(reason);

    // One running paragraph range; Pending[i] collects the states jumping to unit First + i (the last slot is End).
    private sealed class Frame(int first, int end)
    {
        public int First { get; } = first;

        public int End { get; } = end;

        public int Unit { get; set; } = first;

        public int[]?[] Pending { get; } = new int[]?[end - first + 1];

        // A range some jump left early has a result that depends on its caller, so it is never memoised.
        public bool Escaped { get; set; }
    }

    private ReachingDefinitions(ProcedureDivision procedure, ProcedureBindings bindings, ProgramData data, TokenStream tokens, DataItem item, Statement at)
    {
        _procedure = procedure;
        _bindings = bindings;
        _tokens = tokens;
        _item = item;
        _at = at;
        _writes = new NativeWrites(procedure, bindings, data);
        foreach (var section in procedure.Sections)
        {
            var first = _units.Count;
            _units.Add((section.Name, null, section.Leading.SelectMany(s => s.Statements).ToList()));

            foreach (var paragraph in section.Paragraphs)
            {
                _units.Add((section.Name, paragraph.Name, paragraph.Sentences.SelectMany(s => s.Statements).ToList()));
            }

            _sections.Add((section.Name, first, _units.Count));
        }
    }

    // The assignments reaching the statement in source order; Initial when the item's initial value reaches it,
    // Unknown when some unnamed write does. Throws Unsupported when control flow is beyond this analysis.
    public static (List<Statement> Assignments, bool Initial, bool Unknown) Analyze(
        ProcedureDivision procedure, ProcedureBindings bindings, ProgramData data, TokenStream tokens, DataItem item, Statement at)
    {
        var analysis = new ReachingDefinitions(procedure, bindings, data, tokens, item, at);
        analysis.Check();
        analysis.Run(0, analysis._units.Count, [Initial]);
        // An abandoned PERFORM leaves its return armed; flow falling into that range's end would take it.
        if (analysis._abandoned.Any(a => analysis._visits.Any(v => v.Unit == a.End - 1 && (v.First, v.End) != a)))
        {
            throw new Unsupported("abandoned-perform-return");
        }

        var reaching = analysis._reaching ?? throw new Unsupported("unreachable");
        var assignments = reaching.Where(d => d >= 0).Select(d => analysis._assignments[d]).OrderBy(s => s.Span.FirstToken).ToList();
        return (assignments, reaching.Contains(Initial), reaching.Contains(Unknown));
    }

    // Whole-program conditions under which per-statement transfer functions would not be sound.
    private void Check()
    {
        if (_procedure.Declaratives.Count > 0)
        {
            throw new Unsupported("declaratives");
        }

        foreach (var reference in _writes.References)
        {
            var at = reference.Span.FirstToken;
            if (at >= 2 && Word(at - 1, "OF") && Word(at - 2, "ADDRESS") && Overlaps(reference))
            {
                throw new Unsupported("address-of");
            }
        }

        foreach (var statement in _procedure.AllStatements())
        {
            switch (statement)
            {
                case GoToStatement { Targets.Count: 0 } or AlterStatement or NextSentenceStatement or EntryStatement or ErrorStatement:
                case ExitStatement { Kind: not ("" or "PROGRAM" or "PARAGRAPH" or "SECTION") }:
                case ExecStatementSyntax { Parsed: CicsCommand { Verb: "HANDLE" or "PUSH" or "POP" } }:
                case ExecStatementSyntax { Parsed: SqlStatement { StatementKind: SqlStatementKind.Whenever } }:
                    throw new Unsupported("flow:" + statement.GetType().Name);
            }
        }
    }

    private bool Word(int token, string text) => string.Equals(_tokens.Text(token), text, StringComparison.OrdinalIgnoreCase);

    private int[]? Run(int first, int end, int[] state)
    {
        var key = (first, end, string.Join(',', state));
        if (_memo.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (!_active.Add((first, end)))
        {
            throw new Unsupported("recursive-perform");
        }

        var frame = new Frame(first, end);
        _frames.Push(frame);
        int[]? current = state;
        for (var unit = first; unit < end; unit++)
        {
            current = Join(current, frame.Pending[unit - first]);
            if (current is not null)
            {
                frame.Unit = unit;
                _visits.Add((unit, first, end));
                current = Block(_units[unit].Body, current);
            }
        }

        current = Join(current, frame.Pending[end - first]);
        _frames.Pop();
        _active.Remove((first, end));
        if (!frame.Escaped)
        {
            _memo[key] = current;
        }

        return current;
    }

    private int[]? Block(IReadOnlyList<Statement> statements, int[]? state)
    {
        foreach (var statement in statements)
        {
            if (state is null)
            {
                return null;
            }

            state = Statement(statement, state);
        }

        return state;
    }

    private int[]? Statement(Statement statement, int[] state)
    {
        if (++_steps > Budget)
        {
            throw new Unsupported("budget");
        }

        if (ReferenceEquals(statement, _at))
        {
            _reaching = Join(_reaching, state);
        }

        switch (statement)
        {
            case IfStatement s:
                return Join(Block(s.Then, state), s.Else is { } otherwise ? Block(otherwise, state) : state);
            case EvaluateStatement s:
            {
                var result = s.Other is { } other ? Block(other, state) : state;
                foreach (var when in s.Whens)
                {
                    result = Join(result, Block(when.Statements, state));
                }

                return result;
            }

            case SearchStatement { Varying: DataRef varying } when Overlaps(varying):
                throw new Unsupported("search-varying");
            case SearchStatement s:
            {
                var result = s.AtEnd is { } atEnd ? Block(atEnd, state) : state;
                foreach (var when in s.Whens)
                {
                    result = Join(result, Block(when.Statements, state));
                }

                return result;
            }

            case PerformStatement s when s.Varying.Any(v => v.Variable is not DataRef variable || Overlaps(variable)):
                throw new Unsupported("perform-varying");
            case PerformStatement s:
                return Perform(s, state);
            case GobackStatement or StopStatement { Literal: null } or ExitStatement { Kind: "PROGRAM" }:
                return null;
            case GoToStatement s:
                foreach (var target in s.Targets)
                {
                    Jump(Unit(target).First, state);
                }

                // GO TO DEPENDING ON falls through when the index selects no target.
                return s.DependingOn is null ? null : state;
            case ExitStatement { Kind: "PARAGRAPH" }:
                Jump(_frames.Peek().Unit + 1, state);
                return null;
            case ExitStatement { Kind: "SECTION" }:
            {
                var unit = _frames.Peek().Unit;
                Jump(_sections.Single(x => x.First <= unit && unit < x.End).End, state);
                return null;
            }
            case ExecStatementSyntax { Parsed: CicsCommand cics } when Terminates(cics):
                return null;
            case IdmsStatement { On: null, Kind: IdmsKind.DcReturn }:
            case IdmsStatement { On: null, Transfer.Mode: "NORETURN" }:
                return null;
        }

        var after = Writes(statement) ? Define(statement) : state;
        var handlers = statement.Children().ToList();
        if (handlers.Count == 0)
        {
            return after;
        }

        // The ON clause runs after the command when its status is set, otherwise control falls through.
        if (statement is IdmsStatement { On: { } on })
        {
            return Join(after, Block(on.Statements, after));
        }

        // A handler phrase runs instead of falling through, so either path may reach the next statement.
        var phrases = statement switch
        {
            ComputeStatement c => c.Handlers,
            ArithmeticStatement c => c.Handlers,
            CallStatement c => c.Handlers,
            StringStatement c => c.Handlers,
            UnstringStatement c => c.Handlers,
            Syntax.FileStatement c => c.Handlers,
            AcceptStatement c => c.Handlers,
            _ => throw new Unsupported("nested:" + statement.GetType().Name),
        };
        var result2 = after;
        foreach (var phrase in phrases)
        {
            result2 = Join(result2, Block(phrase.Statements, after));
        }

        return result2;
    }

    private int[]? Perform(PerformStatement perform, int[] state)
    {
        Func<int[], int[]?> body;
        if (perform.Inline is { } inline)
        {
            body = s => Block(inline, s);
        }
        else
        {
            var (first, end) = Range(perform.Target!, perform.Thru);
            body = s => Run(first, end, s);
        }

        if (perform.Kind == PerformKind.Once)
        {
            return body(state);
        }

        // Loop: the body may run any number of times (at least once when the test is after it).
        int[]? result = perform.TestAfter ? body(state) : state;
        while (true)
        {
            var next = result is null ? null : Join(result, body(result));
            if (Same(next, result))
            {
                return result;
            }

            result = next;
        }
    }

    // A jump lands later inside the innermost running range that holds it, whose fall-through picks it up.
    private void Jump(int target, int[] state)
    {
        var left = new List<Frame>();
        foreach (var frame in _frames)
        {
            // Landing inside a range being left would really run on and return through that PERFORM.
            if (frame.First <= target && target < frame.End && target <= frame.Unit)
            {
                break;
            }

            if (frame.Unit < target && target <= frame.End)
            {
                foreach (var inner in left)
                {
                    inner.Escaped = true;
                    _abandoned.Add((inner.First, inner.End));
                }

                frame.Pending[target - frame.First] = Join(frame.Pending[target - frame.First], state);
                return;
            }

            left.Add(frame);
        }

        throw new Unsupported("jump-outside-range:" + (target < _units.Count ? _units[target].Paragraph ?? _units[target].Section : "end"));
    }

    private (int First, int End) Range(ProcedureRef target, ProcedureRef? thru)
    {
        var (first, end) = Unit(target);
        if (thru is not null)
        {
            var (_, last) = Unit(thru);
            if (last <= first)
            {
                throw new Unsupported("perform-thru-order");
            }

            end = last;
        }

        return (first, end);
    }

    // A name is a section, or a paragraph unique in the program (or in its qualifying section).
    private (int First, int End) Unit(ProcedureRef reference)
    {
        static bool Is(string? name, string text) => string.Equals(name, text, StringComparison.OrdinalIgnoreCase);
        var matches = new List<(int, int)>();
        if (reference.Qualifier is null)
        {
            matches.AddRange(_sections.Where(s => Is(s.Name, reference.Name)).Select(s => (s.First, s.End)));
        }

        for (var u = 0; u < _units.Count; u++)
        {
            if (Is(_units[u].Paragraph, reference.Name) && (reference.Qualifier is null || Is(_units[u].Section, reference.Qualifier)))
            {
                matches.Add((u, u + 1));
            }
        }

        return matches.Count == 1 ? matches[0] : throw new Unsupported("procedure:" + reference.Name);
    }

    // CICS RETURN and XCTL end the task, as does ABEND; without RESP or NOHANDLE an error raises a condition whose
    // default action abends too, since the analysis already refused any program with HANDLE CONDITION.
    private static bool Terminates(CicsCommand command) =>
        command.Verb is "ABEND"
        || command.Verb is "RETURN" or "XCTL" && !command.Options.Any(o => o.Name is "RESP" or "RESP2" or "NOHANDLE");

    // A recognised assignment of the whole item is a definition; any other write over its storage is Unknown.
    private int[] Define(Statement statement)
    {
        if (!Assignment(statement))
        {
            return [Unknown];
        }

        if (!_definitions.TryGetValue(statement, out var id))
        {
            id = _assignments.Count;
            _definitions[statement] = id;
            _assignments.Add(statement);
        }

        return [id];
    }

    private bool Assignment(Statement statement) => statement switch
    {
        MoveStatement { Corresponding: false, Targets: [DataRef target] } move => Plain(target) is { } item && (
            ReferenceEquals(item, _item) && move.Source is LiteralExpr { Kind: TokenKind.AlphanumericLiteral } or DataRef { Subscripts.Count: 0, RefStart: null }
            || Blank(move.Source) && (ReferenceEquals(item, _item) || _item.Ancestors().Any(a => ReferenceEquals(a, item)))),
        InitializeStatement { Targets: [DataRef target], WithFiller: false, AllOrCategory: null, ToValue: false, ToDefault: false, Replacing.Count: 0 }
            => Plain(target) is { } item && (ReferenceEquals(item, _item) || _item.Ancestors().Any(a => ReferenceEquals(a, item))),
        _ => false,
    };

    private DataItem? Plain(DataRef target) =>
        target is { Subscripts.Count: 0, RefStart: null, RefLength: null } ? _bindings.ItemOf(target) : null;

    public static bool Blank(Expr source) =>
        source is FigurativeExpr { Figurative: FigurativeKind.Space or FigurativeKind.LowValue, Of: null } figurative
        && figurative.Span.TokenCount == 1;

    private bool Writes(Statement statement) =>
        _writes.Writes(statement, _item, (c, o) => NativeCics.NameOptions(c, o) || o.Name == "FROM");

    private bool Overlaps(DataRef reference) => _writes.Overlaps(reference, _item);

    private static int[]? Join(int[]? a, int[]? b) =>
        a is null ? b : b is null ? a : a.Union(b).Order().ToArray();

    private static bool Same(int[]? a, int[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
}
