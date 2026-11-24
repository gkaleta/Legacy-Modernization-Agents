using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CobolToQuarkusMigration.Agents.Infrastructure.Facts;
using CobolToQuarkusMigration.Agents.Infrastructure.Rekt;

namespace Starforge.Cobol.ModelDiff;

// Compares what the application's own readers see in each tree: dependency edges, platform projections, the gate's evidence and graph rows.
internal static class ArtifactDiff
{
    private static readonly MethodInfo DiscoveredEdges = Internal("ReadDiscoveredEdges");
    private static readonly MethodInfo PlatformProjections = Internal("ReadPlatformProjectionNames");

    public static void Deps(Compare cmp, string rektDeps, string sfDeps, string rel)
    {
        cmp.Keyed("deps.discovered-edges", Edges(rektDeps), Edges(sfDeps));
        cmp.Keyed("deps.platform-projections", Projections(rektDeps, rel), Projections(sfDeps, rel));
    }

    // REKT's evidence is only verifiable in the staged frame it was produced in, so its stored record stands for what the gate saw.
    // Likewise a PARSER_ENGINE=starforge output tree (sfEvidence) holds the record its gate wrote in its own staged frame.
    public static void Evidence(Compare cmp, string sourceRoot, string rel, string rektRoot, string sfRoot, string? sfEvidence = null)
    {
        var stored = Path.Combine(rektRoot, rel + ".report", "dependency-evidence.json");
        var left = File.Exists(stored)
            ? JsonNode.Parse(File.ReadAllText(stored))!.AsObject()
            : RektEvidence.BuildEvidence(sourceRoot, rel, rektRoot, 0);
        var sfStored = sfEvidence is null ? null : Path.Combine(sfEvidence, rel + ".report", "dependency-evidence.json");
        var right = sfStored is not null && File.Exists(sfStored)
            ? JsonNode.Parse(File.ReadAllText(sfStored))!.AsObject()
            : RektEvidence.BuildEvidence(sourceRoot, rel, sfEvidence ?? sfRoot, 0, engine: NativeProducer.Starforge);
        cmp.Keyed("evidence.coverage", Members(left["coverage"]), Members(right["coverage"]));
        cmp.Classifier.StarforgeDefinitions(cmp.Program, Definitions(right));
        cmp.Keyed("evidence.definitions", Definitions(left), Definitions(right));
        cmp.Keyed("evidence.diagnostics", Strings(left["unresolvedDiagnostics"]), Strings(right["unresolvedDiagnostics"]));
    }

    // Program names REKT bound only through its flow-insensitive COMMAREA slot universe for an operand that Starforge proved
    // from the assignments reaching the command; Closed is true when Starforge needed no slot universe in this program at all.
    public static (HashSet<string> Names, bool Closed) NarrowedTargets(string rektDeps, string sfDeps)
    {
        var rekt = ProgramTargets(rektDeps);
        var sf = ProgramTargets(sfDeps);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var closed = true;
        foreach (var (operand, universe) in rekt.Where(t => t.Key.Kind == "closed-world-commarea-slot").Select(t => (t.Key.Operand, t.Value)))
        {
            var proven = sf.GetValueOrDefault(("structured-reaching-assignments", operand));
            if (proven is null || sf.Keys.Any(k => k.Operand == operand && k.Kind != "structured-reaching-assignments") || !proven.IsSubsetOf(universe))
            {
                closed = false;
                continue;
            }

            names.UnionWith(universe.Except(proven));
        }

        return (names, closed && sf.Keys.All(k => k.Kind != "closed-world-commarea-slot"));
    }

    private static Dictionary<(string Kind, string Operand), HashSet<string>> ProgramTargets(string deps)
    {
        var result = new Dictionary<(string, string), HashSet<string>>();
        if (!File.Exists(deps) || JsonNode.Parse(File.ReadAllText(deps))?["dependencyDialectStatements"] is not JsonArray statements)
        {
            return result;
        }

        foreach (var binding in statements.SelectMany(s => s?["resourceBindings"] as JsonArray ?? []))
        {
            if ((string?)binding?["kind"] != "CICS_PROGRAM" || binding["targetResolution"] is not JsonObject resolution
                || (string?)resolution["kind"] is not { } kind || (string?)resolution["operand"] is not { } operand)
            {
                continue;
            }

            if (!result.TryGetValue((kind, operand), out var names))
            {
                result[(kind, operand)] = names = new HashSet<string>(StringComparer.Ordinal);
            }

            names.Add((string)binding["name"]!);
        }

        return result;
    }

    // Emulates the graph endpoints' Cypher over the rows GraphIngestion would write; other graph properties have no reader.
    public static void Graph(Compare cmp, string rel, (string? Ast, string? Cfg) rekt, (string? Ast, string? Cfg) sf)
    {
        var left = GraphModel.Load(rel, rekt.Ast, rekt.Cfg);
        var right = GraphModel.Load(rel, sf.Ast, sf.Cfg);
        cmp.Keyed("graph.overview", left.Overview(), right.Overview());
        cmp.Value("graph.has-cfg", "hasCfg", left.HasCfg.ToString(), right.HasCfg.ToString());
        cmp.Keyed("graph.structure", left.Structure().Select(r => (r.Key, "")), right.Structure().Select(r => (r.Key, "")));
        cmp.Shared("graph.structure-lines", left.Structure(), right.Structure());
        cmp.Keyed("graph.perform-edges", left.PerformEdges().Select(r => (r.Key, "")), right.PerformEdges().Select(r => (r.Key, "")));
        cmp.Shared("graph.perform-labels", left.PerformEdges(), right.PerformEdges());
    }

    private static MethodInfo Internal(string name) =>
        typeof(DependencyEvidenceGate).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException(nameof(DependencyEvidenceGate), name);

    private static IEnumerable<(string, string)> Edges(string path)
    {
        var edges = (IReadOnlyList<(string Kind, string Name)>)DiscoveredEdges.Invoke(null, [path])!;
        return edges.Select(e => ($"{e.Kind} {e.Name}", ""));
    }

    private static IEnumerable<(string, string)> Projections(string path, string rel)
    {
        try
        {
            var names = (IReadOnlyList<string>)PlatformProjections.Invoke(null, [path, rel])!;
            return names.Select(n => (n, ""));
        }
        catch (TargetInvocationException e)
        {
            return [("(reader failed)", e.InnerException?.Message ?? "")];
        }
    }

    private static IEnumerable<(string, string)> Members(JsonNode? node) =>
        node is JsonObject o ? o.Select(p => (p.Key, p.Value?.ToString() ?? "null")) : [];

    private static IEnumerable<(string, string)> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(i => (i?.ToString() ?? "null", "")) : [];

    private static IEnumerable<(string, string)> Definitions(JsonObject evidence) =>
        evidence["definitions"] is JsonArray a
            ? a.Select(d => ($"{d?["kind"]} {d?["name"]}", Path.GetFileName(d?["path"]?.ToString() ?? "")))
            : [];
}
