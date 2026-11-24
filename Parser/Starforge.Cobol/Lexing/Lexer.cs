using Starforge.Cobol.Copy;

namespace Starforge.Cobol.Lexing;

public sealed record LexerOptions(bool DecimalPointIsComma = false);

public sealed record LexResult(TokenStream Tokens, IReadOnlyList<Diagnostic> Diagnostics);

// Keywords are not reserved here: every word is a Word and the parser decides by context.
// The only context the lexer keeps is what changes character grouping (PICTURE strings, level numbers, EXEC blocks).
public static class Lexer
{
    public static LexResult Lex(ExpandedSource source, LexerOptions? options = null)
    {
        var state = new LexState(source, options ?? new LexerOptions());
        state.Run();
        return new LexResult(new TokenStream(source, state.Tokens.ToArray()), state.Diagnostics);
    }

    internal static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    internal static FigurativeKind FigurativeOf(ReadOnlySpan<char> word)
    {
        Span<char> upper = stackalloc char[Math.Min(word.Length, 16)];
        if (word.Length > 16)
        {
            return FigurativeKind.None;
        }

        word.ToUpperInvariant(upper);
        return upper switch
        {
            "ZERO" or "ZEROS" or "ZEROES" => FigurativeKind.Zero,
            "SPACE" or "SPACES" => FigurativeKind.Space,
            "HIGH-VALUE" or "HIGH-VALUES" => FigurativeKind.HighValue,
            "LOW-VALUE" or "LOW-VALUES" => FigurativeKind.LowValue,
            "QUOTE" or "QUOTES" => FigurativeKind.Quote,
            "NULL" or "NULLS" => FigurativeKind.Null,
            _ => FigurativeKind.None,
        };
    }

    private sealed class LexState
    {
        private readonly ExpandedSource _source;
        private readonly string _t;
        private readonly LexerOptions _options;
        private int _pos;
        private bool _inDataDivision = true;
        private bool _inIdentificationDivision;
        private int _segment;

        public LexState(ExpandedSource source, LexerOptions options)
        {
            _source = source;
            _t = source.Content;
            _options = options;
        }

        public List<Token> Tokens { get; } = new();

        public List<Diagnostic> Diagnostics { get; } = new();

        public void Run()
        {
            while (true)
            {
                while (_pos < _t.Length && char.IsWhiteSpace(_t[_pos]))
                {
                    _pos++;
                }

                if (_pos >= _t.Length)
                {
                    break;
                }

                LexOne();
            }

            for (var i = 0; i + 1 < Tokens.Count; i++)
            {
                var tok = Tokens[i];
                if (tok.Kind == TokenKind.Word && Is(tok, "ALL") && (Tokens[i + 1].IsLiteral || Tokens[i + 1].Kind == TokenKind.Figurative))
                {
                    Tokens[i] = tok with { Kind = TokenKind.Figurative, Detail = (byte)FigurativeKind.All };
                }
            }
        }

        private void LexOne()
        {
            var c = _t[_pos];
            var start = _pos;

            if (c is '\'' or '"')
            {
                LexQuoted(start, _pos, TokenKind.AlphanumericLiteral);
                return;
            }

            if (char.IsLetterOrDigit(c) || c == '_')
            {
                LexWordOrNumber();
                return;
            }

            switch (c)
            {
                case '.':
                    if (Next(1) is var d && char.IsDigit(d) && IsSeparatorBefore(start))
                    {
                        LexNumber(start, _pos);
                        return;
                    }

                    if (!IsSpaceOrEnd(_pos + 1))
                    {
                        Diag("period-not-followed-by-space", DiagnosticSeverity.Info, "Separator period is not followed by a space", start, start + 1, ".");
                    }

                    Emit(TokenKind.Period, start, 1);
                    return;
                case ',':
                    Emit(TokenKind.Comma, start, 1);
                    return;
                case ';':
                    Emit(TokenKind.Semicolon, start, 1);
                    return;
                case '(':
                    Emit(TokenKind.LeftParen, start, 1);
                    return;
                case ')':
                    Emit(TokenKind.RightParen, start, 1);
                    return;
                case ':':
                    Emit(TokenKind.Colon, start, 1);
                    return;
                case '&':
                    Emit(TokenKind.Ampersand, start, 1);
                    return;
                case '+':
                case '-':
                    if (IsSeparatorBefore(start) && (char.IsDigit(Next(1)) || (Next(1) == DecimalPoint && char.IsDigit(Next(2)))))
                    {
                        LexNumber(start, _pos + 1);
                        return;
                    }

                    Emit(c == '+' ? TokenKind.Plus : TokenKind.Minus, start, 1);
                    return;
                case '*':
                    if (Next(1) == '*')
                    {
                        Emit(TokenKind.Power, start, 2);
                        return;
                    }

                    Emit(TokenKind.Asterisk, start, 1);
                    return;
                case '/':
                    Emit(TokenKind.Slash, start, 1);
                    return;
                case '=':
                    if (Next(1) == '=')
                    {
                        Emit(TokenKind.PseudoTextDelimiter, start, 2);
                        return;
                    }

                    Emit(TokenKind.Equal, start, 1);
                    return;
                case '>':
                    if (Next(1) == '=')
                    {
                        Emit(TokenKind.GreaterOrEqual, start, 2);
                        return;
                    }

                    Emit(TokenKind.Greater, start, 1);
                    return;
                case '<':
                    if (Next(1) == '=')
                    {
                        Emit(TokenKind.LessOrEqual, start, 2);
                        return;
                    }

                    if (Next(1) == '>')
                    {
                        Emit(TokenKind.NotEqual, start, 2);
                        return;
                    }

                    Emit(TokenKind.Less, start, 1);
                    return;
            }

            var end = _pos + 1;
            while (end < _t.Length && !char.IsWhiteSpace(_t[end]) && !IsKnownStart(_t[end]))
            {
                end++;
            }

            Diag("unexpected-character", DiagnosticSeverity.Error, $"Unexpected character '{c}'", start, end, c.ToString());
            Emit(TokenKind.Unknown, start, end - start);
        }

        private static bool IsKnownStart(char c)
            => char.IsLetterOrDigit(c) || c is '\'' or '"' or '.' or ',' or ';' or '(' or ')' or ':' or '&' or '+' or '-' or '*' or '/' or '=' or '>' or '<' or '_';

        private char DecimalPoint => _options.DecimalPointIsComma ? ',' : '.';

        private void LexWordOrNumber()
        {
            var start = _pos;
            var end = _pos;
            while (end < _t.Length && IsWordChar(_t[end]))
            {
                end++;
            }

            var word = _t.AsSpan(start, end - start);
            if (end < _t.Length && _t[end] is '\'' or '"' && TextWordScanner.IsLiteralPrefix(word))
            {
                LexQuoted(start, end, LiteralKindFor(word));
                return;
            }

            var allDigits = true;
            foreach (var ch in word)
            {
                if (!char.IsDigit(ch))
                {
                    allDigits = false;
                    break;
                }
            }

            if (allDigits)
            {
                LexNumber(start, start);
                return;
            }

            // Words cannot end with a hyphen, so "A-" before "(" or an operator is the word plus a minus sign.
            while (end > start + 1 && _t[end - 1] == '-')
            {
                end--;
            }

            _pos = end;
            var text = _t.AsSpan(start, end - start);

            if ((text.Equals("EXEC", StringComparison.OrdinalIgnoreCase) || text.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)) && LexExec(start))
            {
                return;
            }

            if ((text.Equals("READY", StringComparison.OrdinalIgnoreCase) || text.Equals("RESET", StringComparison.OrdinalIgnoreCase)) && PeekWordIs(end, "TRACE", out var traceEnd))
            {
                Emit(TokenKind.CompilerDirective, start, traceEnd - start);
                _pos = traceEnd;
                return;
            }

            var fig = FigurativeOf(text);
            if (fig != FigurativeKind.None)
            {
                Emit(TokenKind.Figurative, start, end - start, (byte)fig);
                return;
            }

            if (text.Equals("DIVISION", StringComparison.OrdinalIgnoreCase) && Tokens.Count > 0 && Tokens[^1].Kind == TokenKind.Word)
            {
                _inDataDivision = Is(Tokens[^1], "DATA");
                _inIdentificationDivision = Is(Tokens[^1], "IDENTIFICATION") || Is(Tokens[^1], "ID");
            }

            var startsCommentEntry = _inIdentificationDivision && AfterPeriodOrStart() && IsCommentEntryParagraph(text);
            Emit(TokenKind.Word, start, end - start);
            if (startsCommentEntry)
            {
                LexCommentEntry();
                return;
            }

            if (text.Equals("PIC", StringComparison.OrdinalIgnoreCase) || text.Equals("PICTURE", StringComparison.OrdinalIgnoreCase))
            {
                LexPicture();
            }
        }

        private static bool IsCommentEntryParagraph(ReadOnlySpan<char> word)
            => word.Equals("AUTHOR", StringComparison.OrdinalIgnoreCase)
                || word.Equals("INSTALLATION", StringComparison.OrdinalIgnoreCase)
                || word.Equals("DATE-WRITTEN", StringComparison.OrdinalIgnoreCase)
                || word.Equals("DATE-COMPILED", StringComparison.OrdinalIgnoreCase)
                || word.Equals("SECURITY", StringComparison.OrdinalIgnoreCase)
                || word.Equals("REMARKS", StringComparison.OrdinalIgnoreCase);

        // A comment-entry is free text (quotes need not pair) running until the next line that starts in Area A
        // or, in free format, starts with another paragraph or division header.
        private void LexCommentEntry()
        {
            var i = _pos;
            while (i < _t.Length && _t[i] is ' ' or '\t')
            {
                i++;
            }

            if (i < _t.Length && _t[i] == '.')
            {
                Emit(TokenKind.Period, i, 1);
                i++;
            }

            var lineEnd = i;
            while (true)
            {
                while (lineEnd < _t.Length && _t[lineEnd] != '\n')
                {
                    lineEnd++;
                }

                var next = lineEnd;
                while (next < _t.Length && char.IsWhiteSpace(_t[next]))
                {
                    next++;
                }

                if (next >= _t.Length || StartsNewParagraph(next))
                {
                    break;
                }

                lineEnd = next;
            }

            var end = lineEnd;
            while (end > i && char.IsWhiteSpace(_t[end - 1]))
            {
                end--;
            }

            var start = i;
            while (start < end && char.IsWhiteSpace(_t[start]))
            {
                start++;
            }

            if (end > start)
            {
                Emit(TokenKind.CommentEntry, start, end - start);
            }

            _pos = Math.Max(_pos, end);
        }

        private bool StartsNewParagraph(int at)
        {
            var origin = _source.OriginOf(at, at + 1);
            var unit = _source.Units[origin.Unit].Source;
            if (unit.InitialFormat == Source.SourceFormat.Fixed && origin.Range.StartColumn < 12)
            {
                return true;
            }

            var e = at;
            while (e < _t.Length && IsWordChar(_t[e]))
            {
                e++;
            }

            var word = _t.AsSpan(at, e - at);
            return IsCommentEntryParagraph(word)
                || word.Equals("PROGRAM-ID", StringComparison.OrdinalIgnoreCase)
                || word.Equals("ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || word.Equals("DATA", StringComparison.OrdinalIgnoreCase)
                || word.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase)
                || word.Equals("END", StringComparison.OrdinalIgnoreCase);
        }

        private static TokenKind LiteralKindFor(ReadOnlySpan<char> prefix)
        {
            Span<char> p = stackalloc char[prefix.Length];
            prefix.ToUpperInvariant(p);
            return p switch
            {
                "X" => TokenKind.HexLiteral,
                "Z" => TokenKind.NullTerminatedLiteral,
                "N" => TokenKind.NationalLiteral,
                "NX" => TokenKind.NationalHexLiteral,
                "G" => TokenKind.DbcsLiteral,
                "B" => TokenKind.BooleanLiteral,
                "BX" => TokenKind.BooleanHexLiteral,
                "U" => TokenKind.Utf8Literal,
                "UX" => TokenKind.Utf8HexLiteral,
                _ => TokenKind.AlphanumericLiteral,
            };
        }

        private void LexQuoted(int start, int quoteAt, TokenKind kind)
        {
            var quote = _t[quoteAt];
            var i = quoteAt + 1;
            var closed = false;
            while (i < _t.Length && _t[i] != '\n')
            {
                if (_t[i] == quote)
                {
                    if (i + 1 < _t.Length && _t[i + 1] == quote)
                    {
                        i += 2;
                        continue;
                    }

                    closed = true;
                    i++;
                    break;
                }

                i++;
            }

            var flags = TokenFlags.None;
            if (!closed)
            {
                while (i > quoteAt + 1 && char.IsWhiteSpace(_t[i - 1]))
                {
                    i--;
                }

                flags = TokenFlags.Unterminated;
                Diag("unterminated-literal", DiagnosticSeverity.Error, "Literal is not closed before the end of the line", start, i, _t[start..(quoteAt + 1)]);
            }

            Emit(kind, start, i - start, 0, flags);
            _pos = i;
        }

        private void LexNumber(int start, int digitsFrom)
        {
            var i = digitsFrom;
            while (i < _t.Length && char.IsDigit(_t[i]))
            {
                i++;
            }

            // A digit-led run that continues with letters or hyphens is a user-defined word (1000-INIT, 9A).
            if (i < _t.Length && (char.IsLetter(_t[i]) || _t[i] is '-' or '_') && start == digitsFrom && !IsExponentAt(i))
            {
                var end = i;
                while (end < _t.Length && IsWordChar(_t[end]))
                {
                    end++;
                }

                while (end > start + 1 && _t[end - 1] == '-')
                {
                    end--;
                }

                _pos = end;
                Emit(TokenKind.Word, start, end - start);
                return;
            }

            var kind = TokenKind.IntegerLiteral;
            if (i + 1 < _t.Length && _t[i] == DecimalPoint && char.IsDigit(_t[i + 1]))
            {
                kind = TokenKind.DecimalLiteral;
                i++;
                while (i < _t.Length && char.IsDigit(_t[i]))
                {
                    i++;
                }

                if (IsExponentAt(i))
                {
                    kind = TokenKind.FloatLiteral;
                    i++;
                    if (_t[i] is '+' or '-')
                    {
                        i++;
                    }

                    while (i < _t.Length && char.IsDigit(_t[i]))
                    {
                        i++;
                    }
                }
            }

            _pos = i;
            if (kind == TokenKind.IntegerLiteral && start == digitsFrom && i - start <= 2 && _inDataDivision && AfterPeriodOrStart())
            {
                var value = int.Parse(_t.AsSpan(start, i - start), System.Globalization.CultureInfo.InvariantCulture);
                if (value is >= 1 and <= 49 or 66 or 77 or 88)
                {
                    Emit(TokenKind.LevelNumber, start, i - start, (byte)value);
                    return;
                }
            }

            Emit(kind, start, i - start);
        }

        private bool IsExponentAt(int i)
        {
            if (i >= _t.Length || _t[i] is not ('E' or 'e'))
            {
                return false;
            }

            var j = i + 1;
            if (j < _t.Length && _t[j] is '+' or '-')
            {
                j++;
            }

            if (j >= _t.Length || !char.IsDigit(_t[j]))
            {
                return false;
            }

            while (j < _t.Length && char.IsDigit(_t[j]))
            {
                j++;
            }

            // Only a decimal mantissa can carry an exponent; "1E5" alone stays a word.
            return (j >= _t.Length || !IsWordChar(_t[j])) && HasDecimalBefore(i);
        }

        private bool HasDecimalBefore(int i)
        {
            var k = i - 1;
            while (k >= 0 && char.IsDigit(_t[k]))
            {
                k--;
            }

            return k >= 0 && _t[k] == DecimalPoint;
        }

        private bool AfterPeriodOrStart() => Tokens.Count == 0 || Tokens[^1].Kind == TokenKind.Period;

        private void LexPicture()
        {
            var i = _pos;
            while (i < _t.Length && char.IsWhiteSpace(_t[i]))
            {
                i++;
            }

            if (PeekWordIs(i, "IS", out var isEnd))
            {
                Emit(TokenKind.Word, i, isEnd - i);
                i = isEnd;
                while (i < _t.Length && char.IsWhiteSpace(_t[i]))
                {
                    i++;
                }
            }

            var start = i;
            while (i < _t.Length && !char.IsWhiteSpace(_t[i]))
            {
                i++;
            }

            var end = i;
            // A trailing period, comma or semicolon is the separator, not part of the character-string.
            if (end > start + 1 && _t[end - 1] is '.' or ',' or ';')
            {
                end--;
            }

            if (end <= start)
            {
                _pos = start;
                return;
            }

            Emit(TokenKind.PictureString, start, end - start);
            _pos = end;
        }

        private bool LexExec(int start)
        {
            var i = _pos;
            while (i < _t.Length && char.IsWhiteSpace(_t[i]))
            {
                i++;
            }

            var nameStart = i;
            while (i < _t.Length && IsWordChar(_t[i]))
            {
                i++;
            }

            if (i == nameStart)
            {
                return false;
            }

            var name = _t.AsSpan(nameStart, i - nameStart);
            var kind = name.Equals("CICS", StringComparison.OrdinalIgnoreCase) ? ExecKind.Cics
                : name.Equals("SQL", StringComparison.OrdinalIgnoreCase) ? ExecKind.Sql
                : name.Equals("SQLIMS", StringComparison.OrdinalIgnoreCase) ? ExecKind.SqlIms
                : name.Equals("DLI", StringComparison.OrdinalIgnoreCase) ? ExecKind.Dli
                : ExecKind.Other;
            var sqlLike = kind is ExecKind.Sql or ExecKind.SqlIms;

            while (i < _t.Length)
            {
                var c = _t[i];
                if (c is '\'' or '"')
                {
                    var close = i + 1;
                    while (close < _t.Length && _t[close] != '\n')
                    {
                        if (_t[close] == c)
                        {
                            if (close + 1 < _t.Length && _t[close + 1] == c)
                            {
                                close += 2;
                                continue;
                            }

                            break;
                        }

                        close++;
                    }

                    i = close + 1;
                    continue;
                }

                if (sqlLike && c == '-' && i + 1 < _t.Length && _t[i + 1] == '-' && (i == 0 || !IsWordChar(_t[i - 1])))
                {
                    while (i < _t.Length && _t[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if ((c is 'E' or 'e') && (i == 0 || !IsWordChar(_t[i - 1])) && PeekWordIs(i, "END-EXEC", out var endExec))
                {
                    Emit(TokenKind.ExecBlock, start, endExec - start, (byte)kind);
                    _pos = endExec;
                    return true;
                }

                i++;
            }

            var tail = _t.Length;
            while (tail > start && char.IsWhiteSpace(_t[tail - 1]))
            {
                tail--;
            }

            Diag("exec-without-end-exec", DiagnosticSeverity.Error, "EXEC block has no matching END-EXEC", start, tail, "EXEC " + name.ToString().ToUpperInvariant());
            Emit(TokenKind.ExecBlock, start, tail - start, (byte)kind, TokenFlags.Unterminated);
            _pos = tail;
            return true;
        }

        private bool PeekWordIs(int at, string word, out int end)
        {
            var i = at;
            while (i < _t.Length && char.IsWhiteSpace(_t[i]))
            {
                i++;
            }

            end = i + word.Length;
            return end <= _t.Length
                && _t.AsSpan(i, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)
                && (end == _t.Length || !IsWordChar(_t[end]));
        }

        private bool IsSeparatorBefore(int at)
            => at == 0 || char.IsWhiteSpace(_t[at - 1]) || _t[at - 1] is '(' or ':' or ',' or ';' or '=';

        private bool IsSpaceOrEnd(int at) => at >= _t.Length || char.IsWhiteSpace(_t[at]);

        private char Next(int offset) => _pos + offset < _t.Length ? _t[_pos + offset] : '\0';

        private bool Is(Token token, string word) => _t.AsSpan(token.Start, token.Length).Equals(word, StringComparison.OrdinalIgnoreCase);

        private void Emit(TokenKind kind, int start, int length, byte detail = 0, TokenFlags flags = TokenFlags.None)
        {
            if (TouchesReplacement(start, start + length))
            {
                flags |= TokenFlags.FromReplacement;
            }

            Tokens.Add(new Token(kind, start, length, detail, flags));
            _pos = Math.Max(_pos, start + length);
        }

        // Tokens are emitted in increasing order, so a forward-only segment cursor keeps this linear.
        private bool TouchesReplacement(int start, int end)
        {
            var segs = _source.Text.Segments;
            while (_segment < segs.Count && segs[_segment].End <= start)
            {
                _segment++;
            }

            for (var s = _segment; s < segs.Count && segs[s].Start < end; s++)
            {
                if (segs[s].IsFromReplacement)
                {
                    return true;
                }
            }

            return false;
        }

        private void Diag(string code, DiagnosticSeverity severity, string message, int start, int end, string shape)
            => Diagnostics.Add(Diagnostic.Create(code, severity, message, _source.OriginOf(start, end).Range, DiagnosticShape.Normalize(shape)));
    }
}
