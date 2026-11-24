using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Syntax;

internal sealed partial class Parser
{
    private static readonly HashSet<string> SupportedVerbs = new(StringComparer.Ordinal)
    {
        "ACCEPT", "ADD", "ALTER", "CALL", "CANCEL", "CLOSE", "COMPUTE", "CONTINUE", "DELETE", "DISPLAY", "DIVIDE",
        "ENTRY", "EVALUATE", "EXIT", "GO", "GOBACK", "IF", "INITIALIZE", "INSPECT", "MERGE", "MOVE", "MULTIPLY",
        "OPEN", "PERFORM", "READ", "RELEASE", "RETURN", "REWRITE", "SEARCH", "SET", "SORT", "START", "STOP",
        "STRING", "SUBTRACT", "UNSTRING", "WRITE",
    };

    // Valid statements Starforge does not model yet; they become explicit error nodes with a stable fingerprint.
    private static readonly HashSet<string> UnsupportedVerbs = new(StringComparer.Ordinal)
    {
        "XML", "JSON", "INVOKE", "ALLOCATE", "FREE", "GENERATE", "INITIATE", "TERMINATE", "SUPPRESS", "EXAMINE",
        "TRANSFORM", "EXHIBIT",
    };

    private readonly Dictionary<int, Exec.ExecStatement> _execs = new();
    private readonly List<DataRef> _dataRefs = new();
    private readonly List<ProcedureRef> _procRefs = new();
    private readonly HashSet<string> _conditionNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _classNames = new(StringComparer.OrdinalIgnoreCase);
    private string?[]? _upper;
    private string? _curSection;
    private string? _curParagraph;
    private bool _inDeclaratives;

    // EXEC blocks parsed while building the procedure tree, by token index; ExecPass reuses them.
    public IReadOnlyDictionary<int, Exec.ExecStatement> ParsedExecs => _execs;

    private sealed class ParseFailure : Exception
    {
        public ParseFailure(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }

    private string Up(int i)
    {
        _upper ??= new string?[_ts.Count];
        return _upper[i] ??= _ts.Text(i).ToUpperInvariant();
    }

    private bool IsWordToken(int i) => i < _ts.Count && _ts[i].Kind == TokenKind.Word;

    private bool IsStatementStart(int i)
    {
        if (i >= _ts.Count)
        {
            return false;
        }

        if (_ts[i].Kind == TokenKind.ExecBlock)
        {
            return true;
        }

        if (_ts[i].Kind != TokenKind.Word)
        {
            return false;
        }

        var w = Up(i);
        return w == "NEXT" ? IsWord(i + 1, "SENTENCE") : SupportedVerbs.Contains(w) || UnsupportedVerbs.Contains(w);
    }

    private static readonly HashSet<string> ScopeTerminators = new(StringComparer.Ordinal)
    {
        "END-ACCEPT", "END-ADD", "END-CALL", "END-COMPUTE", "END-DELETE", "END-DISPLAY", "END-DIVIDE", "END-EVALUATE",
        "END-EXEC", "END-IF", "END-INVOKE", "END-JSON", "END-MULTIPLY", "END-PERFORM", "END-READ", "END-RECEIVE",
        "END-RETURN", "END-REWRITE", "END-SEARCH", "END-START", "END-STRING", "END-SUBTRACT", "END-UNSTRING", "END-WRITE",
        "END-XML",
    };

    private bool IsScopeTerminator(int i) => IsWordToken(i) && ScopeTerminators.Contains(Up(i));

    private bool IsSectionStart(int i)
        => IsWordToken(i) && IsWord(i + 1, "SECTION") && !IsStatementStart(i)
            && (IsPeriod(i + 2) || (i + 2 < _ts.Count && _ts[i + 2].Kind == TokenKind.IntegerLiteral && IsPeriod(i + 3)));

    private bool IsParagraphHeader(int i)
        => i < _ts.Count && IsPeriod(i + 1)
            && ((_ts[i].Kind == TokenKind.Word && !IsReserved(Up(i))) || (_ts[i].Kind == TokenKind.IntegerLiteral && char.IsDigit(_ts.Text(i)[0])));

    private bool IsEndDeclaratives(int i) => IsWord(i, "END") && IsWord(i + 1, "DECLARATIVES");

    private bool BodyStop(int i) => i >= _ts.Count || IsProcedureBoundary(i) || (_inDeclaratives && IsEndDeclaratives(i));

    // Where error recovery inside the procedure body resumes.
    private bool ProcStop(int i)
        => i >= _ts.Count || IsPeriod(i) || IsStatementStart(i) || IsScopeTerminator(i) || IsWord(i, "ELSE") || IsWord(i, "WHEN")
            || BodyStop(i) || IsSectionStart(i);

    private void Sep()
    {
        while (!AtEnd && Cur.Kind is TokenKind.Comma or TokenKind.Semicolon)
        {
            Next();
        }
    }

    private void SetupProcedureContext(DataDivision? data, EnvironmentDivision? env)
    {
        // Condition names of enclosing programs stay visible: nested programs may test GLOBAL 88-levels.
        foreach (var e in data?.Sections.SelectMany(s => s.Entries.Concat(s.Files.SelectMany(f => f.Records))).SelectMany(r => r.SelfAndDescendants()) ?? Enumerable.Empty<DataEntry>())
        {
            if (e.Level == 88 && e.Name is not null)
            {
                _conditionNames.Add(e.Name);
            }
        }

        foreach (var c in env?.Configuration?.SpecialNames?.Classes ?? Array.Empty<ClassDefinition>())
        {
            _classNames.Add(c.Name);
        }
    }

    private (IReadOnlyList<ProcedureSection> Declaratives, IReadOnlyList<ProcedureSection> Sections) ParseProcedureBody()
    {
        _curSection = null;
        _curParagraph = null;
        var declaratives = new List<ProcedureSection>();
        if (IsWord(_pos, "DECLARATIVES") && IsPeriod(_pos + 1))
        {
            Next();
            Next();
            _inDeclaratives = true;
            while (!BodyStop(_pos))
            {
                if (IsSectionStart(_pos))
                {
                    declaratives.Add(ParseSection(named: true));
                }
                else
                {
                    var err = SkipError("expected-declarative-section", "DECLARATIVES must consist of sections", i => BodyStop(i) || IsSectionStart(i));
                    declaratives.Add(new ProcedureSection(err.Span, err.Range, null, null, null, Array.Empty<Sentence>(), Array.Empty<Paragraph>(), ProgramLine(err.Span.FirstToken, false), ProgramLine(err.Span.EndToken - 1, true)));
                }
            }

            _inDeclaratives = false;
            if (IsEndDeclaratives(_pos))
            {
                Next();
                Next();
                ExpectPeriod("END DECLARATIVES");
            }
            else
            {
                AddDiagnostic("missing-end-declaratives", DiagnosticSeverity.Error, "DECLARATIVES is not closed by END DECLARATIVES", Math.Max(0, _pos - 1), _pos, "DECLARATIVES");
            }
        }

        var sections = new List<ProcedureSection>();
        if (!BodyStop(_pos) && !IsSectionStart(_pos))
        {
            sections.Add(ParseSection(named: false));
        }

        while (!BodyStop(_pos))
        {
            sections.Add(ParseSection(named: true));
        }

        return (declaratives, sections);
    }

    private ProcedureSection ParseSection(bool named)
    {
        var first = _pos;
        string? name = null;
        int? priority = null;
        UseStatement? use = null;
        if (named)
        {
            name = Up(_pos);
            Next();
            Next();
            if (TryInteger(out var p))
            {
                priority = p;
            }

            ExpectPeriod("SECTION");
            if (_inDeclaratives && IsWord(_pos, "USE"))
            {
                var useFirst = _pos;
                while (!AtEnd && !IsPeriod(_pos) && !BodyStop(_pos))
                {
                    Next();
                }

                use = new UseStatement(SpanFrom(useFirst), string.Join(" ", Enumerable.Range(useFirst, _pos - useFirst).Select(Up)));
                ExpectPeriod("USE");
            }
        }

        _curSection = name;
        _curParagraph = null;
        var leading = ParseSentences();
        var paragraphs = new List<Paragraph>();
        while (!BodyStop(_pos) && !IsSectionStart(_pos))
        {
            paragraphs.Add(ParseParagraph());
        }

        var span = SpanFrom(first);
        return new ProcedureSection(span, RangeOf(span), name, priority, use, leading, paragraphs, ProgramLine(first, false), ProgramLine(Math.Max(first, _pos - 1), true));
    }

    private Paragraph ParseParagraph()
    {
        var first = _pos;
        var name = Up(_pos);
        Next();
        Next();
        _curParagraph = name;
        var sentences = ParseSentences();
        var span = SpanFrom(first);
        return new Paragraph(span, RangeOf(span), name, sentences, ProgramLine(first, false), ProgramLine(Math.Max(first, _pos - 1), true));
    }

    private List<Sentence> ParseSentences()
    {
        var sentences = new List<Sentence>();
        while (!BodyStop(_pos) && !IsSectionStart(_pos) && !IsParagraphHeader(_pos))
        {
            sentences.Add(ParseSentence());
        }

        return sentences;
    }

    private Sentence ParseSentence()
    {
        var first = _pos;
        var statements = new List<Statement>();
        while (true)
        {
            Sep();
            if (AtEnd)
            {
                break;
            }

            if (AcceptPeriod())
            {
                break;
            }

            if (_pos > first && (BodyStop(_pos) || IsSectionStart(_pos) || (IsParagraphHeader(_pos) && InAreaA(_pos))))
            {
                AddDiagnostic("missing-period", DiagnosticSeverity.Warning, "Sentence is not ended by a separator period", _pos - 1, _pos, ShapeOf(_pos - 1, _pos));
                break;
            }

            if (IsStatementStart(_pos))
            {
                statements.Add(ParseStatement());
                continue;
            }

            var code = IsScopeTerminator(_pos) || IsWord(_pos, "ELSE") || IsWord(_pos, "WHEN") ? "unmatched-scope-word" : "expected-statement";
            var message = code == "unmatched-scope-word" ? $"{Up(_pos)} has no open statement to belong to" : "Expected a statement";
            var err = SkipError(code, message, i => i < _ts.Count && (IsPeriod(i) || IsStatementStart(i) || BodyStop(i) || IsSectionStart(i)));
            statements.Add(new ErrorStatement(err.Span, err.Range, err));
        }

        return new Sentence(SpanFrom(first), statements);
    }

    private bool InAreaA(int i)
    {
        var o = _ts.Origin(i);
        return !o.FromReplacement && o.Range.StartColumn is >= 8 and <= 11;
    }

    private List<Statement> ParseStatementList()
    {
        var list = new List<Statement>();
        while (true)
        {
            Sep();
            if (AtEnd || !IsStatementStart(_pos))
            {
                return list;
            }

            list.Add(ParseStatement());
        }
    }

    private Statement ParseStatement()
    {
        var first = _pos;
        var refMark = (_dataRefs.Count, _procRefs.Count);
        try
        {
            if (Cur.Kind == TokenKind.ExecBlock)
            {
                return ParseExec();
            }

            var verb = Up(_pos);
            if (UnsupportedVerbs.Contains(verb))
            {
                // XML/JSON GENERATE and PARSE carry a second verb word that must not end the skipped span.
                var end = MatchingTerminator(first);
                var minStop = first + (verb is "XML" or "JSON" ? 2 : 1);
                var err = SkipError("unsupported-statement", $"{verb} statement is not supported yet", i => end > 0 ? i >= end : i >= minStop && ProcStop(i));
                return new ErrorStatement(err.Span, err.Range, err);
            }

            return verb switch
            {
                "MOVE" => ParseMove(),
                "COMPUTE" => ParseCompute(),
                "ADD" => ParseArithmetic(ArithmeticVerb.Add),
                "SUBTRACT" => ParseArithmetic(ArithmeticVerb.Subtract),
                "MULTIPLY" => ParseArithmetic(ArithmeticVerb.Multiply),
                "DIVIDE" => ParseArithmetic(ArithmeticVerb.Divide),
                "IF" => ParseIf(),
                "EVALUATE" => ParseEvaluate(),
                "PERFORM" => ParsePerform(),
                "GO" => ParseGoTo(),
                "CALL" => ParseCall(),
                "CANCEL" => ParseCancel(),
                "EXIT" => ParseExit(),
                "GOBACK" => Simple(first, s => new GobackStatement(s, RangeOf(s))),
                "STOP" => ParseStop(),
                "CONTINUE" => Simple(first, s => new ContinueStatement(s, RangeOf(s))),
                "NEXT" => ParseNextSentence(),
                "INITIALIZE" => ParseInitialize(),
                "INSPECT" => ParseInspect(),
                "STRING" => ParseString(),
                "UNSTRING" => ParseUnstring(),
                "SET" => ParseSet(),
                "SEARCH" => ParseSearch(),
                "OPEN" => ParseOpen(),
                "CLOSE" => ParseClose(),
                "READ" or "WRITE" or "REWRITE" or "DELETE" or "START" or "RETURN" or "RELEASE" => ParseFileStatement(verb),
                "ACCEPT" => ParseAccept(),
                "DISPLAY" => ParseDisplay(),
                "SORT" or "MERGE" => ParseSort(verb),
                "ENTRY" => ParseEntry(),
                "ALTER" => ParseAlter(),
                _ => throw new ParseFailure("expected-statement", "Expected a statement"),
            };
        }
        catch (ParseFailure f)
        {
            return Fail(first, refMark, f.Code, f.Message);
        }
    }

    // The whole statement becomes one error node: what was consumed is re-marked Skipped, then recovery skips to ProcStop.
    private ErrorStatement Fail(int first, (int Data, int Proc) refMark, string code, string message)
    {
        for (var k = first; k < _pos; k++)
        {
            if (_marks[k] == Consumed)
            {
                _marks[k] = Skipped;
            }
        }

        _dataRefs.RemoveRange(refMark.Data, _dataRefs.Count - refMark.Data);
        _procRefs.RemoveRange(refMark.Proc, _procRefs.Count - refMark.Proc);
        var failAt = Math.Min(_pos, _ts.Count - 1);
        if (_pos == first)
        {
            _pos++;
            _marks[first] = Skipped;
        }

        var end = MatchingTerminator(first);
        while (!AtEnd && (end > 0 ? _pos < end : !ProcStop(_pos)))
        {
            if (_marks[_pos] == Untouched)
            {
                _marks[_pos] = Skipped;
            }

            _pos++;
        }

        var span = SpanFrom(first);
        var range = RangeOf(span);
        var at = RangeOf(MakeSpan(failAt, failAt + 1));
        var diagnostic = Diagnostic.Create(code, DiagnosticSeverity.Error, $"{message} (at line {at.StartLine}, column {at.StartColumn})", range, ShapeOf(first, Math.Min(_pos, first + 3)));
        Diagnostics.Add(diagnostic);
        var node = new ErrorNode(span, range, diagnostic);
        _allErrors.Add(node);
        return new ErrorStatement(span, range, node);
    }

    // For a failed statement with a scope terminator (IF ... END-IF), the index after its matching terminator when one
    // closes it before the sentence ends; otherwise 0 and recovery stops at the next statement.
    private int MatchingTerminator(int first)
    {
        if (!IsWordToken(first) || !ScopeTerminators.Contains("END-" + Up(first)))
        {
            return 0;
        }

        var verb = Up(first);
        var terminator = "END-" + verb;
        var depth = 0;
        for (var k = first; k < _ts.Count && !IsPeriod(k) && !BodyStop(k); k++)
        {
            if (IsWord(k, verb) && IsStatementStart(k))
            {
                depth++;
            }
            else if (IsWord(k, terminator) && --depth == 0)
            {
                return k + 1;
            }
        }

        return 0;
    }

    private ParseFailure Expected(string what) => new("invalid-statement", $"Expected {what}");

    private void Expect(string word)
    {
        if (!AcceptWord(word))
        {
            throw Expected(word);
        }
    }

    private T Simple<T>(int first, Func<NodeSpan, T> make)
    {
        Next();
        return make(SpanFrom(first));
    }

    private void EndScope(string terminator) => AcceptWord(terminator);

    private Statement ParseExec()
    {
        var first = _pos;
        var parsed = ParsedExec(first);
        Next();
        var span = SpanFrom(first);
        return new ExecStatementSyntax(span, RangeOf(span), _ts[first].Exec, parsed);
    }

    private Exec.ExecStatement ParsedExec(int tokenIndex)
    {
        if (!_execs.TryGetValue(tokenIndex, out var parsed))
        {
            parsed = Exec.ExecParser.Parse(_ts, tokenIndex);
            _execs[tokenIndex] = parsed;
        }

        return parsed;
    }

    private Statement ParseMove()
    {
        var first = _pos;
        Next();
        var corr = AcceptWord("CORRESPONDING") || AcceptWord("CORR");
        var source = ParseOperand();
        Expect("TO");
        var targets = ParseReceivers();
        var span = SpanFrom(first);
        return new MoveStatement(span, RangeOf(span), corr, source, targets);
    }

    private List<Expr> ParseReceivers()
    {
        var targets = new List<Expr>();
        Sep();
        while (IsIdentifierStart(_pos))
        {
            targets.Add(ParseIdentifierOrSpecial());
            Sep();
        }

        if (targets.Count == 0)
        {
            throw Expected("a receiving data item");
        }

        return targets;
    }

    private List<ArithmeticTarget> ParseArithmeticTargets()
    {
        var targets = new List<ArithmeticTarget>();
        Sep();
        while (IsIdentifierStart(_pos))
        {
            var target = ParseIdentifierOrSpecial();
            var rounded = false;
            string? mode = null;
            if (AcceptWord("ROUNDED"))
            {
                rounded = true;
                if (AcceptWord("MODE"))
                {
                    AcceptWord("IS");
                    mode = Up(_pos);
                    Next();
                }
            }

            targets.Add(new ArithmeticTarget(target, rounded, mode));
            Sep();
        }

        if (targets.Count == 0)
        {
            throw Expected("a receiving data item");
        }

        return targets;
    }

    private Statement ParseCompute()
    {
        var first = _pos;
        Next();
        var targets = ParseArithmeticTargets();
        if (!AtEnd && Cur.Kind == TokenKind.Equal)
        {
            Next();
        }
        else if (!AcceptWord("EQUAL"))
        {
            throw Expected("= in COMPUTE");
        }

        var value = ParseExpression();
        var handlers = ParseHandlers(HandlerKind.SizeError);
        EndScope("END-COMPUTE");
        var span = SpanFrom(first);
        return new ComputeStatement(span, RangeOf(span), targets, value, handlers);
    }

    private Statement ParseArithmetic(ArithmeticVerb verb)
    {
        var first = _pos;
        var verbText = Up(_pos);
        Next();
        var corr = AcceptWord("CORRESPONDING") || AcceptWord("CORR");
        var operands = new List<Expr>();
        Sep();
        while (IsOperandStart(_pos))
        {
            operands.Add(ParseOperand());
            Sep();
        }

        if (operands.Count == 0)
        {
            throw Expected($"an operand after {verbText}");
        }

        string? prep = null;
        foreach (var p in verb switch
        {
            ArithmeticVerb.Add => new[] { "TO" },
            ArithmeticVerb.Subtract => new[] { "FROM" },
            ArithmeticVerb.Multiply => new[] { "BY" },
            _ => new[] { "INTO", "BY" },
        })
        {
            if (AcceptWord(p))
            {
                prep = p;
                break;
            }
        }

        Expr? second = null;
        var giving = false;
        List<ArithmeticTarget> targets;
        if (prep is null)
        {
            if (verb != ArithmeticVerb.Add)
            {
                throw Expected($"{(verb == ArithmeticVerb.Subtract ? "FROM" : verb == ArithmeticVerb.Multiply ? "BY" : "INTO or BY")} in {verbText}");
            }

            Expect("GIVING");
            giving = true;
            targets = ParseArithmeticTargets();
        }
        else
        {
            // Literals are only valid as the second operand of a GIVING form, so look ahead before taking receivers.
            if (IsOperandStart(_pos) && !IsIdentifierStart(_pos))
            {
                second = ParseOperand();
                Sep();
                Expect("GIVING");
                giving = true;
                targets = ParseArithmeticTargets();
            }
            else
            {
                targets = ParseArithmeticTargets();
                if (AcceptWord("GIVING"))
                {
                    if (targets.Count != 1 || targets[0].Rounded)
                    {
                        throw Expected("a single operand before GIVING");
                    }

                    second = targets[0].Target;
                    giving = true;
                    targets = ParseArithmeticTargets();
                }
            }
        }

        Expr? remainder = null;
        if (verb == ArithmeticVerb.Divide && AcceptWord("REMAINDER"))
        {
            remainder = ParseIdentifierOrSpecial();
        }

        var handlers = ParseHandlers(HandlerKind.SizeError);
        EndScope("END-" + verbText);
        var span = SpanFrom(first);
        return new ArithmeticStatement(span, RangeOf(span), verb, corr, operands, prep, second, giving, targets, remainder, handlers);
    }

    private Statement ParseIf()
    {
        var first = _pos;
        Next();
        var condition = ParseCondition();
        Sep();
        AcceptWord("THEN");
        var then = ParseStatementList();
        List<Statement>? otherwise = null;
        Sep();
        if (AcceptWord("ELSE"))
        {
            otherwise = ParseStatementList();
        }

        var hasEnd = AcceptWord("END-IF");
        var span = SpanFrom(first);
        return new IfStatement(span, RangeOf(span), condition, then, otherwise, hasEnd);
    }

    private Statement ParseNextSentence()
    {
        var first = _pos;
        Next();
        Next();
        var span = SpanFrom(first);
        return new NextSentenceStatement(span, RangeOf(span));
    }

    private Statement ParseEvaluate()
    {
        var first = _pos;
        Next();
        var subjects = new List<EvaluateOperand> { ParseEvaluateSubject() };
        while (AcceptWord("ALSO"))
        {
            subjects.Add(ParseEvaluateSubject());
        }

        var whens = new List<WhenClause>();
        List<Statement>? other = null;
        Sep();
        while (IsWord(_pos, "WHEN"))
        {
            if (IsWord(_pos + 1, "OTHER"))
            {
                Next();
                Next();
                other = ParseStatementList();
                Sep();
                break;
            }

            var alternatives = new List<IReadOnlyList<EvaluateOperand>>();
            while (AcceptWord("WHEN"))
            {
                var objects = new List<EvaluateOperand>();
                for (var k = 0; k < subjects.Count || objects.Count == 0; k++)
                {
                    if (k > 0)
                    {
                        Expect("ALSO");
                    }

                    objects.Add(ParseEvaluateObject(k < subjects.Count ? subjects[k] : subjects[^1]));
                    if (!IsWord(_pos, "ALSO"))
                    {
                        break;
                    }
                }

                alternatives.Add(objects);
                Sep();
                if (IsWord(_pos, "WHEN") && IsWord(_pos + 1, "OTHER"))
                {
                    break;
                }
            }

            whens.Add(new WhenClause(alternatives, ParseStatementList()));
            Sep();
        }

        if (whens.Count == 0 && other is null)
        {
            throw Expected("WHEN in EVALUATE");
        }

        EndScope("END-EVALUATE");
        var span = SpanFrom(first);
        return new EvaluateStatement(span, RangeOf(span), subjects, whens, other);
    }

    private EvaluateOperand ParseEvaluateSubject()
    {
        if (AcceptWord("TRUE"))
        {
            return new EvaluateOperand(EvaluateOperandKind.True, null, null, null, false);
        }

        if (AcceptWord("FALSE"))
        {
            return new EvaluateOperand(EvaluateOperandKind.False, null, null, null, false);
        }

        if (LooksLikeCondition(_pos))
        {
            return new EvaluateOperand(EvaluateOperandKind.Condition, null, null, ParseCondition(), false);
        }

        return new EvaluateOperand(EvaluateOperandKind.Expression, ParseExpression(), null, null, false);
    }

    private EvaluateOperand ParseEvaluateObject(EvaluateOperand subject)
    {
        if (AcceptWord("ANY"))
        {
            return new EvaluateOperand(EvaluateOperandKind.Any, null, null, null, false);
        }

        if (AcceptWord("TRUE"))
        {
            return new EvaluateOperand(EvaluateOperandKind.True, null, null, null, false);
        }

        if (AcceptWord("FALSE"))
        {
            return new EvaluateOperand(EvaluateOperandKind.False, null, null, null, false);
        }

        if (subject.Kind is EvaluateOperandKind.True or EvaluateOperandKind.False)
        {
            return new EvaluateOperand(EvaluateOperandKind.Condition, null, null, ParseCondition(), false);
        }

        var not = AcceptWord("NOT");
        var value = ParseExpression();
        Expr? thru = null;
        if (AcceptWord("THRU") || AcceptWord("THROUGH"))
        {
            thru = ParseExpression();
        }

        return new EvaluateOperand(EvaluateOperandKind.Expression, value, thru, null, not);
    }

    // An EVALUATE subject is a condition when a relational, class, sign or logical word appears at parenthesis depth 0,
    // or when it is a lone condition-name.
    private bool LooksLikeCondition(int i)
    {
        var depth = 0;
        var start = i;
        for (; i < _ts.Count; i++)
        {
            var t = _ts[i];
            if (t.Kind == TokenKind.LeftParen)
            {
                depth++;
                continue;
            }

            if (t.Kind == TokenKind.RightParen)
            {
                depth--;
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (t.Kind is TokenKind.Period or TokenKind.ExecBlock || IsWord(i, "ALSO") || IsWord(i, "WHEN") || (i > start && IsStatementStart(i)))
            {
                break;
            }

            if (t.Kind is TokenKind.Equal or TokenKind.Greater or TokenKind.Less or TokenKind.GreaterOrEqual or TokenKind.LessOrEqual or TokenKind.NotEqual)
            {
                return true;
            }

            if (t.Kind == TokenKind.Word && Up(i) is "AND" or "OR" or "NOT" or "IS" or "GREATER" or "LESS" or "EQUAL" or "NUMERIC" or "ALPHABETIC"
                or "ALPHABETIC-LOWER" or "ALPHABETIC-UPPER" or "POSITIVE" or "NEGATIVE" or "OMITTED" or "DBCS" or "KANJI")
            {
                return true;
            }
        }

        return IsWordToken(start) && _conditionNames.Contains(Up(start));
    }

    private Statement ParsePerform()
    {
        var first = _pos;
        Next();
        ProcedureRef? target = null;
        ProcedureRef? thru = null;
        if (IsProcedureNameStart(_pos) && !IsWord(_pos + 1, "TIMES") && !LooksLikeTimes(_pos))
        {
            target = ParseProcedureRef();
            if (AcceptWord("THRU") || AcceptWord("THROUGH"))
            {
                thru = ParseProcedureRef();
            }
        }

        var kind = PerformKind.Once;
        Expr? times = null;
        var testAfter = false;
        Condition? until = null;
        var varying = new List<VaryingPhrase>();
        if (IsOperandStart(_pos) && IsWord(_pos + 1, "TIMES") || (IsIdentifierStart(_pos) && LooksLikeTimes(_pos)))
        {
            times = ParseOperand();
            Expect("TIMES");
            kind = PerformKind.Times;
        }
        else
        {
            if (IsWord(_pos, "WITH") || IsWord(_pos, "TEST"))
            {
                AcceptWord("WITH");
                Expect("TEST");
                testAfter = AcceptWord("AFTER");
                if (!testAfter)
                {
                    Expect("BEFORE");
                }
            }

            if (AcceptWord("UNTIL"))
            {
                kind = PerformKind.Until;
                until = ParseCondition();
            }
            else if (AcceptWord("VARYING"))
            {
                kind = PerformKind.Varying;
                varying.Add(ParseVarying());
                while (AcceptWord("AFTER"))
                {
                    varying.Add(ParseVarying());
                }
            }
            else if (testAfter || IsWord(_pos - 1, "BEFORE"))
            {
                throw Expected("UNTIL or VARYING after WITH TEST");
            }
        }

        List<Statement>? inline = null;
        if (target is null)
        {
            inline = ParseStatementList();
            Sep();
            if (!AcceptWord("END-PERFORM"))
            {
                throw new ParseFailure("missing-end-perform", "Inline PERFORM is not closed by END-PERFORM");
            }
        }

        var span = SpanFrom(first);
        return new PerformStatement(span, RangeOf(span), target, thru, kind, times, testAfter, until, varying, inline);
    }

    // PERFORM ... identifier(subscripts) TIMES: the TIMES word comes after the whole identifier.
    private bool LooksLikeTimes(int i)
    {
        var depth = 0;
        for (i++; i < _ts.Count; i++)
        {
            var t = _ts[i];
            if (t.Kind == TokenKind.LeftParen)
            {
                depth++;
            }
            else if (t.Kind == TokenKind.RightParen)
            {
                depth--;
            }
            else if (depth == 0)
            {
                if (IsWord(i, "OF") || IsWord(i, "IN"))
                {
                    i++;
                    continue;
                }

                return IsWord(i, "TIMES");
            }
        }

        return false;
    }

    private VaryingPhrase ParseVarying()
    {
        var variable = ParseIdentifierOrSpecial();
        Expect("FROM");
        var from = ParseExpression();
        Expect("BY");
        var by = ParseExpression();
        Expect("UNTIL");
        var until = ParseCondition();
        return new VaryingPhrase(variable, from, by, until);
    }

    private bool IsProcedureNameStart(int i)
        => i < _ts.Count
            && ((_ts[i].Kind == TokenKind.Word && !IsReserved(Up(i))) || (_ts[i].Kind == TokenKind.IntegerLiteral && char.IsDigit(_ts.Text(i)[0])));

    private ProcedureRef ParseProcedureRef()
    {
        if (!IsProcedureNameStart(_pos))
        {
            throw Expected("a procedure name");
        }

        var first = _pos;
        var name = Up(_pos);
        Next();
        string? qualifier = null;
        if ((IsWord(_pos, "OF") || IsWord(_pos, "IN")) && IsProcedureNameStart(_pos + 1))
        {
            Next();
            qualifier = Up(_pos);
            Next();
        }

        var r = new ProcedureRef(SpanFrom(first), name, qualifier, ProgramLine(first, false)) { FromSection = _curSection, FromParagraph = _curParagraph, Range = RangeOf(SpanFrom(first)) };
        _procRefs.Add(r);
        return r;
    }

    private Statement ParseGoTo()
    {
        var first = _pos;
        Next();
        AcceptWord("TO");
        var targets = new List<ProcedureRef>();
        Sep();
        while (IsProcedureNameStart(_pos))
        {
            targets.Add(ParseProcedureRef());
            Sep();
        }

        Expr? depending = null;
        if (AcceptWord("DEPENDING"))
        {
            AcceptWord("ON");
            depending = ParseIdentifierOrSpecial();
        }

        var span = SpanFrom(first);
        return new GoToStatement(span, RangeOf(span), targets, depending);
    }

    private Statement ParseAlter()
    {
        var first = _pos;
        Next();
        var list = new List<(ProcedureRef, ProcedureRef)>();
        do
        {
            Sep();
            var from = ParseProcedureRef();
            Expect("TO");
            if (AcceptWord("PROCEED"))
            {
                Expect("TO");
            }

            list.Add((from, ParseProcedureRef()));
            Sep();
        }
        while (IsProcedureNameStart(_pos));

        var span = SpanFrom(first);
        return new AlterStatement(span, RangeOf(span), list);
    }

    private Statement ParseCall()
    {
        var first = _pos;
        Next();
        Expr target;
        string? program = null;
        if (!AtEnd && Cur.IsLiteral)
        {
            program = NameText(_pos);
            target = ParseOperand();
        }
        else
        {
            target = ParseIdentifierOrSpecial();
        }

        var args = new List<CallArgument>();
        if (AcceptWord("USING"))
        {
            args = ParseCallArguments(allowContent: true);
        }

        Expr? returning = null;
        if (AcceptWord("RETURNING"))
        {
            returning = ParseIdentifierOrSpecial();
        }

        var handlers = ParseHandlers(HandlerKind.Exception, HandlerKind.Overflow);
        EndScope("END-CALL");
        var span = SpanFrom(first);
        return new CallStatement(span, RangeOf(span), target, program, args, returning, handlers);
    }

    private List<CallArgument> ParseCallArguments(bool allowContent)
    {
        var args = new List<CallArgument>();
        var mode = PassingMode.Reference;
        while (true)
        {
            Sep();
            if (AcceptWord("BY"))
            {
                continue;
            }

            if (AcceptWord("REFERENCE"))
            {
                mode = PassingMode.Reference;
            }
            else if (allowContent && AcceptWord("CONTENT"))
            {
                mode = PassingMode.Content;
            }
            else if (AcceptWord("VALUE"))
            {
                mode = PassingMode.Value;
            }
            else if (AcceptWord("OMITTED"))
            {
                args.Add(new CallArgument(mode, null));
            }
            else if (IsOperandStart(_pos))
            {
                args.Add(new CallArgument(mode, ParseOperand()));
            }
            else
            {
                break;
            }
        }

        if (args.Count == 0)
        {
            throw Expected("an argument after USING");
        }

        return args;
    }

    private Statement ParseCancel()
    {
        var first = _pos;
        Next();
        var programs = new List<Expr>();
        Sep();
        while (IsOperandStart(_pos))
        {
            programs.Add(ParseOperand());
            Sep();
        }

        if (programs.Count == 0)
        {
            throw Expected("a program name after CANCEL");
        }

        var span = SpanFrom(first);
        return new CancelStatement(span, RangeOf(span), programs);
    }

    private Statement ParseExit()
    {
        var first = _pos;
        Next();
        var kind = string.Empty;
        foreach (var k in new[] { "PROGRAM", "PARAGRAPH", "SECTION", "METHOD", "PERFORM" })
        {
            if (AcceptWord(k))
            {
                kind = k;
                if (k == "PERFORM" && AcceptWord("CYCLE"))
                {
                    kind = "PERFORM CYCLE";
                }

                break;
            }
        }

        var span = SpanFrom(first);
        return new ExitStatement(span, RangeOf(span), kind);
    }

    private Statement ParseStop()
    {
        var first = _pos;
        Next();
        Expr? literal = null;
        if (!AcceptWord("RUN"))
        {
            if (AtEnd || !(Cur.IsLiteral || Cur.Kind == TokenKind.Figurative))
            {
                throw Expected("RUN or a literal after STOP");
            }

            literal = ParseOperand();
        }

        var span = SpanFrom(first);
        return new StopStatement(span, RangeOf(span), literal);
    }

    private static readonly HashSet<string> InitializeCategories = new(StringComparer.Ordinal)
    {
        "ALPHABETIC", "ALPHANUMERIC", "ALPHANUMERIC-EDITED", "NUMERIC", "NUMERIC-EDITED", "NATIONAL", "NATIONAL-EDITED", "DBCS", "EGCS",
    };

    private Statement ParseInitialize()
    {
        var first = _pos;
        Next();
        var targets = ParseReceivers();
        var filler = false;
        string? category = null;
        var toValue = false;
        var toDefault = false;
        var replacing = new List<InitializeReplacing>();
        if (IsWord(_pos, "WITH") && IsWord(_pos + 1, "FILLER") || IsWord(_pos, "FILLER"))
        {
            AcceptWord("WITH");
            Next();
            filler = true;
        }

        if ((IsWord(_pos, "ALL") || (IsWordToken(_pos) && InitializeCategories.Contains(Up(_pos)))) && (IsWord(_pos + 1, "TO") || IsWord(_pos + 1, "VALUE")))
        {
            category = Up(_pos);
            Next();
            AcceptWord("TO");
            Expect("VALUE");
            toValue = true;
        }

        AcceptWord("THEN");
        if (AcceptWord("REPLACING"))
        {
            do
            {
                if (!IsWordToken(_pos) || !InitializeCategories.Contains(Up(_pos)))
                {
                    throw Expected("a category after REPLACING");
                }

                var cat = Up(_pos);
                Next();
                AcceptWord("DATA");
                Expect("BY");
                replacing.Add(new InitializeReplacing(cat, ParseOperand()));
                Sep();
            }
            while (IsWordToken(_pos) && InitializeCategories.Contains(Up(_pos)));
        }

        AcceptWord("THEN");
        if (AcceptWord("TO") || IsWord(_pos, "DEFAULT"))
        {
            Expect("DEFAULT");
            toDefault = true;
        }

        var span = SpanFrom(first);
        return new InitializeStatement(span, RangeOf(span), targets, filler, category, toValue, toDefault, replacing);
    }

    private Statement ParseInspect()
    {
        var first = _pos;
        Next();
        var subject = ParseIdentifierOrSpecial();
        var tallying = new List<InspectOperation>();
        var replacing = new List<InspectOperation>();
        InspectOperation? converting = null;
        if (AcceptWord("TALLYING"))
        {
            do
            {
                Sep();
                var counter = ParseIdentifierOrSpecial();
                Expect("FOR");
                do
                {
                    Sep();
                    if (AcceptWord("CHARACTERS"))
                    {
                        tallying.Add(new InspectOperation("CHARACTERS", null, null, counter, ParseInspectBoundaries()));
                        continue;
                    }

                    var mode = Up(_pos);
                    if (!(AcceptWord("ALL") || AcceptWord("LEADING") || (AtEnd ? false : Cur.Kind == TokenKind.Figurative && Cur.Figurative == FigurativeKind.All && Advance())))
                    {
                        throw Expected("CHARACTERS, ALL or LEADING");
                    }

                    do
                    {
                        Sep();
                        var pattern = ParseOperand();
                        tallying.Add(new InspectOperation(mode, pattern, null, counter, ParseInspectBoundaries()));
                        Sep();
                    }
                    while (IsOperandStart(_pos) && !IsCounterStart(_pos));
                }
                while (IsWord(_pos, "CHARACTERS") || IsWord(_pos, "ALL") || IsWord(_pos, "LEADING") || IsAllFigurative(_pos));
            }
            while (IsIdentifierStart(_pos) && IsCounterStart(_pos));
        }

        if (AcceptWord("REPLACING"))
        {
            do
            {
                Sep();
                if (AcceptWord("CHARACTERS"))
                {
                    Expect("BY");
                    var by = ParseOperand();
                    replacing.Add(new InspectOperation("CHARACTERS", null, by, null, ParseInspectBoundaries()));
                    Sep();
                    continue;
                }

                var mode = Up(_pos);
                if (!(AcceptWord("ALL") || AcceptWord("LEADING") || AcceptWord("FIRST") || (IsAllFigurative(_pos) && Advance())))
                {
                    throw Expected("CHARACTERS, ALL, LEADING or FIRST");
                }

                do
                {
                    Sep();
                    var pattern = ParseOperand();
                    Expect("BY");
                    var by = ParseOperand();
                    replacing.Add(new InspectOperation(mode, pattern, by, null, ParseInspectBoundaries()));
                    Sep();
                }
                while (IsOperandStart(_pos) && !IsAllFigurative(_pos));
            }
            while (IsWord(_pos, "CHARACTERS") || IsWord(_pos, "ALL") || IsWord(_pos, "LEADING") || IsWord(_pos, "FIRST") || IsAllFigurative(_pos));
        }

        if (tallying.Count == 0 && replacing.Count == 0 && AcceptWord("CONVERTING"))
        {
            var from = ParseOperand();
            Expect("TO");
            var to = ParseOperand();
            converting = new InspectOperation(string.Empty, from, to, null, ParseInspectBoundaries());
        }

        if (tallying.Count == 0 && replacing.Count == 0 && converting is null)
        {
            throw Expected("TALLYING, REPLACING or CONVERTING");
        }

        var span = SpanFrom(first);
        return new InspectStatement(span, RangeOf(span), subject, tallying, replacing, converting);
    }

    // ALL before a literal is lexed as Figurative.All; as an INSPECT mode it is the ALL keyword.
    private bool IsAllFigurative(int i) => i < _ts.Count && _ts[i].Kind == TokenKind.Figurative && _ts[i].Figurative == FigurativeKind.All;

    private bool Advance()
    {
        Next();
        return true;
    }

    private bool IsCounterStart(int i)
    {
        var j = SkipIdentifier(i);
        return j > i && IsWord(j, "FOR");
    }

    private List<(bool Before, bool Initial, Expr Delimiter)> ParseInspectBoundaries()
    {
        var list = new List<(bool, bool, Expr)>();
        while (IsWord(_pos, "BEFORE") || IsWord(_pos, "AFTER"))
        {
            var before = Up(_pos) == "BEFORE";
            Next();
            var initial = AcceptWord("INITIAL");
            list.Add((before, initial, ParseOperand()));
        }

        return list;
    }

    private Statement ParseString()
    {
        var first = _pos;
        Next();
        var sources = new List<StringSource>();
        Sep();
        while (IsOperandStart(_pos))
        {
            var values = new List<Expr>();
            while (IsOperandStart(_pos))
            {
                values.Add(ParseOperand());
                Sep();
            }

            Expr? delimiter = null;
            var size = false;
            if (AcceptWord("DELIMITED"))
            {
                AcceptWord("BY");
                if (AcceptWord("SIZE"))
                {
                    size = true;
                }
                else
                {
                    delimiter = ParseOperand();
                }
            }
            else
            {
                size = true;
            }

            sources.Add(new StringSource(values, delimiter, size));
            Sep();
        }

        if (sources.Count == 0)
        {
            throw Expected("a sending item in STRING");
        }

        Expect("INTO");
        var into = ParseIdentifierOrSpecial();
        Expr? pointer = null;
        if (IsWord(_pos, "WITH") && IsWord(_pos + 1, "POINTER") || IsWord(_pos, "POINTER"))
        {
            AcceptWord("WITH");
            Next();
            pointer = ParseIdentifierOrSpecial();
        }

        var handlers = ParseHandlers(HandlerKind.Overflow);
        EndScope("END-STRING");
        var span = SpanFrom(first);
        return new StringStatement(span, RangeOf(span), sources, into, pointer, handlers);
    }

    private Statement ParseUnstring()
    {
        var first = _pos;
        Next();
        var source = ParseIdentifierOrSpecial();
        var delimiters = new List<(bool, Expr)>();
        if (AcceptWord("DELIMITED"))
        {
            AcceptWord("BY");
            do
            {
                var all = AcceptWord("ALL") || (IsAllFigurative(_pos) && Advance());
                delimiters.Add((all, ParseOperand()));
                Sep();
            }
            while (AcceptWord("OR"));
        }

        Expect("INTO");
        var targets = new List<UnstringTarget>();
        Sep();
        while (IsIdentifierStart(_pos))
        {
            var into = ParseIdentifierOrSpecial();
            Expr? delimIn = null;
            Expr? countIn = null;
            if (AcceptWord("DELIMITER"))
            {
                AcceptWord("IN");
                delimIn = ParseIdentifierOrSpecial();
            }

            if (AcceptWord("COUNT"))
            {
                AcceptWord("IN");
                countIn = ParseIdentifierOrSpecial();
            }

            targets.Add(new UnstringTarget(into, delimIn, countIn));
            Sep();
        }

        if (targets.Count == 0)
        {
            throw Expected("a receiving item after INTO");
        }

        Expr? pointer = null;
        if (IsWord(_pos, "WITH") && IsWord(_pos + 1, "POINTER") || IsWord(_pos, "POINTER"))
        {
            AcceptWord("WITH");
            Next();
            pointer = ParseIdentifierOrSpecial();
        }

        Expr? tallying = null;
        if (AcceptWord("TALLYING"))
        {
            AcceptWord("IN");
            tallying = ParseIdentifierOrSpecial();
        }

        var handlers = ParseHandlers(HandlerKind.Overflow);
        EndScope("END-UNSTRING");
        var span = SpanFrom(first);
        return new UnstringStatement(span, RangeOf(span), source, delimiters, targets, pointer, tallying, handlers);
    }

    private Statement ParseSet()
    {
        var first = _pos;
        Next();
        var targets = ParseReceivers();
        SetKind kind;
        Expr? value = null;
        string? onOff = null;
        if (AcceptWord("TO"))
        {
            if (AcceptWord("TRUE"))
            {
                kind = SetKind.ToTrue;
            }
            else if (AcceptWord("FALSE"))
            {
                kind = SetKind.ToFalse;
            }
            else if (IsWord(_pos, "ON") || IsWord(_pos, "OFF"))
            {
                kind = SetKind.ToOnOff;
                onOff = Up(_pos);
                Next();
            }
            else
            {
                value = ParseOperand();
                kind = value is SpecialRef { Operator: SpecialOperator.AddressOf } || targets.Any(t => t is SpecialRef { Operator: SpecialOperator.AddressOf })
                    ? SetKind.ToAddress
                    : SetKind.To;
            }
        }
        else if (IsWord(_pos, "UP") || IsWord(_pos, "DOWN"))
        {
            kind = Up(_pos) == "UP" ? SetKind.UpBy : SetKind.DownBy;
            Next();
            Expect("BY");
            value = ParseOperand();
        }
        else
        {
            throw Expected("TO, UP BY or DOWN BY in SET");
        }

        var span = SpanFrom(first);
        return new SetStatement(span, RangeOf(span), kind, targets, value, onOff);
    }

    private Statement ParseSearch()
    {
        var first = _pos;
        Next();
        var all = AcceptWord("ALL") || (IsAllFigurative(_pos) && Advance());
        var table = ParseDataRef(RefKind.Data);
        Expr? varying = null;
        if (AcceptWord("VARYING"))
        {
            varying = ParseIdentifierOrSpecial();
        }

        List<Statement>? atEnd = null;
        Sep();
        if (IsWord(_pos, "AT") && IsWord(_pos + 1, "END") || IsWord(_pos, "END") && !IsWord(_pos + 1, "PROGRAM"))
        {
            AcceptWord("AT");
            Next();
            atEnd = ParseStatementList();
        }

        var whens = new List<SearchWhen>();
        Sep();
        while (AcceptWord("WHEN"))
        {
            var cond = ParseCondition();
            whens.Add(new SearchWhen(cond, ParseStatementList()));
            Sep();
        }

        if (whens.Count == 0)
        {
            throw Expected("WHEN in SEARCH");
        }

        EndScope("END-SEARCH");
        var span = SpanFrom(first);
        return new SearchStatement(span, RangeOf(span), all, table, varying, atEnd, whens);
    }

    private Statement ParseOpen()
    {
        var first = _pos;
        Next();
        var files = new List<OpenFile>();
        while (true)
        {
            Sep();
            var mode = AtEnd ? string.Empty : Up(_pos);
            if (mode is not ("INPUT" or "OUTPUT" or "I-O" or "EXTEND"))
            {
                break;
            }

            Next();
            var any = false;
            Sep();
            while (IsIdentifierStart(_pos))
            {
                files.Add(new OpenFile(mode, ParseDataRef(RefKind.File)));
                any = true;
                if (AcceptWord("REVERSED") || (IsWord(_pos, "WITH") && IsWord(_pos + 1, "NO")) || IsWord(_pos, "NO"))
                {
                    AcceptWord("WITH");
                    AcceptWord("NO");
                    AcceptWord("REWIND");
                }

                Sep();
            }

            if (!any)
            {
                throw Expected($"a file name after {mode}");
            }
        }

        if (files.Count == 0)
        {
            throw Expected("INPUT, OUTPUT, I-O or EXTEND");
        }

        var span = SpanFrom(first);
        return new OpenStatement(span, RangeOf(span), files);
    }

    private Statement ParseClose()
    {
        var first = _pos;
        Next();
        var files = new List<DataRef>();
        Sep();
        while (IsIdentifierStart(_pos))
        {
            files.Add(ParseDataRef(RefKind.File));
            if (AcceptWord("REEL") || AcceptWord("UNIT"))
            {
                if (AcceptWord("FOR"))
                {
                    Expect("REMOVAL");
                }
            }

            if (AcceptWord("WITH") || IsWord(_pos, "NO") || IsWord(_pos, "LOCK"))
            {
                if (AcceptWord("NO"))
                {
                    Expect("REWIND");
                }
                else
                {
                    Expect("LOCK");
                }
            }

            Sep();
        }

        if (files.Count == 0)
        {
            throw Expected("a file name after CLOSE");
        }

        var span = SpanFrom(first);
        return new CloseStatement(span, RangeOf(span), files);
    }

    private Statement ParseFileStatement(string verb)
    {
        var first = _pos;
        Next();
        var target = ParseDataRef(verb is "READ" or "DELETE" or "START" or "RETURN" ? RefKind.File : RefKind.Data);
        Expr? into = null;
        Expr? from = null;
        Expr? key = null;
        RelationalOperator? keyOp = null;
        string? advancing = null;
        Expr? amount = null;
        var next = false;
        if (verb == "READ" && (AcceptWord("NEXT") || AcceptWord("PREVIOUS")))
        {
            next = true;
        }

        if (verb is "READ" or "DELETE" or "RETURN")
        {
            AcceptWord("RECORD");
        }

        if (verb is "READ" or "RETURN" && AcceptWord("INTO"))
        {
            into = ParseIdentifierOrSpecial();
        }

        if (verb is "WRITE" or "REWRITE" or "RELEASE" && AcceptWord("FROM"))
        {
            from = ParseOperand();
        }

        if (verb == "READ")
        {
            if (IsWord(_pos, "WITH") || IsWord(_pos, "NO") || IsWord(_pos, "LOCK"))
            {
                AcceptWord("WITH");
                AcceptWord("NO");
                Expect("LOCK");
            }

            if (AcceptWord("KEY"))
            {
                AcceptWord("IS");
                key = ParseIdentifierOrSpecial();
            }
        }

        if (verb == "START" && AcceptWord("KEY"))
        {
            keyOp = ParseRelationalOperator() ?? throw Expected("a relational operator after KEY");
            key = ParseIdentifierOrSpecial();
        }

        if (verb == "WRITE" && (IsWord(_pos, "BEFORE") || IsWord(_pos, "AFTER")))
        {
            advancing = Up(_pos);
            Next();
            AcceptWord("ADVANCING");
            if (AcceptWord("PAGE"))
            {
                advancing += " PAGE";
            }
            else
            {
                amount = ParseOperand();
                if (!AcceptWord("LINES"))
                {
                    AcceptWord("LINE");
                }
            }
        }

        var handlers = verb switch
        {
            "READ" => ParseHandlers(HandlerKind.AtEnd, HandlerKind.InvalidKey),
            "RETURN" => ParseHandlers(HandlerKind.AtEnd),
            "WRITE" => ParseHandlers(HandlerKind.InvalidKey, HandlerKind.EndOfPage),
            "RELEASE" => new List<ConditionalPhrase>(),
            _ => ParseHandlers(HandlerKind.InvalidKey),
        };
        if (verb != "RELEASE")
        {
            EndScope("END-" + verb);
        }

        var span = SpanFrom(first);
        return new FileStatement(span, RangeOf(span), verb, target, into, from, key, keyOp, advancing, amount, handlers) { Next = next };
    }

    private Statement ParseAccept()
    {
        var first = _pos;
        Next();
        var target = ParseIdentifierOrSpecial();
        string? device = null;
        string? special = null;
        if (AcceptWord("FROM"))
        {
            var w = AtEnd ? string.Empty : Up(_pos);
            if (w is "DATE" or "DAY" or "DAY-OF-WEEK" or "TIME")
            {
                Next();
                special = w;
                if (w is "DATE" or "DAY" && (IsWord(_pos, "YYYYMMDD") || IsWord(_pos, "YYYYDDD")))
                {
                    special += " " + Up(_pos);
                    Next();
                }
            }
            else if (IsWordToken(_pos))
            {
                device = w;
                Next();
            }
            else
            {
                throw Expected("a device or DATE/DAY/TIME after FROM");
            }
        }

        var handlers = ParseHandlers(HandlerKind.Exception);
        EndScope("END-ACCEPT");
        var span = SpanFrom(first);
        return new AcceptStatement(span, RangeOf(span), target, device, special, handlers);
    }

    private Statement ParseDisplay()
    {
        var first = _pos;
        Next();
        var values = new List<Expr>();
        Sep();
        while (IsOperandStart(_pos))
        {
            values.Add(ParseOperand());
            Sep();
        }

        if (values.Count == 0)
        {
            throw Expected("an operand after DISPLAY");
        }

        string? upon = null;
        if (AcceptWord("UPON"))
        {
            if (!IsWordToken(_pos))
            {
                throw Expected("a device after UPON");
            }

            upon = Up(_pos);
            Next();
        }

        var noAdvancing = false;
        if (IsWord(_pos, "WITH") && IsWord(_pos + 1, "NO") || IsWord(_pos, "NO"))
        {
            AcceptWord("WITH");
            Next();
            Expect("ADVANCING");
            noAdvancing = true;
        }

        EndScope("END-DISPLAY");
        var span = SpanFrom(first);
        return new DisplayStatement(span, RangeOf(span), values, upon, noAdvancing);
    }

    private Statement ParseSort(string verb)
    {
        var first = _pos;
        Next();
        var file = ParseDataRef(RefKind.File);
        var keys = new List<SortKey>();
        var duplicates = false;
        var usingFiles = new List<DataRef>();
        var givingFiles = new List<DataRef>();
        (ProcedureRef, ProcedureRef?)? input = null;
        (ProcedureRef, ProcedureRef?)? output = null;
        while (!AtEnd)
        {
            Sep();
            if (IsWord(_pos, "ON") || IsWord(_pos, "ASCENDING") || IsWord(_pos, "DESCENDING"))
            {
                AcceptWord("ON");
                var asc = AcceptWord("ASCENDING");
                if (!asc)
                {
                    Expect("DESCENDING");
                }

                AcceptWord("KEY");
                var list = new List<Expr>();
                Sep();
                while (IsIdentifierStart(_pos))
                {
                    list.Add(ParseDataRef(RefKind.Data));
                    Sep();
                }

                keys.Add(new SortKey(asc, list));
            }
            else if (IsWord(_pos, "WITH") || IsWord(_pos, "DUPLICATES"))
            {
                AcceptWord("WITH");
                Expect("DUPLICATES");
                AcceptWord("IN");
                AcceptWord("ORDER");
                duplicates = true;
            }
            else if (IsWord(_pos, "COLLATING") || IsWord(_pos, "SEQUENCE"))
            {
                AcceptWord("COLLATING");
                Expect("SEQUENCE");
                AcceptWord("IS");
                Next();
            }
            else if (AcceptWord("USING"))
            {
                Sep();
                while (IsIdentifierStart(_pos))
                {
                    usingFiles.Add(ParseDataRef(RefKind.File));
                    Sep();
                }
            }
            else if (AcceptWord("GIVING"))
            {
                Sep();
                while (IsIdentifierStart(_pos))
                {
                    givingFiles.Add(ParseDataRef(RefKind.File));
                    Sep();
                }
            }
            else if (IsWord(_pos, "INPUT") || IsWord(_pos, "OUTPUT"))
            {
                var isInput = Up(_pos) == "INPUT";
                Next();
                Expect("PROCEDURE");
                AcceptWord("IS");
                var from = ParseProcedureRef();
                ProcedureRef? thru = null;
                if (AcceptWord("THRU") || AcceptWord("THROUGH"))
                {
                    thru = ParseProcedureRef();
                }

                if (isInput)
                {
                    input = (from, thru);
                }
                else
                {
                    output = (from, thru);
                }
            }
            else
            {
                break;
            }
        }

        if (keys.Count == 0)
        {
            throw Expected($"a KEY phrase in {verb}");
        }

        var span = SpanFrom(first);
        return new SortStatement(span, RangeOf(span), verb, file, keys, duplicates, usingFiles, givingFiles, input, output);
    }

    private Statement ParseEntry()
    {
        var first = _pos;
        Next();
        if (AtEnd || !Cur.IsLiteral)
        {
            throw Expected("a literal after ENTRY");
        }

        var name = NameText(_pos);
        Next();
        var args = new List<CallArgument>();
        if (AcceptWord("USING"))
        {
            args = ParseCallArguments(allowContent: false);
        }

        var span = SpanFrom(first);
        return new EntryStatement(span, RangeOf(span), name, args);
    }

    private List<ConditionalPhrase> ParseHandlers(params HandlerKind[] allowed)
    {
        var list = new List<ConditionalPhrase>();
        while (true)
        {
            Sep();
            var not = IsWord(_pos, "NOT");
            var at = not ? _pos + 1 : _pos;
            HandlerKind? found = null;
            var length = 0;
            foreach (var kind in allowed)
            {
                length = MatchHandler(at, kind);
                if (length > 0)
                {
                    found = kind;
                    break;
                }
            }

            if (found is null)
            {
                return list;
            }

            for (var k = _pos; k < at + length; k++)
            {
                Next();
            }

            list.Add(new ConditionalPhrase(found.Value, not, ParseStatementList()));
        }
    }

    private int MatchHandler(int i, HandlerKind kind)
    {
        switch (kind)
        {
            case HandlerKind.AtEnd:
                if (IsWord(i, "AT") && IsWord(i + 1, "END"))
                {
                    return 2;
                }

                return IsWord(i, "END") && !IsWord(i + 1, "PROGRAM") && !IsWord(i + 1, "DECLARATIVES") ? 1 : 0;
            case HandlerKind.InvalidKey:
                return IsWord(i, "INVALID") ? (IsWord(i + 1, "KEY") ? 2 : 1) : 0;
            case HandlerKind.SizeError:
                var s = IsWord(i, "ON") ? i + 1 : i;
                return IsWord(s, "SIZE") && IsWord(s + 1, "ERROR") ? s + 2 - i : 0;
            case HandlerKind.Exception:
                var e = IsWord(i, "ON") ? i + 1 : i;
                return IsWord(e, "EXCEPTION") ? e + 1 - i : 0;
            case HandlerKind.Overflow:
                var o = IsWord(i, "ON") ? i + 1 : i;
                return IsWord(o, "OVERFLOW") ? o + 1 - i : 0;
            case HandlerKind.EndOfPage:
                var p = IsWord(i, "AT") ? i + 1 : i;
                return IsWord(p, "END-OF-PAGE") || IsWord(p, "EOP") ? p + 1 - i : 0;
            default:
                return 0;
        }
    }

    // Lines of the program file; tokens from a copybook map to the line(s) of the COPY statement that brought them in.
    private int ProgramLine(int tokenIndex, bool end) => _ts.ProgramLine(tokenIndex, end);
}
