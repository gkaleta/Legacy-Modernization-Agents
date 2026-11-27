using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CobolToQuarkusMigration.Agents;
using CobolToQuarkusMigration.Models;
using Microsoft.Extensions.Logging;

namespace CobolToQuarkusMigration.Helpers;

public sealed record JavaCompileResult(bool Compiled, bool Measured, int? ExitCode,
    string? FailureReason, IReadOnlyList<CompilerDiagnostic> Errors)
{
    public int RepairRounds { get; init; }
    public IReadOnlyList<JavaCompileRound> Rounds { get; init; } = [];
}

public sealed record JavaCompileRound(int Round, bool Compiled, bool Measured, int? ExitCode,
    string? FailureReason, IReadOnlyList<CompilerDiagnostic> Errors, bool RolledBack = false);

public static class JavaCompileGate
{
    public const int FailureExitCode = 5;

    public static async Task<JavaCompileResult> RunAsync(string folder, CompileGateSettings settings,
        ILogger logger, CancellationToken cancellationToken = default, string executable = "mvn",
        Func<ParityRepairRequest, Task<IReadOnlyDictionary<string, string>?>>? repair = null,
        IReadOnlyList<CodeFile>? generatedFiles = null)
    {
        Directory.CreateDirectory(folder);
        JavaCompileResult result;
        var round = 0;
        var rounds = new List<JavaCompileRound>();
        void Record(JavaCompileResult build, bool rolledBack = false)
        {
            rounds.Add(new(round, build.Compiled, build.Measured, build.ExitCode, build.FailureReason, build.Errors, rolledBack));
            var log = Path.Combine(folder, "java-build.log");
            if (File.Exists(log)) File.Copy(log, Path.Combine(folder, $"java-build.round-{round}.log"), overwrite: true);
        }
        try
        {
            var maxRounds = Math.Max(0, settings.MaxRepairRounds);
            // Why a file's last candidate was not kept. A rejected or rolled-back round used to end
            // repair with rounds to spare; the next round now tries again and is told what went wrong.
            var feedback = new Dictionary<string, string>(StringComparer.Ordinal);
            result = await BuildAsync(folder, Timeout(settings), executable, cancellationToken);
            Record(result);
            AiLoopEvents.ProgressUpdate(0, maxRounds, result.Compiled ? "First build compiled"
                : $"First build: {result.Errors.Count} compile error(s)");
            while (!result.Compiled && result.Measured && result.Errors.Count > 0
                   && repair is not null && round < maxRounds)
            {
                var startedRound = false;
                var originals = new Dictionary<string, string>();
                var candidates = new Dictionary<string, string>();
                var errorFiles = result.Errors.GroupBy(error => error.File).ToList();
                var fileIndex = 0;
                foreach (var group in errorFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fileIndex++;
                    AiLoopEvents.ProgressUpdate(fileIndex - 1, errorFiles.Count,
                        $"Repair round {round + (startedRound ? 0 : 1)} of {maxRounds}: file {fileIndex} of {errorFiles.Count} "
                        + $"({result.Errors.Count} compile error(s) before this round)");
                    var path = Path.GetFullPath(Path.Combine(folder, group.Key));
                    var mapped = (generatedFiles ?? []).Where(file => !string.IsNullOrWhiteSpace(file.FilePath)
                        && Path.GetFullPath(file.FilePath) == path).ToList();
                    if (!IsInside(folder, path) || mapped.Count != 1 || !File.Exists(mapped[0].OriginalSourcePath))
                    {
                        logger.LogWarning("Compiler repair skipped {File}: no unique authoritative source mapping.", group.Key);
                        continue;
                    }
                    var file = mapped[0];
                    var before = await File.ReadAllTextAsync(path, cancellationToken);
                    var related = new Dictionary<string, string>();
                    var pom = Path.Combine(folder, JavaProjectScaffold.PomFile);
                    related[pom] = await File.ReadAllTextAsync(pom, cancellationToken);
                    // A missing member is usually called on a class in another package (a shared copybook
                    // type), so same-package peers alone never show the model the names that really exist.
                    foreach (var typePath in ReferencedTypeFiles(folder, group))
                        if (typePath != path)
                            related[typePath] = PublicSurface(await File.ReadAllTextAsync(typePath, cancellationToken));
                    foreach (var peer in (generatedFiles ?? []).Where(peer => peer.FilePath != path
                        && !related.ContainsKey(peer.FilePath)
                        && File.Exists(peer.FilePath) && PackageOf(peer.Content) == PackageOf(before))
                        .Take(Math.Max(0, settings.MaxDeclarationsPerRepair)))
                    {
                        var code = await File.ReadAllTextAsync(peer.FilePath, cancellationToken);
                        related[peer.FilePath] = string.Join('\n', code.Split('\n')
                            .Take(Math.Max(0, settings.MaxDeclarationLines)));
                    }
                    IReadOnlyDictionary<string, string>? candidate;
                    if (!startedRound)
                    {
                        round++;
                        startedRound = true;
                    }
                    try
                    {
                        candidate = await repair(new(file.OriginalCobolFileName, "Java",
                            await File.ReadAllTextAsync(file.OriginalSourcePath, cancellationToken),
                            new Dictionary<string, string> { [path] = before },
                            new ProgramParityResult { Program = file.OriginalCobolFileName, Outcome = ParityOutcome.NotEvaluated },
                            group.ToList(), related,
                            PreviousAttempt: feedback.GetValueOrDefault(path))).WaitAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Compiler repair failed for {File}; retaining original output.", group.Key);
                        feedback[path] = "the repair request failed: " + ex.Message;
                        continue;
                    }
                    var after = candidate is not null && candidate.Count == 1 && candidate.TryGetValue(path, out var proposed)
                        ? proposed : null;
                    var rejection = candidate is null ? "no candidate returned"
                        : after is null ? $"answer named {candidate.Count} file(s) but not this one"
                        : string.IsNullOrWhiteSpace(after) ? "empty candidate"
                        : before == after ? "candidate unchanged"
                        : RejectCandidate(before, after, preserveAllTypes: true);
                    if (rejection is not null)
                    {
                        logger.LogWarning("Compiler repair rejected for {File}: {Reason}.", group.Key, rejection);
                        feedback[path] = "it was rejected because " + rejection;
                        continue;
                    }
                    originals[path] = before;
                    candidates[path] = after!;
                }
                if (!startedRound) break;
                if (originals.Count == 0)
                {
                    Record(result, rolledBack: true);
                    AiLoopEvents.ProgressUpdate(round, maxRounds, $"Repair round {round}: no candidate kept; "
                        + $"{result.Errors.Count} compile error(s) remain");
                    logger.LogWarning("Java compile repair round {Round}: no candidate was kept.", round);
                    continue;
                }
                JavaCompileResult measured;
                try
                {
                    foreach (var candidate in candidates)
                        await File.WriteAllTextAsync(candidate.Key, candidate.Value, cancellationToken);
                    measured = await BuildAsync(folder, Timeout(settings), executable, cancellationToken);
                    // javac stops before resolving symbols when any file fails to parse, so a broken file
                    // hides every other error and the count is no longer comparable. Revert just those files.
                    var broken = SyntaxBroken(result, measured, candidates.Keys, folder);
                    if (broken.Count > 0)
                    {
                        logger.LogWarning("Java compile repair round {Round}: reverted {Count} candidate(s) that introduced syntax errors: {Files}",
                            round, broken.Count, string.Join(", ", broken.Select(path => Path.GetRelativePath(folder, path))));
                        foreach (var path in broken)
                        {
                            feedback[path] = "it introduced Java syntax errors, so javac could not check the rest of the project";
                            await File.WriteAllTextAsync(path, originals[path], CancellationToken.None);
                            originals.Remove(path);
                            candidates.Remove(path);
                        }
                        if (candidates.Count > 0)
                            measured = await BuildAsync(folder, Timeout(settings), executable, cancellationToken);
                    }
                }
                catch
                {
                    await RestoreAsync(originals);
                    throw;
                }
                if (candidates.Count == 0 || !measured.Compiled && (!measured.Measured || measured.Errors.Count == 0
                    || measured.Errors.Count >= result.Errors.Count))
                {
                    Record(measured, rolledBack: true);
                    AiLoopEvents.ProgressUpdate(round, maxRounds, $"Repair round {round} rolled back: "
                        + $"{result.Errors.Count} compile error(s) remain");
                    await RestoreAsync(originals);
                    logger.LogWarning("Java compile repair round {Round} rolled back: {Reason}.", round,
                        candidates.Count == 0 ? "every candidate introduced syntax errors" : "compiler failures did not decrease");
                    foreach (var path in candidates.Keys)
                        feedback[path] = string.Create(CultureInfo.InvariantCulture,
                            $"the build with it had {(measured.Measured ? measured.Errors.Count.ToString(CultureInfo.InvariantCulture) : "unmeasured")} compile error(s), not fewer than the {result.Errors.Count} before");
                    result = await BuildAsync(folder, Timeout(settings), executable, cancellationToken);
                    continue;
                }
                foreach (var path in candidates.Keys) feedback.Remove(path);
                Record(measured);
                AiLoopEvents.ProgressUpdate(round, maxRounds, measured.Compiled
                    ? $"Repair round {round}: compiled ({result.Errors.Count} error(s) before)"
                    : $"Repair round {round}: {result.Errors.Count} → {measured.Errors.Count} compile error(s)");
                result = measured;
                foreach (var file in generatedFiles ?? [])
                    if (candidates.TryGetValue(file.FilePath, out var content)) file.Content = content;
            }
        }
        catch (OperationCanceledException)
        {
            await PersistAsync(folder, new(false, false, null, "Java validation was cancelled.", [])
                { RepairRounds = round, Rounds = rounds });
            Environment.ExitCode = FailureExitCode;
            throw;
        }
        result = result with { RepairRounds = round, Rounds = rounds };
        await PersistAsync(folder, result);
        if (!result.Compiled)
        {
            logger.LogError("Generated Java build failed: {Reason}; {Count} compiler diagnostics",
                result.FailureReason, result.Errors.Count);
            Environment.ExitCode = FailureExitCode;
        }
        return result;
    }

    private static TimeSpan Timeout(CompileGateSettings settings) =>
        TimeSpan.FromSeconds(Math.Max(1, settings.BuildTimeoutSeconds));

    private static bool IsInside(string folder, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static async Task RestoreAsync(IReadOnlyDictionary<string, string> originals)
    {
        foreach (var original in originals)
            await File.WriteAllTextAsync(original.Key, original.Value, CancellationToken.None);
    }

    private static Task PersistAsync(string folder, JavaCompileResult result) =>
        File.WriteAllTextAsync(Path.Combine(folder, "java-compile-status.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);

    internal static string? RejectCandidate(string before, string after, bool preserveAllTypes = false)
    {
        if (string.IsNullOrWhiteSpace(after)) return "empty output";
        var original = ConversionParityValidator.Tokenizer.Split(before).Code;
        var candidate = ConversionParityValidator.Tokenizer.Split(after).Code;
        if (PackageOf(before) != PackageOf(after)) return "package changed";
        var types = preserveAllTypes
            ? @"\b(?:class|interface|enum|record)\s+(\w+)"
            : @"\bpublic\s+(?:(?:abstract|final|static)\s+)*(?:class|interface|enum|record)\s+(\w+)";
        HashSet<string> Names(string code) => Regex.Matches(code, types)
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (!Names(original).IsSubsetOf(Names(candidate))) return "type declarations lost";
        const string publicTypes = @"\bpublic\s+(?:(?:abstract|final|static)\s+)*(?:class|interface|enum|record)\s+(\w+)";
        var publicBefore = Regex.Matches(original, publicTypes).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var publicAfter = Regex.Matches(candidate, publicTypes).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (!publicBefore.IsSubsetOf(publicAfter)) return "public type visibility lost";
        const string methods = @"\b(?:public|protected)\s+(?:(?:static|final|synchronized|abstract|native|default)\s+)*(?:[\w.$<>\[\]?,]+\s+)?\w+\s*\([^)]*\)";
        HashSet<string> Signatures(string code) => Regex.Matches(code, methods)
            .Select(match => Regex.Replace(match.Value, @"\s+", " ").Trim()).ToHashSet(StringComparer.Ordinal);
        if (!Signatures(original).IsSubsetOf(Signatures(candidate))) return "public or protected APIs lost";
        const string fields = @"\b(?:public|protected)\s+(?:(?:static|final|volatile|transient)\s+)*[\w.$<>\[\]?,]+\s+\w+(?=\s*[=;])";
        HashSet<string> Fields(string code) => Regex.Matches(code, fields)
            .Select(match => Regex.Replace(match.Value, @"\s+", " ").Trim()).ToHashSet(StringComparer.Ordinal);
        return Fields(original).IsSubsetOf(Fields(candidate)) ? null : "public or protected fields lost";
    }

    private static string PackageOf(string source) =>
        Regex.Match(ConversionParityValidator.Tokenizer.Split(source).Code,
            @"(?m)^\s*package\s+([\w.]+)\s*;").Groups[1].Value;

    internal static async Task<JavaCompileResult> BuildAsync(string folder, TimeSpan timeout,
        string executable = "mvn", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(Path.Combine(folder, "pom.xml")))
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "java-build.log"),
                "No pom.xml in the generated project.", cancellationToken);
            return new(false, false, null, "No pom.xml in the generated project.", []);
        }
        var arguments = new[] { "-B", "-q", "-Dstyle.color=never", "-DskipTests", "compile" };
        var commandScript = OperatingSystem.IsWindows() && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(commandScript ? "cmd.exe" : executable)
        {
            WorkingDirectory = Path.GetFullPath(folder),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (commandScript)
        {
            var command = executable == "mvn" ? "mvn.cmd" : executable;
            start.Arguments = $"/d /s /c \"\"{command}\" {string.Join(' ', arguments)}\"";
        }
        else
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("Maven did not start."); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "java-build.log"),
                $"Could not start Maven: {ex.Message}", cancellationToken);
            return new(false, false, null, $"Could not start Maven: {ex.Message}", []);
        }
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            try { await process.WaitForExitAsync(timer.Token); }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                await File.WriteAllTextAsync(Path.Combine(folder, "java-build.log"),
                    await stdout + "\n" + await stderr, CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
                return new(false, false, null, $"Maven build exceeded {timeout.TotalSeconds:0} seconds.", []);
            }
            var output = await stdout + "\n" + await stderr;
            await File.WriteAllTextAsync(Path.Combine(folder, "java-build.log"), output, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return new(process.ExitCode == 0, true, process.ExitCode,
                process.ExitCode == 0 ? null : $"Maven exited {process.ExitCode}; see java-build.log.", Parse(output, folder));
        }
    }

    // javac's parser messages; any of them stops compilation before symbols and types are checked.
    private static readonly Regex SyntaxMessage = new(
        @"expected$|^illegal (?:start|character|line end)|^unclosed |^not a statement$|reached end of file while parsing|without 'try'|'try' without|^else without if$|^orphaned |^malformed |^invalid method declaration",
        RegexOptions.Compiled);

    internal static bool IsSyntaxError(CompilerDiagnostic diagnostic) => SyntaxMessage.IsMatch(diagnostic.Message);

    internal static IReadOnlyList<string> SyntaxBroken(JavaCompileResult before, JavaCompileResult after,
        IEnumerable<string> changedFiles, string folder)
    {
        static HashSet<string> Files(JavaCompileResult build) =>
            build.Errors.Where(IsSyntaxError).Select(error => error.File.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var already = Files(before);
        var now = Files(after);
        return changedFiles.Where(path =>
        {
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            return now.Contains(relative) && !already.Contains(relative);
        }).ToList();
    }

    // javac puts the missing name and the type it was looked up on in follow-up lines; without them
    // every error reads only "cannot find symbol" and repair cannot tell what to rename.
    internal static IReadOnlyList<CompilerDiagnostic> Parse(string output, string folder) =>
        Regex.Matches(output, @"(?m)^\[ERROR\]\s+(?<file>.+?\.java):\[(?<line>\d+),(?<column>\d+)\]\s*(?<message>.+?)\r?$"
                + @"(?:\n(?:\[ERROR\])?[ \t]+symbol:\s*(?<symbol>.+?)\r?$)?(?:\n(?:\[ERROR\])?[ \t]+location:\s*(?<location>.+?)\r?$)?")
            .Select(match => new CompilerDiagnostic(RelativeFile(match.Groups["file"].Value, folder),
                int.Parse(match.Groups["line"].Value), int.Parse(match.Groups["column"].Value),
                "javac", match.Groups["message"].Value.Trim()
                    + (match.Groups["symbol"].Success ? ": " + Regex.Replace(match.Groups["symbol"].Value.Trim(), @"\s+", " ") : "")
                    + (match.Groups["location"].Success ? " (location: " + Regex.Replace(match.Groups["location"].Value.Trim(), @"\s+", " ") + ")" : "")))
            .Distinct().ToList();

    internal static IEnumerable<string> ReferencedTypeFiles(string folder, IEnumerable<CompilerDiagnostic> errors) =>
        errors.SelectMany(error => Regex.Matches(error.Message, @"\(location: (?:.+? of type |class |interface )(?<type>[\w.]+)")
                .Select(match => match.Groups["type"].Value))
            .Distinct(StringComparer.Ordinal)
            .Select(type => Path.GetFullPath(Path.Combine(folder, type.Replace('.', Path.DirectorySeparatorChar) + ".java")))
            .Where(file => IsInside(folder, file) && File.Exists(file));

    // Only the names a caller can use: the package, type headers and public member signatures, so a
    // large shared class fits in the prompt in full instead of being cut after its first lines.
    internal static string PublicSurface(string code) => string.Join('\n', code.Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Where(line => Regex.IsMatch(line, @"^\s*(package\s|public\s|protected\s)"))
        .Select(line => Regex.Replace(line, @"\s*\{.*$", "")));

    private static string RelativeFile(string path, string folder)
    {
        var normalized = path.Replace('\\', '/');
        return Directory.EnumerateFiles(folder, "*.java", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .FirstOrDefault(file => normalized == file || normalized.EndsWith("/" + file, StringComparison.Ordinal))
            ?? path;
    }
}
