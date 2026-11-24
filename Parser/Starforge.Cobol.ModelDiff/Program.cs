using System.Text.Json;
using CobolToQuarkusMigration.Agents.Infrastructure.Facts;
using CobolToQuarkusMigration.Helpers;

namespace Starforge.Cobol.ModelDiff;

internal static class Program
{
    private const string Usage = "usage: starforge-model-diff --rekt <dir> --starforge <dir> --source <dir>... --out <dir> [--program <rel>]...";

    public static async Task<int> Main(string[] args)
    {
        string? rekt = null, starforge = null, outDir = null;
        var sources = new List<string>();
        var only = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--rekt": rekt = value; i++; break;
                case "--starforge": starforge = value; i++; break;
                case "--source": sources.Add(value ?? ""); i++; break;
                case "--out": outDir = value; i++; break;
                case "--program": only.Add(value ?? ""); i++; break;
                default:
                    Console.Error.WriteLine($"unknown argument: {args[i]}");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        if (rekt is null || starforge is null || sources.Count == 0 || outDir is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        rekt = Path.GetFullPath(rekt);
        starforge = Path.GetFullPath(starforge);
        sources = sources.Select(s => Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar)).ToList();
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);

        var programs = Programs(rekt).Where(p => only.Count == 0 || only.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        var rootOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in programs)
        {
            if (SourceRoot(sources, rel) is not { } root)
            {
                Console.Error.WriteLine($"error: {rel} is not found under any --source");
                return 2;
            }

            rootOf[rel] = root;
        }

        // The baseline also holds pipeline outputs (dependency-evidence.json, .commarea-closure.json, *.facts.json);
        // comparing only parser artefacts keeps both sides on the same footing, except the gate evidence REKT can only show stored.
        var rektBaseline = rekt;
        rekt = ParserMirror(rekt, Path.Combine(outDir, "rekt-parser"), programs);
        var report = new DiffReport { Programs = programs.Count };
        var classifier = new Classifier(NativeUnclaimed(starforge, programs));

        var rektFacts = Path.Combine(outDir, "facts-rekt");
        var sfFacts = Path.Combine(outDir, "facts-starforge");
        var loaders = new Dictionary<string, (RektContextLoader Rekt, RektContextLoader Starforge)>(StringComparer.Ordinal);
        foreach (var group in programs.GroupBy(rel => rootOf[rel], StringComparer.Ordinal))
        {
            var repoRoot = Path.GetDirectoryName(group.Key)!;
            loaders[group.Key] = (new RektContextLoader(repoRoot, rekt), new RektContextLoader(repoRoot, starforge));
            await new ProgramFactsExtractor(repoRoot, group.Key, rekt).ExtractAllAsync(group.ToList(), rektFacts);
            await new ProgramFactsExtractor(repoRoot, group.Key, starforge).ExtractAllAsync(group.ToList(), sfFacts);
        }

        foreach (var rel in programs)
        {
            var cmp = new Compare(report, classifier, rel);
            var sfDeps = Path.Combine(starforge, rel + "-deps.json");
            if (!File.Exists(sfDeps))
            {
                report["programs"].Add(DiffKind.OnlyRekt, Classifier.Unexplained + " program not emitted", rel);
                continue;
            }

            report["programs"].Match();
            var source = rootOf[rel];
            var (rektLoader, sfLoader) = loaders[source];
            var rektContext = rektLoader.Load(rel, Path.GetFileName(source));
            var sfContext = sfLoader.Load(rel, Path.GetFileName(source));
            classifier.Begin(ProgramEvidence.Build(rel, rektContext, sfContext, Report(rekt, rel).Ast, Report(starforge, rel).Ast));
            ContextDiff.Run(cmp, rektContext, sfContext);
            ArtifactDiff.Deps(cmp, Path.Combine(rekt, rel + "-deps.json"), sfDeps, rel);
            ArtifactDiff.Evidence(cmp, source, rel, rektBaseline, starforge);
            ArtifactDiff.Graph(cmp, rel, Report(rekt, rel), Report(starforge, rel));

            using var left = JsonDocument.Parse(File.ReadAllText(ProgramFactsArtifactLocator.GetFactsFilePath(rektFacts, rel)));
            using var right = JsonDocument.Parse(File.ReadAllText(ProgramFactsArtifactLocator.GetFactsFilePath(sfFacts, rel)));
            var l = JsonFlatten.Flatten(left.RootElement, "facts").ToList();
            var r = JsonFlatten.Flatten(right.RootElement, "facts").ToList();
            foreach (var category in l.Select(x => x.Category).Union(r.Select(x => x.Category)).Order(StringComparer.Ordinal))
            {
                cmp.Keyed(category, l.Where(x => x.Category == category).Select(x => (x.Key, x.Value)),
                    r.Where(x => x.Category == category).Select(x => (x.Key, x.Value)));
            }
        }

        report.WriteJson(Path.Combine(outDir, "model-diff.json"));
        File.WriteAllText(Path.Combine(outDir, "model-diff.md"), report.Markdown());
        report.WriteRows(Path.Combine(outDir, "model-diff-rows.tsv"));
        Console.WriteLine($"programs={report.Programs} unexplained={report.Unexplained}");
        foreach (var t in report.Tallies)
        {
            Console.WriteLine($"{t.Category,-32} matched={t.Matched} onlyStarforge={t.OnlyStarforge} onlyRekt={t.OnlyRekt} mismatch={t.Mismatch}");
        }

        return report.Unexplained == 0 ? 0 : 1;
    }

    private static IEnumerable<string> Programs(string root) =>
        Directory.EnumerateFiles(root, "*-deps.json", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(p => !p.Split('/').Any(s => s.StartsWith('.') || s is "verification" or "llm-derived" || s.EndsWith(".report", StringComparison.Ordinal)))
            .Select(p => p[..^"-deps.json".Length])
            .Order(StringComparer.Ordinal);

    // Program paths start with the estate name (as emit writes them), so an estate directory serves the programs named
    // after it and any other --source must contain the path as given.
    private static string? SourceRoot(IReadOnlyList<string> sources, string rel)
    {
        var estate = rel.Split('/')[0];
        foreach (var source in sources)
        {
            var parent = Path.GetDirectoryName(source);
            if (parent is not null && Path.GetFileName(source) == estate && File.Exists(Path.Combine(parent, rel)))
            {
                return parent;
            }
        }

        return sources.FirstOrDefault(s => File.Exists(Path.Combine(s, rel)));
    }

    private static (string? Ast, string? Cfg) Report(string root, string rel)
    {
        var report = Path.Combine(root, rel + ".report");
        return (First(report, "flow_ast"), First(report, "cfg"));
    }

    private static HashSet<string> NativeUnclaimed(string starforge, IEnumerable<string> programs)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rel in programs)
        {
            var path = Path.Combine(starforge, rel + "-deps.json");
            if (!File.Exists(path))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("dependencyCoverage", out _)
                && root.TryGetProperty("dependencyAnalysisComplete", out var complete) && complete.ValueKind == JsonValueKind.False)
            {
                result.Add(rel);
            }
        }

        return result;
    }

    private static string ParserMirror(string rekt, string mirror, IEnumerable<string> programs)
    {
        if (Directory.Exists(mirror))
        {
            Directory.Delete(mirror, recursive: true);
        }

        foreach (var rel in programs)
        {
            Copy(Path.Combine(rekt, rel + "-deps.json"), Path.Combine(mirror, rel + "-deps.json"));
            foreach (var folder in new[] { "flow_ast", "cfg", "data_structures" })
            {
                var from = Path.Combine(rekt, rel + ".report", folder);
                if (!Directory.Exists(from))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(from))
                {
                    Copy(file, Path.Combine(mirror, rel + ".report", folder, Path.GetFileName(file)));
                }
            }
        }

        return mirror;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
    }

    private static string? First(string report, string folder)
    {
        var dir = Path.Combine(report, folder);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).FirstOrDefault() : null;
    }
}
