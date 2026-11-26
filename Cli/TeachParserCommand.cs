using System.CommandLine;
using Starforge.Cobol.Dialect;

namespace CobolToQuarkusMigration.Cli;

// teach-parser report|run|list|remove|share. Only "run" talks to the AI endpoint, and only after consent per group.
public static class TeachParserCommand
{
    public const string DefaultRulesPath = "Config/starforge-dialect.local.json";

    public static Command Build(Func<TeachAiEndpoint> endpoint)
    {
        var cmd = new Command("teach-parser", "Teach Starforge your site's own COBOL words, using your AI endpoint (rules stay in " + DefaultRulesPath + ").");

        var rulesOption = new Option<string>("--rules", () => DefaultRulesPath, "Local rules file.");
        Option<string[]> SourceOption() => new("--source", "Estate folder (repeatable).") { IsRequired = true, AllowMultipleArgumentsPerToken = false };

        var reportSource = SourceOption();
        var report = new Command("report", "List what Starforge cannot read, grouped. Sends nothing.");
        report.AddOption(reportSource);
        report.AddOption(rulesOption);
        report.SetHandler((string[] sources, string rulesPath) =>
            Environment.ExitCode = Guard(rulesPath, rules => new TeachParser(Console.In, Console.Out).Report(TeachScan.Run(sources, rules), rulesPath)),
            reportSource, rulesOption);

        var runSource = SourceOption();
        var run = new Command("run", "Walk through each group, ask your AI endpoint after you agree, check the proposed rule and save it if you say so.");
        run.AddOption(runSource);
        run.AddOption(rulesOption);
        run.SetHandler(async (string[] sources, string rulesPath) =>
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("teach-parser run asks before sending anything, so it needs a terminal. Use 'teach-parser report' in scripts.");
                Environment.ExitCode = 2;
                return;
            }

            if (!TryLoad(rulesPath, out var rules))
            {
                return;
            }

            Environment.ExitCode = await new TeachParser(Console.In, Console.Out).RunAsync(sources, rulesPath, rules, endpoint, journalRoot: TeachJournal.Folder);
        }, runSource, rulesOption);

        var list = new Command("list", "Show the taught rules.");
        list.AddOption(rulesOption);
        list.SetHandler((string rulesPath) =>
            Environment.ExitCode = Guard(rulesPath, rules => new TeachParser(Console.In, Console.Out).List(rules, rulesPath)), rulesOption);

        var idArgument = new Argument<string>("id", "Rule id (see teach-parser list).");
        var remove = new Command("remove", "Remove a taught rule.");
        remove.AddArgument(idArgument);
        remove.AddOption(rulesOption);
        remove.SetHandler((string id, string rulesPath) =>
            Environment.ExitCode = Guard(rulesPath, rules => new TeachParser(Console.In, Console.Out).Remove(rules, rulesPath, id)), idArgument, rulesOption);

        var shareId = new Argument<string>("id", "Rule id (see teach-parser list).");
        var share = new Command("share", "Print a generic copy of a rule with a made-up example, for you to send to the Starforge team yourself.");
        share.AddArgument(shareId);
        share.AddOption(rulesOption);
        share.SetHandler((string id, string rulesPath) =>
            Environment.ExitCode = Guard(rulesPath, rules => new TeachParser(Console.In, Console.Out).Share(rules, id)), shareId, rulesOption);

        cmd.AddCommand(report);
        cmd.AddCommand(run);
        cmd.AddCommand(list);
        cmd.AddCommand(remove);
        cmd.AddCommand(share);
        return cmd;
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
