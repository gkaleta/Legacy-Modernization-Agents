using System.Text;
using Starforge.Cobol.Lexing;

namespace Starforge.Cobol.Syntax;

// Canonical, fully parenthesised text of expressions and conditions; abbreviated relations print expanded.
public static class ProcedureText
{
    public static string Of(Expr e)
    {
        var sb = new StringBuilder();
        Append(sb, e);
        return sb.ToString();
    }

    public static string Of(Condition c)
    {
        var sb = new StringBuilder();
        Append(sb, c);
        return sb.ToString();
    }

    public static string Symbol(RelationalOperator op) => op switch
    {
        RelationalOperator.Equal => "=",
        RelationalOperator.NotEqual => "<>",
        RelationalOperator.Greater => ">",
        RelationalOperator.Less => "<",
        RelationalOperator.GreaterOrEqual => ">=",
        _ => "<=",
    };

    private static void Append(StringBuilder sb, Expr e)
    {
        switch (e)
        {
            case DataRef d:
                sb.Append(d.Name.Name);
                foreach (var q in d.Name.Qualifiers)
                {
                    sb.Append(" OF ").Append(q);
                }

                if (d.Subscripts.Count > 0)
                {
                    sb.Append('(');
                    List(sb, d.Subscripts);
                    sb.Append(')');
                }

                RefMod(sb, d.RefStart, d.RefLength);
                break;
            case AllSubscript:
                sb.Append("ALL");
                break;
            case LiteralExpr l:
                sb.Append(l.Text);
                break;
            case FigurativeExpr f:
                sb.Append(f.Figurative switch
                {
                    FigurativeKind.Zero => "ZERO",
                    FigurativeKind.Space => "SPACE",
                    FigurativeKind.HighValue => "HIGH-VALUE",
                    FigurativeKind.LowValue => "LOW-VALUE",
                    FigurativeKind.Quote => "QUOTE",
                    FigurativeKind.Null => "NULL",
                    _ => "ALL",
                });
                if (f.Of is not null)
                {
                    sb.Append(' ');
                    Append(sb, f.Of);
                }

                break;
            case FunctionCall f:
                sb.Append("FUNCTION ").Append(f.Name);
                if (f.Arguments.Count > 0)
                {
                    sb.Append('(');
                    List(sb, f.Arguments);
                    sb.Append(')');
                }

                RefMod(sb, f.RefStart, f.RefLength);
                break;
            case SpecialRef s:
                sb.Append(s.Operator == SpecialOperator.LengthOf ? "LENGTH OF " : "ADDRESS OF ");
                Append(sb, s.Operand);
                break;
            case CicsValue c:
                sb.Append(c.Function).Append('(').Append(c.Argument).Append(')');
                break;
            case UnaryExpr u:
                sb.Append(u.Operator);
                Append(sb, u.Operand);
                break;
            case BinaryExpr b:
                sb.Append('(');
                Append(sb, b.Left);
                sb.Append(b.Operator switch
                {
                    ArithmeticOperator.Add => " + ",
                    ArithmeticOperator.Subtract => " - ",
                    ArithmeticOperator.Multiply => " * ",
                    ArithmeticOperator.Divide => " / ",
                    _ => " ** ",
                });
                Append(sb, b.Right);
                sb.Append(')');
                break;
        }
    }

    private static void Append(StringBuilder sb, Condition c)
    {
        switch (c)
        {
            case RelationCondition r:
                sb.Append('(');
                Append(sb, r.Left);
                sb.Append(' ').Append(Symbol(r.Operator)).Append(' ');
                Append(sb, r.Right);
                sb.Append(')');
                break;
            case ClassCondition k:
                sb.Append('(');
                Append(sb, k.Subject);
                sb.Append(k.Not ? " NOT " : " ").Append(k.Class).Append(')');
                break;
            case SignCondition s:
                sb.Append('(');
                Append(sb, s.Subject);
                sb.Append(s.Not ? " NOT " : " ").Append(s.Sign).Append(')');
                break;
            case ConditionNameCondition n:
                Append(sb, n.Name);
                break;
            case NotCondition n:
                sb.Append("NOT ");
                Append(sb, n.Operand);
                break;
            case AndCondition a:
                sb.Append('(');
                Append(sb, a.Left);
                sb.Append(" AND ");
                Append(sb, a.Right);
                sb.Append(')');
                break;
            case OrCondition o:
                sb.Append('(');
                Append(sb, o.Left);
                sb.Append(" OR ");
                Append(sb, o.Right);
                sb.Append(')');
                break;
        }
    }

    private static void List(StringBuilder sb, IReadOnlyList<Expr> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Append(sb, items[i]);
        }
    }

    private static void RefMod(StringBuilder sb, Expr? start, Expr? length)
    {
        if (start is null)
        {
            return;
        }

        sb.Append('(');
        Append(sb, start);
        sb.Append(':');
        if (length is not null)
        {
            Append(sb, length);
        }

        sb.Append(')');
    }
}
