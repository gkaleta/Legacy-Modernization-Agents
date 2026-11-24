using System.Globalization;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Deps;

// Values a data-name TRANSID can take, proven from the code around the command rather than guessed.
public static class TransactionValues
{
    // PERFORM VARYING c FROM a BY b UNTIL c > n (or >=) ... STRING 'lit' DELIMITED BY SIZE c DELIMITED BY SIZE INTO operand ...
    // EXEC CICS RUN TRANSID(operand): one value per iteration, with c as an unsigned DISPLAY integer that fills operand exactly.
    public static IReadOnlyList<string>? BoundedVaryingString(IReadOnlyList<Statement> statements, ProcedureBindings bindings, ProgramData data, int execToken, string operand)
    {
        var loop = statements.OfType<PerformStatement>()
            .Where(p => p.Kind == PerformKind.Varying && p.Inline is not null && p.Varying.Count == 1
                && p.Span.FirstToken <= execToken && execToken < p.Span.EndToken)
            .MinBy(p => p.Span.TokenCount);
        if (loop is null || data.Symbols.Resolve(operand).Item is not { } target)
        {
            return null;
        }

        var varying = loop.Varying[0];
        if (varying.Variable is not DataRef counterRef || bindings.ItemOf(counterRef) is not { } counter
            || Integer(varying.From) is not { } from || Integer(varying.By) is not { } by || by <= 0
            || varying.Until is not RelationCondition { Left: DataRef untilRef, Operator: RelationalOperator.Greater or RelationalOperator.GreaterOrEqual, Abbreviated: false } until
            || !ReferenceEquals(bindings.ItemOf(untilRef), counter) || Integer(until.Right) is not { } limit
            || counter.Category != DataCategory.Numeric || counter.Usage != DataUsage.Display || counter.Signed || counter.Scale != 0
            || counter.Digits is < 1 or > 4 || counter.Occurs is not null)
        {
            return null;
        }

        var body = loop.Inline!.SelectMany(Statement.SelfAndNested).ToList();
        var writers = body.Where(s => s.Span.FirstToken < execToken && Writes(s, target, bindings)).ToList();
        if (writers is not [StringStatement { Pointer: null, Sources: [var prefix, var suffix] }]
            || !prefix.DelimitedBySize || prefix.Values is not [LiteralExpr { Kind: TokenKind.AlphanumericLiteral } literal]
            || !suffix.DelimitedBySize || suffix.Values is not [DataRef suffixRef] || !ReferenceEquals(bindings.ItemOf(suffixRef), counter)
            || body.Any(s => Writes(s, counter, bindings)))
        {
            return null;
        }

        var text = ExecArguments.LiteralValue(literal.Text);
        if (text.Length + counter.Digits != target.Length || target.Category != DataCategory.Alphanumeric)
        {
            return null;
        }

        var values = new List<string>();
        var ceiling = (int)Math.Pow(10, counter.Digits);
        for (var value = from; until.Operator == RelationalOperator.Greater ? value <= limit : value < limit; value += by)
        {
            if (value < 0 || value >= ceiling || values.Count >= 100)
            {
                return null;
            }

            values.Add(text + value.ToString(CultureInfo.InvariantCulture).PadLeft(counter.Digits, '0'));
        }

        return values;
    }

    private static int? Integer(Expr? expr) =>
        expr is LiteralExpr { Kind: TokenKind.IntegerLiteral } literal && int.TryParse(literal.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : null;

    // Conservative: any statement naming the item as a receiving field, or naming it at all in a verb that can write.
    private static bool Writes(Statement statement, DataItem item, ProcedureBindings bindings)
    {
        bool Is(Expr? e) => e is DataRef r && bindings.ItemOf(r) is { } bound
            && (ReferenceEquals(bound, item) || bound.Ancestors().Any(a => ReferenceEquals(a, item)) || item.Ancestors().Any(a => ReferenceEquals(a, bound)));
        return statement switch
        {
            MoveStatement m => m.Targets.Any(Is),
            StringStatement s => Is(s.Into),
            InitializeStatement i => i.Targets.Any(Is),
            UnstringStatement u => u.Targets.Any(t => Is(t.Into)),
            ComputeStatement c => c.Targets.Any(t => Is(t.Target)),
            ArithmeticStatement a => a.Targets.Any(t => Is(t.Target)),
            SetStatement s => s.Targets.Any(Is),
            _ => false,
        };
    }
}
