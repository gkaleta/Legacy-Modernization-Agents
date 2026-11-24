using Starforge.Cobol.Coverage;

namespace Starforge.Cobol.Copy;

internal sealed record ReplaceStatement(int EndWord, int Start, int End, bool Off, IReadOnlyList<ReplacingPair> Pairs, ParseError? Error);

// REPLACE runs after all COPY statements are expanded and affects text up to the next REPLACE or end of unit.
internal static class ReplaceStage
{
    public static bool LooksLikeReplace(string text, List<TextWord> words, int i)
    {
        if (i + 1 >= words.Count)
        {
            return false;
        }

        var next = words[i + 1];
        return next.Kind == TextWordKind.PseudoTextDelimiter
            || ReplacingParser.Is(text, next, "OFF")
            || ReplacingParser.Is(text, next, "ALSO")
            || ReplacingParser.Is(text, next, "LAST")
            || ReplacingParser.Is(text, next, "LEADING")
            || ReplacingParser.Is(text, next, "TRAILING");
    }

    public static ReplaceStatement Parse(OriginText source, List<TextWord> words, int i)
    {
        var text = source.Text;
        var start = words[i].Start;
        var j = i + 1;
        var pairs = new List<ReplacingPair>();
        var off = false;
        if (j < words.Count && ReplacingParser.Is(text, words[j], "LAST"))
        {
            j++;
        }

        if (j < words.Count && ReplacingParser.Is(text, words[j], "OFF"))
        {
            off = true;
            j++;
        }
        else
        {
            if (j < words.Count && ReplacingParser.Is(text, words[j], "ALSO"))
            {
                j++;
            }

            while (j < words.Count && words[j].Kind != TextWordKind.Period)
            {
                var pair = ReplacingParser.ParsePair(source, words, ref j, pseudoTextOnly: true, out var error);
                if (pair is null)
                {
                    var stop = Math.Min(j, words.Count - 1);
                    return new ReplaceStatement(stop, start, words[stop].End, false, pairs, error);
                }

                pairs.Add(pair);
            }
        }

        if (j < words.Count && words[j].Kind == TextWordKind.Period)
        {
            return new ReplaceStatement(j, start, words[j].End, off, pairs, null);
        }

        var last = Math.Min(j, words.Count) - 1;
        return new ReplaceStatement(last, start, words[last].End, off, pairs,
            new ParseError("replace-period-expected", "REPLACE statement must end with a separator period", start, words[last].End));
    }

    public static OriginText Apply(OriginText expanded, ExpandedSource locator, TokenAccounting accounting, List<Diagnostic> diagnostics)
    {
        var text = expanded.Text;
        if (text.IndexOf("REPLACE", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return expanded;
        }

        var words = TextWordScanner.Scan(text, 0, text.Length);
        var statements = new List<ReplaceStatement>();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (ReplacingParser.Is(text, w, "EXEC") || ReplacingParser.Is(text, w, "EXECUTE"))
            {
                while (i + 1 < words.Count && !ReplacingParser.Is(text, words[i + 1], "END-EXEC"))
                {
                    i++;
                }

                continue;
            }

            if (ReplacingParser.Is(text, w, "REPLACE") && LooksLikeReplace(text, words, i))
            {
                var stmt = Parse(expanded, words, i);
                statements.Add(stmt);
                i = stmt.EndWord;
            }
        }

        if (statements.Count == 0)
        {
            return expanded;
        }

        var output = new OriginTextBuilder(text.Length + 256);
        var cursor = 0;
        IReadOnlyList<ReplacingPair> active = Array.Empty<ReplacingPair>();
        foreach (var stmt in statements)
        {
            Replacer.Apply(expanded, cursor, stmt.Start, active, output, (s, e) => accounting.MarkExpanded(expanded, s, e, CoverageClass.Directive, "replaced-by-replace-statement"));
            var range = locator.OriginOf(stmt.Start, stmt.End).Range;
            if (stmt.Error is not null)
            {
                diagnostics.Add(Diagnostic.Create("malformed-replace-statement", DiagnosticSeverity.Error, $"{stmt.Error.Message} ({stmt.Error.Code})", range, stmt.Error.Code));
                accounting.MarkExpanded(expanded, stmt.Start, stmt.End, CoverageClass.Unsupported, "malformed-replace-statement");
            }
            else
            {
                accounting.MarkExpanded(expanded, stmt.Start, stmt.End, CoverageClass.Directive, "replace-statement");
                active = stmt.Off ? Array.Empty<ReplacingPair>() : stmt.Pairs;
            }

            output.AppendSynthetic(" ");
            cursor = stmt.End;
        }

        Replacer.Apply(expanded, cursor, text.Length, active, output, (s, e) => accounting.MarkExpanded(expanded, s, e, CoverageClass.Directive, "replaced-by-replace-statement"));
        return output.Build();
    }
}
