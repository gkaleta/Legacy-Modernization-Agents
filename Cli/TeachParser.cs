using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Starforge.Cobol.Dialect;

namespace CobolToQuarkusMigration.Cli;

// The customer's own AI endpoint, described before anything is sent and created only after consent.
public sealed record TeachAiEndpoint(string Service, string Endpoint, string Model, Func<IChatClient> CreateClient);

public sealed record TeachAnswer(string Category, string Explanation, string? RuleKind, string? End);

// Teaches Starforge the site's own COBOL words. The AI only classifies a pseudonymised excerpt; the rule itself is
// built from what Starforge found, checked by reading the whole estate again, and saved only when the person agrees.
public sealed class TeachParser(TextReader input, TextWriter output)
{
    public const string IssuesUrl = "https://github.com/Azure-Samples/Legacy-Modernization-Agents/issues/new";

    private static readonly string[] Categories = ["dialect-rule", "source-error", "not-cobol", "missing-copybook", "parser-gap"];

    private static readonly Dictionary<string, string> CodeMeaning = new(StringComparer.Ordinal)
    {
        ["expected-division-header"] = "the file does not start like a COBOL program",
        ["parser-gap"] = "Starforge read past text it did not understand",
        ["unexpected-data-text"] = "text in the DATA DIVISION that is not a data description",
        ["invalid-statement"] = "a statement Starforge could not read",
        ["unterminated-literal"] = "a quoted literal without its closing quote",
        ["unexpected-character"] = "a character COBOL does not allow here",
        ["exec-without-end-exec"] = "an EXEC block without END-EXEC",
        ["malformed-copy-statement"] = "a COPY statement Starforge could not read",
        ["taught-statement-boundary"] = "a taught statement whose end Starforge could not find",
    };

    public static string Meaning(string code) => CodeMeaning.GetValueOrDefault(code, code);

    public int Report(TeachScan scan, string rulesPath)
    {
        WriteSummary(scan, rulesPath);
        if (scan.Groups.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Teach Starforge these with your own AI endpoint: ./doctor.sh teach-parser");
        }

        return 0;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> roots, string rulesPath, DialectRules rules, Func<TeachAiEndpoint> endpoint,
        CancellationToken cancellationToken = default)
    {
        var scan = TeachScan.Run(roots, rules);
        WriteSummary(scan, rulesPath);
        if (scan.Groups.Count == 0)
        {
            return 0;
        }

        TeachAiEndpoint ai;
        try
        {
            ai = endpoint();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            output.WriteLine($"Your AI endpoint is not set up ({ex.Message}). Run ./doctor.sh setup first.");
            return 1;
        }

        IChatClient? client = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var saved = new List<string>();
        try
        {
            while (scan.Groups.FirstOrDefault(g => !visited.Contains(g.Key)) is { } group)
            {
                visited.Add(group.Key);
                var site = group.Sites[0];
                var excerpt = scan.Excerpt(site);
                var missing = scan.Programs.Where(p => group.Programs.Contains(p.Program) && p.MissingCopybooks.Count > 0).ToList();
                var prompt = UserPrompt(group, excerpt, missing.Count > 0);

                output.WriteLine();
                output.WriteLine($"── {Label(group)}: {Meaning(group.Code)}");
                output.WriteLine($"   {group.Places} place(s) in {group.Programs.Count} program(s). First one: {site.File} line {site.Line}");
                if (missing.Count > 0)
                {
                    output.WriteLine($"   {missing.Count} of these program(s) miss copybooks; the problem may come from those.");
                }

                output.WriteLine();
                output.WriteLine($"   This is exactly what would be sent to {ai.Service} ({ai.Endpoint}, model {ai.Model}).");
                output.WriteLine("   Names from your code are replaced by W1, W2, …; comments and literal contents are removed.");
                output.WriteLine("   It is still your code's structure; your endpoint's own data terms apply.");
                output.WriteLine();
                output.WriteLine(Indent(prompt));
                output.WriteLine();
                var send = Ask("   Send it? [y]es / [s]kip / [q]uit (default skip): ");
                if (send == "q")
                {
                    break;
                }

                if (send != "y")
                {
                    continue;
                }

                client ??= ai.CreateClient();
                TeachAnswer answer;
                try
                {
                    var response = await client.GetResponseAsync(
                        [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, prompt)],
                        new ChatOptions { ModelId = ai.Model },
                        cancellationToken);
                    answer = ParseAnswer(response.Text);
                }
                catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or InvalidOperationException or TaskCanceledException)
                {
                    output.WriteLine($"   The AI answer could not be used ({ex.Message}). Nothing saved.");
                    continue;
                }

                output.WriteLine($"   Your AI says: {answer.Category}. {answer.Explanation}");
                if (Candidate(group, answer, scan.Rules) is not { } rule)
                {
                    output.WriteLine(NoRuleAdvice(answer, missing));
                    continue;
                }

                DialectRules candidate;
                try
                {
                    candidate = scan.Rules.With(rule);
                }
                catch (InvalidDataException ex)
                {
                    output.WriteLine($"   That rule is not allowed ({ex.Message}). Nothing saved.");
                    continue;
                }

                output.WriteLine($"   Proposed rule: {Describe(rule)}");
                output.WriteLine("   Checking it by reading the whole estate again…");
                var check = scan.Verify(group, candidate);
                WriteVerification(check, group);
                if (!check.Passed)
                {
                    output.WriteLine("   Not saved: a rule must fix every place in this group and change nothing else.");
                    continue;
                }

                if (rule.Kind == DialectRuleKind.Statement)
                {
                    var effects = Ask($"   Can {rule.Word} change data or decide what runs next? Answer n only if you are sure it does neither. [n]o / [u]nsure (default unsure): ");
                    if (effects == "n")
                    {
                        rule = rule with { Effects = StatementEffects.None };
                        candidate = scan.Rules.With(rule);
                    }
                    else
                    {
                        output.WriteLine("   Kept as effects unknown: programs that use it stay Partial in the evidence.");
                    }
                }

                if (Ask($"   Save rule '{rule.Id}' to {rulesPath}? [y/N]: ") != "y")
                {
                    continue;
                }

                Save(rulesPath, candidate);
                saved.Add(rule.Id);
                output.WriteLine($"   Saved. Reading the estate again with {candidate.Rules.Count} rule(s)…");
                scan = TeachScan.Run(roots, candidate);
            }
        }
        finally
        {
            client?.Dispose();
        }

        output.WriteLine();
        output.WriteLine(saved.Count == 0
            ? "No rules saved."
            : $"Saved {saved.Count} rule(s): {string.Join(", ", saved)}. Share one with the Starforge team: ./doctor.sh teach-parser share <id>");
        WriteSummary(scan, rulesPath);
        return 0;
    }

    public int List(DialectRules rules, string rulesPath)
    {
        if (rules.IsEmpty)
        {
            output.WriteLine($"No taught rules ({rulesPath}).");
            return 0;
        }

        output.WriteLine($"{rules.Rules.Count} taught rule(s) in {rulesPath} (sha256 {rules.Sha256[..12]}):");
        foreach (var r in rules.Rules)
        {
            output.WriteLine($"  {r.Id,-24} {Describe(r)}{(string.IsNullOrWhiteSpace(r.Reason) ? string.Empty : " — " + r.Reason)}");
        }

        return 0;
    }

    public int Remove(DialectRules rules, string rulesPath, string id)
    {
        if (rules.Rules.All(r => r.Id != id))
        {
            output.WriteLine($"No rule '{id}' in {rulesPath}.");
            return 1;
        }

        Save(rulesPath, rules.Without(id));
        output.WriteLine($"Removed rule '{id}'. The next scan reads without it.");
        return 0;
    }

    // Nothing is sent: the person opens the issue themselves with a generic rule and a made-up example.
    public int Share(DialectRules rules, string id)
    {
        if (rules.Rules.FirstOrDefault(r => r.Id == id) is not { } rule)
        {
            output.WriteLine($"No rule '{id}'.");
            return 1;
        }

        if (rule.Kind == DialectRuleKind.NotProgram)
        {
            output.WriteLine($"Rule '{id}' names files in your estate, so there is nothing generic to share.");
            return 1;
        }

        var generic = rule with { Id = "shared-" + Slug(rule.Word!), Paths = null, Reason = null };
        output.WriteLine("Nothing has been sent. To share this rule with the Starforge team, open");
        output.WriteLine($"{IssuesUrl} and paste the text below.");
        output.WriteLine("It holds only the generic rule and a made-up example, no code from your estate.");
        output.WriteLine();
        output.WriteLine("Rule:");
        output.WriteLine(DialectRules.Create([generic]).ToJson().TrimEnd());
        output.WriteLine();
        output.WriteLine("Made-up example:");
        foreach (var line in Example(generic))
        {
            output.WriteLine(line);
        }

        return 0;
    }

    public static void Save(string rulesPath, DialectRules rules)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(rulesPath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(rulesPath, rules.ToJson());
    }

    internal const string SystemPrompt = """
        You help Starforge, a COBOL parser for IBM Enterprise COBOL with CICS, SQL, DL/I and IDMS.
        Starforge could not read the line marked >> in the excerpt. Names chosen by the site were replaced by
        W1, W2, …; literal contents and comments were removed. Decide what the place is.
        Answer with one JSON object and nothing else:
        {"category": "...", "explanation": "...", "rule": {...}}
        Categories:
        - dialect-rule: a site or vendor extension, not standard COBOL, such as a preprocessor include word or a
          site statement. rule is {"kind":"copyAlias"} when the word includes a member the way COPY does, or
          {"kind":"statement","end":"period"} / {"kind":"statement","end":"END-<WORD>"} when the word starts a
          statement; end says where that statement stops.
        - source-error: the code is wrong (typo, missing quote, misplaced text) and would not compile.
        - not-cobol: the file is not a COBOL program (data, JCL, a listing, a definitions file).
        - missing-copybook: the problem comes from a copybook that was not found.
        - parser-gap: valid standard COBOL that a COBOL parser should read.
        Omit rule unless the category is dialect-rule. Keep explanation to two plain-language sentences.
        """;

    internal static string UserPrompt(TeachGroup group, string excerpt, bool missingCopybooks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Problem: {Meaning(group.Code)} ({group.Code}).");
        sb.AppendLine($"Word at the place: {(group.Word.Length == 0 ? "(none)" : group.Word)}");
        sb.AppendLine($"Places with the same problem and word: {group.Places} in {group.Programs.Count} program(s).");
        sb.AppendLine($"Copybooks missing in these programs: {(missingCopybooks ? "yes" : "no")}.");
        sb.AppendLine("Excerpt:");
        sb.AppendLine(excerpt);
        return sb.ToString().TrimEnd();
    }

    public static TeachAnswer ParseAnswer(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidDataException("no JSON object in the answer");
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var category = Str(root, "category") ?? throw new InvalidDataException("no category");
            if (!Categories.Contains(category))
            {
                throw new InvalidDataException($"unknown category '{category}'");
            }

            var explanation = (Str(root, "explanation") ?? string.Empty).Trim();
            if (explanation.Length > 400)
            {
                explanation = explanation[..400] + "…";
            }

            string? kind = null, ruleEnd = null;
            if (root.TryGetProperty("rule", out var rule) && rule.ValueKind == JsonValueKind.Object)
            {
                kind = Str(rule, "kind");
                ruleEnd = Str(rule, "end");
            }

            return new TeachAnswer(category, explanation, kind, ruleEnd);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"the answer is not valid JSON ({ex.Message})");
        }

        static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    }

    // The rule comes from what Starforge found (the word, the files); the AI only chooses the kind and the end.
    internal static DialectRule? Candidate(TeachGroup group, TeachAnswer answer, DialectRules existing)
    {
        var reason = answer.Explanation.Length > 200 ? answer.Explanation[..200] + "…" : answer.Explanation;
        DialectRule? rule = answer switch
        {
            { Category: "not-cobol" } when group.WholeFile => new DialectRule("not-programs", DialectRuleKind.NotProgram, Paths: group.Programs, Reason: reason),
            { Category: "dialect-rule", RuleKind: "copyAlias" } when group.Word.Length > 0
                => new DialectRule("copy-" + Slug(group.Word), DialectRuleKind.CopyAlias, group.Word, Reason: reason),
            { Category: "dialect-rule", RuleKind: "statement" } when group.Word.Length > 0
                => new DialectRule("stmt-" + Slug(group.Word), DialectRuleKind.Statement, group.Word,
                    string.IsNullOrWhiteSpace(answer.End) ? DialectRule.PeriodEnd : answer.End.Trim(), Reason: reason),
            _ => null,
        };
        return rule is null ? null : rule with { Id = UniqueId(rule.Id, existing) };
    }

    private string NoRuleAdvice(TeachAnswer answer, IReadOnlyList<TeachProgram> missing) => answer.Category switch
    {
        "source-error" => "   This looks like an error in your code. Fix the source; no rule is saved.",
        "missing-copybook" => missing.Count > 0
            ? $"   Add the missing copybook(s) ({string.Join(", ", missing.SelectMany(p => p.MissingCopybooks).Distinct().Take(5))}) to the estate and scan again."
            : "   Add the missing copybook(s) to the estate and scan again.",
        "not-cobol" => "   Only files that fail at their very first line can be marked as not programs; this one reads partly as COBOL. Nothing saved.",
        "parser-gap" => $"   This looks like standard COBOL that Starforge should read. Nothing saved; please tell us at {IssuesUrl} (describe the construct, do not paste your code).",
        _ => "   The answer did not include a rule Starforge can use. Nothing saved.",
    };

    private void WriteSummary(TeachScan scan, string rulesPath)
    {
        var places = scan.Groups.Sum(g => g.Places);
        output.WriteLine($"Starforge read {scan.Programs.Count} program(s)"
            + (scan.Excluded.Count > 0 ? $", {scan.Excluded.Count} excluded as not programs" : string.Empty)
            + (scan.Rules.IsEmpty ? ", no taught rules." : $", {scan.Rules.Rules.Count} taught rule(s) from {rulesPath}."));
        if (scan.Groups.Count == 0)
        {
            output.WriteLine("It read every place in every program.");
            return;
        }

        output.WriteLine($"Places it could not read: {places} in {scan.Groups.Count} group(s).");
        foreach (var g in scan.Groups)
        {
            output.WriteLine($"  {Label(g),-24} {g.Places,4} place(s) in {g.Programs.Count} program(s) — {Meaning(g.Code)}");
        }

        var missing = scan.Programs.Count(p => p.Sites.Count > 0 && p.MissingCopybooks.Count > 0);
        if (missing > 0)
        {
            output.WriteLine($"{missing} of these program(s) also miss copybooks; some places may come from those.");
        }
    }

    private void WriteVerification(TeachVerification check, TeachGroup group)
    {
        output.WriteLine($"   Fixes {check.Fixed} of {group.Places} place(s)."
            + (check.Excluded > 0 ? $" Excludes {check.Excluded} file(s) as not programs." : string.Empty));
        if (check.Remaining.Count > 0)
        {
            output.WriteLine($"   Still not read: {check.Remaining.Count} (first: {check.Remaining[0].File} line {check.Remaining[0].Line}, {check.Remaining[0].Code}).");
        }

        if (check.NewSites.Count > 0)
        {
            output.WriteLine($"   New problems: {check.NewSites.Count} (first: {check.NewSites[0].File} line {check.NewSites[0].Line}, {check.NewSites[0].Code}).");
        }

        if (check.UsesElsewhere.Count > 0)
        {
            output.WriteLine($"   Changes code Starforge already read: {check.UsesElsewhere.Count} place(s) (first: {check.UsesElsewhere[0].File} line {check.UsesElsewhere[0].Line}).");
        }

        if (check.Passed)
        {
            output.WriteLine("   Nothing else changed.");
        }
    }

    private string Ask(string question)
    {
        output.Write(question);
        output.Flush();
        var line = input.ReadLine();
        if (line is null)
        {
            output.WriteLine();
            return "q";
        }

        return line.Trim().ToLowerInvariant() switch
        {
            "y" or "yes" => "y",
            "n" or "no" => "n",
            "q" or "quit" => "q",
            "u" or "unsure" => "u",
            "s" or "skip" => "s",
            _ => string.Empty,
        };
    }

    private static string Label(TeachGroup g) => g.WholeFile ? "(whole file) " + g.Word : g.Word.Length == 0 ? "(no word)" : g.Word;

    private static string Describe(DialectRule r) => r.Kind switch
    {
        DialectRuleKind.CopyAlias => $"{r.Word} includes a member like COPY",
        DialectRuleKind.Statement => $"{r.Word} is a site statement ending at {(r.End == DialectRule.PeriodEnd ? "the period" : r.End)}, effects {(r.Effects == StatementEffects.None ? "none" : "unknown")}",
        _ => $"{r.PathList.Count} file(s) are not programs",
    };

    private static string Indent(string text) => string.Join("\n", text.Split('\n').Select(l => "   │ " + l.TrimEnd('\r')));

    internal static string Slug(string word)
    {
        var slug = Regex.Replace(word.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "word" : slug.Length > 30 ? slug[..30].TrimEnd('-') : slug;
    }

    private static string UniqueId(string id, DialectRules existing)
    {
        var taken = existing.Rules.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var candidate = id;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = $"{id}-{n}";
        }

        return candidate;
    }

    private static IEnumerable<string> Example(DialectRule rule)
    {
        string A(string t) => "       " + t;
        string B(string t) => "           " + t;
        if (rule.Kind == DialectRuleKind.CopyAlias)
        {
            return
            [
                "EXAMPLE.cbl:",
                A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. EXAMPLE."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
                A($"{rule.Word} EXAMPLEC"), A("PROCEDURE DIVISION."), B("MOVE 1 TO EX-ID."), B("GOBACK."),
                "EXAMPLEC.cpy:",
                A("01  EX-REC."), B("05  EX-ID PIC 9(4)."),
            ];
        }

        var end = rule.End == DialectRule.PeriodEnd ? "." : " " + rule.End + ".";
        return
        [
            "EXAMPLE.cbl:",
            A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. EXAMPLE."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
            A("01  EX-FIELD PIC X(8)."), A("PROCEDURE DIVISION."), B($"{rule.Word} EX-FIELD{end}"), B("GOBACK."),
        ];
    }
}
