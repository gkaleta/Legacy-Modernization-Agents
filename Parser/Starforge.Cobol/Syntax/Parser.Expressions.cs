using Starforge.Cobol.Lexing;

namespace Starforge.Cobol.Syntax;

internal sealed partial class Parser
{
    // Reserved words that can never start a data or procedure name in the procedure division (verbs are added below).
    // Lazy: static initialisers of partial-class files run in unspecified order, and this one needs the verb sets.
    private static readonly Lazy<HashSet<string>> ReservedWords = new(BuildReserved);

    private static readonly HashSet<string> ClassWords = new(StringComparer.Ordinal)
    {
        "NUMERIC", "ALPHABETIC", "ALPHABETIC-LOWER", "ALPHABETIC-UPPER", "DBCS", "KANJI", "OMITTED",
    };

    private Expr? _impliedSubject;
    private RelationalOperator _impliedOperator;

    private static HashSet<string> BuildReserved()
    {
        var words = new HashSet<string>(StringComparer.Ordinal)
        {
            "ACCESS", "ADDRESS", "ADVANCING", "AFTER", "ALL", "ALPHABET", "ALPHABETIC", "ALPHABETIC-LOWER", "ALPHABETIC-UPPER",
            "ALPHANUMERIC", "ALPHANUMERIC-EDITED", "ALSO", "AND", "ANY", "ARE", "AREA", "AREAS", "ASCENDING", "ASSIGN", "AT",
            "BEFORE", "BINARY", "BY", "CHARACTER", "CHARACTERS", "CLASS", "CODE-SET", "COLLATING", "CONTENT", "CONVERTING",
            "CORR", "CORRESPONDING", "COUNT", "CYCLE", "DATA", "DBCS", "DECLARATIVES", "DEFAULT", "DELIMITED", "DELIMITER",
            "DEPENDING", "DESCENDING", "DIVISION", "DOWN", "DUPLICATES", "EGCS", "ELSE", "END", "END-OF-PAGE", "EOP", "EQUAL",
            "EQUALS", "ERROR", "EXCEPTION", "EXTEND", "FALSE", "FILE", "FILLER", "FIRST", "FOR", "FROM", "FUNCTION", "GIVING",
            "GREATER", "I-O", "IN", "INITIAL", "INPUT", "INTO", "INVALID", "IS", "KANJI", "KEY", "LEADING", "LENGTH", "LESS",
            "LINE", "LINES", "LOCK", "MODE", "NATIONAL", "NATIONAL-EDITED", "NEGATIVE", "NEXT", "NO", "NOT", "NUMERIC",
            "NUMERIC-EDITED", "OF", "OFF", "OMITTED", "ON", "OR", "ORDER", "OTHER", "OUTPUT", "OVERFLOW", "PAGE", "POINTER",
            "POSITIVE", "PREVIOUS", "PROCEDURE", "PROCEED", "RECORD", "REEL", "REFERENCE", "REMAINDER", "REMOVAL", "REPLACING",
            "REVERSED", "REWIND", "ROUNDED", "RUN", "SECTION", "SENTENCE", "SEQUENCE", "SIZE", "STATUS", "TALLYING", "TEST",
            "THAN", "THEN", "THROUGH", "THRU", "TIMES", "TO", "TRUE", "UNIT", "UNTIL", "UP", "UPON", "USING", "VALUE",
            "VARYING", "WHEN", "WITH", "RETURNING", "USE",
        };
        words.UnionWith(SupportedVerbs);
        words.UnionWith(UnsupportedVerbs);
        return words;
    }

    private static bool IsReserved(string upper) => ReservedWords.Value.Contains(upper) || ScopeTerminators.Contains(upper);

    internal static bool IsReservedWord(string upper) => IsReserved(upper);

    private bool IsIdentifierStart(int i)
    {
        if (!IsWordToken(i))
        {
            return false;
        }

        var w = Up(i);
        if (w is "LENGTH" or "ADDRESS")
        {
            return IsWord(i + 1, "OF");
        }

        return !IsReserved(w);
    }

    private bool IsOperandStart(int i)
    {
        if (i >= _ts.Count)
        {
            return false;
        }

        var t = _ts[i];
        return t.IsLiteral || t.Kind == TokenKind.Figurative || IsIdentifierStart(i) || IsWord(i, "FUNCTION");
    }

    // Literal, figurative constant, identifier, FUNCTION call, LENGTH OF / ADDRESS OF; no arithmetic.
    private Expr ParseOperand()
    {
        if (AtEnd)
        {
            throw Expected("an operand");
        }

        var first = _pos;
        var t = Cur;
        if (t.IsLiteral)
        {
            Next();
            return new LiteralExpr(SpanFrom(first), t.Kind, _ts.Text(t));
        }

        if (t.Kind == TokenKind.Figurative)
        {
            Next();
            if (t.Figurative == FigurativeKind.All)
            {
                var of = ParseOperand();
                return new FigurativeExpr(SpanFrom(first), FigurativeKind.All, of);
            }

            return new FigurativeExpr(SpanFrom(first), t.Figurative, null);
        }

        if (IsWord(_pos, "FUNCTION"))
        {
            return ParseFunction();
        }

        return ParseIdentifierOrSpecial();
    }

    private Expr ParseIdentifierOrSpecial()
    {
        var first = _pos;
        if ((IsWord(_pos, "LENGTH") || IsWord(_pos, "ADDRESS")) && IsWord(_pos + 1, "OF"))
        {
            var op = Up(_pos) == "LENGTH" ? SpecialOperator.LengthOf : SpecialOperator.AddressOf;
            Next();
            Next();
            var operand = ParseIdentifierOrSpecial();
            return new SpecialRef(SpanFrom(first), op, operand);
        }

        if ((IsWord(_pos, "DFHRESP") || IsWord(_pos, "DFHVALUE")) && _pos + 3 < _ts.Count && _ts[_pos + 1].Kind == TokenKind.LeftParen && _ts[_pos + 3].Kind == TokenKind.RightParen)
        {
            var fn = Up(_pos);
            Next();
            Next();
            var arg = Up(_pos);
            Next();
            Next();
            return new CicsValue(SpanFrom(first), fn, arg);
        }

        return ParseDataRef(RefKind.Data);
    }

    private DataRef ParseDataRef(RefKind kind)
    {
        if (!IsWordToken(_pos) || IsReserved(Up(_pos)))
        {
            throw Expected(kind == RefKind.File ? "a file name" : "a data name");
        }

        var first = _pos;
        var name = Up(_pos);
        Next();
        List<string>? qualifiers = null;
        while ((IsWord(_pos, "OF") || IsWord(_pos, "IN")) && IsWordToken(_pos + 1) && !IsReserved(Up(_pos + 1)))
        {
            Next();
            (qualifiers ??= new List<string>()).Add(Up(_pos));
            Next();
        }

        var subscripts = new List<Expr>();
        Expr? refStart = null;
        Expr? refLength = null;
        if (!AtEnd && Cur.Kind == TokenKind.LeftParen && !ParenHasColon(_pos))
        {
            Next();
            while (true)
            {
                Sep();
                if (AtEnd)
                {
                    throw Expected(") after subscripts");
                }

                if (Cur.Kind == TokenKind.RightParen)
                {
                    Next();
                    break;
                }

                if (IsWord(_pos, "ALL"))
                {
                    var a = _pos;
                    Next();
                    subscripts.Add(new AllSubscript(SpanFrom(a)));
                    continue;
                }

                subscripts.Add(ParseExpression());
            }

            if (subscripts.Count == 0)
            {
                throw Expected("a subscript");
            }
        }

        if (!AtEnd && Cur.Kind == TokenKind.LeftParen && ParenHasColon(_pos))
        {
            (refStart, refLength) = ParseRefMod();
        }

        var r = new DataRef(SpanFrom(first), new QualifiedName(name, (IReadOnlyList<string>?)qualifiers ?? Array.Empty<string>()), subscripts, refStart, refLength)
        {
            Kind = kind,
            AllSubscript = subscripts.Any(s => s is AllSubscript),
            Range = RangeOf(MakeSpan(first, first + 1)),
        };
        _dataRefs.Add(r);
        return r;
    }

    private (Expr Start, Expr? Length) ParseRefMod()
    {
        Next();
        var start = ParseExpression();
        if (AtEnd || Cur.Kind != TokenKind.Colon)
        {
            throw Expected(": in reference modification");
        }

        Next();
        Expr? length = null;
        if (!AtEnd && Cur.Kind != TokenKind.RightParen)
        {
            length = ParseExpression();
        }

        if (AtEnd || Cur.Kind != TokenKind.RightParen)
        {
            throw Expected(") after reference modification");
        }

        Next();
        return (start, length);
    }

    private Expr ParseFunction()
    {
        var first = _pos;
        Next();
        if (!IsWordToken(_pos))
        {
            throw Expected("a function name");
        }

        var name = Up(_pos);
        Next();
        var args = new List<Expr>();
        if (!AtEnd && Cur.Kind == TokenKind.LeftParen && !ParenHasColon(_pos))
        {
            Next();
            while (true)
            {
                Sep();
                if (AtEnd)
                {
                    throw Expected(") after function arguments");
                }

                if (Cur.Kind == TokenKind.RightParen)
                {
                    Next();
                    break;
                }

                args.Add(ParseExpression());
            }
        }

        Expr? refStart = null;
        Expr? refLength = null;
        if (!AtEnd && Cur.Kind == TokenKind.LeftParen && ParenHasColon(_pos))
        {
            (refStart, refLength) = ParseRefMod();
        }

        return new FunctionCall(SpanFrom(first), name, args, refStart, refLength);
    }

    private int MatchingParen(int i)
    {
        var depth = 0;
        for (; i < _ts.Count; i++)
        {
            var k = _ts[i].Kind;
            if (k == TokenKind.LeftParen)
            {
                depth++;
            }
            else if (k == TokenKind.RightParen && --depth == 0)
            {
                return i;
            }
            else if (k is TokenKind.Period or TokenKind.ExecBlock)
            {
                return -1;
            }
        }

        return -1;
    }

    private bool ParenHasColon(int i)
    {
        var depth = 0;
        for (; i < _ts.Count; i++)
        {
            var k = _ts[i].Kind;
            if (k == TokenKind.LeftParen)
            {
                depth++;
            }
            else if (k == TokenKind.RightParen)
            {
                if (--depth == 0)
                {
                    return false;
                }
            }
            else if (k == TokenKind.Colon && depth == 1)
            {
                return true;
            }
            else if (k is TokenKind.Period or TokenKind.ExecBlock)
            {
                return false;
            }
        }

        return false;
    }

    // Index after an identifier (name, qualifiers, parenthesised parts) starting at i, or i if there is none.
    private int SkipIdentifier(int i)
    {
        if (!IsWordToken(i) || IsReserved(Up(i)))
        {
            return i;
        }

        i++;
        while ((IsWord(i, "OF") || IsWord(i, "IN")) && IsWordToken(i + 1))
        {
            i += 2;
        }

        while (i < _ts.Count && _ts[i].Kind == TokenKind.LeftParen)
        {
            var close = MatchingParen(i);
            if (close < 0)
            {
                return i;
            }

            i = close + 1;
        }

        return i;
    }

    private Expr ParseExpression()
    {
        var first = _pos;
        var left = ParseMultiplicative(null);
        while (!AtEnd)
        {
            ArithmeticOperator op;
            Expr right;
            if (Cur.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                op = Cur.Kind == TokenKind.Plus ? ArithmeticOperator.Add : ArithmeticOperator.Subtract;
                Next();
                right = ParseMultiplicative(null);
            }
            else if (Cur.Kind is TokenKind.IntegerLiteral or TokenKind.DecimalLiteral && _ts.Text(_pos)[0] is '+' or '-')
            {
                // "B -1" lexes as B and the literal -1; after an operand it can only be a subtraction.
                var t = Cur;
                var text = _ts.Text(t);
                op = text[0] == '+' ? ArithmeticOperator.Add : ArithmeticOperator.Subtract;
                AddDiagnostic("signed-literal-as-operator", DiagnosticSeverity.Info, $"'{text}' after an operand is read as '{text[0]} {text[1..]}'", _pos, _pos + 1, "LIT");
                var at = _pos;
                Next();
                right = ParseMultiplicative(new LiteralExpr(SpanFrom(at), t.Kind, text[1..]));
            }
            else
            {
                break;
            }

            left = new BinaryExpr(SpanFrom(first), op, left, right);
        }

        return left;
    }

    private Expr ParseMultiplicative(Expr? firstFactor)
    {
        var first = firstFactor?.Span.FirstToken ?? _pos;
        var left = ParsePower(firstFactor);
        while (!AtEnd && Cur.Kind is TokenKind.Asterisk or TokenKind.Slash)
        {
            var op = Cur.Kind == TokenKind.Asterisk ? ArithmeticOperator.Multiply : ArithmeticOperator.Divide;
            Next();
            var right = ParsePower(null);
            left = new BinaryExpr(SpanFrom(first), op, left, right);
        }

        return left;
    }

    // COBOL evaluates ** left to right, like the other binary operators.
    private Expr ParsePower(Expr? firstFactor)
    {
        var first = firstFactor?.Span.FirstToken ?? _pos;
        var left = firstFactor ?? ParseUnary();
        while (!AtEnd && Cur.Kind == TokenKind.Power)
        {
            Next();
            var right = ParseUnary();
            left = new BinaryExpr(SpanFrom(first), ArithmeticOperator.Power, left, right);
        }

        return left;
    }

    private Expr ParseUnary()
    {
        var first = _pos;
        if (!AtEnd && Cur.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Cur.Kind == TokenKind.Plus ? '+' : '-';
            Next();
            var operand = ParseUnary();
            return new UnaryExpr(SpanFrom(first), op, operand);
        }

        if (!AtEnd && Cur.Kind == TokenKind.LeftParen)
        {
            Next();
            var inner = ParseExpression();
            if (AtEnd || Cur.Kind != TokenKind.RightParen)
            {
                throw Expected(") in arithmetic expression");
            }

            Next();
            return inner;
        }

        if (!IsOperandStart(_pos))
        {
            throw Expected("an arithmetic operand");
        }

        return ParseOperand();
    }

    private Condition ParseCondition()
    {
        var saved = (_impliedSubject, _impliedOperator);
        _impliedSubject = null;
        try
        {
            return ParseOr(afterLogical: false);
        }
        finally
        {
            (_impliedSubject, _impliedOperator) = saved;
        }
    }

    private Condition ParseOr(bool afterLogical)
    {
        var first = _pos;
        var left = ParseAnd(afterLogical);
        while (IsWord(_pos, "OR"))
        {
            Next();
            var right = ParseAnd(afterLogical: true);
            left = new OrCondition(SpanFrom(first), left, right);
        }

        return left;
    }

    private Condition ParseAnd(bool afterLogical)
    {
        var first = _pos;
        var left = ParseNot(afterLogical);
        while (IsWord(_pos, "AND"))
        {
            Next();
            var right = ParseNot(afterLogical: true);
            left = new AndCondition(SpanFrom(first), left, right);
        }

        return left;
    }

    private Condition ParseNot(bool afterLogical)
    {
        // In an abbreviated relation "NOT >" is the operator, not a logical NOT.
        if (IsWord(_pos, "NOT") && !(afterLogical && _impliedSubject is not null && IsRelationalOperatorAt(_pos)))
        {
            var first = _pos;
            Next();
            var operand = ParseNot(afterLogical);
            return new NotCondition(SpanFrom(first), operand);
        }

        return ParseSimpleCondition(afterLogical);
    }

    private Condition ParseSimpleCondition(bool afterLogical)
    {
        var first = _pos;
        if (!AtEnd && Cur.Kind == TokenKind.LeftParen && !IsParenthesisedExpression(_pos))
        {
            Next();
            var inner = ParseOr(afterLogical);
            if (AtEnd || Cur.Kind != TokenKind.RightParen)
            {
                throw Expected(") in condition");
            }

            Next();
            return inner;
        }

        if (afterLogical && _impliedSubject is not null && IsRelationalOperatorAt(_pos))
        {
            var op = ParseRelationalOperator()!.Value;
            var right = ParseExpression();
            _impliedOperator = op;
            return new RelationCondition(SpanFrom(first), _impliedSubject, op, right, Abbreviated: true);
        }

        if (_idms && IdmsSetTest(_pos) is { } test)
        {
            return test;
        }

        var subject = ParseExpression();
        return ParseConditionTail(first, subject, afterLogical);
    }

    private Condition ParseConditionTail(int first, Expr subject, bool afterLogical)
    {
        var op = ParseRelationalOperator();
        if (op is not null)
        {
            _impliedSubject = subject;
            _impliedOperator = op.Value;
            // IBM accepts a parenthesised object list: X = (A OR B) means X = A OR X = B.
            if (!AtEnd && Cur.Kind == TokenKind.LeftParen && HasTopLevelLogical(_pos))
            {
                Next();
                var objects = ParseOr(afterLogical: true);
                if (AtEnd || Cur.Kind != TokenKind.RightParen)
                {
                    throw Expected(") in abbreviated condition");
                }

                Next();
                _impliedSubject = subject;
                _impliedOperator = op.Value;
                return objects;
            }

            var right = ParseExpression();
            return new RelationCondition(SpanFrom(first), subject, op.Value, right, Abbreviated: false);
        }

        var k = _pos;
        if (IsWord(k, "IS"))
        {
            k++;
        }

        var not = IsWord(k, "NOT");
        if (not)
        {
            k++;
        }

        if (k < _ts.Count)
        {
            var t = _ts[k];
            var isClass = t.Kind == TokenKind.Word && (ClassWords.Contains(Up(k)) || _classNames.Contains(Up(k)));
            var isSign = (t.Kind == TokenKind.Word && Up(k) is "POSITIVE" or "NEGATIVE") || t.Figurative == FigurativeKind.Zero;
            if (isClass || isSign)
            {
                while (_pos <= k)
                {
                    Next();
                }

                var word = t.Kind == TokenKind.Figurative ? "ZERO" : Up(k);
                return isClass
                    ? new ClassCondition(SpanFrom(first), subject, word, not)
                    : new SignCondition(SpanFrom(first), subject, word, not);
            }
        }

        if (subject is DataRef d && (!afterLogical || _impliedSubject is null || _conditionNames.Contains(d.Name.Name)))
        {
            return new ConditionNameCondition(SpanFrom(first), d);
        }

        if (afterLogical && _impliedSubject is not null)
        {
            return new RelationCondition(SpanFrom(first), _impliedSubject, _impliedOperator, subject, Abbreviated: true);
        }

        throw new ParseFailure("invalid-condition", "Expected a condition");
    }

    private bool HasTopLevelLogical(int open)
    {
        var close = MatchingParen(open);
        var depth = 0;
        for (var k = open + 1; k < close; k++)
        {
            var kind = _ts[k].Kind;
            if (kind == TokenKind.LeftParen)
            {
                depth++;
            }
            else if (kind == TokenKind.RightParen)
            {
                depth--;
            }
            else if (depth == 0 && (IsWord(k, "OR") || IsWord(k, "AND")))
            {
                return true;
            }
        }

        return false;
    }

    // '(' starts an arithmetic expression when what follows its matching ')' continues an expression or relation.
    private bool IsParenthesisedExpression(int i)
    {
        var close = MatchingParen(i);
        if (close < 0)
        {
            return false;
        }

        var n = close + 1;
        if (n >= _ts.Count)
        {
            return false;
        }

        var k = _ts[n].Kind;
        return k is TokenKind.Plus or TokenKind.Minus or TokenKind.Asterisk or TokenKind.Slash or TokenKind.Power
            || IsRelationalOperatorAt(n)
            || (k is TokenKind.IntegerLiteral or TokenKind.DecimalLiteral && _ts.Text(n)[0] is '+' or '-');
    }

    private bool IsRelationalOperatorAt(int i)
    {
        if (IsWord(i, "IS"))
        {
            i++;
        }

        if (IsWord(i, "NOT"))
        {
            i++;
        }

        if (i >= _ts.Count)
        {
            return false;
        }

        return _ts[i].Kind is TokenKind.Equal or TokenKind.Greater or TokenKind.Less or TokenKind.GreaterOrEqual or TokenKind.LessOrEqual or TokenKind.NotEqual
            || IsWord(i, "GREATER") || IsWord(i, "LESS") || IsWord(i, "EQUAL") || IsWord(i, "EQUALS");
    }

    private RelationalOperator? ParseRelationalOperator()
    {
        if (!IsRelationalOperatorAt(_pos))
        {
            return null;
        }

        AcceptWord("IS");
        var not = AcceptWord("NOT");
        RelationalOperator op;
        switch (Cur.Kind)
        {
            case TokenKind.Equal:
                op = RelationalOperator.Equal;
                Next();
                break;
            case TokenKind.Greater:
                op = RelationalOperator.Greater;
                Next();
                break;
            case TokenKind.Less:
                op = RelationalOperator.Less;
                Next();
                break;
            case TokenKind.GreaterOrEqual:
                op = RelationalOperator.GreaterOrEqual;
                Next();
                break;
            case TokenKind.LessOrEqual:
                op = RelationalOperator.LessOrEqual;
                Next();
                break;
            case TokenKind.NotEqual:
                op = RelationalOperator.NotEqual;
                Next();
                break;
            default:
                var w = Up(_pos);
                Next();
                if (w is "EQUAL" or "EQUALS")
                {
                    AcceptWord("TO");
                    op = RelationalOperator.Equal;
                    break;
                }

                AcceptWord("THAN");
                var orEqual = IsWord(_pos, "OR") && IsWord(_pos + 1, "EQUAL");
                if (orEqual)
                {
                    Next();
                    Next();
                    AcceptWord("TO");
                }

                op = w == "GREATER"
                    ? orEqual ? RelationalOperator.GreaterOrEqual : RelationalOperator.Greater
                    : orEqual ? RelationalOperator.LessOrEqual : RelationalOperator.Less;
                break;
        }

        if (!not)
        {
            return op;
        }

        return op switch
        {
            RelationalOperator.Equal => RelationalOperator.NotEqual,
            RelationalOperator.NotEqual => RelationalOperator.Equal,
            RelationalOperator.Greater => RelationalOperator.LessOrEqual,
            RelationalOperator.Less => RelationalOperator.GreaterOrEqual,
            RelationalOperator.GreaterOrEqual => RelationalOperator.Less,
            _ => RelationalOperator.Greater,
        };
    }
}
