namespace Starforge.Cobol.Cli;

public static class CliApp
{
    public const string Usage = """
        Usage:
          starforge coverage --source <estate-dir> [--source <estate-dir>...] --out <dir> [--rekt-baseline <dir>] [--rules <file>]
          starforge parse <program-file> [--source <estate-dir>...] [--out <file.json>] [--rules <file>]
          starforge emit --source <estate-dir> [--source <estate-dir>...] --out <dir> [--rules <file>]
          starforge native-export --program <file> --copybooks <dir> [--profile <file.json>] [--cics-definitions <csd|yaml>...]
            [--vsam-definitions <idcams-jcl>...] [--sql-declarations <member>...] [--platform-contracts <file.json>]
            [--commarea-closure <.commarea-closure.json>] [--rules <file>] --out <file-deps.json>
          starforge diff-rekt --layouts --rekt <rekt-baseline-dir> --source <estate-dir> [--source <estate-dir>...] --out <dir>
          starforge diff-rekt --procedure --rekt <rekt-baseline-dir> --source <estate-dir> [--source <estate-dir>...] --out <dir>
          starforge diff-rekt exec --source <estate-dir> [--source <estate-dir>...] --rekt-baseline <dir> --out <dir>
          starforge diff-rekt --model --rekt <rekt-baseline-dir> --starforge <emit-dir> --source <estate-dir>... --out <dir>
            [--starforge-evidence <PARSER_ENGINE=starforge output/rekt>]
            (runs Parser/Starforge.Cobol.ModelDiff, which loads both trees through the application's own readers)
        --rules reads the site's taught dialect rules (Config/starforge-dialect.local.json; see docs/teach-the-parser.md).
        """;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            stdout.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            if (args[0] == "diff-rekt" && args.Skip(1).Contains("--layouts", StringComparer.Ordinal))
            {
                return LayoutDiffCommand.Run(CommandLine.Parse(args.Skip(1).Where(a => a != "--layouts")), stdout, stderr);
            }

            if (args[0] == "diff-rekt" && args.Skip(1).Contains("--model", StringComparer.Ordinal))
            {
                return ModelDiffLauncher.Run(args.Skip(1).Where(a => a != "--model").ToArray(), stdout, stderr);
            }

            if (args[0] == "diff-rekt" && args.Skip(1).Contains("--procedure", StringComparer.Ordinal))
            {
                return ProcedureDiffCommand.Run(CommandLine.Parse(args.Skip(1).Where(a => a != "--procedure")), stdout, stderr);
            }

            var parsed = CommandLine.Parse(args.Skip(1));
            return args[0] switch
            {
                "coverage" => CoverageCommand.Run(parsed, stdout, stderr),
                "parse" => ParseCommand.Run(parsed, stdout, stderr),
                "emit" => EmitCommand.Run(parsed, stdout, stderr),
                "native-export" => NativeExportCommand.Run(parsed, stdout, stderr),
                "diff-rekt" when parsed.Positional is ["exec"] => ExecDiffCommand.Run(parsed, stdout, stderr),
                "diff-rekt" => NotYet(stderr),
                _ => UnknownCommand(args[0], stderr),
            };
        }
        catch (UsageException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            stderr.WriteLine(Usage);
            return 2;
        }
        catch (RulesFileException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    internal static Dialect.DialectRules LoadRules(CommandLine cmd)
    {
        if (cmd.Single("rules") is not { Length: > 0 } path)
        {
            return Dialect.DialectRules.Empty;
        }

        try
        {
            return Dialect.DialectRules.Load(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new RulesFileException(ex.Message);
        }
    }

    private static int NotYet(TextWriter stderr)
    {
        stderr.WriteLine("diff-rekt needs a comparison: --layouts, --procedure, --model or exec");
        return 2;
    }

    private static int UnknownCommand(string name, TextWriter stderr)
    {
        stderr.WriteLine($"error: unknown command '{name}'");
        stderr.WriteLine(Usage);
        return 2;
    }
}

public sealed class UsageException(string message) : Exception(message);

public sealed class RulesFileException(string message) : Exception(message);

public sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = new();

    public static CommandLine Parse(IEnumerable<string> args)
    {
        var result = new CommandLine();
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var a = e.Current;
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positional.Add(a);
                continue;
            }

            var eq = a.IndexOf('=');
            string name;
            string value;
            if (eq > 0)
            {
                name = a[2..eq];
                value = a[(eq + 1)..];
            }
            else
            {
                name = a[2..];
                if (!e.MoveNext())
                {
                    throw new UsageException($"option --{name} needs a value");
                }

                value = e.Current;
            }

            if (!result._options.TryGetValue(name, out var list))
            {
                result._options[name] = list = new List<string>();
            }

            list.Add(value);
        }

        return result;
    }

    public IReadOnlyList<string> All(string name) => _options.TryGetValue(name, out var v) ? v : Array.Empty<string>();

    public string? Single(string name)
    {
        var all = All(name);
        if (all.Count > 1)
        {
            throw new UsageException($"option --{name} given more than once");
        }

        return all.Count == 1 ? all[0] : null;
    }

    public string Required(string name) => Single(name) ?? throw new UsageException($"missing required option --{name}");

    public void RejectUnknown(params string[] known)
    {
        var unknown = _options.Keys.Where(k => !known.Contains(k, StringComparer.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException($"unknown option --{unknown[0]}");
        }
    }
}
