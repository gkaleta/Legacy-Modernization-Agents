using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// EXEC CICS RUN TRANSID: a literal names one transaction. A data name is bounded only when the RUN sits in an
// inline PERFORM VARYING c FROM a BY 1 UNTIL c > b whose first statement is STRING 'xyz' DELIMITED BY SIZE
// c DELIMITED BY SIZE INTO x, and nothing else in the program writes x or c: x then holds xyz||a .. xyz||b.
internal sealed partial class NativeCics
{
    private const string RunUsageProof = "single-native-program:unconditional-string-with-read-only-target-and-counter-uses";

    private IEnumerable<JsonObject> Started(Command command)
    {
        var option = command.Single("TRANSID");
        var (names, proof) = option.Argument!.Kind == Exec.ExecArgumentKind.Literal
            ? ([Name(command, option, 4, 4).Value], new JsonObject { ["kind"] = "literal" })
            : Bounded(command, option.Argument.Text);
        var bindings = new List<JsonObject>();
        foreach (var name in names)
        {
            var binding = Transaction(name, (JsonObject)proof.DeepClone());
            command.Started.Add((string)binding["programBinding"]!["name"]!);
            bindings.Add(binding);
        }

        return bindings;
    }

    private (List<string> Names, JsonObject Proof) Bounded(Command command, string operand)
    {
        var unit = command.Unit;
        if (!NameR().IsMatch(operand) || unit.Procedure is not { } procedure || _data.DataOf(unit) is not { } data
            || data.Symbols.Resolve(operand).Item is not { } target)
        {
            throw new Problem("run-transid:" + operand);
        }

        var perform = procedure.AllStatements().OfType<PerformStatement>()
            .Where(p => p.Inline is not null && p.Inline.Skip(1).SelectMany(Statement.SelfAndNested).Any(s => ReferenceEquals(s, command.Exec)))
            .MinBy(p => p.Span.EndToken - p.Span.FirstToken)
            ?? throw new Problem("run-transid-loop:" + operand);
        var bindings = _data.BindingsOf(unit);
        if (perform is not { Kind: PerformKind.Varying, TestAfter: false, Target: null, Varying: [var varying], Inline: [StringStatement build, ..] }
            || varying.Variable is not DataRef { Subscripts: [], RefStart: null } counterRef
            || bindings.ItemOf(counterRef) is not { } counter
            || Integer(varying.From) is not { } from || Integer(varying.By) != 1
            || varying.Until is not RelationCondition { Operator: RelationalOperator.Greater, Abbreviated: false } until
            || until.Left is not DataRef { Subscripts: [], RefStart: null } untilRef || bindings.ItemOf(untilRef) != counter
            || Integer(until.Right) is not { } to || from < 1 || to > 8 || from > to)
        {
            throw new Problem("run-transid-loop:" + operand);
        }

        if (build is not { Pointer: null, Handlers: [], Sources: [{ DelimitedBySize: true, Values: [LiteralExpr prefix] }, { DelimitedBySize: true, Values: [DataRef { Subscripts: [], RefStart: null } digit] }] }
            || build.Into is not DataRef { Subscripts: [], RefStart: null } into
            || bindings.ItemOf(digit) != counter || bindings.ItemOf(into) != target
            || !Regex.IsMatch(prefix.Text, """^(['"])[A-Za-z0-9@$#_-]{3}\1$"""))
        {
            throw new Problem("run-transid-string:" + operand);
        }

        // Target and counter must be fixed-shape storage the STRING fully overwrites with one digit.
        if (target.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage)
            || counter.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage)
            || _data.EntryText(target) is not { } targetText || _data.EntryText(counter) is not { } counterText
            || !Regex.IsMatch(targetText, "^01" + Regex.Escape(target.DisplayName) + @"PIC(?:TURE)?(?:IS)?X\(0*4\)(?:VALUE(?:IS)?SPACES?)?\.$", RegexOptions.IgnoreCase)
            || !Regex.IsMatch(counterText, "^01" + Regex.Escape(counter.DisplayName) + @"PIC(?:TURE)?(?:IS)?9(?:VALUE(?:IS)?0)?\.$", RegexOptions.IgnoreCase))
        {
            throw new Problem("run-transid-storage:" + operand);
        }

        var writes = new NativeWrites(procedure, bindings, data);
        foreach (var statement in procedure.AllStatements())
        {
            if (!ReferenceEquals(statement, build) && writes.Writes(statement, target, NameOptions)
                || !ReferenceEquals(statement, perform) && writes.Writes(statement, counter, NameOptions))
            {
                throw new Problem("run-transid-written:" + operand);
            }
        }

        var first = perform.Span.FirstToken + 1;
        var prefixValue = prefix.Text[1..^1];
        var names = Enumerable.Range(from, to - from + 1).Select(n => prefixValue + n).ToList();
        return (names, new JsonObject
        {
            ["kind"] = "bounded-varying-string",
            ["operand"] = operand,
            ["counter"] = counterRef.Name.Name,
            ["definitionSyntax"] = _data.Entry(target) ?? throw new Problem("run-transid-storage:" + operand),
            ["counterDefinitionSyntax"] = _data.Entry(counter) ?? throw new Problem("run-transid-storage:" + operand),
            ["loopHeaderSyntax"] = _data.Phrase(first, build.Span.FirstToken, "PerformPhrase"),
            ["stringSyntax"] = _data.Statement(build, "StringStatement"),
            ["usageProof"] = RunUsageProof,
            ["transactionNames"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()),
        });
    }

    private static int? Integer(Expr expression) =>
        expression is LiteralExpr literal && Regex.IsMatch(literal.Text, "^[0-9]+$") && int.TryParse(literal.Text, out var value) ? value : null;
}
