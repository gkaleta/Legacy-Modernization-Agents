using System.Text.RegularExpressions;

namespace Starforge.Cobol.ModelDiff;

// Maps each difference to a triaged class after checking the evidence that proves it; anything not proven stays UNEXPLAINED.
internal sealed partial class Classifier
{
    public const string Unexplained = "UNEXPLAINED";

    public const string RektNoLines = "REKT-WRONG no-line-spans";
    public const string RektDropsNested = "REKT-WRONG childless-EVALUATE/GENERIC_STATEMENT";
    public const string RektInlinePerformName = "REKT-WRONG inline-PERFORM-name-as-target";
    public const string RektNoConditional = "REKT-WRONG no-PERFORM-conditional";
    public const string RektPerformTextName = "REKT-WRONG PERFORM-node-named-by-text";
    public const string RektNoSqlText = "REKT-WRONG no-SQL-text";
    public const string RektNoLocalStorage = "REKT-WRONG no-LOCAL-STORAGE";
    public const string RektNoLevel77 = "REKT-WRONG no-level-77";
    public const string RektSpecialRegister = "REKT-WRONG WHEN-COMPILED-as-data";
    public const string RektFillerRename = "REKT-WRONG FILLER-renamed-FIL-R";
    public const string RektNoOccurs = "REKT-WRONG no-OCCURS";
    public const string RepCopybookOrder = "REPRESENTATION copybook-order";
    public const string RepDataOrder = "REPRESENTATION data-order-by-section";
    public const string GapNativeContract = "GAP native-dependency-contract";
    public const string Derived = "DERIVED";

    private readonly HashSet<string> _nativeUnclaimed;
    private readonly Dictionary<(string Program, string Category), (int Explained, int Unexplained)> _stats = new();
    private ProgramEvidence? _evidence;

    // nativeUnclaimed: programs whose Starforge export declares no native dependency coverage, so the gate cannot verify them.
    public Classifier(HashSet<string> nativeUnclaimed)
    {
        _nativeUnclaimed = nativeUnclaimed;
    }

    public void Begin(ProgramEvidence evidence) => _evidence = evidence;

    public string Classify(string category, DiffKind kind, string program, string key, string? rekt, string? starforge)
    {
        var result = Rule(category, kind, program, key, rekt, starforge) ?? $"{Unexplained} {category}";
        var stat = _stats.GetValueOrDefault((program, category));
        _stats[(program, category)] = result.StartsWith(Unexplained, StringComparison.Ordinal)
            ? (stat.Explained, stat.Unexplained + 1)
            : (stat.Explained + 1, stat.Unexplained);
        return result;
    }

    private string? Rule(string category, DiffKind kind, string program, string key, string? rekt, string? sf)
    {
        var e = _evidence is not null && _evidence.Program == program ? _evidence : null;
        switch (category)
        {
            case "context.section-lines" or "context.paragraph-lines" when kind == DiffKind.Mismatch && rekt == "0-0":
            case "context.call-lines" when kind == DiffKind.Mismatch && rekt == "0":
                return RektNoLines;
            case "graph.structure-lines" when kind == DiffKind.Mismatch && rekt!.Split(" / ").All(p => p == "-1--1"):
                return RektNoLines;

            case "context.perform-edges" when e is not null:
                return PerformEdge(e, kind, Base(key));
            case "context.perform-conditional" when e is not null && kind == DiffKind.Mismatch && rekt == "False" && sf == "True":
            {
                var (section, target) = Edge(Base(key));
                return e.RektPerformCarriesNoTarget && e.StarforgePerforms.Any(p => p.Section == section && p.Target == target && p.Conditional)
                    ? RektNoConditional : null;
            }

            case "graph.perform-edges" when e is not null:
                return GraphPerformEdge(e, kind, Base(key));

            case "context.sql" when e is not null && kind == DiffKind.OnlyStarforge:
                return e.Rekt.SqlStatements.Count == 0 && e.RektSqlNodesWithText == 0 ? RektNoSqlText : null;

            case "context.copybook-order" when e is not null:
                return e.Rekt.CopybookUsage.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(e.Starforge.CopybookUsage) ? RepCopybookOrder : null;

            case "context.data-items" when e is not null && kind == DiffKind.OnlyStarforge:
            {
                var item = e.StarforgeData[key];
                if (item.SourceSection == "LOCAL_STORAGE")
                {
                    return e.RektData.Values.Any(d => d.SourceSection == "LOCAL_STORAGE") ? null : RektNoLocalStorage;
                }

                return item.Level == 77 && !e.RektData.Values.Any(d => d.Level == 77) ? RektNoLevel77 : null;
            }

            case "context.data-items" when e is not null && kind == DiffKind.OnlyRekt:
                return key == "WHEN-COMPILED" && !e.StarforgeData.ContainsKey(key) ? RektSpecialRegister : null;
            case "context.data-name" when kind == DiffKind.Mismatch && sf == "FILLER" && FilR().IsMatch(rekt!):
                return RektFillerRename;
            case "context.data-occurs" when kind == DiffKind.Mismatch && rekt == "null" && int.TryParse(sf, out _):
                return RektNoOccurs;
            case "context.data-order" when e is not null:
                return SectionOrder(e, rekt!, sf!) ? RepDataOrder : null;

            case "evidence.coverage" or "evidence.definitions" or "evidence.diagnostics":
                return _nativeUnclaimed.Contains(program) ? GapNativeContract : null;

            case "facts.controlFlow":
                return DerivedFrom(program, "context.perform-edges");
            case "facts.io" or "facts.externalEffects":
                return DerivedFrom(program, "context.sql");
            case "facts.data":
                return DerivedFrom(program, "context.data-");
        }

        return null;
    }

    // Facts are computed from the RektContext, so a facts difference is explained when its source category differs only for proven reasons.
    private string? DerivedFrom(string program, string prefix)
    {
        var upstream = _stats.Where(s => s.Key.Program == program && s.Key.Category.StartsWith(prefix, StringComparison.Ordinal)).Select(s => s.Value).ToList();
        return upstream.Sum(s => s.Explained) > 0 && upstream.Sum(s => s.Unexplained) == 0 ? $"{Derived} from {prefix.TrimEnd('-')}" : null;
    }

    private static string? PerformEdge(ProgramEvidence e, DiffKind kind, string key)
    {
        var (section, target) = Edge(key);
        if (kind == DiffKind.OnlyStarforge)
        {
            var sfTotal = e.Starforge.PerformGraph.Count(p => p.From == section && p.To == target);
            var rektTotal = e.Rekt.PerformGraph.Count(p => p.From == section && p.To == target);
            var hidden = e.StarforgePerforms.Count(p => p.Section == section && p.Target == target && p.UnderChildless);
            return e.RektDropsNestedStatements && sfTotal - rektTotal <= hidden ? RektDropsNested : null;
        }

        return kind == DiffKind.OnlyRekt && e.RektPerformCarriesNoTarget
            && e.StarforgePerforms.Any(p => p.Section == section && p.Target is null && p.Compressed == target)
            ? RektInlinePerformName : null;
    }

    private static string? GraphPerformEdge(ProgramEvidence e, DiffKind kind, string key)
    {
        var (caller, name) = Edge(key);
        if (!e.RektPerformCarriesNoTarget)
        {
            return null;
        }

        if (kind == DiffKind.OnlyRekt)
        {
            return e.StarforgePerforms.Any(p => p.Caller == caller && p.Compressed == name) ? RektPerformTextName : null;
        }

        // Each Starforge PERFORM behind this row must either appear in REKT under its text name or sit where REKT drops statements.
        var nodes = e.StarforgePerforms.Where(p => p.Caller == caller && p.Target == name).ToList();
        if (nodes.Count == 0 || !nodes.All(p => p.UnderChildless || e.RektGraphPerforms.Contains((caller, p.Compressed))))
        {
            return null;
        }

        if (nodes.Any(p => !p.UnderChildless))
        {
            return RektPerformTextName;
        }

        return e.RektDropsNestedStatements ? RektDropsNested : null;
    }

    // REKT groups top-level items by section; Starforge's shared items, stably sorted by REKT's section order, must equal REKT's order.
    private static bool SectionOrder(ProgramEvidence e, string rekt, string sf)
    {
        var left = rekt.Split(" | ");
        var right = sf.Split(" | ");
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in left)
        {
            rank.TryAdd(e.RektData[path].SourceSection ?? "", rank.Count);
        }

        return right.OrderBy(p => rank[e.StarforgeData[p].SourceSection ?? ""]).SequenceEqual(left, StringComparer.Ordinal);
    }

    private static (string From, string To) Edge(string key)
    {
        var at = key.IndexOf(" -> ", StringComparison.Ordinal);
        return (key[..at], key[(at + 4)..]);
    }

    // Compare suffixes repeated keys with #n.
    private static string Base(string key)
    {
        var hash = key.LastIndexOf('#');
        return hash > 0 && hash < key.Length - 1 && key[(hash + 1)..].All(char.IsAsciiDigit) ? key[..hash] : key;
    }

    [GeneratedRegex(@"^FIL-R\d+$")]
    private static partial Regex FilR();
}
