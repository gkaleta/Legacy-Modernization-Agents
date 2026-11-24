namespace Starforge.Cobol.Copy;

public enum ReplaceMode : byte
{
    Full,
    Leading,
    Trailing,
}

// One "operand-1 BY operand-2" pair. The replacement is a slice of the directive's own text so that
// replaced characters keep pointing at the COPY/REPLACE statement that supplied them.
public sealed class ReplacingPair
{
    internal ReplacingPair(ReplaceMode mode, string[] pattern, TextWordKind[] kinds, OriginText replacementSource, int replacementStart, int replacementEnd)
    {
        Mode = mode;
        Pattern = pattern;
        PatternKinds = kinds;
        ReplacementSource = replacementSource;
        ReplacementStart = replacementStart;
        ReplacementEnd = replacementEnd;
    }

    public ReplaceMode Mode { get; }

    public IReadOnlyList<string> Pattern { get; }

    internal TextWordKind[] PatternKinds { get; }

    internal OriginText ReplacementSource { get; }

    internal int ReplacementStart { get; }

    internal int ReplacementEnd { get; }

    public string ReplacementText => ReplacementSource.Text[ReplacementStart..ReplacementEnd];
}

internal sealed record ParseError(string Code, string Message, int Start, int End);

internal static class ReplacingParser
{
    public static bool Is(string text, TextWord w, string keyword)
        => w.Kind == TextWordKind.Word && w.Length == keyword.Length && string.Compare(text, w.Start, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0;

    public static int FindDelimiter(List<TextWord> words, int from)
    {
        for (var k = from; k < words.Count; k++)
        {
            if (words[k].Kind == TextWordKind.PseudoTextDelimiter)
            {
                return k;
            }
        }

        return -1;
    }

    // Parses "[LEADING|TRAILING] op1 BY op2" starting at words[j]. On success j points past op2.
    public static ReplacingPair? ParsePair(OriginText source, List<TextWord> words, ref int j, bool pseudoTextOnly, out ParseError? error)
    {
        var text = source.Text;
        error = null;
        var mode = ReplaceMode.Full;
        var startWord = j;
        if (j < words.Count && Is(text, words[j], "LEADING"))
        {
            mode = ReplaceMode.Leading;
            j++;
        }
        else if (j < words.Count && Is(text, words[j], "TRAILING"))
        {
            mode = ReplaceMode.Trailing;
            j++;
        }

        if (j >= words.Count)
        {
            error = new ParseError("replacing-incomplete", "REPLACING operand expected", words[startWord].Start, words[^1].End);
            return null;
        }

        List<TextWord> patternWords;
        if (words[j].Kind == TextWordKind.PseudoTextDelimiter)
        {
            var close = FindDelimiter(words, j + 1);
            if (close < 0)
            {
                error = new ParseError("unclosed-pseudo-text", "Pseudo-text is not closed with ==", words[j].Start, words[j].End);
                return null;
            }

            patternWords = words.GetRange(j + 1, close - j - 1);
            j = close + 1;
            if (patternWords.Count == 0)
            {
                error = new ParseError("empty-pseudo-text-1", "Pseudo-text-1 must contain at least one text word", words[startWord].Start, words[close].End);
                return null;
            }
        }
        else
        {
            if (pseudoTextOnly || mode != ReplaceMode.Full)
            {
                error = new ParseError("pseudo-text-expected", "Pseudo-text (==...==) expected", words[j].Start, words[j].End);
                return null;
            }

            patternWords = new List<TextWord>();
            while (j < words.Count && !Is(text, words[j], "BY") && words[j].Kind != TextWordKind.Period)
            {
                patternWords.Add(words[j]);
                j++;
            }

            if (patternWords.Count == 0)
            {
                error = new ParseError("replacing-operand-expected", "REPLACING operand-1 expected", words[startWord].Start, words[Math.Min(j, words.Count - 1)].End);
                return null;
            }
        }

        if (j >= words.Count || !Is(text, words[j], "BY"))
        {
            var at = words[Math.Min(j, words.Count - 1)];
            error = new ParseError("by-expected", "BY expected in REPLACING phrase", at.Start, at.End);
            return null;
        }

        j++;
        if (j >= words.Count)
        {
            error = new ParseError("replacing-operand-expected", "REPLACING operand-2 expected", words[^1].Start, words[^1].End);
            return null;
        }

        int replStart, replEnd;
        if (words[j].Kind == TextWordKind.PseudoTextDelimiter)
        {
            var close = FindDelimiter(words, j + 1);
            if (close < 0)
            {
                error = new ParseError("unclosed-pseudo-text", "Pseudo-text is not closed with ==", words[j].Start, words[j].End);
                return null;
            }

            replStart = words[j].End;
            replEnd = words[close].Start;
            while (replStart < replEnd && char.IsWhiteSpace(text[replStart]))
            {
                replStart++;
            }

            while (replEnd > replStart && char.IsWhiteSpace(text[replEnd - 1]))
            {
                replEnd--;
            }

            j = close + 1;
        }
        else
        {
            if (pseudoTextOnly)
            {
                error = new ParseError("pseudo-text-expected", "Pseudo-text (==...==) expected", words[j].Start, words[j].End);
                return null;
            }

            if (words[j].Kind is not (TextWordKind.Word or TextWordKind.Literal))
            {
                error = new ParseError("replacing-operand-expected", "REPLACING operand-2 expected", words[j].Start, words[j].End);
                return null;
            }

            replStart = words[j].Start;
            j++;
            while (j + 1 < words.Count && (Is(text, words[j], "OF") || Is(text, words[j], "IN")) && words[j + 1].Kind == TextWordKind.Word)
            {
                j += 2;
            }

            if (j < words.Count && words[j].Kind == TextWordKind.LeftParen)
            {
                var depth = 0;
                var k = j;
                for (; k < words.Count; k++)
                {
                    if (words[k].Kind == TextWordKind.LeftParen)
                    {
                        depth++;
                    }
                    else if (words[k].Kind == TextWordKind.RightParen && --depth == 0)
                    {
                        break;
                    }
                    else if (words[k].Kind == TextWordKind.Period)
                    {
                        break;
                    }
                }

                if (k < words.Count && words[k].Kind == TextWordKind.RightParen)
                {
                    j = k + 1;
                }
            }

            replEnd = words[j - 1].End;
        }

        if (mode != ReplaceMode.Full && (patternWords.Count != 1 || patternWords[0].Kind != TextWordKind.Word))
        {
            error = new ParseError("partial-word-pattern", "LEADING/TRAILING pseudo-text-1 must be a single text word", patternWords[0].Start, patternWords[^1].End);
            return null;
        }

        var pattern = new string[patternWords.Count];
        var kinds = new TextWordKind[patternWords.Count];
        for (var k = 0; k < patternWords.Count; k++)
        {
            pattern[k] = text.Substring(patternWords[k].Start, patternWords[k].Length);
            kinds[k] = patternWords[k].Kind;
        }

        return new ReplacingPair(mode, pattern, kinds, source, replStart, replEnd);
    }
}

internal static class Replacer
{
    // Applies pairs to input[start, end) and appends the result; onReplaced receives each matched input range.
    public static int Apply(OriginText input, int start, int end, IReadOnlyList<ReplacingPair> pairs, OriginTextBuilder output, Action<int, int>? onReplaced)
    {
        if (pairs.Count == 0)
        {
            output.Append(input, start, end);
            return 0;
        }

        var text = input.Text;
        var words = TextWordScanner.Scan(text, start, end);
        var cursor = start;
        var replacements = 0;
        var i = 0;
        while (i < words.Count)
        {
            var matched = false;
            foreach (var pair in pairs)
            {
                var w = words[i];
                switch (pair.Mode)
                {
                    case ReplaceMode.Full:
                    {
                        var n = pair.Pattern.Count;
                        if (i + n > words.Count || !MatchesAt(text, words, i, pair))
                        {
                            continue;
                        }

                        var last = words[i + n - 1];
                        output.Append(input, cursor, w.Start);
                        output.AppendReplacement(pair.ReplacementSource, pair.ReplacementStart, pair.ReplacementEnd, input, w.Start, last.End);
                        onReplaced?.Invoke(w.Start, last.End);
                        cursor = last.End;
                        i += n;
                        matched = true;
                        break;
                    }

                    case ReplaceMode.Leading:
                    {
                        var p = pair.Pattern[0];
                        if (w.Kind != TextWordKind.Word || w.Length < p.Length || string.Compare(text, w.Start, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            continue;
                        }

                        output.Append(input, cursor, w.Start);
                        output.AppendReplacement(pair.ReplacementSource, pair.ReplacementStart, pair.ReplacementEnd, input, w.Start, w.Start + p.Length);
                        onReplaced?.Invoke(w.Start, w.Start + p.Length);
                        cursor = w.Start + p.Length;
                        i++;
                        matched = true;
                        break;
                    }

                    case ReplaceMode.Trailing:
                    {
                        var p = pair.Pattern[0];
                        if (w.Kind != TextWordKind.Word || w.Length < p.Length || string.Compare(text, w.End - p.Length, p, 0, p.Length, StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            continue;
                        }

                        output.Append(input, cursor, w.End - p.Length);
                        output.AppendReplacement(pair.ReplacementSource, pair.ReplacementStart, pair.ReplacementEnd, input, w.End - p.Length, w.End);
                        onReplaced?.Invoke(w.End - p.Length, w.End);
                        cursor = w.End;
                        i++;
                        matched = true;
                        break;
                    }
                }

                if (matched)
                {
                    replacements++;
                    break;
                }
            }

            if (!matched)
            {
                i++;
            }
        }

        output.Append(input, cursor, end);
        return replacements;
    }

    private static bool MatchesAt(string text, List<TextWord> words, int i, ReplacingPair pair)
    {
        for (var k = 0; k < pair.Pattern.Count; k++)
        {
            var w = words[i + k];
            var p = pair.Pattern[k];
            if (w.Kind != pair.PatternKinds[k] || w.Length != p.Length)
            {
                return false;
            }

            // Words compare case-insensitively; literal contents must match exactly.
            var cmp = w.Kind == TextWordKind.Literal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (string.Compare(text, w.Start, p, 0, p.Length, cmp) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
