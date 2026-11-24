using System.Text;
using System.Text.Json;
using Starforge.Cobol.Emit;
using Starforge.Cobol.Exec;

namespace Starforge.Cobol.Cli;

// `starforge diff-rekt exec`: compares the EXEC facts of the M4 pass with REKT's <program>-deps.json per program.
// Each category is a per-program multiset of "OPERATION target" keys, deduplicated per statement on both sides.
internal static class ExecDiffCommand
{
    public static readonly string[] Categories =
    {
        "cics-commands", "cics-program", "cics-transid", "cics-file", "cics-queue", "cics-map", "cics-label", "cics-data",
        "sql-statements", "sql-tables", "sql-cursors", "sql-host-variables", "dli-commands", "dli-segments",
    };

    public static int Run(CommandLine cmd, TextWriter stdout, TextWriter stderr)
    {
        cmd.RejectUnknown("source", "out", "rekt-baseline");
        if (cmd.Positional.Count != 1 || cmd.Positional[0] != "exec")
        {
            throw new UsageException("diff-rekt supports only 'exec' until M5");
        }

        var roots = cmd.All("source").Select(Path.GetFullPath).ToList();
        if (roots.Count == 0)
        {
            throw new UsageException("diff-rekt exec needs at least one --source <estate-dir>");
        }

        foreach (var r in roots.Where(r => !Directory.Exists(r)))
        {
            stderr.WriteLine($"error: source directory not found: {r}");
            return 2;
        }

        var baseline = Path.GetFullPath(cmd.Required("rekt-baseline"));
        var outDir = Path.GetFullPath(cmd.Required("out"));
        var display = new PathDisplay(roots.Select(r => (r, Path.GetFileName(r))));
        var programs = roots
            .SelectMany(r => CoverageCommand.DiscoverPrograms(r).Select(p => (Display: display.Show(p), Path: p)))
            .OrderBy(p => p.Display, StringComparer.Ordinal)
            .ToList();

        var frontEnd = new CobolFrontEnd(new FrontEndOptions(roots));
        var results = new ProgramDiff[programs.Count];
        Parallel.For(0, programs.Count, i =>
        {
            var (name, path) = programs[i];
            var exec = ExecPass.Run(frontEnd.Process(path));
            var deps = Path.Combine(baseline, name + "-deps.json");
            results[i] = new ProgramDiff(name, File.Exists(deps), Starforge(exec), File.Exists(deps) ? Rekt(deps) : Empty());
        });

        Directory.CreateDirectory(outDir);
        File.WriteAllBytes(Path.Combine(outDir, "exec-diff.json"), Json(results));
        File.WriteAllText(Path.Combine(outDir, "exec-diff.md"), Markdown(results), new UTF8Encoding(false));
        var totals = Totals(results);
        stdout.WriteLine($"programs={results.Length} rekt-missing={results.Count(r => !r.RektFound)} " +
            string.Join(' ', Categories.Select(c => $"{c}={totals[c].Matched}/{totals[c].Starforge}:{totals[c].Rekt}")));
        stdout.WriteLine($"report: {Path.Combine(outDir, "exec-diff.md")}");
        return 0;
    }

    internal sealed record ProgramDiff(string Program, bool RektFound, Facts Starforge, Facts Rekt)
    {
        public IReadOnlyList<string> OnlyStarforge(string category) => Minus(Starforge[category], Rekt[category]);

        public IReadOnlyList<string> OnlyRekt(string category) => Minus(Rekt[category], Starforge[category]);
    }

    internal sealed class Facts
    {
        private readonly Dictionary<string, List<string>> _items = Categories.ToDictionary(c => c, _ => new List<string>(), StringComparer.Ordinal);

        public IReadOnlyList<string> this[string category] => _items[category];

        public void Add(string category, IEnumerable<string> perStatement) => _items[category].AddRange(perStatement.Distinct(StringComparer.Ordinal));
    }

    internal sealed record Total(int Starforge, int Rekt, int Matched)
    {
        public int OnlyStarforge => Starforge - Matched;

        public int OnlyRekt => Rekt - Matched;
    }

    internal static Facts Starforge(ExecPassResult exec)
    {
        var f = new Facts();
        foreach (var s in exec.Statements.Where(s => s.Status == ExecStatus.Parsed))
        {
            var op = FirstWord(s.Operation);
            switch (s)
            {
                case CicsCommand c:
                    f.Add("cics-commands", new[] { op });
                    f.Add("cics-program", Targets(c, op, ExecResourceKind.Program));
                    f.Add("cics-transid", Targets(c, op, ExecResourceKind.TransId));
                    f.Add("cics-file", Targets(c, op, ExecResourceKind.File));
                    f.Add("cics-queue", c.Resources.Where(r => r.Kind is ExecResourceKind.TsQueue or ExecResourceKind.TdQueue)
                        .Select(r => $"{op} {(r.Kind == ExecResourceKind.TdQueue ? "TD" : "TS")} {r.Target.Key}"));
                    f.Add("cics-map", c.Resources.Where(r => r.Kind == ExecResourceKind.Map)
                        .Select(r => $"{op} {r.Target.Key}/{(r.QualifierImplied ? "-" : r.Qualifier?.Key ?? "-")}"));
                    f.Add("cics-label", c.Resources.Where(r => r.Kind == ExecResourceKind.Label).Select(r => $"{op} {r.Target.Key.TrimStart('&')}"));
                    // Paragraph names given to HANDLE are labels, not data.
                    var labels = c.Resources.Where(r => r.Kind == ExecResourceKind.Label).Select(r => r.Option).ToHashSet(StringComparer.Ordinal);
                    f.Add("cics-data", c.Options.Where(o => !labels.Contains(o.Name)).Select(o => o.Argument)
                        .Where(a => a?.DataName is not null).Select(a => $"{op} {a!.DataName!.ToUpperInvariant()}"));
                    break;
                case SqlStatement q:
                    f.Add("sql-statements", new[] { op });
                    f.Add("sql-tables", q.Tables.Select(t => $"{t.Operation.ToString().ToUpperInvariant()} {t.Name}"));
                    f.Add("sql-cursors", q.Cursor is null ? Array.Empty<string>() : new[] { $"{op} {q.Cursor.ToUpperInvariant()}" });
                    f.Add("sql-host-variables", q.HostVariables
                        .SelectMany(h => h.Indicator is null ? new[] { h.Name } : new[] { h.Name, h.Indicator })
                        .Select(n => $"{op} {n.ToUpperInvariant()}"));
                    break;
                case DliCommand d:
                    f.Add("dli-commands", new[] { op });
                    f.Add("dli-segments", d.Segments.Select(g => $"{op} {g.Name.Key}"));
                    break;
            }
        }

        return f;
    }

    private static IEnumerable<string> Targets(CicsCommand c, string op, ExecResourceKind kind)
        => c.Resources.Where(r => r.Kind == kind).Select(r => $"{op} {r.Target.Key}");

    internal static Facts Rekt(string depsPath)
    {
        var f = new Facts();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        if (!doc.RootElement.TryGetProperty("dependencyDialectStatements", out var statements) || statements.ValueKind != JsonValueKind.Array)
        {
            return f;
        }

        foreach (var s in statements.EnumerateArray())
        {
            var kind = Str(s, "kind");
            var op = Str(s, "operation")?.ToUpperInvariant() ?? "?";
            var refs = Items(s, "resourceReferences");
            var binds = Items(s, "resourceBindings");
            if (kind == "EXEC_CICS")
            {
                // REKT reports DFHRESP(x)/DFHVALUE(x) in-line conditions as EXEC CICS statements; they are not EXEC blocks.
                if (op is "DFHRESP" or "DFHVALUE")
                {
                    continue;
                }

                var tokens = Tokens(s.TryGetProperty("syntax", out var syn) ? syn : default);
                f.Add("cics-commands", new[] { op });
                f.Add("cics-program", binds.Where(b => Str(b, "kind") == "CICS_PROGRAM").Select(b => $"{op} {BindingTarget(b)}"));
                f.Add("cics-transid", binds.Where(b => Str(b, "kind") == "CICS_TRANSACTION").Select(b => $"{op} {BindingTarget(b)}"));
                f.Add("cics-file", binds.Where(b => Str(b, "kind") == "CICS_FILE").Select(b => $"{op} {BindingTarget(b)}"));
                f.Add("cics-queue", binds.Where(b => Str(b, "kind") is "CICS_TDQUEUE" or "CICS_TSQUEUE")
                    .Select(b => $"{op} {(Str(b, "kind") == "CICS_TDQUEUE" ? "TD" : "TS")} {BindingTarget(b)}"));

                // Map bindings carry resolved names only; the written operands come from REKT's own syntax tree.
                f.Add("cics-map", binds.Where(b => Str(b, "kind") == "CICS_MAP")
                    .Select(_ => $"{op} {OperandAfter(tokens, "MAP") ?? "?"}/{OperandAfter(tokens, "MAPSET") ?? "-"}"));
                f.Add("cics-label", refs.Where(r => Str(r, "kind") == "CICS_LABEL").Select(r => $"{op} {Str(r, "name")?.ToUpperInvariant() ?? "?"}"));
                // REKT splits data operands over CICS_DATA, CICS_CONTEXT (EIB fields) and the operands of data-flow-resolved
                // bindings; its names are concatenated tokens ("LENGTHOFX", "AOFB", "T(I)"), mapped back to the data name.
                f.Add("cics-data", refs.Where(r => Str(r, "kind") is "CICS_DATA" or "CICS_CONTEXT").Select(r => Str(r, "name") ?? "?")
                    .Concat(binds.Select(b => b.TryGetProperty("targetResolution", out var t) ? Str(t, "operand") : null).OfType<string>())
                    .Select(n => $"{op} {DataNameOf(tokens, n)}"));
            }
            else if (kind == "EXEC_SQL")
            {
                f.Add("sql-statements", new[] { op });
                f.Add("sql-tables", refs.Where(r => Str(r, "kind") == "SQL_RELATION")
                    .Select(r => $"{SqlTableOp(op, Str(r, "role"))} {Str(r, "name")?.ToUpperInvariant() ?? "?"}"));
                f.Add("sql-cursors", refs.Where(r => Str(r, "kind") == "SQL_CURSOR").Select(r => $"{op} {Str(r, "name")?.ToUpperInvariant() ?? "?"}"));
                f.Add("sql-host-variables", refs.Where(r => Str(r, "kind") == "SQL_HOST_VARIABLE").Select(r => $"{op} {Str(r, "name")?.ToUpperInvariant() ?? "?"}"));
            }
        }

        return f;
    }

    private static string SqlTableOp(string op, string? role) => op switch
    {
        "DECLARE" => role == "declaration" ? "DECLARE" : "SELECT",
        "FETCH" or "OPEN" or "VALUES" => "SELECT",
        _ => op,
    };

    private static string BindingTarget(JsonElement b)
    {
        var res = b.TryGetProperty("targetResolution", out var t) ? t : default;
        if (res.ValueKind == JsonValueKind.Object && Str(res, "operand") is { } operand)
        {
            return "&" + operand.ToUpperInvariant();
        }

        return Str(b, "name")?.ToUpperInvariant() ?? "?";
    }

    private static string? OperandAfter(IReadOnlyList<string> tokens, string option)
    {
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (string.Equals(tokens[i], option, StringComparison.OrdinalIgnoreCase) && tokens[i + 1] == "(")
            {
                var t = tokens[i + 2];
                return t.Length >= 2 && t[0] is '\'' or '"' ? t[1..^1].ToUpperInvariant() : "&" + t.ToUpperInvariant();
            }
        }

        return null;
    }

    private static string DataNameOf(IReadOnlyList<string> tokens, string name)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var joined = "";
            for (var j = i; j < tokens.Count && name.StartsWith(joined + tokens[j], StringComparison.OrdinalIgnoreCase); j++)
            {
                joined += tokens[j];
                if (joined.Length == name.Length)
                {
                    var first = tokens.Skip(i).Take(j - i + 1).FirstOrDefault(t => t.ToUpperInvariant() is not ("LENGTH" or "ADDRESS" or "OF"));
                    return (first ?? name).ToUpperInvariant();
                }
            }
        }

        return name.ToUpperInvariant();
    }

    private static List<string> Tokens(JsonElement node)
    {
        var list = new List<string>();
        void Walk(JsonElement n)
        {
            if (n.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (Str(n, "token") is { } t)
            {
                list.Add(t);
            }

            foreach (var c in Items(n, "children"))
            {
                Walk(c);
            }
        }

        Walk(node);
        return list;
    }

    private static Facts Empty() => new();

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<JsonElement> Items(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string FirstWord(string op) => op.Split(' ', 2)[0];

    private static IReadOnlyList<string> Minus(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var left = b.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var x in a)
        {
            if (left.TryGetValue(x, out var n) && n > 0)
            {
                left[x] = n - 1;
            }
            else
            {
                result.Add(x);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    internal static Dictionary<string, Total> Totals(IEnumerable<ProgramDiff> results)
    {
        var compared = results.Where(r => r.RektFound).ToList();
        return Categories.ToDictionary(
            c => c,
            c => new Total(
                compared.Sum(r => r.Starforge[c].Count),
                compared.Sum(r => r.Rekt[c].Count),
                compared.Sum(r => r.Starforge[c].Count - r.OnlyStarforge(c).Count)),
            StringComparer.Ordinal);
    }

    private static byte[] Json(IReadOnlyList<ProgramDiff> results)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("milestone", "M4");
            w.WriteNumber("programs", results.Count);
            w.WriteStartArray("rektMissing");
            foreach (var r in results.Where(r => !r.RektFound))
            {
                w.WriteStringValue(r.Program);
            }

            w.WriteEndArray();
            w.WriteStartObject("totals");
            foreach (var (c, t) in Totals(results))
            {
                w.WriteStartObject(c);
                w.WriteNumber("starforge", t.Starforge);
                w.WriteNumber("rekt", t.Rekt);
                w.WriteNumber("matched", t.Matched);
                w.WriteNumber("onlyStarforge", t.OnlyStarforge);
                w.WriteNumber("onlyRekt", t.OnlyRekt);
                w.WriteEndObject();
            }

            w.WriteEndObject();
            w.WriteStartArray("mismatches");
            foreach (var r in results.Where(r => r.RektFound))
            {
                foreach (var c in Categories)
                {
                    var s = r.OnlyStarforge(c);
                    var k = r.OnlyRekt(c);
                    if (s.Count == 0 && k.Count == 0)
                    {
                        continue;
                    }

                    w.WriteStartObject();
                    w.WriteString("program", r.Program);
                    w.WriteString("category", c);
                    WriteList(w, "onlyStarforge", s);
                    WriteList(w, "onlyRekt", k);
                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        w.WriteStartArray(name);
        foreach (var i in items)
        {
            w.WriteStringValue(i);
        }

        w.WriteEndArray();
    }

    private static string Markdown(IReadOnlyList<ProgramDiff> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Starforge M4 EXEC diff vs REKT");
        sb.AppendLine();
        sb.AppendLine($"Programs: {results.Count}; compared with a REKT deps.json: {results.Count(r => r.RektFound)}.");
        sb.AppendLine("Keys are `OPERATION target`; a literal target is its value, a data-name target is `&NAME` (REKT data-flow resolutions are mapped back to their operand).");
        sb.AppendLine("Triage of every mismatch class: `Parser/docs-local/diff-triage-exec.md`.");
        sb.AppendLine();
        sb.AppendLine("| Category | Starforge | REKT | Matched | Only Starforge | Only REKT |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (c, t) in Totals(results))
        {
            sb.AppendLine($"| {c} | {t.Starforge} | {t.Rekt} | {t.Matched} | {t.OnlyStarforge} | {t.OnlyRekt} |");
        }

        foreach (var c in Categories)
        {
            var rows = results.Where(r => r.RektFound)
                .Select(r => (r.Program, S: r.OnlyStarforge(c), K: r.OnlyRekt(c)))
                .Where(x => x.S.Count > 0 || x.K.Count > 0)
                .ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            sb.AppendLine();
            sb.AppendLine($"## {c}");
            sb.AppendLine();
            sb.AppendLine("| Program | Only Starforge | Only REKT |");
            sb.AppendLine("|---|---|---|");
            foreach (var (program, s, k) in rows)
            {
                sb.AppendLine($"| {program} | {Cell(s)} | {Cell(k)} |");
            }
        }

        return sb.ToString();
    }

    private static string Cell(IReadOnlyList<string> items)
        => string.Join("<br>", items.GroupBy(i => i, StringComparer.Ordinal).Select(g => (g.Count() > 1 ? $"{g.Count()}× " : "") + "`" + g.Key + "`"));
}
