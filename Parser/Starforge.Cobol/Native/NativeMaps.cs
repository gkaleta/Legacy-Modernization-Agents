using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Native;

// The BMS layouts staged next to generated symbolic-map copybooks: <copybook>.bms-source is the BMS macro source
// and <copybook>.bms-layout.json the field layout derived from it, which the gate re-derives from the source.
internal sealed class BmsLayouts
{
    public static readonly BmsLayouts Empty = new([]);

    public sealed record Layout(string Output, string Source, string LayoutPath, JsonObject Document);

    private readonly List<Layout> _layouts;

    private BmsLayouts(List<Layout> layouts) => _layouts = layouts;

    public static BmsLayouts Load(string copybookRoot)
    {
        var root = Path.GetFullPath(copybookRoot);
        if (!Directory.Exists(root))
        {
            return Empty;
        }

        var layouts = new List<Layout>();
        foreach (var path in Directory.EnumerateFiles(root, "*.bms-layout.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var output = path[..^".bms-layout.json".Length];
            var source = output + ".bms-source";
            if (File.Exists(output) && File.Exists(source) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject document)
            {
                layouts.Add(new Layout(output, source, path, document));
            }
        }

        return new BmsLayouts(layouts);
    }

    public Layout? ForCopybook(string path) =>
        _layouts.FirstOrDefault(l => string.Equals(l.Output, Path.GetFullPath(path), StringComparison.Ordinal));

    // Staged layouts describing map NAME of mapset MAPSET, with the map's definition in each.
    public List<(Layout Layout, JsonObject Map)> Find(string name, string mapset) =>
        _layouts
            .SelectMany(l => (l.Document["maps"] as JsonArray ?? []).OfType<JsonObject>().Select(m => (l, m)))
            .Where(p => (string?)p.m["name"] == name && (string?)p.m["mapset"] == mapset)
            .ToList();

    // The BMS_LAYOUT derived input binding a COPY of a generated symbolic-map copybook to its layout.
    public static JsonObject DerivedInput(Layout layout, string copyName) => new()
    {
        ["kind"] = "BMS_LAYOUT",
        ["name"] = copyName,
        ["sourcePath"] = layout.Source,
        ["sourceSha256"] = NativeSyntax.FileSha256(layout.Source),
        ["layoutPath"] = layout.LayoutPath,
        ["layoutSha256"] = NativeSyntax.FileSha256(layout.LayoutPath),
        ["outputPath"] = layout.Output,
        ["outputSha256"] = NativeSyntax.FileSha256(layout.Output),
    };
}

internal sealed partial class NativeCics
{
    // SEND/RECEIVE MAP binds the map named by its MAP and MAPSET operands (MAPSET defaults to the map name) to the
    // staged layout of that mapset; the layout must be current for its BMS source and copybook.
    private JsonObject Map(Command command, string verb)
    {
        var mapOption = command.Single("MAP");
        var mapsets = command.All("MAPSET");
        if (mapsets.Count > 1 || mapsets.Count == 1 && mapsets[0].Argument is null)
        {
            throw new Problem("option:MAPSET");
        }

        var (name, mapProof) = MapName(command, mapOption, verb);
        var (mapset, setProof) = mapsets.Count == 1 ? MapName(command, mapsets[0], verb) : (name, null);
        var found = _layouts.Find(name, mapset);
        if (found.Count != 1)
        {
            throw new Problem((found.Count == 0 ? "map-layout:" : "map-layout-ambiguous:") + mapset + "." + name);
        }

        var (layout, map) = found[0];
        var sourceSha = NativeSyntax.FileSha256(layout.Source);
        var outputSha = NativeSyntax.FileSha256(layout.Output);
        if ((string?)layout.Document["status"] != "complete" || (string?)layout.Document["sourceSha256"] != sourceSha
            || (string?)layout.Document["outputSha256"] != outputSha)
        {
            throw new Problem("map-layout-stale:" + mapset);
        }

        string[] modes = verb == "RECEIVE" ? ["IN", "INOUT"] : ["OUT", "INOUT"];
        if (!modes.Contains((string?)map["mode"]))
        {
            throw new Problem("map-mode:" + name);
        }

        var binding = new JsonObject
        {
            ["kind"] = "CICS_MAP",
            ["name"] = name,
            ["mapset"] = mapset,
            ["sourcePath"] = layout.Source,
            ["sourceSha256"] = sourceSha,
            ["layoutPath"] = layout.LayoutPath,
            ["layoutSha256"] = NativeSyntax.FileSha256(layout.LayoutPath),
            ["outputPath"] = layout.Output,
            ["outputSha256"] = outputSha,
            ["definition"] = map.DeepClone(),
        };
        if (!IsLiteral(mapProof) || setProof is not null && !IsLiteral(setProof))
        {
            var resolution = new JsonObject { ["map"] = mapProof };
            if (mapsets.Count == 1)
            {
                resolution["mapset"] = setProof;
            }

            binding["targetResolution"] = resolution;
        }

        return binding;
    }

    private static bool IsLiteral(JsonObject proof) => (string?)proof["kind"] == "literal" && proof.Count == 1;

    // A map or mapset name: a literal or read-only constant, or for SEND one value reaching the operand.
    private (string Value, JsonObject Proof) MapName(Command command, Option option, string verb)
    {
        try
        {
            return Name(command, option, 1, 7);
        }
        catch (Problem) when (verb == "SEND" && option.Argument is { Kind: Exec.ExecArgumentKind.DataRef })
        {
            var (values, proof) = Reaching(command, option, 1, 7);
            return values.Count == 1 && values[0] is { Length: > 0 } single ? (single, proof) : throw new Problem("map-reaching:" + option.Argument.Text);
        }
    }

    // The value a SEND operand holds when the one assignment reaching it is the last statement writing it before the
    // SEND in the same paragraph: everything between is a MOVE into disjoint storage, so no other path reaches it.
    private (List<string?> Values, JsonObject Proof) Reaching(Command command, Option option, int minimum, int maximum)
    {
        var operand = option.Argument!.Text;
        var unit = command.Unit;
        var item = _data.DataOf(unit)?.Symbols.Resolve(operand).Item ?? throw new Problem("reaching-name:" + operand);
        if (item.Section is not (DataSectionKind.WorkingStorage or DataSectionKind.LocalStorage) || item.External || item.Record.External)
        {
            throw new Problem("reaching-section:" + operand);
        }

        var definition = _data.Entry(item) ?? throw new Problem("reaching-definition:" + operand);
        var shape = Regex.Match(
            string.Concat(Words(definition)),
            "^(0?[1-9]|[1-4][0-9]|77)" + Regex.Escape(operand) + """PIC(?:TURE)?(?:IS)?X\(0*([1-9][0-9]{0,3})\)(?:VALUE(?:IS)?(['"])([^'"]*)\3)?\.$""",
            RegexOptions.IgnoreCase);
        var ancestors = item.Ancestors().Reverse().Select(_data.Entry).ToList();
        var levels = item.Ancestors().Reverse().Select(a => a.Level).Append(item.Level).ToList();
        if (!shape.Success || ancestors.Any(a => a is null || !Regex.IsMatch(string.Concat(Words(a)), @"^(0?[1-9]|[1-4][0-9])[A-Za-z][A-Za-z0-9-]*\.$"))
            || levels.Count > 1 && (levels[0] != 1 || levels.Zip(levels.Skip(1)).Any(p => p.First >= p.Second)))
        {
            throw new Problem("reaching-shape:" + operand);
        }

        var length = int.Parse(shape.Groups[2].Value);
        var bindings = _data.BindingsOf(unit);
        var statements = Paragraph(unit, command.Exec) ?? throw new Problem("reaching-flow:" + operand);
        var at = statements.FindIndex(s => ReferenceEquals(s, command.Exec));
        for (var i = at - 1; i >= 0; i--)
        {
            if (statements[i] is not MoveStatement { Corresponding: false } move
                || move.Targets.Any(t => t is not DataRef r || bindings.ItemOf(r) is null))
            {
                throw new Problem("reaching-flow:" + operand);
            }

            if (!move.Targets.Any(t => NativeData.Overlap(bindings.ItemOf((DataRef)t)!, item)))
            {
                continue;
            }

            if (move.Targets is not [DataRef { Subscripts.Count: 0, RefStart: null } target] || !ReferenceEquals(bindings.ItemOf(target), item))
            {
                throw new Problem("reaching-write:" + operand);
            }

            var assignment = new JsonObject { ["syntax"] = _data.Statement(move, "MoveStatement") };
            string value;
            switch (move.Source)
            {
                case LiteralExpr { Kind: TokenKind.AlphanumericLiteral } literal when literal.Text.Length >= 2 && literal.Text[0] is '\'' or '"':
                    value = ExecArguments.LiteralValue(literal.Text).TrimEnd(' ');
                    break;
                case DataRef { Subscripts.Count: 0, RefStart: null, Name.Qualifiers.Count: 0 } source:
                    (value, var proof) = Constant(unit, source.Name.Name, minimum, maximum);
                    assignment["sourceResolution"] = proof;
                    break;
                default:
                    throw new Problem("reaching-source:" + operand);
            }

            if (!NameR().IsMatch(value) || value.Length < minimum || value.Length > Math.Min(maximum, length))
            {
                throw new Problem("reaching-value:" + operand);
            }

            return ([value], new JsonObject
            {
                ["kind"] = "structured-reaching-assignments",
                ["operand"] = operand,
                ["definitionSyntax"] = definition,
                ["ancestorSyntax"] = new JsonArray(ancestors.Select(a => (JsonNode)a!).ToArray()),
                ["assignments"] = new JsonArray(assignment),
                ["initialReaches"] = false,
                ["reachingValues"] = new JsonArray(value),
                ["usageProof"] = "single-native-program:paragraph-straight-line-assignment",
            });
        }

        throw new Problem("reaching-unassigned:" + operand);
    }

    // The top-level statements of the paragraph (or the leading sentences of a section) holding statement.
    private static List<Statement>? Paragraph(ProgramUnit unit, Statement statement)
    {
        var bodies = unit.Procedure?.AllSections().SelectMany(s => s.Paragraphs.Select(p => p.Sentences).Prepend(s.Leading)) ?? [];
        return bodies.Select(b => b.SelectMany(x => x.Statements).ToList()).FirstOrDefault(list => list.Any(s => ReferenceEquals(s, statement)));
    }
}
