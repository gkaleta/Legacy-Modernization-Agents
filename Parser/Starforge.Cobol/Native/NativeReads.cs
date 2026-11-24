using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// Data references in positions where a statement only reads the storage: sources, conditions, operands and
// subscripts. Anything not listed here (targets, BY REFERENCE arguments, ADDRESS OF, INSPECT subjects, ...)
// counts as a possible write, so a constant proof over the remaining references stays conservative.
internal static class NativeReads
{
    public static HashSet<DataRef> Of(IEnumerable<Statement> statements)
    {
        var reads = new HashSet<DataRef>(ReferenceEqualityComparer.Instance);
        foreach (var statement in statements)
        {
            Statement(statement, reads);
        }

        return reads;
    }

    private static void Statement(Statement statement, HashSet<DataRef> reads)
    {
        switch (statement)
        {
            case MoveStatement s:
                Read(s.Source, reads);
                Targets(s.Targets, reads);
                break;
            case ComputeStatement s:
                Read(s.Value, reads);
                Targets(s.Targets.Select(t => t.Target), reads);
                break;
            case ArithmeticStatement s:
                s.Operands.ToList().ForEach(o => Read(o, reads));
                if (s.Giving)
                {
                    Read(s.Second, reads);
                }

                Targets(s.Targets.Select(t => t.Target), reads);
                break;
            case IfStatement s:
                Condition(s.Condition, reads);
                break;
            case EvaluateStatement s:
                foreach (var operand in s.Subjects.Concat(s.Whens.SelectMany(w => w.Alternatives.SelectMany(a => a))))
                {
                    Read(operand.Value, reads);
                    Read(operand.Thru, reads);
                    Condition(operand.Condition, reads);
                }

                break;
            case PerformStatement s:
                Read(s.Times, reads);
                Condition(s.Until, reads);
                foreach (var varying in s.Varying)
                {
                    Read(varying.From, reads);
                    Read(varying.By, reads);
                    Condition(varying.Until, reads);
                    Targets([varying.Variable], reads);
                }

                break;
            case GoToStatement s:
                Read(s.DependingOn, reads);
                break;
            case CallStatement s:
                Read(s.Target, reads);
                foreach (var argument in s.Using.Where(a => a.Mode is PassingMode.Content or PassingMode.Value))
                {
                    Read(argument.Value, reads);
                }

                break;
            case CancelStatement s:
                s.Programs.ToList().ForEach(p => Read(p, reads));
                break;
            case DisplayStatement s:
                s.Values.ToList().ForEach(v => Read(v, reads));
                break;
            case StringStatement s:
                foreach (var source in s.Sources)
                {
                    source.Values.ToList().ForEach(v => Read(v, reads));
                    Read(source.Delimiter, reads);
                }

                Targets([s.Into], reads);
                break;
            case UnstringStatement s:
                Read(s.Source, reads);
                s.Delimiters.ToList().ForEach(d => Read(d.Delimiter, reads));
                Targets(s.Targets.Select(t => t.Into), reads);
                break;
            case SearchStatement s:
                s.Whens.ToList().ForEach(w => Condition(w.Condition, reads));
                break;
            case SetStatement { Kind: SetKind.To or SetKind.UpBy or SetKind.DownBy } s:
                Read(s.Value, reads);
                Targets(s.Targets, reads);
                break;
            case Syntax.FileStatement s:
                Read(s.From, reads);
                Read(s.Key, reads);
                break;
            case StopStatement s:
                Read(s.Literal, reads);
                break;
        }
    }

    // A target's own storage may change, but its subscripts and reference modifiers are only read.
    private static void Targets(IEnumerable<Expr> targets, HashSet<DataRef> reads)
    {
        foreach (var target in targets.OfType<DataRef>())
        {
            target.Subscripts.ToList().ForEach(s => Read(s, reads));
            Read(target.RefStart, reads);
            Read(target.RefLength, reads);
        }
    }

    private static void Read(Expr? expr, HashSet<DataRef> reads)
    {
        switch (expr)
        {
            case DataRef reference:
                reads.Add(reference);
                Targets([reference], reads);
                break;
            case FunctionCall call:
                call.Arguments.ToList().ForEach(a => Read(a, reads));
                Read(call.RefStart, reads);
                Read(call.RefLength, reads);
                break;
            case SpecialRef { Operator: SpecialOperator.LengthOf } special:
                Read(special.Operand, reads);
                break;
            case UnaryExpr unary:
                Read(unary.Operand, reads);
                break;
            case BinaryExpr binary:
                Read(binary.Left, reads);
                Read(binary.Right, reads);
                break;
            case FigurativeExpr figurative:
                Read(figurative.Of, reads);
                break;
        }
    }

    private static void Condition(Condition? condition, HashSet<DataRef> reads)
    {
        switch (condition)
        {
            case RelationCondition c:
                Read(c.Left, reads);
                Read(c.Right, reads);
                break;
            case ClassCondition c:
                Read(c.Subject, reads);
                break;
            case SignCondition c:
                Read(c.Subject, reads);
                break;
            case ConditionNameCondition c:
                Read(c.Name, reads);
                break;
            case NotCondition c:
                Condition(c.Operand, reads);
                break;
            case AndCondition c:
                Condition(c.Left, reads);
                Condition(c.Right, reads);
                break;
            case OrCondition c:
                Condition(c.Left, reads);
                Condition(c.Right, reads);
                break;
        }
    }
}
