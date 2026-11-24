using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Starforge.Cobol.Emit;

namespace Starforge.Cobol.Cli;

// Writes a REKT-shaped report tree: <out>/<estate>/<path>-deps.json and <out>/<estate>/<path>.report/...
internal static class EmitCommand
{
    public static int Run(CommandLine cmd, TextWriter stdout, TextWriter stderr)
    {
        cmd.RejectUnknown("source", "out");
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
        var programs = roots
            .SelectMany(CoverageCommand.DiscoverPrograms)
            .OrderBy(display.Show, StringComparer.Ordinal)
            .ToList();

        var frontEnd = new CobolFrontEnd(new FrontEndOptions(roots));
        var failures = new ConcurrentBag<(string Program, string Error)>();
        var counts = new ConcurrentBag<int>();
        var clock = Stopwatch.StartNew();
        Parallel.ForEach(programs, path =>
        {
            var rel = display.Show(path);
            try
            {
                var files = RektEmitter.Render(frontEnd.Process(path), rel, display);
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
            $"elapsed={clock.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s");
        foreach (var (program, error) in failures.OrderBy(f => f.Program, StringComparer.Ordinal))
        {
            stderr.WriteLine($"{program}: {error}");
        }

        return failures.IsEmpty ? 0 : 1;
    }
}
