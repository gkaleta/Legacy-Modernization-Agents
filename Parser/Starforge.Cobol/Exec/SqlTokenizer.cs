namespace Starforge.Cobol.Exec;

internal enum SKind
{
    Word,
    QuotedId,
    String,
    Number,
    Host,
    LeftParen,
    RightParen,
    Comma,
    Dot,
    Operator,
    Other,
}

// Text is upper-cased for words, the identifier body for delimited identifiers, and the host name for :host.
internal readonly record struct SToken(SKind Kind, int Start, int End, string Text, string? Indicator = null)
{
    public bool Is(string word) => Kind == SKind.Word && Text == word;

    public bool IsName => Kind is SKind.Word or SKind.QuotedId;
}

// DB2 embedded SQL tokens. Words take COBOL hyphens (host-language names for cursors and statements may contain them),
// so an unspaced "A-B" reads as one word, which never matters for table extraction.
internal static class SqlTokenizer
{
    public static List<SToken> Tokenize(string text, int start, int end, List<ExecSpan> comments, out int unterminatedAt)
    {
        var list = new List<SToken>();
        unterminatedAt = -1;
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < end && text[i + 1] == '-' && (i == 0 || !CobolishTokenizer.IsWordChar(text[i - 1])))
            {
                var eol = text.IndexOf('\n', i);
                var stop = eol < 0 || eol > end ? end : eol;
                comments.Add(new ExecSpan(i, stop));
                i = stop;
                continue;
            }

            if (c == '/' && i + 1 < end && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0 || close + 2 > end)
                {
                    unterminatedAt = i;
                    return list;
                }

                comments.Add(new ExecSpan(i, close + 2));
                i = close + 2;
                continue;
            }

            if (IsStringStart(text, i, end, out var quoteAt))
            {
                var close = ScanQuoted(text, quoteAt, end, '\'');
                if (close < 0)
                {
                    unterminatedAt = i;
                    return list;
                }

                list.Add(new SToken(SKind.String, i, close + 1, text[i..(close + 1)]));
                i = close + 1;
                continue;
            }

            if (c == '"')
            {
                var close = ScanQuoted(text, i, end, '"');
                if (close < 0)
                {
                    unterminatedAt = i;
                    return list;
                }

                list.Add(new SToken(SKind.QuotedId, i, close + 1, text[(i + 1)..close].Replace("\"\"", "\"", StringComparison.Ordinal)));
                i = close + 1;
                continue;
            }

            if (c == ':' && TryHost(text, i, end, out var host, out var next))
            {
                list.Add(host);
                i = next;
                continue;
            }

            if (char.IsDigit(c) || (c == '.' && i + 1 < end && char.IsDigit(text[i + 1])))
            {
                var j = i;
                while (j < end && (char.IsLetterOrDigit(text[j]) || text[j] == '.'
                    || (text[j] is '+' or '-' && j > i && text[j - 1] is 'E' or 'e' && char.IsDigit(text[i]))))
                {
                    j++;
                }

                list.Add(new SToken(SKind.Number, i, j, text[i..j].ToUpperInvariant()));
                i = j;
                continue;
            }

            if (CobolishTokenizer.IsWordChar(c))
            {
                var j = WordEnd(text, i, end);
                list.Add(new SToken(SKind.Word, i, j, text[i..j].ToUpperInvariant()));
                i = j;
                continue;
            }

            var kind = c switch
            {
                '(' => SKind.LeftParen,
                ')' => SKind.RightParen,
                ',' => SKind.Comma,
                '.' => SKind.Dot,
                '+' or '-' or '*' or '/' or '=' or '<' or '>' or '|' or '!' or '^' or '¬' => SKind.Operator,
                _ => SKind.Other,
            };
            var len = kind == SKind.Operator && i + 1 < end && text[i + 1] is '=' or '>' or '|' && c is '<' or '>' or '!' or '^' or '¬' or '|' ? 2 : 1;
            list.Add(new SToken(kind, i, i + len, text.Substring(i, len)));
            i += len;
        }

        return list;
    }

    private static int WordEnd(string text, int i, int end)
    {
        var j = i;
        while (j < end && (CobolishTokenizer.IsWordChar(text[j]) || (text[j] == '-' && j + 1 < end && CobolishTokenizer.IsWordChar(text[j + 1]) && j > i)))
        {
            j++;
        }

        return j;
    }

    // :name[.name...] followed by an optional indicator written as :ind or INDICATOR :ind.
    private static bool TryHost(string text, int colon, int end, out SToken host, out int next)
    {
        host = default;
        next = colon;
        var i = SkipBlanks(text, colon + 1, end);
        if (i >= end || !CobolishTokenizer.IsWordChar(text[i]))
        {
            return false;
        }

        var j = WordEnd(text, i, end);
        while (j + 1 < end && text[j] == '.' && CobolishTokenizer.IsWordChar(text[j + 1]))
        {
            j = WordEnd(text, j + 1, end);
        }

        var name = text[i..j].ToUpperInvariant();
        var stop = j;
        string? indicator = null;
        var k = SkipBlanks(text, j, end);
        if (k < end && text[k] == ':')
        {
            var ind = SkipBlanks(text, k + 1, end);
            if (ind < end && CobolishTokenizer.IsWordChar(text[ind]))
            {
                stop = WordEnd(text, ind, end);
                indicator = text[ind..stop].ToUpperInvariant();
            }
        }
        else if (k + 9 < end && text.AsSpan(k, 9).Equals("INDICATOR", StringComparison.OrdinalIgnoreCase) && !CobolishTokenizer.IsWordChar(text[k + 9]))
        {
            var c2 = SkipBlanks(text, k + 9, end);
            var ind = c2 < end && text[c2] == ':' ? SkipBlanks(text, c2 + 1, end) : -1;
            if (ind > 0 && ind < end && CobolishTokenizer.IsWordChar(text[ind]))
            {
                stop = WordEnd(text, ind, end);
                indicator = text[ind..stop].ToUpperInvariant();
            }
        }

        host = new SToken(SKind.Host, colon, stop, name, indicator);
        next = stop;
        return true;
    }

    private static int SkipBlanks(string text, int i, int end)
    {
        while (i < end && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    private static bool IsStringStart(string text, int i, int end, out int quoteAt)
    {
        quoteAt = i;
        if (text[i] == '\'')
        {
            return true;
        }

        var j = i;
        while (j < end && j - i < 2 && char.IsLetter(text[j]))
        {
            j++;
        }

        if (j < end && j > i && text[j] == '\'' && (i == 0 || !CobolishTokenizer.IsWordChar(text[i - 1])))
        {
            var prefix = text[i..j].ToUpperInvariant();
            if (prefix is "X" or "G" or "N" or "GX" or "UX" or "BX")
            {
                quoteAt = j;
                return true;
            }
        }

        return false;
    }

    private static int ScanQuoted(string text, int quoteAt, int end, char q)
    {
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
