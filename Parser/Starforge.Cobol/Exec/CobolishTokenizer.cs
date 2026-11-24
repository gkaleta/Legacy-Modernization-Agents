namespace Starforge.Cobol.Exec;

internal enum XKind
{
    Word,
    Literal,
    Number,
    LeftParen,
    RightParen,
    Colon,
    Comma,
    Period,
    Operator,
    Other,
}

// Start/End are offsets into the EXEC block text.
internal readonly record struct XToken(XKind Kind, int Start, int End, string Text)
{
    public string Upper => Text.ToUpperInvariant();

    public bool IsWord(string word) => Kind == XKind.Word && Text.Equals(word, StringComparison.OrdinalIgnoreCase);
}

// Tokenizes the inside of EXEC CICS / EXEC DLI with COBOL word rules: hyphens belong to words, so arithmetic
// operators need surrounding spaces exactly as in COBOL.
internal static class CobolishTokenizer
{
    public static List<XToken> Tokenize(string text, int start, int end, out int unterminatedLiteralAt)
    {
        var list = new List<XToken>();
        unterminatedLiteralAt = -1;
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (IsLiteralStart(text, i, end, out var quoteAt))
            {
                var close = ScanQuoted(text, quoteAt, end);
                if (close < 0)
                {
                    unterminatedLiteralAt = i;
                    list.Add(new XToken(XKind.Literal, i, end, text[i..end]));
                    return list;
                }

                list.Add(new XToken(XKind.Literal, i, close + 1, text[i..(close + 1)]));
                i = close + 1;
                continue;
            }

            if (IsWordChar(c))
            {
                var j = i;
                while (j < end && (IsWordChar(text[j]) || (text[j] == '-' && j + 1 < end && IsWordChar(text[j + 1]))))
                {
                    j++;
                }

                // A decimal point inside a number belongs to it; a trailing period would separate sentences.
                if (j + 1 < end && text[j] == '.' && char.IsDigit(text[j + 1]) && AllDigits(text, i, j))
                {
                    j++;
                    while (j < end && char.IsDigit(text[j]))
                    {
                        j++;
                    }
                }

                var word = text[i..j];
                list.Add(new XToken(IsNumber(word) ? XKind.Number : XKind.Word, i, j, word));
                i = j;
                continue;
            }

            var kind = c switch
            {
                '(' => XKind.LeftParen,
                ')' => XKind.RightParen,
                ':' => XKind.Colon,
                ',' or ';' => XKind.Comma,
                '.' => XKind.Period,
                '+' or '-' or '*' or '/' or '=' or '<' or '>' => XKind.Operator,
                _ => XKind.Other,
            };
            var len = kind == XKind.Operator && i + 1 < end && (text.AsSpan(i, 2) is "**" or "<=" or ">=" or "<>") ? 2 : 1;
            list.Add(new XToken(kind, i, i + len, text.Substring(i, len)));
            i += len;
        }

        return list;
    }

    // Returns the index of the matching ')' for the '(' at tokens[open], or -1 when unbalanced.
    public static int MatchParen(IReadOnlyList<XToken> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == XKind.LeftParen)
            {
                depth++;
            }
            else if (tokens[i].Kind == XKind.RightParen && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '#' or '@' or '$';

    private static bool AllDigits(string text, int start, int end)
    {
        for (var k = start; k < end; k++)
        {
            if (!char.IsDigit(text[k]))
            {
                return false;
            }
        }

        return end > start;
    }

    private static bool IsNumber(string word)
    {
        var seenDigit = false;
        foreach (var ch in word)
        {
            if (char.IsDigit(ch))
            {
                seenDigit = true;
            }
            else if (ch != '.')
            {
                return false;
            }
        }

        return seenDigit;
    }

    private static bool IsLiteralStart(string text, int i, int end, out int quoteAt)
    {
        quoteAt = i;
        if (text[i] is '\'' or '"')
        {
            return true;
        }

        // X'..', N'..', G'..', Z'..', U'..', B'..' and two-letter forms NX'..', UX'..', BX'..'.
        var j = i;
        while (j < end && j - i < 2 && char.IsLetter(text[j]))
        {
            j++;
        }

        if (j < end && j > i && text[j] is '\'' or '"' && (i == 0 || !IsWordChar(text[i - 1])))
        {
            var prefix = text[i..j].ToUpperInvariant();
            if (prefix is "X" or "N" or "G" or "Z" or "U" or "B" or "NX" or "UX" or "BX")
            {
                quoteAt = j;
                return true;
            }
        }

        return false;
    }

    private static int ScanQuoted(string text, int quoteAt, int end)
    {
        var q = text[quoteAt];
        var i = quoteAt + 1;
        while (i < end)
        {
            if (text[i] == q)
            {
                if (i + 1 < end && text[i + 1] == q)
                {
                    i += 2;
                    continue;
                }

                return i;
            }

            i++;
        }

        return -1;
    }
}

internal static class ExecArguments
{
    private static readonly HashSet<string> Figuratives = new(StringComparer.OrdinalIgnoreCase)
    {
        "ZERO", "ZEROS", "ZEROES", "SPACE", "SPACES", "HIGH-VALUE", "HIGH-VALUES", "LOW-VALUE", "LOW-VALUES",
        "QUOTE", "QUOTES", "NULL", "NULLS",
    };

    // CICS translator built-ins usable as arguments; they are values, not data references.
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase) { "DFHRESP", "DFHVALUE" };

    // Classifies tokens[from..to) (the inside of an option's parentheses).
    public static ExecArgument Classify(string text, IReadOnlyList<XToken> tokens, int from, int to)
    {
        if (to <= from)
        {
            return new ExecArgument(ExecArgumentKind.Empty, string.Empty, null, null);
        }

        var raw = Collapse(text, tokens[from].Start, tokens[to - 1].End);
        var first = tokens[from];
        if (to - from == 1 && first.Kind == XKind.Literal)
        {
            return new ExecArgument(ExecArgumentKind.Literal, raw, LiteralValue(first.Text), null);
        }

        if (to - from == 1 && first.Kind == XKind.Number)
        {
            return new ExecArgument(ExecArgumentKind.Numeric, raw, first.Text, null);
        }

        if (to - from == 2 && first.Kind == XKind.Operator && first.Text is "+" or "-" && tokens[from + 1].Kind == XKind.Number)
        {
            return new ExecArgument(ExecArgumentKind.Numeric, raw, first.Text + tokens[from + 1].Text, null);
        }

        if (to - from == 1 && first.Kind == XKind.Word && Figuratives.Contains(first.Text))
        {
            return new ExecArgument(ExecArgumentKind.Figurative, raw, first.Upper, null);
        }

        if (first.Kind == XKind.Word && (first.IsWord("LENGTH") || first.IsWord("ADDRESS")) && from + 2 < to && tokens[from + 1].IsWord("OF"))
        {
            var end = DataRefEnd(tokens, from + 2, to);
            var kind = end == to ? (first.IsWord("LENGTH") ? ExecArgumentKind.LengthOf : ExecArgumentKind.AddressOf) : ExecArgumentKind.Expression;
            return new ExecArgument(kind, raw, null, tokens[from + 2].Kind == XKind.Word ? tokens[from + 2].Upper : null);
        }

        if (first.Kind == XKind.Word && Functions.Contains(first.Text) && from + 1 < to && tokens[from + 1].Kind == XKind.LeftParen
            && CobolishTokenizer.MatchParen(Slice(tokens, from + 1, to), 0) == to - from - 2)
        {
            return new ExecArgument(ExecArgumentKind.Function, raw, null, null);
        }

        if (first.Kind == XKind.Word && DataRefEnd(tokens, from, to) == to)
        {
            return new ExecArgument(ExecArgumentKind.DataRef, raw, null, first.Upper);
        }

        return new ExecArgument(ExecArgumentKind.Expression, raw, null, first.Kind == XKind.Word ? first.Upper : null);
    }

    // A COBOL data reference: name [OF|IN name]... [(subscripts)] [(refmod)]. Returns where it stops.
    public static int DataRefEnd(IReadOnlyList<XToken> tokens, int from, int to)
    {
        if (from >= to || tokens[from].Kind != XKind.Word)
        {
            return from;
        }

        var i = from + 1;
        while (i + 1 < to && (tokens[i].IsWord("OF") || tokens[i].IsWord("IN")) && tokens[i + 1].Kind == XKind.Word)
        {
            i += 2;
        }

        for (var groups = 0; groups < 2 && i < to && tokens[i].Kind == XKind.LeftParen; groups++)
        {
            var close = MatchWithin(tokens, i, to);
            if (close < 0)
            {
                return i;
            }

            i = close + 1;
        }

        return i;
    }

    public static string LiteralValue(string literal)
    {
        var q = literal.IndexOfAny(new[] { '\'', '"' });
        if (q < 0 || literal.Length < q + 2)
        {
            return literal;
        }

        var quote = literal[q];
        var body = literal.Substring(q + 1, literal.Length - q - 2);
        return q == 0 ? body.Replace(new string(quote, 2), quote.ToString(), StringComparison.Ordinal) : literal;
    }

    public static string Collapse(string text, int start, int end)
    {
        var sb = new System.Text.StringBuilder(end - start);
        var space = false;
        var quote = '\0';
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }

            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space && sb.Length > 0 && c != ')' && sb[^1] != '(')
            {
                sb.Append(' ');
            }

            space = false;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int MatchWithin(IReadOnlyList<XToken> tokens, int open, int to)
    {
        var depth = 0;
        for (var i = open; i < to; i++)
        {
            if (tokens[i].Kind == XKind.LeftParen)
            {
                depth++;
            }
            else if (tokens[i].Kind == XKind.RightParen && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static List<XToken> Slice(IReadOnlyList<XToken> tokens, int from, int to)
    {
        var list = new List<XToken>(to - from);
        for (var i = from; i < to; i++)
        {
            list.Add(tokens[i]);
        }

        return list;
    }
}
