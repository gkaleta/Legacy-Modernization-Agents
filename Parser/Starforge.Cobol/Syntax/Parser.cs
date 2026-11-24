using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Syntax;

public sealed record ParserOptions(bool DecimalPointIsComma = false);

// ParsedExecs: EXEC blocks the procedure parser already parsed, by token index, so ExecPass need not re-parse them.
public sealed record ParseResult(CompilationUnitSyntax Syntax, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyDictionary<int, Exec.ExecStatement> ParsedExecs);

public static class CobolParser
{
    // When accounting is given, consumed tokens are promoted Lexed -> Parsed and skipped ones Lexed -> Unsupported.
    public static ParseResult Parse(TokenStream tokens, ParserOptions? options = null, TokenAccounting? accounting = null)
    {
        var parser = new Parser(tokens, options ?? new ParserOptions());
        var unit = parser.ParseCompilationUnit();
        parser.ApplyCoverage(accounting);
        return new ParseResult(unit, parser.Diagnostics, parser.ParsedExecs);
    }
}

internal sealed partial class Parser
{
    private const byte Untouched = 0;
    private const byte Consumed = 1;
    private const byte Skipped = 2;
    private const byte Opaque = 3;

    private readonly TokenStream _ts;
    private readonly byte[] _marks;
    private readonly List<ErrorNode> _allErrors = new();
    private readonly List<char> _currencySymbols = new();
    private bool _decimalPointIsComma;
    private int _pos;

    public Parser(TokenStream tokens, ParserOptions options)
    {
        _ts = tokens;
        _marks = new byte[tokens.Count];
        _decimalPointIsComma = options.DecimalPointIsComma;
    }

    public List<Diagnostic> Diagnostics { get; } = new();

    private bool AtEnd => _pos >= _ts.Count;

    private Token Cur => _ts[_pos];

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        var programs = new List<ProgramUnit>();
        var errors = new List<ErrorNode>();
        while (!AtEnd)
        {
            if (IsDivisionHeader(_pos) || IsDataFragmentStart(_pos))
            {
                programs.Add(ParseProgram(new List<string>()));
                continue;
            }

            if (IsEndProgram(_pos))
            {
                errors.Add(SkipError("unmatched-end-program", "END PROGRAM without an open program", i => i > _pos + 1 && (IsPeriod(i - 1) || IsDivisionHeader(i))));
                continue;
            }

            errors.Add(SkipError("expected-division-header", "Expected a division header", i => IsDivisionHeader(i) || IsEndProgram(i)));
        }

        return new CompilationUnitSyntax(SpanFrom(0), RangeOf(SpanFrom(0)), programs, errors);
    }

    private ProgramUnit ParseProgram(List<string> openNames)
    {
        var first = _pos;
        var errors = new List<ErrorNode>();
        IdentificationDivision? id = null;
        EnvironmentDivision? env = null;
        DataDivision? data = null;
        ProcedureDivision? proc = null;

        if (IsDivision(_pos, "IDENTIFICATION") || IsDivision(_pos, "ID"))
        {
            id = ParseIdentification();
        }
        else if (!IsDataFragmentStart(_pos))
        {
            AddDiagnostic("missing-identification-division", DiagnosticSeverity.Warning, "Program has no IDENTIFICATION DIVISION", _pos, _pos + 1, string.Empty);
        }

        var name = id?.ProgramName;
        var nested = new List<ProgramUnit>();
        string? endName = null;
        while (!AtEnd)
        {
            if (env is null && data is null && proc is null && IsDivision(_pos, "ENVIRONMENT"))
            {
                env = ParseEnvironment();
            }
            else if (data is null && proc is null && IsDivision(_pos, "DATA"))
            {
                data = ParseData(hasHeader: true);
            }
            else if (data is null && proc is null && IsDataFragmentStart(_pos))
            {
                // A copybook-like fragment (entries or data sections without a DATA DIVISION header).
                AddDiagnostic("implicit-data-division", DiagnosticSeverity.Info, "Data entries without a DATA DIVISION header", _pos, _pos + 1, string.Empty);
                data = ParseData(hasHeader: false);
            }
            else if (proc is null && IsDivision(_pos, "PROCEDURE"))
            {
                proc = ParseProcedure(data, env);
            }
            else if (IsDivision(_pos, "IDENTIFICATION") || IsDivision(_pos, "ID"))
            {
                // A program that follows this one's PROCEDURE DIVISION before its END PROGRAM is contained in it.
                if (proc is null && env is null && data is null)
                {
                    break;
                }

                nested.Add(ParseProgram(name is null ? openNames : openNames.Append(name).ToList()));
            }
            else if (IsEndProgram(_pos))
            {
                var at = _pos;
                var target = _pos + 2 < _ts.Count ? NameText(_pos + 2) : null;
                if (target is not null && name is not null && !Same(target, name) && openNames.Any(o => Same(o, target)))
                {
                    AddDiagnostic("missing-end-program", DiagnosticSeverity.Warning, $"Program {name} is closed by END PROGRAM {target} of an enclosing program", at, at + 2, "END PROGRAM");
                    break;
                }

                Next();
                Next();
                if (!AtEnd && (Cur.Kind == TokenKind.Word || Cur.Kind == TokenKind.AlphanumericLiteral))
                {
                    endName = NameText(_pos);
                    Next();
                }

                if (endName is not null && name is not null && !Same(endName, name))
                {
                    AddDiagnostic("end-program-name-mismatch", DiagnosticSeverity.Error, $"END PROGRAM {endName} does not match PROGRAM-ID {name}", at, _pos, "END PROGRAM");
                }

                ExpectPeriod("END PROGRAM");
                break;
            }
            else
            {
                errors.Add(SkipError("unexpected-division", "Division is out of order or repeated", i => i > _pos && (IsDivisionHeader(i) || IsEndProgram(i))));
            }
        }

        var span = SpanFrom(first);
        return new ProgramUnit(span, RangeOf(span), id, env, data, proc, nested, endName, errors);
    }

    private IdentificationDivision ParseIdentification()
    {
        var first = _pos;
        Next();
        Next();
        ExpectPeriod("IDENTIFICATION DIVISION");
        string? programName = null;
        var literal = false;
        var attributes = ProgramAttributes.None;
        var paragraphs = new List<CommentParagraph>();
        var errors = new List<ErrorNode>();
        while (!AtEnd && !IsDivisionHeader(_pos) && !IsEndProgram(_pos))
        {
            if (IsWord(_pos, "PROGRAM-ID"))
            {
                Next();
                AcceptPeriod();
                if (!AtEnd && (Cur.Kind == TokenKind.Word || Cur.Kind == TokenKind.AlphanumericLiteral))
                {
                    literal = Cur.Kind == TokenKind.AlphanumericLiteral;
                    programName = NameText(_pos);
                    Next();
                    AcceptWord("IS");
                    while (true)
                    {
                        if (AcceptWord("INITIAL"))
                        {
                            attributes |= ProgramAttributes.Initial;
                        }
                        else if (AcceptWord("COMMON"))
                        {
                            attributes |= ProgramAttributes.Common;
                        }
                        else if (AcceptWord("RECURSIVE"))
                        {
                            attributes |= ProgramAttributes.Recursive;
                        }
                        else
                        {
                            break;
                        }
                    }

                    AcceptWord("PROGRAM");
                    if (!AcceptPeriod() && !AtEnd && !IsParagraphStart(_pos))
                    {
                        errors.Add(SkipError("invalid-program-id", "Unexpected text in PROGRAM-ID paragraph", IdentificationBoundary));
                        AcceptPeriod();
                    }
                }
                else
                {
                    errors.Add(SkipError("missing-program-name", "PROGRAM-ID needs a program name", IdentificationBoundary));
                    AcceptPeriod();
                }

                continue;
            }

            if (Cur.Kind == TokenKind.Word && IsCommentParagraphName(Text(_pos)))
            {
                var pFirst = _pos;
                var pName = Text(_pos).ToUpperInvariant();
                Next();
                AcceptPeriod();
                while (!AtEnd && Cur.Kind == TokenKind.CommentEntry)
                {
                    Next();
                }

                var pSpan = SpanFrom(pFirst);
                paragraphs.Add(new CommentParagraph(pSpan, RangeOf(pSpan), pName));
                continue;
            }

            errors.Add(SkipError("unexpected-identification-text", "Unexpected text in IDENTIFICATION DIVISION", IdentificationBoundary));
            AcceptPeriod();
        }

        if (programName is null)
        {
            AddDiagnostic("missing-program-id", DiagnosticSeverity.Error, "IDENTIFICATION DIVISION has no PROGRAM-ID", first, first + 2, "IDENTIFICATION DIVISION");
        }

        var span = SpanFrom(first);
        return new IdentificationDivision(span, RangeOf(span), programName, literal, attributes, paragraphs, errors);
    }

    private bool IdentificationBoundary(int i)
        => IsPeriod(i) || IsDivisionHeader(i) || IsEndProgram(i) || (i > 0 && IsPeriod(i - 1) && IsParagraphStart(i));

    private bool IsParagraphStart(int i)
        => _ts[i].Kind == TokenKind.Word && (IsWord(i, "PROGRAM-ID") || IsCommentParagraphName(Text(i)));

    private static bool IsCommentParagraphName(string word)
        => word.ToUpperInvariant() is "AUTHOR" or "INSTALLATION" or "DATE-WRITTEN" or "DATE-COMPILED" or "SECURITY" or "REMARKS";

    private ProcedureDivision ParseProcedure(DataDivision? data, EnvironmentDivision? env)
    {
        var first = _pos;
        Next();
        Next();
        var usingList = new List<UsingParameter>();
        QualifiedName? returning = null;
        var errors = new List<ErrorNode>();
        if (AcceptWord("USING"))
        {
            var mode = PassingMode.Reference;
            while (!AtEnd)
            {
                if (AcceptWord("BY"))
                {
                    continue;
                }

                if (AcceptWord("REFERENCE"))
                {
                    mode = PassingMode.Reference;
                }
                else if (AcceptWord("VALUE"))
                {
                    mode = PassingMode.Value;
                }
                else if (AcceptWord("CONTENT"))
                {
                    mode = PassingMode.Content;
                }
                else if (Cur.Kind == TokenKind.Comma)
                {
                    Next();
                }
                else if (Cur.Kind == TokenKind.Word && !IsWord(_pos, "RETURNING"))
                {
                    usingList.Add(new UsingParameter(mode, ParseQualifiedName()));
                }
                else
                {
                    break;
                }
            }
        }

        if (AcceptWord("RETURNING"))
        {
            if (!AtEnd && Cur.Kind == TokenKind.Word)
            {
                returning = ParseQualifiedName();
            }
            else
            {
                AddDiagnostic("missing-returning-item", DiagnosticSeverity.Error, "RETURNING needs a data name", _pos - 1, _pos, "RETURNING");
            }
        }

        if (!AcceptPeriod())
        {
            errors.Add(SkipError("invalid-procedure-header", "Unexpected text in PROCEDURE DIVISION header", i => IsPeriod(i) || IsProcedureBoundary(i)));
            AcceptPeriod();
        }

        SetupProcedureContext(data, env);
        _dataRefs.Clear();
        _procRefs.Clear();
        var bodyFirst = _pos;
        var errorMark = _allErrors.Count;
        var (declaratives, sections) = ParseProcedureBody();
        errors.AddRange(_allErrors.Skip(errorMark));
        var body = SpanFrom(bodyFirst);
        var span = SpanFrom(first);
        return new ProcedureDivision(span, RangeOf(span), usingList, returning, body, errors)
        {
            Declaratives = declaratives,
            Sections = sections,
            DataReferences = _dataRefs.ToArray(),
            ProcedureReferences = _procRefs.ToArray(),
        };
    }

    // The procedure body runs to a nested program, END PROGRAM, or the end of the compilation unit.
    private bool IsProcedureBoundary(int i)
        => (i == 0 || IsPeriod(i - 1)) && (IsDivision(i, "IDENTIFICATION") || IsDivision(i, "ID") || IsEndProgram(i));

    private QualifiedName ParseQualifiedName()
    {
        var name = Text(_pos);
        Next();
        var qualifiers = new List<string>();
        while (!AtEnd && (IsWord(_pos, "OF") || IsWord(_pos, "IN")) && _pos + 1 < _ts.Count && _ts[_pos + 1].Kind == TokenKind.Word)
        {
            Next();
            qualifiers.Add(Text(_pos));
            Next();
        }

        return new QualifiedName(name, qualifiers);
    }

    public void ApplyCoverage(TokenAccounting? accounting)
    {
        var text = _ts.Source.Text;
        var i = 0;
        while (i < _marks.Length)
        {
            var mark = _marks[i];
            var j = i + 1;
            while (j < _marks.Length && _marks[j] == mark)
            {
                j++;
            }

            if (mark == Untouched && Enumerable.Range(i, j - i).All(k => _ts[k].Kind is TokenKind.CompilerDirective or TokenKind.CommentEntry))
            {
                mark = Opaque;
            }

            if (mark == Untouched)
            {
                for (var k = i; k < j; k++)
                {
                    _marks[k] = Skipped;
                }

                AddDiagnostic("parser-gap", DiagnosticSeverity.Error, "Tokens were neither parsed nor reported by the parser", i, j, ShapeOf(i, Math.Min(j, i + 3)));
                mark = Skipped;
            }

            if (accounting is not null && mark is Consumed or Skipped)
            {
                var to = mark == Consumed ? CoverageClass.Parsed : CoverageClass.Unsupported;
                accounting.PromoteExpanded(text, _ts[i].Start, _ts[j - 1].End, CoverageClass.Lexed, to, mark == Consumed ? string.Empty : "syntax-error");
            }

            i = j;
        }
    }

    private void Next()
    {
        if (_marks[_pos] == Untouched)
        {
            _marks[_pos] = Consumed;
        }

        _pos++;
    }

    private string Text(int i) => _ts.Text(i);

    // Program names may be given as literals; the quotes are not part of the name.
    private string NameText(int i)
    {
        var t = _ts[i];
        var s = _ts.Text(t);
        return t.Kind == TokenKind.AlphanumericLiteral && s.Length >= 2 ? s[1..^1] : s;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private bool IsWord(int i, string word) => i < _ts.Count && _ts[i].Kind == TokenKind.Word && _ts.Is(_ts[i], word);

    private bool IsPeriod(int i) => i < _ts.Count && _ts[i].Kind == TokenKind.Period;

    private bool IsDivision(int i, string name) => IsWord(i, name) && IsWord(i + 1, "DIVISION");

    private bool IsDivisionHeader(int i)
        => i < _ts.Count && _ts[i].Kind == TokenKind.Word && IsWord(i + 1, "DIVISION")
            && Text(i).ToUpperInvariant() is "IDENTIFICATION" or "ID" or "ENVIRONMENT" or "DATA" or "PROCEDURE";

    private bool IsSectionHeader(int i) => i < _ts.Count && _ts[i].Kind == TokenKind.Word && IsWord(i + 1, "SECTION");

    private bool IsEndProgram(int i) => IsWord(i, "END") && IsWord(i + 1, "PROGRAM");

    private bool AcceptWord(string word)
    {
        if (IsWord(_pos, word))
        {
            Next();
            return true;
        }

        return false;
    }

    private bool AcceptPeriod()
    {
        if (IsPeriod(_pos))
        {
            Next();
            return true;
        }

        return false;
    }

    private void ExpectPeriod(string after)
    {
        if (!AcceptPeriod())
        {
            AddDiagnostic("missing-period", DiagnosticSeverity.Warning, $"Expected a separator period after {after}", Math.Max(0, _pos - 1), _pos, after);
        }
    }

    private bool TryInteger(out int value)
    {
        value = 0;
        if (AtEnd || Cur.Kind is not (TokenKind.IntegerLiteral or TokenKind.LevelNumber))
        {
            return false;
        }

        if (!int.TryParse(_ts.Span(Cur), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        Next();
        return true;
    }

    // Skips at least one token, up to (not including) the first index where stop is true; the tokens become Unsupported.
    private ErrorNode SkipError(string code, string message, Func<int, bool> stop)
    {
        var first = _pos;
        while (!AtEnd && (_pos == first || !stop(_pos)))
        {
            if (_marks[_pos] == Untouched)
            {
                _marks[_pos] = Skipped;
            }

            _pos++;
        }

        var span = SpanFrom(first);
        var range = RangeOf(span);
        var diagnostic = Diagnostic.Create(code, DiagnosticSeverity.Error, message, range, ShapeOf(first, Math.Min(_pos, first + 3)));
        Diagnostics.Add(diagnostic);
        var node = new ErrorNode(span, range, diagnostic);
        _allErrors.Add(node);
        return node;
    }

    private string ShapeOf(int from, int to)
    {
        var parts = new List<string>();
        for (var i = from; i < to && i < _ts.Count; i++)
        {
            var t = _ts[i];
            parts.Add(t.Kind switch
            {
                TokenKind.Word => _ts.Text(t),
                TokenKind.Period => ".",
                TokenKind.LevelNumber => "LEVEL",
                TokenKind.PictureString => "PIC-STRING",
                TokenKind.ExecBlock => "EXEC",
                _ when t.IsLiteral => "LIT",
                _ => t.Kind.ToString(),
            });
        }

        return DiagnosticShape.Normalize(string.Join(" ", parts));
    }

    private void AddDiagnostic(string code, DiagnosticSeverity severity, string message, int fromToken, int toToken, string shape)
    {
        var span = MakeSpan(fromToken, Math.Max(fromToken + 1, toToken));
        Diagnostics.Add(Diagnostic.Create(code, severity, message, RangeOf(span), DiagnosticShape.Normalize(shape)));
    }

    private NodeSpan SpanFrom(int first) => MakeSpan(first, _pos);

    private NodeSpan MakeSpan(int first, int end)
    {
        end = Math.Min(end, _ts.Count);
        first = Math.Min(first, _ts.Count);
        if (_ts.Count == 0)
        {
            return NodeSpan.Empty;
        }

        if (end <= first)
        {
            var at = first < _ts.Count ? _ts[first].Start : _ts[_ts.Count - 1].End;
            return new NodeSpan(first, first, at, at);
        }

        return new NodeSpan(first, end, _ts[first].Start, _ts[end - 1].End);
    }

    // Built from the first and last token origins; a span that leaves its file (e.g. ends in a copybook) keeps the first file.
    private SourceRange RangeOf(NodeSpan span)
    {
        if (_ts.Count == 0)
        {
            return new SourceRange(_ts.Source.Units.Count > 0 ? _ts.Source.Units[0].Path : string.Empty, 1, 1, 1, 1);
        }

        var firstIndex = Math.Min(span.FirstToken, _ts.Count - 1);
        var start = _ts.Origin(firstIndex).Range;
        if (span.TokenCount <= 1)
        {
            return start;
        }

        var end = _ts.Origin(Math.Min(span.EndToken, _ts.Count) - 1).Range;
        if (end.File != start.File || end.EndLine < start.StartLine || (end.EndLine == start.StartLine && end.EndColumn < start.StartColumn))
        {
            return start;
        }

        return start with { EndLine = end.EndLine, EndColumn = end.EndColumn };
    }

    private bool IsDataFragmentStart(int i)
        => IsLevelAt(i, out _) || (IsSectionHeader(i) && Text(i).ToUpperInvariant() is "FILE" or "WORKING-STORAGE" or "LOCAL-STORAGE" or "LINKAGE");
}
