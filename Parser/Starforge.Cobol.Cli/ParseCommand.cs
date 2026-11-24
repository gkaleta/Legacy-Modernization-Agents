using System.Text.Json;
using Starforge.Cobol.Emit;

namespace Starforge.Cobol.Cli;

internal static class ParseCommand
{
    public static int Run(CommandLine cmd, TextWriter stdout, TextWriter stderr)
    {
        cmd.RejectUnknown("source", "out");
        if (cmd.Positional.Count != 1)
        {
            throw new UsageException("parse expects exactly one program file");
        }

        var file = Path.GetFullPath(cmd.Positional[0]);
        if (!File.Exists(file))
        {
            stderr.WriteLine($"error: file not found: {file}");
            return 2;
        }

        var roots = cmd.All("source").Select(Path.GetFullPath).ToList();
        if (roots.Count == 0)
        {
            roots.Add(Path.GetDirectoryName(file)!);
        }

        var frontEnd = new CobolFrontEnd(new FrontEndOptions(roots));
        var result = frontEnd.Process(file);
        var display = new PathDisplay(roots.Select(r => (r, Path.GetFileName(r))));

        var outPath = cmd.Single("out");
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer, JsonReport.WriterOptions))
            {
                JsonReport.WriteTokens(writer, result, display);
            }

            if (outPath is null)
            {
                stdout.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            }
            else
            {
                File.WriteAllBytes(outPath, buffer.ToArray());
            }
        }

        foreach (var d in result.Diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).Distinct())
        {
            var severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            stderr.WriteLine($"{(d.Range is { } r ? display.Show(r) : display.Show(file))}: {severity}: {d.Code}: {d.Message}");
        }

        return result.Coverage.Unaccounted == 0 && result.Coverage.Conflicts == 0 ? 0 : 1;
    }
}
