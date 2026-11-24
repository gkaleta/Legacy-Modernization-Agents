using System.Text.Json;
using CobolToQuarkusMigration.Persistence.GraphIngest;

namespace Starforge.Cobol.ModelDiff;

// The ASTNode/CONTAINS/CFG rows GraphIngestion writes for one program, queried the way McpChatWeb/Endpoints/RektGraphEndpoints.cs does.
internal sealed class GraphModel
{
    private readonly List<Dictionary<string, object?>> _nodes = [];
    private readonly Dictionary<string, Dictionary<string, object?>> _byUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.Ordinal);

    public bool HasAst => _nodes.Count > 0;

    public bool HasCfg { get; private set; }

    public static GraphModel Load(string rel, string? astPath, string? cfgPath)
    {
        var model = new GraphModel();
        if (astPath is null)
        {
            return model;
        }

        using (var ast = JsonDocument.Parse(File.ReadAllText(astPath)))
        {
            if (ast.RootElement.ValueKind != JsonValueKind.Object)
            {
                return model;
            }

            var (nodes, contains) = RektGraphRows.Ast(rel, ast.RootElement, 1);
            foreach (var node in nodes)
            {
                // MERGE on uid keeps the first row's properties.
                if (model._byUid.TryAdd((string)node["uid"]!, node))
                {
                    model._nodes.Add(node);
                }
            }

            foreach (var row in contains.Rows)
            {
                var from = (string)row["from_id"]!;
                var to = (string)row["to_id"]!;
                model._parent.TryAdd(to, from);
                if (!model._children.TryGetValue(from, out var list))
                {
                    model._children[from] = list = [];
                }

                list.Add(to);
            }
        }

        if (cfgPath is not null)
        {
            using var cfg = JsonDocument.Parse(File.ReadAllText(cfgPath));
            if (cfg.RootElement.ValueKind == JsonValueKind.Object)
            {
                // A relationship MERGE only lands when both endpoints exist.
                model.HasCfg = RektGraphRows.Cfg(rel, cfg.RootElement, 1).SelectMany(b => b.Rows)
                    .Any(r => model._byUid.ContainsKey((string)r["from_id"]!) && model._byUid.ContainsKey((string)r["to_id"]!));
            }
        }

        return model;
    }

    // RektGraphEndpoints.cs:136-150: node-type counts at CONTAINS depth 1..3 below the HAS_AST root.
    public IEnumerable<(string Key, string Value)> Overview()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (HasAst)
        {
            var frontier = new List<string> { (string)_nodes[0]["uid"]! };
            for (var depth = 1; depth <= 3; depth++)
            {
                frontier = frontier.SelectMany(Children).ToList();
                seen.UnionWith(frontier);
            }
        }

        int Count(params string[] types) => seen.Count(uid => types.Contains(Type(uid)));
        yield return ("hasAst", HasAst.ToString());
        yield return ("sqlCount", Count("DIALECT", "DIALECT_CONTAINER").ToString());
        yield return ("callCount", Count("CALL").ToString());
        yield return ("performCount", Count("PERFORM").ToString());
        yield return ("displayCount", Count("DISPLAY").ToString());
    }

    // RektGraphEndpoints.cs:306-330: SECTION rows, or root-level PARAGRAPHS, each with PARAGRAPH nodes at CONTAINS depth 1..2.
    public IEnumerable<(string Key, string Value)> Structure()
    {
        var rows = new List<(string Key, string Value)>();
        foreach (var sec in _nodes)
        {
            var uid = (string)sec["uid"]!;
            var type = Type(uid);
            var rootLevel = type == "PARAGRAPHS" && _parent.TryGetValue(uid, out var p) && Type(p) == "PROCEDURE_DIVISION_BODY";
            if (type != "SECTION" && !rootLevel)
            {
                continue;
            }

            var paragraphs = Children(uid).Concat(Children(uid).SelectMany(Children)).Distinct(StringComparer.Ordinal)
                .Where(c => Type(c) == "PARAGRAPH").ToList();
            var head = $"{type} {Str(sec, "name") ?? "UNNAMED"}";
            var lines = $"{sec["startLine"]}-{sec["endLine"]}";
            if (paragraphs.Count == 0 && type == "SECTION")
            {
                rows.Add((head, lines));
            }

            rows.AddRange(paragraphs.Select(c => ($"{head} / {Str(_byUid[c], "name")}", $"{lines} / {_byUid[c]["startLine"]}-{_byUid[c]["endLine"]}")));
        }

        return rows.OrderBy(r => r.Key, StringComparer.Ordinal);
    }

    // RektGraphEndpoints.cs:366-380: each named PERFORM's nearest PARAGRAPH/PARAGRAPHS/SECTION ancestor within 6 hops; DISTINCT rows.
    public IEnumerable<(string Key, string Value)> PerformEdges()
    {
        var rows = new HashSet<(string Key, string Value)>();
        foreach (var node in _nodes)
        {
            var name = Str(node, "name");
            if (Type((string)node["uid"]!) != "PERFORM" || string.IsNullOrEmpty(name))
            {
                continue;
            }

            string? caller = null;
            var at = (string)node["uid"]!;
            for (var hop = 0; hop < 6 && _parent.TryGetValue(at, out var up); hop++, at = up)
            {
                var candidate = Str(_byUid[up], "name");
                if (Type(up) is "PARAGRAPH" or "PARAGRAPHS" or "SECTION" && !string.IsNullOrEmpty(candidate) && candidate != "para-group:")
                {
                    caller = candidate;
                    break;
                }
            }

            if (caller is not null)
            {
                rows.Add(($"{caller} -> {name}", Str(node, "label") ?? ""));
            }
        }

        return rows.OrderBy(r => r.Key, StringComparer.Ordinal).ThenBy(r => r.Value, StringComparer.Ordinal);
    }

    private IEnumerable<string> Children(string uid) => _children.TryGetValue(uid, out var list) ? list : [];

    private string Type(string uid) => _byUid.TryGetValue(uid, out var n) ? (string)n["nodeType"]! : "";

    private static string? Str(Dictionary<string, object?> node, string key) =>
        node.TryGetValue(key, out var v) ? v as string : null;
}
