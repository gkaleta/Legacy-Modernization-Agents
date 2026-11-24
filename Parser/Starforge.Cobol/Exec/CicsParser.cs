namespace Starforge.Cobol.Exec;

internal static class CicsParser
{
    // A top-level word of the command with its optional parenthesised argument (token indices).
    private readonly record struct Item(XToken Word, int Open, int Close)
    {
        public string Name => Word.Upper;

        public bool HasArgument => Open >= 0;
    }

    private static readonly HashSet<string> SpiVerbs = new(StringComparer.Ordinal) { "INQUIRE", "SET", "DISCARD", "CREATE" };

    private static readonly HashSet<string> FileVerbs = new(StringComparer.Ordinal)
    {
        "READ", "READNEXT", "READPREV", "WRITE", "REWRITE", "DELETE", "STARTBR", "ENDBR", "RESETBR", "UNLOCK",
        "INQUIRE FILE", "SET FILE", "INQUIRE DSNAME",
    };

    private static readonly HashSet<string> ProgramVerbs = new(StringComparer.Ordinal)
    {
        "LINK", "XCTL", "LOAD", "RELEASE", "HANDLE ABEND", "INQUIRE PROGRAM", "SET PROGRAM", "DISCARD PROGRAM",
        "DEFINE ACTIVITY", "DEFINE PROCESS",
    };

    private static readonly HashSet<string> TransIdVerbs = new(StringComparer.Ordinal)
    {
        "START", "RETURN", "CANCEL", "SEND PAGE", "LINK", "RUN TRANSID", "DEFINE ACTIVITY", "DEFINE PROCESS",
        "INQUIRE TRANSACTION", "SET TRANSACTION", "DISCARD TRANSACTION",
    };

    private static readonly HashSet<string> CommareaVerbs = new(StringComparer.Ordinal) { "LINK", "XCTL", "RETURN" };

    public static CicsCommand Parse(ExecBlockContext ctx)
    {
        var tokens = CobolishTokenizer.Tokenize(ctx.Text, ctx.InnerStart, ctx.InnerEnd, out var badLiteral);
        if (badLiteral >= 0)
        {
            return Fail(ctx, string.Empty, "cics-unterminated-literal", "Literal inside EXEC CICS is not terminated", badLiteral, ctx.InnerEnd, "LITERAL");
        }

        if (tokens.Count == 0)
        {
            return Fail(ctx, string.Empty, "cics-empty-command", "EXEC CICS contains no command", ctx.InnerStart, ctx.InnerEnd, string.Empty);
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
                var first = items.Count > 0 ? items[0].Name : string.Empty;
                return Fail(ctx, first, "cics-unexpected-token", $"Unexpected '{t.Text}' in EXEC CICS {first}".TrimEnd(), t.Start, t.End,
                    DiagnosticShape.Normalize(t.Kind == XKind.Literal ? "'x'" : t.Text));
            }

            if (i + 1 < tokens.Count && tokens[i + 1].Kind == XKind.LeftParen)
            {
                var close = CobolishTokenizer.MatchParen(tokens, i + 1);
                if (close < 0)
                {
                    return Fail(ctx, items.Count > 0 ? items[0].Name : t.Upper, "cics-unbalanced-parentheses",
                        $"Unbalanced parentheses after {t.Upper} in EXEC CICS", t.Start, ctx.InnerEnd, t.Upper);
                }

                items.Add(new Item(t, i + 1, close));
                i = close;
            }
            else
            {
                items.Add(new Item(t, -1, -1));
            }
        }

        if (items[0].HasArgument || !CicsCommandTable.ByFirstWord.TryGetValue(items[0].Name, out var candidates))
        {
            return Fail(ctx, items[0].Name, "cics-unknown-command", $"Unknown CICS command {items[0].Name}",
                items[0].Word.Start, items[0].Word.End, items[0].Name);
        }

        var (spec, verbItems, verb) = Resolve(items, candidates);
        if (spec is null)
        {
            var shape = items.Count > 1 ? items[0].Name + " " + items[1].Name : items[0].Name;
            return Fail(ctx, items[0].Name, "cics-unknown-command", $"Unknown CICS command {shape}", items[0].Word.Start,
                items[Math.Min(1, items.Count - 1)].Word.End, shape);
        }

        var options = new List<ExecOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var k = 0; k < items.Count; k++)
        {
            var item = items[k];
            var isVerbWord = verbItems.Contains(k);
            if (isVerbWord && !item.HasArgument)
            {
                continue;
            }

            var arg = item.HasArgument ? ExecArguments.Classify(ctx.Text, tokens, item.Open + 1, item.Close) : null;
            var end = item.HasArgument ? tokens[item.Close].End : item.Word.End;
            var range = ctx.RangeOf(item.Word.Start, end);
            var known = isVerbWord
                || spec.OpenEnded
                || spec.Options.ContainsKey(item.Name)
                || CicsCommandTable.Common.ContainsKey(item.Name);
            var expected = isVerbWord ? spec.VerbArgument
                : spec.Options.TryGetValue(item.Name, out var e1) ? e1
                : CicsCommandTable.Common.TryGetValue(item.Name, out var e2) ? e2
                : CicsCommandTable.Optional;
            options.Add(new ExecOption(item.Name, arg, known, range));

            if (!known)
            {
                ctx.Report("cics-unknown-option", DiagnosticSeverity.Warning, $"Option {item.Name} is not known for EXEC CICS {verb}",
                    item.Word.Start, end, verb + " " + item.Name);
            }
            else if (!spec.OpenEnded && expected == CicsCommandTable.Required && !item.HasArgument)
            {
                ctx.Report("cics-option-missing-argument", DiagnosticSeverity.Warning, $"Option {item.Name} of EXEC CICS {verb} needs an argument",
                    item.Word.Start, end, verb + " " + item.Name);
            }
            else if (!spec.OpenEnded && expected == CicsCommandTable.NoArgument && item.HasArgument)
            {
                ctx.Report("cics-option-unexpected-argument", DiagnosticSeverity.Warning, $"Option {item.Name} of EXEC CICS {verb} takes no argument",
                    item.Word.Start, end, verb + " " + item.Name);
            }

            if (!seen.Add(item.Name) && !spec.OpenEnded)
            {
                ctx.Report("cics-duplicate-option", DiagnosticSeverity.Warning, $"Option {item.Name} appears more than once in EXEC CICS {verb}",
                    item.Word.Start, end, verb + " " + item.Name);
            }
        }

        return new CicsCommand
        {
            Kind = ctx.Kind,
            Status = ExecStatus.Parsed,
            Range = ctx.Range,
            ExpandedStart = ctx.ExpandedStart,
            ExpandedEnd = ctx.ExpandedEnd,
            Diagnostics = ctx.Diagnostics,
            Verb = verb,
            Options = options,
            Resources = Resources(verb, options),
        };
    }

    // Longest adjacent verb words first, then a second verb word written later among the options
    // (CICS accepts SEND MAPSET(x) MAP(y)), then the single-word form or an assumed qualifier.
    private static (CicsCommandSpec? Spec, HashSet<int> VerbItems, string Verb) Resolve(List<Item> items, IReadOnlyList<CicsCommandSpec> candidates)
    {
        foreach (var c in candidates)
        {
            if (c.Words.Length < 2 || c.Words.Length > items.Count)
            {
                continue;
            }

            var ok = true;
            for (var w = 1; w < c.Words.Length && ok; w++)
            {
                var last = w == c.Words.Length - 1;
                ok = items[w].Name == c.Words[w] && (last ? ArgumentFits(items[w], c.VerbArgument) : !items[w].HasArgument);
            }

            if (ok)
            {
                return (c, Enumerable.Range(0, c.Words.Length).ToHashSet(), c.Verb);
            }
        }

        CicsCommandSpec? best = null;
        var bestAt = int.MaxValue;
        foreach (var c in candidates.Where(c => c.Words.Length == 2))
        {
            for (var k = 1; k < items.Count && k < bestAt; k++)
            {
                if (items[k].Name == c.Words[1] && ArgumentFits(items[k], c.VerbArgument))
                {
                    best = c;
                    bestAt = k;
                }
            }
        }

        if (best is not null)
        {
            return (best, new HashSet<int> { 0, bestAt }, best.Verb);
        }

        var single = candidates.FirstOrDefault(c => c.Words.Length == 1);
        if (single is not null)
        {
            if (single.OpenEnded && single.Words[0] != "ASSIGN" && items.Count > 1)
            {
                // SPI and family commands (INQUIRE PROGRAM, WEB SEND, ISSUE ERROR) take their object from the second word.
                if (!SpiVerbs.Contains(single.Words[0]) || CicsCommandTable.SpiResourceTypes.Contains(items[1].Name) || !items[1].HasArgument)
                {
                    return (single, new HashSet<int> { 0, 1 }, single.Words[0] + " " + items[1].Name);
                }
            }

            return (single, new HashSet<int> { 0 }, single.Verb);
        }

        var implied = candidates.FirstOrDefault(c => c.OptionalQualifier);
        return implied is null ? (null, new HashSet<int>(), string.Empty) : (implied, new HashSet<int> { 0 }, implied.Verb);
    }

    private static bool ArgumentFits(Item item, char expected)
        => expected switch
        {
            CicsCommandTable.Required => item.HasArgument,
            CicsCommandTable.NoArgument => !item.HasArgument,
            _ => true,
        };

    private static IReadOnlyList<ExecResource> Resources(string verb, List<ExecOption> options)
    {
        var list = new List<ExecResource>();
        ExecArgument? Arg(string name) => options.FirstOrDefault(o => o.Name == name && o.Argument is { Kind: not ExecArgumentKind.Empty })?.Argument;

        void Add(ExecResourceKind kind, string name, ExecTarget? qualifier = null, bool implied = false)
        {
            if (Arg(name) is { } a)
            {
                list.Add(new ExecResource(kind, name, ExecTarget.From(a), qualifier, implied));
            }
        }

        if (ProgramVerbs.Contains(verb))
        {
            Add(ExecResourceKind.Program, "PROGRAM");
        }

        if (TransIdVerbs.Contains(verb))
        {
            Add(ExecResourceKind.TransId, verb == "INQUIRE TRANSACTION" || verb == "SET TRANSACTION" || verb == "DISCARD TRANSACTION" ? "TRANSACTION" : "TRANSID");
        }

        if (FileVerbs.Contains(verb))
        {
            Add(ExecResourceKind.File, "FILE");
            Add(ExecResourceKind.File, "DATASET");
            Add(ExecResourceKind.File, "DSNAME");
        }

        if (verb is "READQ TS" or "WRITEQ TS" or "DELETEQ TS")
        {
            Add(ExecResourceKind.TsQueue, "QUEUE");
            Add(ExecResourceKind.TsQueue, "QNAME");
        }
        else if (verb is "READQ TD" or "WRITEQ TD" or "DELETEQ TD")
        {
            Add(ExecResourceKind.TdQueue, "QUEUE");
        }
        else if (verb is "INQUIRE TSQUEUE" or "SET TSQUEUE" or "INQUIRE TSQNAME" or "SET TSQNAME")
        {
            Add(ExecResourceKind.TsQueue, verb[(verb.IndexOf(' ') + 1)..]);
        }
        else if (verb is "INQUIRE TDQUEUE" or "SET TDQUEUE")
        {
            Add(ExecResourceKind.TdQueue, "TDQUEUE");
        }

        if (verb is "SEND MAP" or "RECEIVE MAP" && Arg("MAP") is { } map)
        {
            var mapTarget = ExecTarget.From(map);
            var mapset = Arg("MAPSET") is { } ms ? ExecTarget.From(ms) : null;
            list.Add(new ExecResource(ExecResourceKind.Map, "MAP", mapTarget, mapset ?? mapTarget, mapset is null));
        }

        if (verb is "GET CONTAINER" or "PUT CONTAINER" or "MOVE CONTAINER" or "DELETE CONTAINER" && Arg("CONTAINER") is { } container)
        {
            var channel = Arg("CHANNEL") is { } ch ? ExecTarget.From(ch) : null;
            list.Add(new ExecResource(ExecResourceKind.Container, "CONTAINER", ExecTarget.From(container), channel));
        }

        if (verb != "ASSIGN")
        {
            Add(ExecResourceKind.Channel, "CHANNEL");
            Add(ExecResourceKind.Channel, "TOCHANNEL");
        }

        if (CommareaVerbs.Contains(verb) && Arg("COMMAREA") is { } commarea)
        {
            var length = Arg("LENGTH") is { } len ? ExecTarget.From(len) : null;
            list.Add(new ExecResource(ExecResourceKind.Commarea, "COMMAREA", ExecTarget.From(commarea), length));
        }

        if (verb == "HANDLE ABEND")
        {
            Add(ExecResourceKind.Label, "LABEL");
        }
        else if (verb is "HANDLE CONDITION" or "HANDLE AID")
        {
            foreach (var o in options.Where(o => o.Argument is { Kind: ExecArgumentKind.DataRef }))
            {
                list.Add(new ExecResource(ExecResourceKind.Label, o.Name, ExecTarget.From(o.Argument!)));
            }
        }

        return list;
    }

    private static CicsCommand Fail(ExecBlockContext ctx, string verb, string code, string message, int start, int end, string shape)
    {
        ctx.Report(code, DiagnosticSeverity.Error, message, start, end, shape);
        return new CicsCommand
        {
            Kind = ctx.Kind,
            Status = ExecStatus.Unsupported,
            Range = ctx.Range,
            ExpandedStart = ctx.ExpandedStart,
            ExpandedEnd = ctx.ExpandedEnd,
            Diagnostics = ctx.Diagnostics,
            Verb = verb,
            Options = Array.Empty<ExecOption>(),
            UnsupportedReason = code,
        };
    }
}
