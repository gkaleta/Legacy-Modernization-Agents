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

    public static void Evidence(Compare cmp, string sourceRoot, string rel, string rektRoot, string sfRoot)
    {
        var left = RektEvidence.BuildEvidence(sourceRoot, rel, rektRoot, 0);
        var right = RektEvidence.BuildEvidence(sourceRoot, rel, sfRoot, 0);
        cmp.Keyed("evidence.coverage", Members(left["coverage"]), Members(right["coverage"]));
        cmp.Keyed("evidence.definitions", Definitions(left), Definitions(right));
        cmp.Keyed("evidence.diagnostics", Strings(left["unresolvedDiagnostics"]), Strings(right["unresolvedDiagnostics"]));
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
