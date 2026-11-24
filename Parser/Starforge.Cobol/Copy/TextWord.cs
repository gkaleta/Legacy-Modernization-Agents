namespace Starforge.Cobol.Copy;

public enum TextWordKind : byte
{
    Word,
    Literal,
    PseudoTextDelimiter,
    Period,
    LeftParen,
    RightParen,
    Colon,
}

// A COBOL text-word as used by COPY REPLACING / REPLACE matching (separators excluded).
public readonly record struct TextWord(int Start, int End, TextWordKind Kind)
{
    public int Length => End - Start;
}

public static class TextWordScanner
{
    public static List<TextWord> Scan(string text, int start, int end)
    {
        var words = new List<TextWord>(Math.Max(16, (end - start) / 6));
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if ((c is ',' or ';') && IsBoundary(text, i + 1, end))
            {
                i++;
                continue;
            }

            if (c == '=' && i + 1 < end && text[i + 1] == '=')
            {
                words.Add(new TextWord(i, i + 2, TextWordKind.PseudoTextDelimiter));
                i += 2;
                continue;
            }

            if (c is '\'' or '"')
            {
                var close = ScanLiteral(text, i, end);
                words.Add(new TextWord(i, close, TextWordKind.Literal));
                i = close;
                continue;
            }

            switch (c)
            {
                case '(':
                    words.Add(new TextWord(i, i + 1, TextWordKind.LeftParen));
                    i++;
                    continue;
                case ')':
                    words.Add(new TextWord(i, i + 1, TextWordKind.RightParen));
                    i++;
                    continue;
                case ':':
                    words.Add(new TextWord(i, i + 1, TextWordKind.Colon));
                    i++;
                    continue;
                case '.' when IsBoundary(text, i + 1, end):
                    words.Add(new TextWord(i, i + 1, TextWordKind.Period));
                    i++;
                    continue;
            }

            var j = i;
            while (j < end)
            {
                var d = text[j];
                if (char.IsWhiteSpace(d) || d is '(' or ')' or ':' or '\'' or '"')
                {
                    break;
                }

                if ((d is '.' or ',' or ';') && IsBoundary(text, j + 1, end))
                {
                    break;
                }

                if (d == '=' && j + 1 < end && text[j + 1] == '=')
                {
                    break;
                }

                j++;
            }

            if (j < end && text[j] is '\'' or '"' && IsLiteralPrefix(text.AsSpan(i, j - i)))
            {
                var close = ScanLiteral(text, j, end);
                words.Add(new TextWord(i, close, TextWordKind.Literal));
                i = close;
                continue;
            }

            words.Add(new TextWord(i, j, TextWordKind.Word));
            i = j;
        }

        return words;
    }

    public static bool IsLiteralPrefix(ReadOnlySpan<char> prefix)
    {
        if (prefix.Length == 0 || prefix.Length > 2)
        {
            return false;
        }

        Span<char> up = stackalloc char[2];
        var n = prefix.ToUpperInvariant(up);
        var p = up[..n];
        return p is "X" or "N" or "NX" or "G" or "Z" or "B" or "BX" or "U" or "UX";
    }

    // Returns the exclusive end of a literal starting at the quote; an unclosed literal stops at end of line.
    public static int ScanLiteral(string text, int quoteAt, int end)
    {
        var q = text[quoteAt];
        var i = quoteAt + 1;
        while (i < end)
        {
            var c = text[i];
            if (c == q)
            {
                if (i + 1 < end && text[i + 1] == q)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            if (c == '\n')
            {
                return i;
            }

            i++;
        }

        return end;
    }

    private static bool IsBoundary(string text, int at, int end)
        => at >= end || char.IsWhiteSpace(text[at]) || (text[at] == '=' && at + 1 < end && text[at + 1] == '=');
}
