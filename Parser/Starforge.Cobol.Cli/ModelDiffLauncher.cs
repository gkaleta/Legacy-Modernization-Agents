using System.Diagnostics;

namespace Starforge.Cobol.Cli;

// The model diff loads both trees through the application's RektContextLoader and ProgramFactsExtractor, so it lives
// in its own project that references the application; this CLI stays free of application dependencies and runs it.
internal static class ModelDiffLauncher
{
    private const string ProjectPath = "Parser/Starforge.Cobol.ModelDiff/Starforge.Cobol.ModelDiff.csproj";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var project = FindProject(AppContext.BaseDirectory) ?? FindProject(Directory.GetCurrentDirectory());
        if (project is null)
        {
            stderr.WriteLine($"error: {ProjectPath} not found above {AppContext.BaseDirectory}; run it with dotnet run --project");
            return 2;
        }

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "run", "--project", project, "-c", "Release", "--" }.Concat(args))
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        stdout.Write(process.StandardOutput.ReadToEnd());
        stderr.Write(error.Result);
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string? FindProject(string from)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, ProjectPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
