using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Starforge.Cobol.Deps;
using Starforge.Cobol.Emit;

namespace Starforge.Cobol.Cli;

// Writes a REKT-shaped report tree: <out>/<estate>/<path>-deps.json and <out>/<estate>/<path>.report/...
internal static class EmitCommand
{
    public static int Run(CommandLine cmd, TextWriter stdout, TextWriter stderr)
    {
        cmd.RejectUnknown("source", "out", "rules");
        if (cmd.Positional.Count > 0)
        {
            throw new UsageException($"unexpected argument '{cmd.Positional[0]}' (repeat --source for each estate)");
        }

        var roots = cmd.All("source").Select(Path.GetFullPath).ToList();
        if (roots.Count == 0)
        {
            throw new UsageException("emit needs at least one --source <estate-dir>");
        }

        foreach (var r in roots.Where(r => !Directory.Exists(r)))
        {
            stderr.WriteLine($"error: source directory not found: {r}");
            return 2;
        }

        var outDir = Path.GetFullPath(cmd.Required("out"));
        var display = new PathDisplay(roots.Select(r => (r, Path.GetFileName(r))));
        var rules = CliApp.LoadRules(cmd);
        var discovered = roots
            .SelectMany(CoverageCommand.DiscoverPrograms)
            .OrderBy(display.Show, StringComparer.Ordinal)
            .ToList();
        var programs = discovered.Where(p => rules.NotProgram(display.Show(p)) is null).ToList();

        var frontEnd = new CobolFrontEnd(new FrontEndOptions(roots, Rules: rules));
        var options = roots.ToDictionary(r => r, EstateOptions, StringComparer.Ordinal);
        var failures = new ConcurrentBag<(string Program, string Error)>();
        var counts = new ConcurrentBag<int>();
        var clock = Stopwatch.StartNew();
        Parallel.ForEach(programs, path =>
        {
            var rel = display.Show(path);
            try
            {
                var root = roots.First(r => path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                var files = RektEmitter.Render(frontEnd.Process(path), rel, options[root]);
                foreach (var file in files)
                {
                    var target = Path.Combine(outDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    file.WriteTo(stream);
                }

                counts.Add(files.Count);
            }
            catch (Exception ex)
            {
                failures.Add((rel, ex.ToString()));
            }
        });
        clock.Stop();

        stdout.WriteLine($"programs={programs.Count} files={counts.Sum()} exceptions={failures.Count} " +
            $"elapsed={clock.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s" +
            (rules.IsEmpty ? string.Empty : $" excluded={discovered.Count - programs.Count} rules={rules.Sha256[..12]}"));
        foreach (var (program, error) in failures.OrderBy(f => f.Program, StringComparer.Ordinal))
        {
            stderr.WriteLine($"{program}: {error}");
        }

        return failures.IsEmpty ? 0 : 1;
    }

    // Each estate carries its own CICS resource definitions and platform-contracts snapshot.
    internal static RektEmitOptions EstateOptions(string root)
    {
        var contracts = Path.Combine(root, ".preprocessed", "platform-contracts.json");
        return new RektEmitOptions(CicsDefinitions.Discover(root), File.Exists(contracts) ? contracts : null);
    }
}
