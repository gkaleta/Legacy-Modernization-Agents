using System.Text.Json;
using CobolToQuarkusMigration.Helpers;

namespace Starforge.Cobol.ModelDiff;

// A PERFORM node in the Starforge AST with what the classifier needs to locate its REKT counterpart.
internal sealed record PerformNode(string Section, string? Caller, string? Target, string Compressed, bool UnderChildless, bool Conditional);

// Per-program facts both trees prove about themselves; classifier rules test these instead of trusting a pattern.
internal sealed class ProgramEvidence
{
    public required string Program { get; init; }

    public required RektContext Rekt { get; init; }

    public required RektContext Starforge { get; init; }

    public required Dictionary<string, RektDataItem> RektData { get; init; }

    public required Dictionary<string, RektDataItem> StarforgeData { get; init; }

    public List<PerformNode> StarforgePerforms { get; } = [];

    // (caller, name) of every REKT PERFORM row the graph's performEdges query would return.
    public HashSet<(string Caller, string Name)> RektGraphPerforms { get; } = [];

    public int RektEvaluateWithChildren { get; private set; }

    public int RektGenericWithChildren { get; private set; }

    public int RektPerformWithTargetOrConditional { get; private set; }

    public int RektPerformNameNotCompressedText { get; private set; }

    public int RektSqlNodesWithText { get; private set; }

    public bool RektDropsNestedStatements => RektEvaluateWithChildren == 0 && RektGenericWithChildren == 0;

    public bool RektPerformCarriesNoTarget => RektPerformWithTargetOrConditional == 0 && RektPerformNameNotCompressedText == 0;

    public static ProgramEvidence Build(string program, RektContext rekt, RektContext sf, string? rektAst, string? sfAst)
    {
        var evidence = new ProgramEvidence
        {
            Program = program,
            Rekt = rekt,
            Starforge = sf,
            RektData = ContextDiff.Data(rekt.DataStructure).ToDictionary(d => d.Path, d => d.Item, StringComparer.Ordinal),
            StarforgeData = ContextDiff.Data(sf.DataStructure).ToDictionary(d => d.Path, d => d.Item, StringComparer.Ordinal),
        };
        if (rektAst is not null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(rektAst));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                evidence.ScanRekt(doc.RootElement, []);
            }
        }

        if (sfAst is not null)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sfAst));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                evidence.ScanStarforge(doc.RootElement, [], "", false);
            }
        }

        return evidence;
    }

    // REKT's node name is the statement text with whitespace removed, cut to 15 characters.
    public static string Compress(string text)
    {
        var chars = text.Where(c => !char.IsWhiteSpace(c)).Take(15).ToArray();
        return new string(chars);
    }

    private void ScanRekt(JsonElement node, List<(string Type, string Name)> ancestors)
    {
        var type = Str(node, "type") ?? "";
        var hasChildren = node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0;
        switch (type)
        {
            case "EVALUATE" when hasChildren:
                RektEvaluateWithChildren++;
                break;
            case "GENERIC_STATEMENT" when hasChildren:
                RektGenericWithChildren++;
                break;
            case "PERFORM":
                if (node.TryGetProperty("target", out _) || node.TryGetProperty("conditional", out _))
                {
                    RektPerformWithTargetOrConditional++;
                }

                if (Str(node, "name") != Compress(Str(node, "originalText") ?? ""))
                {
                    RektPerformNameNotCompressedText++;
                }

                if (Caller(ancestors) is { } caller && Str(node, "name") is { Length: > 0 } name)
                {
                    RektGraphPerforms.Add((caller, name));
                }

                break;
            case "DIALECT" or "DIALECT_CONTAINER" when node.TryGetProperty("text", out _) || node.TryGetProperty("excerpt", out _):
                RektSqlNodesWithText++;
                break;
        }

        if (hasChildren)
        {
            ancestors.Add((type, Str(node, "name") ?? ""));
            foreach (var child in children.EnumerateArray())
            {
                ScanRekt(child, ancestors);
            }

            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    // ancestors holds (type, name) from the root down to the parent.
    private void ScanStarforge(JsonElement node, List<(string Type, string Name)> ancestors, string section, bool underChildless)
    {
        var type = Str(node, "type") ?? "";
        var name = Str(node, "name") ?? "";
        if (type == "SECTION")
        {
            section = name;
        }

        if (type == "PERFORM")
        {
            var target = Str(node, "target");
            var conditional = node.TryGetProperty("conditional", out var c) && c.ValueKind == JsonValueKind.True;
            StarforgePerforms.Add(new PerformNode(section, Caller(ancestors), target, Compress(Str(node, "originalText") ?? ""), underChildless, conditional));
        }

        // REKT keeps EVALUATE and GENERIC_STATEMENT (e.g. READ with INVALID KEY) as leaves; everything Starforge nests below them is absent there.
        var childless = underChildless || type is "EVALUATE" or "GENERIC_STATEMENT";
        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            ancestors.Add((type, name));
            foreach (var child in children.EnumerateArray())
            {
                ScanStarforge(child, ancestors, section, childless);
            }

            ancestors.RemoveAt(ancestors.Count - 1);
        }
    }

    // Same rule as the performEdges Cypher: nearest PARAGRAPH/PARAGRAPHS/SECTION within 6 CONTAINS hops.
    private static string? Caller(List<(string Type, string Name)> ancestors)
    {
        for (var hop = 1; hop <= 6 && hop <= ancestors.Count; hop++)
        {
            var (type, name) = ancestors[^hop];
            if (type is "PARAGRAPH" or "PARAGRAPHS" or "SECTION" && name.Length > 0 && name != "para-group:")
            {
                return name;
            }
        }

        return null;
    }

    private static string? Str(JsonElement node, string key) =>
        node.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
