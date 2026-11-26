using System.CommandLine;
using Starforge.Cobol.Dialect;

namespace CobolToQuarkusMigration.Cli;

// teach-parser report|run|list|remove|share|forget. Only "run" talks to the AI endpoint: automatically once the person has
// agreed for the estate folders and endpoint, or group by group with --review. "share" writes a note and sends nothing
// unless --github is given and the person confirms.
public static class TeachParserCommand
{
    public const string DefaultRulesPath = "Config/starforge-dialect.local.json";

    public static Command Build(Func<TeachAiEndpoint> endpoint)
    {
        var cmd = new Command("teach-parser", "Teach Starforge your site's own COBOL words, using your AI endpoint (rules stay in " + DefaultRulesPath + ").");

        var rulesOption = new Option<string>("--rules", () => DefaultRulesPath, "Local rules file.");
        Option<string[]> SourceOption() => Existing(new("--source", "Estate folder (repeatable).") { IsRequired = true, AllowMultipleArgumentsPerToken = false });

        var reportSource = SourceOption();
        var report = new Command("report", "List what Starforge cannot read, grouped. Sends nothing.");
        report.AddOption(reportSource);
        report.AddOption(rulesOption);
        report.SetHandler((string[] sources, string rulesPath) =>
            Environment.ExitCode = Guard(rulesPath, rules => new TeachParser(Console.In, Console.Out).Report(TeachScan.Run(sources, rules), rulesPath)),
            reportSource, rulesOption);

        var runSource = SourceOption();
        var reviewOption = new Option<bool>("--review", "Decide group by group what is sent and saved, instead of teaching automatically.");
        var agreeOption = new Option<bool>("--agree", "Agree to send excerpts from these estate folders to your AI endpoint automatically (remembered).");
        var run = new Command("run", "Ask your AI endpoint about each group, check each proposed rule against the whole estate and save the ones that pass. Asks once per estate before sending; --review asks per group.");
        run.AddOption(runSource);
        run.AddOption(rulesOption);
        run.AddOption(reviewOption);
        run.AddOption(agreeOption);
        run.SetHandler(async (string[] sources, string rulesPath, bool review, bool agree) =>
        {
            if (review && Console.IsInputRedirected)
            {
                Console.Error.WriteLine("teach-parser run --review asks before sending anything, so it needs a terminal. Use 'teach-parser report' in scripts.");
                Environment.ExitCode = 2;
                return;
            }

            if (!TryLoad(rulesPath, out var rules))
            {
                return;
            }

            var options = new TeachRunOptions(
                Automatic: !review,
                Consent: new TeachConsent(TeachConsent.DefaultPath),
                Agree: agree,
                CanAsk: !Console.IsInputRedirected,
                JournalRoot: TeachJournal.Folder,
                Notes: new TeachNotes(TeachNotes.DefaultFolder));
            Environment.ExitCode = await new TeachParser(Console.In, Console.Out).RunAsync(sources, rulesPath, rules, endpoint, options);
        }, runSource, rulesOption, reviewOption, agreeOption);

        var list = new Command("list", "Show the taught rules.");
        list.AddOption(rulesOption);
        list.SetHandler((string rulesPath) =>
            Environment.ExitCode = Guard(rulesPath, rules => new TeachParser(Console.In, Console.Out).List(rules, rulesPath, new TeachNotes(TeachNotes.DefaultFolder))), rulesOption);

        var idArgument = new Argument<string>("id", "Rule id (see teach-parser list).");
        var remove = new Command("remove", "Remove a taught rule.");
        remove.AddArgument(idArgument);
        remove.AddOption(rulesOption);
        remove.SetHandler((string id, string rulesPath) =>
            Environment.ExitCode = Guard(rulesPath, rules => new TeachParser(Console.In, Console.Out).Remove(rules, rulesPath, id)), idArgument, rulesOption);

        var shareId = new Argument<string>("id", "Rule or note id (see teach-parser list).");
        var shareSource = Existing(new Option<string[]>("--source", "Estate folder whose names the note is checked against (repeatable).") { AllowMultipleArgumentsPerToken = false });
        var githubOption = new Option<bool>("--github", "After you read the note and confirm, post it as a public issue with gh.");
        var share = new Command("share", "Write a note for the Starforge team (a generic rule with a made-up example, or a masked parser-gap note), checked against your estate's names. Sends nothing unless --github and you confirm.");
        share.AddArgument(shareId);
        share.AddOption(rulesOption);
        share.AddOption(shareSource);
        share.AddOption(githubOption);
        share.SetHandler((string id, string rulesPath, string[] sources, bool github) =>
            Environment.ExitCode = Guard(rulesPath, rules => new TeachParser(Console.In, Console.Out).Share(rules, id,
                new TeachNotes(TeachNotes.DefaultFolder), EstateNames.From(sources ?? []), github, canAsk: !Console.IsInputRedirected)),
            shareId, rulesOption, shareSource, githubOption);

        var forget = new Command("forget", "Withdraw your agreement to send excerpts automatically; the next run asks again.");
        forget.SetHandler(() =>
        {
            var consent = new TeachConsent(TeachConsent.DefaultPath);
            Console.WriteLine(consent.Forget()
                ? $"Removed {consent.FilePath}. The next teach-parser run asks before sending anything."
                : "There was no agreement to withdraw.");
        });

        cmd.AddCommand(report);
        cmd.AddCommand(run);
        cmd.AddCommand(list);
        cmd.AddCommand(remove);
        cmd.AddCommand(share);
        cmd.AddCommand(forget);
        return cmd;
    }

    // A mistyped folder would otherwise be read as an empty estate, and share would check the note against no names.
    private static Option<string[]> Existing(Option<string[]> option)
    {
        option.AddValidator(result =>
        {
            var missing = result.GetValueOrDefault<string[]>()?.FirstOrDefault(folder => !Directory.Exists(folder));
            if (missing is not null)
            {
                result.ErrorMessage = $"No folder '{missing}'.";
            }
        });
        return option;
    }

    public static DialectRules LoadRules(string rulesPath)
        => File.Exists(rulesPath) ? DialectRules.Load(Path.GetFullPath(rulesPath)) : DialectRules.Empty;

    private static int Guard(string rulesPath, Func<DialectRules, int> action)
        => TryLoad(rulesPath, out var rules) ? action(rules) : 2;

    private static bool TryLoad(string rulesPath, out DialectRules rules)
    {
        try
        {
            rules = LoadRules(rulesPath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine($"Fix or remove {rulesPath}; './doctor.sh teach-parser list' shows what it holds.");
            rules = DialectRules.Empty;
            Environment.ExitCode = 2;
            return false;
        }
    }
}
