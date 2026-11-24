namespace Starforge.Cobol.Exec;

// EXEC DLI command-level interface. Options after a SEGMENT belong to that path level; options before the first
// SEGMENT (USING PCB, KEYFEEDBACK, ...) belong to the call.
internal static class DliParser
{
    private static readonly Dictionary<string, string> LongForms = new(StringComparer.Ordinal)
    {
        ["GET UNIQUE"] = "GU",
        ["GET NEXT"] = "GN",
        ["GET NEXT IN PARENT"] = "GNP",
        ["GET HOLD UNIQUE"] = "GHU",
        ["GET HOLD NEXT"] = "GHN",
        ["GET HOLD NEXT IN PARENT"] = "GHNP",
        ["INSERT"] = "ISRT",
        ["REPLACE"] = "REPL",
        ["DELETE"] = "DLET",
        ["SCHEDULE"] = "SCHD",
        ["TERMINATE"] = "TERM",
        ["CHECKPOINT"] = "CHKP",
        ["SYMBOLIC CHECKPOINT"] = "SYMCHKP",
        ["EXTENDED RESTART"] = "XRST",
        ["STATISTICS"] = "STAT",
    };

    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "GU", "GHU", "GN", "GHN", "GNP", "GHNP", "ISRT", "REPL", "DLET", "CHKP", "SYMCHKP", "XRST", "SCHD", "TERM", "LOG",
        "STAT", "QUERY", "REFRESH", "ROLB", "ROLL", "ROLS", "SETS", "SETU", "DEQ", "LOAD", "POS", "ACCEPT", "GMSG", "ICMD",
        "RCMD", "INQY",
    };

    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "USING", "PCB", "KEYFEEDBACK", "FEEDBACKLEN", "VARIABLE", "FIRST", "LAST", "CURRENT", "SEGMENT", "SEGLENGTH", "OFFSET",
        "INTO", "FROM", "LOCKED", "LOCKCLASS", "MOVENEXT", "GETFIRST", "SET", "SETCOND", "SETZERO", "SETPARENT", "FIELDLENGTH",
        "KEYLENGTH", "KEYS", "WHERE", "PSB", "SYSSERVE", "NODHABEND", "ID", "MAXLENGTH", "AREA1", "AREA2", "AREA3", "AREA4",
        "AREA5", "AREA6", "AREA7", "LENGTH1", "LENGTH2", "LENGTH3", "LENGTH4", "LENGTH5", "LENGTH6", "LENGTH7", "LENGTH", "TOKEN",
        "AREA", "VSAM", "NONVSAM", "FORMATTED", "UNFORMATTED", "SUMMARY", "STATSOK", "CHECKPOINT", "ENDPSB",
    };

    private readonly record struct Item(XToken Word, int Open, int Close)
    {
        public string Name => Word.Upper;

        public bool HasArgument => Open >= 0;
    }

    public static ExecStatement Parse(ExecBlockContext ctx)
    {
        var tokens = CobolishTokenizer.Tokenize(ctx.Text, ctx.InnerStart, ctx.InnerEnd, out var badLiteral);
        if (badLiteral >= 0)
        {
            return Fail(ctx, string.Empty, "dli-unterminated-literal", "Literal inside EXEC DLI is not terminated", badLiteral, ctx.InnerEnd, "LITERAL");
        }

        if (tokens.Count == 0)
        {
            return Fail(ctx, string.Empty, "dli-empty-command", "EXEC DLI contains no command", ctx.InnerStart, ctx.InnerEnd, string.Empty);
        }

        var items = new List<Item>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == XKind.Comma)
            {
                continue;
            }

            if (t.Kind != XKind.Word)
            {
                return Fail(ctx, items.Count > 0 ? items[0].Name : string.Empty, "dli-unexpected-token", $"Unexpected '{t.Text}' in EXEC DLI", t.Start, t.End,
                    DiagnosticShape.Normalize(t.Kind == XKind.Literal ? "'x'" : t.Text));
            }

            if (i + 1 < tokens.Count && tokens[i + 1].Kind == XKind.LeftParen)
            {
                var close = CobolishTokenizer.MatchParen(tokens, i + 1);
                if (close < 0)
                {
                    return Fail(ctx, items.Count > 0 ? items[0].Name : t.Upper, "dli-unbalanced-parentheses", $"Unbalanced parentheses after {t.Upper} in EXEC DLI",
                        t.Start, ctx.InnerEnd, t.Upper);
                }

                items.Add(new Item(t, i + 1, close));
                i = close;
            }
            else
            {
                items.Add(new Item(t, -1, -1));
            }
        }

        var (function, consumed) = Function(items);
        if (function is null)
        {
            return Fail(ctx, items[0].Name, "dli-unknown-command", $"Unknown DL/I command {items[0].Name}", items[0].Word.Start, items[0].Word.End, items[0].Name);
        }

        var callOptions = new List<ExecOption>();
        var segments = new List<(ExecTarget Name, List<ExecOption> Options)>();
        ExecArgument? pcb = null;
        ExecTarget? psb = null;
        for (var k = consumed; k < items.Count; k++)
        {
            var item = items[k];
            var end = item.HasArgument ? tokens[item.Close].End : item.Word.End;
            var range = ctx.RangeOf(item.Word.Start, end);
            if (item.Name == "USING" && !item.HasArgument && k + 1 < items.Count && items[k + 1].Name == "PCB")
            {
                continue;
            }

            if (item.Name == "SEGMENT" || item.Name == "PSB")
            {
                var target = NameTarget(ctx, tokens, item);
                if (target is null)
                {
                    return Fail(ctx, function, "dli-malformed-command", $"{item.Name} of EXEC DLI {function} needs a name or (data-name)", item.Word.Start, end,
                        function + " " + item.Name);
                }

                if (item.Name == "PSB")
                {
                    psb = target;
                    callOptions.Add(new ExecOption(item.Name, Argument(ctx, tokens, item), true, range));
                }
                else
                {
                    segments.Add((target, new List<ExecOption>()));
                }

                continue;
            }

            var option = new ExecOption(item.Name, Argument(ctx, tokens, item), KnownOptions.Contains(item.Name), range);
            if (item.Name == "PCB")
            {
                pcb = option.Argument;
            }

            if (!option.Known)
            {
                ctx.Report("dli-unknown-option", DiagnosticSeverity.Warning, $"Option {item.Name} is not known for EXEC DLI {function}", item.Word.Start, end,
                    function + " " + item.Name);
            }

            (segments.Count > 0 ? segments[^1].Options : callOptions).Add(option);
        }

        var resources = new List<ExecResource>();
        if (psb is not null)
        {
            resources.Add(new ExecResource(ExecResourceKind.Psb, "PSB", psb));
        }

        return new DliCommand
        {
            Kind = ctx.Kind,
            Status = ExecStatus.Parsed,
            Range = ctx.Range,
            ExpandedStart = ctx.ExpandedStart,
            ExpandedEnd = ctx.ExpandedEnd,
            Diagnostics = ctx.Diagnostics,
            Function = function,
            Pcb = pcb,
            Psb = psb,
            Segments = segments.Select(s => new DliSegment(s.Name, s.Options)).ToList(),
            Options = callOptions,
            Resources = resources,
        };
    }

    private static (string? Function, int Consumed) Function(List<Item> items)
    {
        if (items[0].HasArgument)
        {
            return (null, 0);
        }

        for (var n = Math.Min(5, items.Count); n >= 1; n--)
        {
            if (items.Take(n - 1).Any(i => i.HasArgument))
            {
                continue;
            }

            var words = string.Join(' ', items.Take(n).Select(i => i.Name));
            if (LongForms.TryGetValue(words, out var shortForm) && !items[n - 1].HasArgument)
            {
                return (shortForm, n);
            }
        }

        return Functions.Contains(items[0].Name) ? (items[0].Name, 1) : (null, 0);
    }

    // SEGMENT(name) names the segment; SEGMENT((data-name)) takes it from storage at run time.
    private static ExecTarget? NameTarget(ExecBlockContext ctx, List<XToken> tokens, Item item)
    {
        if (!item.HasArgument || item.Close - item.Open < 2)
        {
            return null;
        }

        var first = tokens[item.Open + 1];
        if (first.Kind == XKind.LeftParen && CobolishTokenizer.MatchParen(tokens, item.Open + 1) == item.Close - 1)
        {
            var inner = ExecArguments.Classify(ctx.Text, tokens, item.Open + 2, item.Close - 1);
            return inner.Kind == ExecArgumentKind.Empty ? null : new ExecTarget(inner.Text, false, inner.DataName ?? inner.Text.ToUpperInvariant());
        }

        if (item.Close - item.Open == 2 && first.Kind == XKind.Word)
        {
            return new ExecTarget(first.Upper, true, null);
        }

        if (item.Close - item.Open == 2 && first.Kind == XKind.Literal)
        {
            return new ExecTarget(ExecArguments.LiteralValue(first.Text), true, null);
        }

        return null;
    }

    private static ExecArgument? Argument(ExecBlockContext ctx, List<XToken> tokens, Item item)
        => item.HasArgument ? ExecArguments.Classify(ctx.Text, tokens, item.Open + 1, item.Close) : null;

    private static DliCommand Fail(ExecBlockContext ctx, string function, string code, string message, int start, int end, string shape)
    {
        ctx.Report(code, DiagnosticSeverity.Error, message, start, end, shape);
        return new DliCommand
        {
            Kind = ctx.Kind,
            Status = ExecStatus.Unsupported,
            Range = ctx.Range,
            ExpandedStart = ctx.ExpandedStart,
            ExpandedEnd = ctx.ExpandedEnd,
            Diagnostics = ctx.Diagnostics,
            Function = function,
            Options = Array.Empty<ExecOption>(),
            UnsupportedReason = code,
        };
    }
}
