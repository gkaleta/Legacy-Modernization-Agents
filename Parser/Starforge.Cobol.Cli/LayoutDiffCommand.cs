using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Starforge.Cobol.Emit;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Cli;

// diff-rekt --layouts: compares Starforge data items with REKT's data_structures trees (names, levels, PIC, USAGE,
// OCCURS, REDEFINES, data type, section) and checks Starforge offsets/lengths against the two REKT outputs that carry
// byte positions: commarea slot lengths and BMS symbolic-map field offsets.
internal static partial class LayoutDiffCommand
{
    private const int ExamplesPerCategory = 5;

    public static int Run(CommandLine cmd, TextWriter stdout, TextWriter stderr)
    {
        cmd.RejectUnknown("source", "rekt", "out");
        var roots = cmd.All("source").Select(Path.GetFullPath).ToList();
        if (roots.Count == 0)
        {
            throw new UsageException("diff-rekt --layouts needs at least one --source <estate-dir>");
        }

        var rekt = Path.GetFullPath(cmd.Required("rekt"));
        var outDir = Path.GetFullPath(cmd.Required("out"));
        foreach (var dir in roots.Append(rekt).Where(d => !Directory.Exists(d)))
        {
            stderr.WriteLine($"error: directory not found: {dir}");
            return 2;
        }

        var display = new PathDisplay(roots.Select(r => (r, Path.GetFileName(r))));
        var programs = roots
            .SelectMany(r => CoverageCommand.DiscoverPrograms(r))
            .OrderBy(p => display.Show(p), StringComparer.Ordinal)
            .ToList();
        var frontEnd = new CobolFrontEnd(new FrontEndOptions(roots));
        var results = new ProgramDiff[programs.Count];
        Parallel.For(0, programs.Count, i => results[i] = Compare(frontEnd, programs[i], display.Show(programs[i]), rekt));

        Directory.CreateDirectory(outDir);
        var report = new DiffReport(results);
        using (var stream = File.Create(Path.Combine(outDir, "layout-diff.json")))
        using (var w = new Utf8JsonWriter(stream, JsonReport.WriterOptions))
        {
            report.WriteJson(w);
        }

        File.WriteAllText(Path.Combine(outDir, "layout-diff.md"), report.Markdown(), new UTF8Encoding(false));
        stdout.WriteLine(report.OneLine());
        stdout.WriteLine($"report: {Path.Combine(outDir, "layout-diff.md")}");
        foreach (var p in results.Where(p => p.Exception is not null))
        {
            stderr.WriteLine($"{p.Program}: {p.Exception}");
        }

        return results.Any(p => p.Exception is not null) ? 1 : 0;
    }

    private static ProgramDiff Compare(CobolFrontEnd frontEnd, string path, string program, string rektRoot)
    {
        var diff = new ProgramDiff(program);
        try
        {
            var dataFile = Path.Combine(rektRoot, program + ".report", "data_structures", Path.GetFileName(path) + "-data.json");
            if (!File.Exists(dataFile))
            {
                diff.NoBaseline = true;
                return diff;
            }

            var result = frontEnd.Process(path);
            var mine = result.Programs.SelectMany(p => p.Items).Where(i => i.HasStorage).ToList();
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(dataFile)))
            {
                CompareTree(diff, mine, FlattenRekt(doc.RootElement));
            }

            var depsFile = Path.Combine(rektRoot, program + "-deps.json");
            if (File.Exists(depsFile))
            {
                using var deps = JsonDocument.Parse(File.ReadAllBytes(depsFile));
                CompareCommarea(diff, result, deps.RootElement);
                CompareMaps(diff, result, deps.RootElement);
            }
        }
        catch (Exception ex)
        {
            diff.Exception = $"{ex.GetType().Name}: {ex.Message}";
        }

        return diff;
    }

    private sealed record RektItem(string Path, int Level, string Name, string RawText, string Redefines, string DataType, string Section);

    private static List<RektItem> FlattenRekt(JsonElement root)
    {
        var items = new List<RektItem>();
        void Walk(JsonElement node, string? parentPath)
        {
            foreach (var c in node.GetProperty("children").EnumerateArray())
            {
                var level = c.GetProperty("levelNumber").GetInt32();
                var section = c.GetProperty("sourceSection").GetString() ?? string.Empty;

                // Level -1 nodes are PROCEDURE DIVISION literals, not data description entries.
                if (level < 1 || section == "PROCEDURE_DIVISION")
                {
                    continue;
                }

                // REKT names an unnamed FILLER REDEFINES entry FIL-Rnnn; the source says FILLER.
                var name = (c.GetProperty("name").GetString() ?? string.Empty).ToUpperInvariant();
                if (SyntheticFillerRegex().IsMatch(name))
                {
                    name = "FILLER";
                }
                var path = parentPath is null || level is 1 or 77 ? name : parentPath + "." + name;
                items.Add(new RektItem(path, level, name, c.GetProperty("rawText").GetString() ?? string.Empty,
                    (c.GetProperty("redefines").GetString() ?? string.Empty).ToUpperInvariant(), c.GetProperty("dataType").GetString() ?? string.Empty, section));
                Walk(c, path);
            }
        }

        Walk(root, null);
        return items;
    }

    private static void CompareTree(ProgramDiff diff, List<DataItem> mine, List<RektItem> rekt)
    {
        var mineByKey = Keyed(mine, i => (i.Path.ToUpperInvariant(), i.Level));
        var rektByKey = Keyed(rekt, r => (r.Path, r.Level));
        diff.StarforgeItems = mine.Count;
        diff.RektItems = rekt.Count;
        foreach (var (key, r) in rektByKey)
        {
            if (!mineByKey.TryGetValue(key, out var m))
            {
                diff.Add("onlyInRekt", OnlyInRektCategory(r, mine), $"{r.Path} (level {r.Level}): `{r.RawText}`");
                continue;
            }

            diff.Matched++;
            var rektPicture = RektPicture(r.RawText);
            var truncated = m.Picture is { } mp && rektPicture is not null && rektPicture.StartsWith(mp.ToUpperInvariant(), StringComparison.Ordinal);
            CompareField(diff, "pic", rektPicture, m.Picture?.ToUpperInvariant(), m, r, truncated ? "Starforge PIC is a prefix of REKT's (text cut at column 72)" : null);
            CompareField(diff, "usage", RektUsage(r.RawText), m.Entry.Usage is { } u ? UsageName(u) : null, m, r, null);
            CompareField(diff, "occurs", RektOccurs(r.RawText), m.Occurs is { } o ? OccursText(o) : null, m, r, null);
            CompareField(diff, "redefines", r.Redefines.Length == 0 ? null : r.Redefines, m.RedefinesName?.ToUpperInvariant(), m, r, null);
            CompareField(diff, "dataType", r.DataType, ExpectedDataType(m), m, r, $"{r.DataType} vs {m.Category}");
            CompareField(diff, "section", r.Section, SectionName(m.Section), m, r, null);
        }

        foreach (var (key, m) in mineByKey.Where(kv => !rektByKey.ContainsKey(kv.Key)))
        {
            diff.Add("onlyInStarforge", OnlyInStarforgeCategory(m, rekt), $"{m.Path} (level {m.Level:00}) {Describe(m)}");
        }
    }

    // Duplicate paths (FILLERs, repeated copybooks) are told apart by their order of appearance.
    private static Dictionary<(string, int, int), T> Keyed<T>(IEnumerable<T> items, Func<T, (string Path, int Level)> key)
    {
        var seen = new Dictionary<(string, int), int>();
        var result = new Dictionary<(string, int, int), T>();
        foreach (var item in items)
        {
            var (path, level) = key(item);
            var n = seen.TryGetValue((path, level), out var c) ? c + 1 : 0;
            seen[(path, level)] = n;
            result[(path, level, n)] = item;
        }

        return result;
    }

    private static void CompareField(ProgramDiff diff, string field, string? rekt, string? mine, DataItem m, RektItem r, string? category)
    {
        diff.FieldsCompared[field] = diff.FieldsCompared.GetValueOrDefault(field) + 1;
        if (string.Equals(rekt ?? string.Empty, mine ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        diff.Add("mismatch:" + field, category ?? $"REKT {rekt ?? "(none)"} vs Starforge {mine ?? "(none)"}", $"{m.Path}: REKT `{r.RawText}`; Starforge {Describe(m)}");
    }

    private static string OnlyInRektCategory(RektItem r, List<DataItem> mine)
        => !mine.Any(m => m.Parent is null && string.Equals(m.DisplayName, RecordName(r.Path), StringComparison.OrdinalIgnoreCase)) ? "record not in Starforge"
            : mine.Any(m => string.Equals(m.Name, r.Name, StringComparison.OrdinalIgnoreCase) && m.Level == r.Level) ? "same name and level elsewhere in the tree"
            : mine.Any(m => string.Equals(m.Name, r.Name, StringComparison.OrdinalIgnoreCase)) ? "same name at a different level"
            : "name not found";

    private static string OnlyInStarforgeCategory(DataItem m, List<RektItem> rekt)
        => m.Section == DataSectionKind.LocalStorage ? "LOCAL-STORAGE item (REKT exports no LOCAL-STORAGE)"
            : m.Record.Level == 77 ? "level-77 item (REKT exports no level 77)"
            : !rekt.Any(r => r.Path == m.Record.DisplayName.ToUpperInvariant()) ? $"record not in REKT ({SectionName(m.Section)})"
            : rekt.Any(r => string.Equals(r.Name, m.DisplayName, StringComparison.OrdinalIgnoreCase) && r.Level == m.Level) ? "same name and level elsewhere in the tree"
            : rekt.Any(r => string.Equals(r.Name, m.DisplayName, StringComparison.OrdinalIgnoreCase)) ? "same name at a different level"
            : m.FromCopybook is not null ? "name not found (from copybook)" : "name not found";

    private static string RecordName(string path) => path.Split('.')[0];

    private static string Describe(DataItem m)
    {
        var sb = new StringBuilder();
        sb.Append(m.Category);
        if (m.Picture is not null)
        {
            sb.Append(" PIC ").Append(m.Picture);
        }

        sb.Append(' ').Append(UsageName(m.Usage));
        if (m.Occurs is { } o)
        {
            sb.Append(" OCCURS ").Append(OccursText(o));
        }

        sb.Append(CultureInfo.InvariantCulture, $" @{m.Offset}+{m.Length}");
        if (m.FromCopybook is not null)
        {
            sb.Append(" [").Append(m.FromCopybook).Append(']');
        }

        return sb.ToString();
    }

    private static string? RektPicture(string raw)
    {
        var match = PictureRegex().Match(StripLiterals(raw));
        return match.Success ? RawPicture(raw, match) : null;
    }

    // The picture string itself may contain quote-free characters only, so it is read from the original text.
    private static string RawPicture(string raw, Match match) => match.Groups["pic"].Value.TrimEnd('.').ToUpperInvariant();

    private static string? RektUsage(string raw)
    {
        var words = WordRegex().Matches(StripLiterals(raw).ToUpperInvariant()).Select(m => m.Value).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i] is "PIC" or "PICTURE")
            {
                i++;
                continue;
            }

            var usage = words[i] switch
            {
                "COMP" or "COMPUTATIONAL" or "BINARY" or "COMP-4" or "COMPUTATIONAL-4" => "BINARY",
                "COMP-5" or "COMPUTATIONAL-5" => "COMP-5",
                "COMP-3" or "COMPUTATIONAL-3" or "PACKED-DECIMAL" => "PACKED-DECIMAL",
                "COMP-1" or "COMPUTATIONAL-1" => "COMP-1",
                "COMP-2" or "COMPUTATIONAL-2" => "COMP-2",
                "INDEX" when i == 0 || words[i - 1] is not ("INDEXED" or "BY") => "INDEX",
                "POINTER" => "POINTER",
                "PROCEDURE-POINTER" => "PROCEDURE-POINTER",
                "FUNCTION-POINTER" => "FUNCTION-POINTER",
                "DISPLAY" => "DISPLAY",
                "DISPLAY-1" => "DISPLAY-1",
                "NATIONAL" when i == 0 || words[i - 1] != "GROUP-USAGE" => "NATIONAL",
                _ => null,
            };
            if (usage is not null)
            {
                return usage;
            }
        }

        return null;
    }

    private static string? RektOccurs(string raw)
    {
        var match = OccursRegex().Match(StripLiterals(raw).ToUpperInvariant());
        if (!match.Success)
        {
            return null;
        }

        var min = int.Parse(match.Groups["a"].Value, CultureInfo.InvariantCulture);
        return match.Groups["b"].Success ? $"{min} TO {int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture)}" : min.ToString(CultureInfo.InvariantCulture);
    }

    private static string OccursText(OccursClause o)
        => o.HasTo ? $"{o.Min} TO {o.Max}" : o.Max.ToString(CultureInfo.InvariantCulture);

    private static string StripLiterals(string raw) => LiteralRegex().Replace(raw, "''");

    private static string UsageName(DataUsage usage) => usage switch
    {
        DataUsage.Binary => "BINARY",
        DataUsage.Comp5 => "COMP-5",
        DataUsage.PackedDecimal => "PACKED-DECIMAL",
        DataUsage.Comp1 => "COMP-1",
        DataUsage.Comp2 => "COMP-2",
        DataUsage.Index => "INDEX",
        DataUsage.Pointer or DataUsage.Pointer32 => "POINTER",
        DataUsage.ProcedurePointer => "PROCEDURE-POINTER",
        DataUsage.FunctionPointer => "FUNCTION-POINTER",
        DataUsage.ObjectReference => "OBJECT-REFERENCE",
        DataUsage.National => "NATIONAL",
        DataUsage.Display1 => "DISPLAY-1",
        DataUsage.Utf8 => "UTF-8",
        _ => "DISPLAY",
    };

    // REKT's abstract types: groups are OBJECT, addresses POINTER, arithmetic items NUMBER, everything else STRING.
    private static string ExpectedDataType(DataItem m) => m.Category switch
    {
        DataCategory.Group => "OBJECT",
        DataCategory.Pointer => "POINTER",
        DataCategory.Numeric or DataCategory.InternalFloatingPoint or DataCategory.Index => "NUMBER",
        _ => "STRING",
    };

    private static string SectionName(DataSectionKind kind) => kind switch
    {
        DataSectionKind.File => "FILE_DESCRIPTOR",
        DataSectionKind.Linkage => "LINKAGE",
        DataSectionKind.LocalStorage => "LOCAL_STORAGE",
        _ => "WORKING_STORAGE",
    };

    private static void CompareCommarea(ProgramDiff diff, CompilationResult result, JsonElement deps)
    {
        if (!deps.TryGetProperty("commareaInventory", out var inventory) || inventory.ValueKind != JsonValueKind.Object
            || !inventory.TryGetProperty("records", out var records))
        {
            return;
        }

        foreach (var rec in records.EnumerateArray())
        {
            var name = rec.GetProperty("name").GetString() ?? string.Empty;
            var record = result.Programs.SelectMany(p => p.Records).FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!rec.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var slot in slots.EnumerateObject())
            {
                if (!slot.Value.TryGetProperty("length", out var lengthElement) || lengthElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var length = lengthElement.GetInt32();
                var item = record?.SelfAndDescendants().FirstOrDefault(i => i.HasStorage && string.Equals(i.DisplayName, slot.Name, StringComparison.OrdinalIgnoreCase));
                diff.CommareaSlots++;
                if (item is null)
                {
                    diff.Add("commarea", "slot not found", $"{name}.{slot.Name}: REKT length {length}");
                }
                else if (item.Length != length)
                {
                    diff.Add("commarea", "length differs", $"{item.Path}: REKT length {length}; Starforge {Describe(item)}");
                }
                else
                {
                    diff.CommareaMatched++;
                }
            }
        }
    }

    private static void CompareMaps(ProgramDiff diff, CompilationResult result, JsonElement deps)
    {
        if (!deps.TryGetProperty("dependencyDialectStatements", out var statements) || statements.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var records = result.Programs.SelectMany(p => p.Records).ToList();
        foreach (var statement in statements.EnumerateArray())
        {
            if (!statement.TryGetProperty("resourceBindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var binding in bindings.EnumerateArray())
            {
                if (!binding.TryGetProperty("definition", out var def) || def.ValueKind != JsonValueKind.Object
                    || !def.TryGetProperty("byteLength", out var byteLength) || !def.TryGetProperty("fields", out var fields))
                {
                    continue;
                }

                var mapName = def.GetProperty("name").GetString() ?? string.Empty;
                if (!seen.Add(mapName))
                {
                    continue;
                }

                var input = records.FirstOrDefault(r => string.Equals(r.Name, mapName + "I", StringComparison.OrdinalIgnoreCase));
                diff.Maps++;
                if (input is null)
                {
                    diff.Add("bmsMap", "symbolic map record not found", $"{mapName}I");
                    continue;
                }

                diff.MapChecks++;
                if (input.Length != byteLength.GetInt32())
                {
                    diff.Add("bmsMap", "map length differs", $"{input.Path}: REKT byteLength {byteLength.GetInt32()}; Starforge {input.Length}");
                }
                else
                {
                    diff.MapMatched++;
                }

                foreach (var field in fields.EnumerateArray())
                {
                    CompareMapField(diff, input, field);
                }
            }
        }
    }

    private static void CompareMapField(ProgramDiff diff, DataItem input, JsonElement field)
    {
        var name = field.GetProperty("name").GetString() ?? string.Empty;
        var offset = field.GetProperty("offset").GetInt32();
        var dataOffset = field.GetProperty("dataOffset").GetInt32();
        var length = field.GetProperty("length").GetInt32();
        var occurs = field.TryGetProperty("occurs", out var o) ? o.GetInt32() : 1;
        var stride = field.TryGetProperty("stride", out var s) ? s.GetInt32() : 0;
        var items = input.SelfAndDescendants().ToList();
        var lengthItem = items.FirstOrDefault(i => string.Equals(i.Name, name + "L", StringComparison.OrdinalIgnoreCase));
        var dataItem = items.FirstOrDefault(i => string.Equals(i.Name, name + "I", StringComparison.OrdinalIgnoreCase));
        diff.MapChecks++;
        if (lengthItem is null || dataItem is null)
        {
            diff.Add("bmsMap", "field not found", $"{input.Path}: {name}L/{name}I");
            return;
        }

        var problems = new List<string>();
        if (lengthItem.Offset != offset)
        {
            problems.Add($"L offset REKT {offset} vs {lengthItem.Offset}");
        }

        if (dataItem.Offset != dataOffset)
        {
            problems.Add($"I offset REKT {dataOffset} vs {dataItem.Offset}");
        }

        if (dataItem.Length != length)
        {
            problems.Add($"I length REKT {length} vs {dataItem.Length}");
        }

        // Arrays repeat the L/F/A/I group; one occurrence of it is REKT's stride.
        var repeat = dataItem.Ancestors().Prepend(dataItem).FirstOrDefault(a => a.Occurs is not null);
        if (occurs > 1 && repeat is not null && (repeat.Length != stride || repeat.Occurs!.Max != occurs))
        {
            problems.Add($"stride/occurs REKT {stride}x{occurs} vs {repeat.Length}x{repeat.Occurs!.Max}");
        }

        if (problems.Count == 0)
        {
            diff.MapMatched++;
        }
        else
        {
            diff.Add("bmsMap", "field position differs", $"{dataItem.Path}: {string.Join("; ", problems)}");
        }
    }

    [GeneratedRegex(@"\bPIC(?:TURE)?\s+(?:IS\s+)?(?<pic>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex PictureRegex();

    [GeneratedRegex(@"^FIL-R\d+$")]
    private static partial Regex SyntheticFillerRegex();

    [GeneratedRegex(@"\bOCCURS\s+(?<a>\d+)(?:\s+TO\s+(?<b>\d+))?")]
    private static partial Regex OccursRegex();

    [GeneratedRegex(@"[A-Z0-9][A-Z0-9-]*")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"'[^']*'|""[^""]*""")]
    private static partial Regex LiteralRegex();

    private sealed class ProgramDiff(string program)
    {
        public string Program { get; } = program;

        public string? Exception { get; set; }

        public bool NoBaseline { get; set; }

        public int StarforgeItems { get; set; }

        public int RektItems { get; set; }

        public int Matched { get; set; }

        public Dictionary<string, int> FieldsCompared { get; } = new(StringComparer.Ordinal);

        public int CommareaSlots { get; set; }

        public int CommareaMatched { get; set; }

        public int Maps { get; set; }

        public int MapChecks { get; set; }

        public int MapMatched { get; set; }

        public List<(string Kind, string Category, string Example)> Findings { get; } = new();

        public void Add(string kind, string category, string example) => Findings.Add((kind, category, example));
    }

    private sealed class DiffReport(IReadOnlyList<ProgramDiff> programs)
    {
        private readonly List<ProgramDiff> _compared = programs.Where(p => !p.NoBaseline && p.Exception is null).ToList();

        private int Count(string kind) => _compared.Sum(p => p.Findings.Count(f => f.Kind == kind));

        private IEnumerable<string> Fields => _compared.SelectMany(p => p.FieldsCompared.Keys).Distinct().OrderBy(f => f, StringComparer.Ordinal);

        private IEnumerable<IGrouping<(string Kind, string Category), (string Program, string Example)>> Categories
            => _compared
                .SelectMany(p => p.Findings.Select(f => (Key: (f.Kind, f.Category), Value: (p.Program, f.Example))))
                .GroupBy(x => x.Key, x => x.Value)
                .OrderBy(g => g.Key.Kind, StringComparer.Ordinal)
                .ThenByDescending(g => g.Count())
                .ThenBy(g => g.Key.Category, StringComparer.Ordinal);

        public string OneLine()
            => $"programs={_compared.Count} noBaseline={programs.Count(p => p.NoBaseline)} exceptions={programs.Count(p => p.Exception is not null)} "
                + $"rektItems={_compared.Sum(p => p.RektItems)} starforgeItems={_compared.Sum(p => p.StarforgeItems)} matched={_compared.Sum(p => p.Matched)} "
                + $"onlyInStarforge={Count("onlyInStarforge")} onlyInRekt={Count("onlyInRekt")} "
                + string.Join(" ", Fields.Select(f => $"{f}Mismatches={Count("mismatch:" + f)}"))
                + $" commarea={_compared.Sum(p => p.CommareaMatched)}/{_compared.Sum(p => p.CommareaSlots)} bmsMaps={_compared.Sum(p => p.MapMatched)}/{_compared.Sum(p => p.MapChecks)}";

        public void WriteJson(Utf8JsonWriter w)
        {
            w.WriteStartObject();
            w.WriteString("milestone", "M2");
            w.WriteStartObject("totals");
            w.WriteNumber("programs", _compared.Count);
            w.WriteNumber("programsWithoutBaseline", programs.Count(p => p.NoBaseline));
            w.WriteNumber("exceptions", programs.Count(p => p.Exception is not null));
            w.WriteNumber("rektItems", _compared.Sum(p => p.RektItems));
            w.WriteNumber("starforgeItems", _compared.Sum(p => p.StarforgeItems));
            w.WriteNumber("matched", _compared.Sum(p => p.Matched));
            w.WriteNumber("onlyInStarforge", Count("onlyInStarforge"));
            w.WriteNumber("onlyInRekt", Count("onlyInRekt"));
            w.WriteStartObject("fieldMismatches");
            foreach (var f in Fields)
            {
                w.WriteStartObject(f);
                w.WriteNumber("compared", _compared.Sum(p => p.FieldsCompared.GetValueOrDefault(f)));
                w.WriteNumber("mismatched", Count("mismatch:" + f));
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteStartObject("commareaSlotLengths");
            w.WriteNumber("compared", _compared.Sum(p => p.CommareaSlots));
            w.WriteNumber("matched", _compared.Sum(p => p.CommareaMatched));
            w.WriteEndObject();
            w.WriteStartObject("bmsSymbolicMaps");
            w.WriteNumber("maps", _compared.Sum(p => p.Maps));
            w.WriteNumber("checks", _compared.Sum(p => p.MapChecks));
            w.WriteNumber("matched", _compared.Sum(p => p.MapMatched));
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartArray("categories");
            foreach (var g in Categories)
            {
                w.WriteStartObject();
                w.WriteString("kind", g.Key.Kind);
                w.WriteString("category", g.Key.Category);
                w.WriteNumber("count", g.Count());
                w.WriteNumber("programs", g.Select(x => x.Program).Distinct().Count());
                w.WriteStartArray("examples");
                foreach (var (program, example) in g.Take(ExamplesPerCategory))
                {
                    w.WriteStringValue($"{program}: {example}");
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("programs");
            foreach (var p in programs)
            {
                w.WriteStartObject();
                w.WriteString("program", p.Program);
                if (p.NoBaseline)
                {
                    w.WriteBoolean("noBaseline", true);
                }

                if (p.Exception is not null)
                {
                    w.WriteString("exception", p.Exception);
                }

                w.WriteNumber("rektItems", p.RektItems);
                w.WriteNumber("starforgeItems", p.StarforgeItems);
                w.WriteNumber("matched", p.Matched);
                w.WriteStartObject("findings");
                foreach (var g in p.Findings.GroupBy(f => f.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    w.WriteNumber(g.Key, g.Count());
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        public string Markdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Starforge M2 data layout vs REKT");
            sb.AppendLine();
            sb.AppendLine("Starforge data items (88/66 excluded, as REKT omits them) matched to REKT `data_structures` nodes by qualified path + level + order among duplicates.");
            sb.AppendLine("PIC, USAGE and OCCURS on the REKT side are read from the entry's raw text (declared, not inherited). Byte positions are checked against");
            sb.AppendLine("REKT commarea slot lengths and BMS symbolic-map definitions (the only REKT outputs that carry them). See `Parser/docs-local/diff-triage.md` for who is right per category.");
            sb.AppendLine();
            sb.AppendLine("| Measure | Value |");
            sb.AppendLine("|---|---:|");
            sb.AppendLine($"| Programs compared | {_compared.Count} |");
            sb.AppendLine($"| Programs without REKT baseline | {programs.Count(p => p.NoBaseline)} |");
            sb.AppendLine($"| Exceptions | {programs.Count(p => p.Exception is not null)} |");
            sb.AppendLine($"| REKT items | {_compared.Sum(p => p.RektItems)} |");
            sb.AppendLine($"| Starforge items | {_compared.Sum(p => p.StarforgeItems)} |");
            sb.AppendLine($"| Matched | {_compared.Sum(p => p.Matched)} |");
            sb.AppendLine($"| Only in Starforge | {Count("onlyInStarforge")} |");
            sb.AppendLine($"| Only in REKT | {Count("onlyInRekt")} |");
            foreach (var f in Fields)
            {
                sb.AppendLine($"| `{f}` mismatches (of compared) | {Count("mismatch:" + f)} / {_compared.Sum(p => p.FieldsCompared.GetValueOrDefault(f))} |");
            }

            sb.AppendLine($"| Commarea slot lengths matching | {_compared.Sum(p => p.CommareaMatched)} / {_compared.Sum(p => p.CommareaSlots)} |");
            sb.AppendLine($"| BMS map checks matching (map lengths + fields) | {_compared.Sum(p => p.MapMatched)} / {_compared.Sum(p => p.MapChecks)} ({_compared.Sum(p => p.Maps)} maps) |");
            sb.AppendLine();
            sb.AppendLine("## Categories");
            var categories = Categories.ToList();
            if (categories.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("None.");
            }

            foreach (var g in categories)
            {
                sb.AppendLine();
                sb.AppendLine($"### {g.Key.Kind}: {g.Key.Category} ({g.Count()} in {g.Select(x => x.Program).Distinct().Count()} programs)");
                sb.AppendLine();
                foreach (var (program, example) in g.Take(ExamplesPerCategory))
                {
                    sb.AppendLine($"- {program}: {example.Replace("|", "\\|", StringComparison.Ordinal)}");
                }
            }

            return sb.ToString();
        }
    }
}
