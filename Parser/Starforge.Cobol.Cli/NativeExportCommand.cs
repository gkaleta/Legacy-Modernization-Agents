using System.Text.Json;
using Starforge.Cobol.Native;

namespace Starforge.Cobol.Cli;

// Writes one program's native dependency contract (the file the dependency evidence gate verifies).
internal static class NativeExportCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run(CommandLine cmd, TextWriter stdout, TextWriter stderr)
    {
        cmd.RejectUnknown("program", "copybooks", "profile", "cics-definitions", "vsam-definitions", "sql-declarations", "platform-contracts", "commarea-closure", "out");
        if (cmd.Positional.Count > 0)
        {
            throw new UsageException($"unexpected argument '{cmd.Positional[0]}'");
        }

        var program = Path.GetFullPath(cmd.Required("program"));
        var copybooks = Path.GetFullPath(cmd.Required("copybooks"));
        var output = Path.GetFullPath(cmd.Required("out"));
        if (!File.Exists(program))
        {
            stderr.WriteLine($"error: file not found: {program}");
            return 2;
        }

        if (!Directory.Exists(copybooks))
        {
            stderr.WriteLine($"error: copybook directory not found: {copybooks}");
            return 2;
        }

        var profile = cmd.Single("profile");
        if (profile is { Length: > 0 } && !File.Exists(profile))
        {
            stderr.WriteLine($"error: dependency profile not found: {profile}");
            return 2;
        }

        // Each option repeats or takes a path-separator list, the form the pipeline's REKT_* settings use.
        var cics = Files(cmd, "cics-definitions");
        var vsam = Files(cmd, "vsam-definitions");
        var sql = Files(cmd, "sql-declarations");
        if (cics.Concat(vsam).Concat(sql).FirstOrDefault(f => !File.Exists(f)) is { } missing)
        {
            stderr.WriteLine($"error: resource definition file not found: {missing}");
            return 2;
        }

        var platform = cmd.Single("platform-contracts");
        if (platform is { Length: > 0 } && !File.Exists(platform))
        {
            stderr.WriteLine($"error: platform replacement profile not found: {platform}");
            return 2;
        }

        var closure = cmd.Single("commarea-closure");
        if (closure is { Length: > 0 } && !File.Exists(closure))
        {
            stderr.WriteLine($"error: COMMAREA closure not found: {closure}");
            return 2;
        }

        var document = NativeExporter.Export(new NativeExportOptions(
            program, copybooks, profile is { Length: > 0 } ? profile : null, cics, vsam, sql, platform is { Length: > 0 } ? platform : null,
            closure is { Length: > 0 } ? closure : null));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, document.ToJsonString(Indented) + "\n");
        var diagnostics = document["dependencyDiagnostics"]!.AsArray();
        foreach (var diagnostic in diagnostics)
        {
            stderr.WriteLine($"{Path.GetFileName(program)}: {diagnostic}");
        }

        return 0;
    }

    private static List<string> Files(CommandLine cmd, string option) =>
        cmd.All(option).SelectMany(v => v.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)).Select(Path.GetFullPath).ToList();
}
