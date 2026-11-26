using CobolToQuarkusMigration.Agents.Infrastructure.Facts;
using CobolToQuarkusMigration.Models;

namespace CobolToQuarkusMigration.Helpers;

/// <summary>
/// Lets one estate be converted after a parse of the folder that holds several.
/// That parse keys each program's evidence by its path from the parsed folder
/// (output/rekt/bank-of-z/cobol/X.cbl.report), so a run over source/bank-of-z would look for
/// output/rekt/cobol/X.cbl.report and find nothing. Such a run is moved to the parsed folder
/// with the estate's programs selected, so every gate reads the keys the parse wrote.
/// The parsed folder is taken from the evidence itself: it must name the program's exact file.
/// </summary>
public sealed record EstateEvidenceScope(string SourceRoot, IReadOnlyList<string> Selection, string? Estate)
{
    public bool Widened => Estate is not null;

    public static EstateEvidenceScope Resolve(string sourceRoot, string factsDirectory, IReadOnlyList<string>? selection)
    {
        var root = Path.GetFullPath(sourceRoot);
        var unchanged = new EstateEvidenceScope(sourceRoot, selection ?? [], null);
        if (!Directory.Exists(root) || !Directory.Exists(factsDirectory)) return unchanged;

        var programs = SourceTypeRegistry.EnumerateProgramFiles(root).Select(Path.GetFullPath).ToList();
        if (programs.Count == 0 || programs.Any(program => Bound(factsDirectory, root, program))) return unchanged;

        for (var parent = Path.GetDirectoryName(root); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (!programs.Any(program => Bound(factsDirectory, parent, program))) continue;

            var estate = Relative(parent, root);
            var chosen = programs.Select(path => new CobolFile
            {
                FileName = Path.GetFileName(path),
                FilePath = path,
                Content = ""
            }).ToList();
            IReadOnlyList<string> unmatched = [];
            if (selection is { Count: > 0 })
                chosen = ProgramSelection.Apply(chosen, selection, out unmatched);
            // An unmatched name is passed on unchanged, so the run reports it as it would have.
            var widened = chosen.Select(file => Relative(parent, file.FilePath)).Concat(unmatched).ToList();
            return new EstateEvidenceScope(parent, widened, estate);
        }
        return unchanged;
    }

    private static bool Bound(string factsDirectory, string root, string program)
    {
        var identity = Relative(root, program);
        if (identity.StartsWith("../", StringComparison.Ordinal)) return false;
        try
        {
            return DependencyEvidenceGate.LoadEvidence(factsDirectory, identity) is { } evidence
                && Path.IsPathFullyQualified(evidence.OriginalSourcePath)
                && string.Equals(Path.GetFullPath(evidence.OriginalSourcePath), program,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string Relative(string root, string path) =>
        SourcePathHelper.NormalizeRelativePath(Path.GetRelativePath(root, path));
}
