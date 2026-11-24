using System.Text;
using System.Text.Json;

namespace Starforge.Cobol.Cli;

internal sealed record RektProgram(
    string Program,
    bool Found,
    int? RektLoc,
    int PhysicalLines,
    int? PreprocessedLines,
    int ExpandedPhysicalLines,
    IReadOnlyList<string> RektCopybooks,
    IReadOnlyList<string> StarforgeCopybooks,
    IReadOnlyList<string> RektUnresolved,
    IReadOnlyList<string> StarforgeUnresolved)
{
    public IEnumerable<string> OnlyStarforge => StarforgeCopybooks.Except(RektCopybooks, StringComparer.Ordinal);

    public IEnumerable<string> OnlyRekt => RektCopybooks.Except(StarforgeCopybooks, StringComparer.Ordinal);
}

// Reads the REKT baseline layout <baseline>/<estate>/<relpath>.facts.json and .report/dependency-evidence.json.
internal sealed class RektComparison
{
    private RektComparison(IReadOnlyList<RektProgram> programs, IReadOnlyList<string> missingCopybooks, bool missingListPresent)
    {
        Programs = programs;
        RektMissingCopybooks = missingCopybooks;
        MissingListPresent = missingListPresent;
    }

    public IReadOnlyList<RektProgram> Programs { get; }

    public IReadOnlyList<string> RektMissingCopybooks { get; }

    public bool MissingListPresent { get; }

    public static RektComparison Build(string baseline, IReadOnlyList<ProgramSummary> programs)
    {
        var missingFile = Path.Combine(baseline, "missing-copybooks.txt");
        var missing = File.Exists(missingFile)
            ? File.ReadAllLines(missingFile).Select(l => l.Trim()).Where(l => l.Length > 0).Select(l => l.ToUpperInvariant()).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToList()
            : new List<string>();
        var list = new List<RektProgram>();
        foreach (var p in programs.Where(p => p.Exception is null))
        {
            var facts = Path.Combine(baseline, p.Program + ".facts.json");
            var evidence = Path.Combine(baseline, p.Program + ".report", "dependency-evidence.json");
            int? loc = null;
            var copies = new List<string>();
            var unresolved = new List<string>();
            if (File.Exists(facts))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(facts));
                if (doc.RootElement.TryGetProperty("summary", out var summary) && summary.TryGetProperty("loc", out var l) && l.TryGetInt32(out var v))
                {
                    loc = v;
                }
            }

            if (File.Exists(evidence))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(evidence));
                if (doc.RootElement.TryGetProperty("definitions", out var defs) && defs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in defs.EnumerateArray())
                    {
                        if (d.TryGetProperty("kind", out var k) && k.GetString() == "COPY" && d.TryGetProperty("name", out var n) && n.GetString() is { } name)
                        {
                            copies.Add(name.ToUpperInvariant());
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("unresolvedDiagnostics", out var un) && un.ValueKind == JsonValueKind.Array)
                {
                    unresolved.AddRange(un.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()));
                }
            }

            list.Add(new RektProgram(
                p.Program,
                File.Exists(facts) || File.Exists(evidence),
                loc,
                p.PhysicalLines,
                p.PreprocessedLines,
                p.ExpandedPhysicalLines,
                copies.Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList(),
                p.ResolvedCopybooks,
                unresolved,
                p.Unresolved));
        }

        return new RektComparison(list, missing, File.Exists(missingFile));
    }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject("rektComparison");
        w.WriteNumber("programsInBaseline", Programs.Count(p => p.Found));
        w.WriteNumber("locMatches", Programs.Count(p => p.RektLoc == p.PhysicalLines));
        w.WriteNumber("locDiffers", Programs.Count(p => p.RektLoc is not null && p.RektLoc != p.PhysicalLines));
        w.WriteNumber("locMatchesPreprocessedCopy", Programs.Count(p => p.RektLoc is not null && p.RektLoc == p.PreprocessedLines));
        w.WriteStartArray("rektMissingCopybooks");
        foreach (var m in RektMissingCopybooks)
        {
            w.WriteStringValue(m);
        }

        w.WriteEndArray();
        w.WriteStartArray("programs");
        foreach (var p in Programs)
        {
            w.WriteStartObject();
            w.WriteString("program", p.Program);
            w.WriteBoolean("inBaseline", p.Found);
            if (p.RektLoc is { } loc)
            {
                w.WriteNumber("rektLoc", loc);
            }

            w.WriteNumber("physicalLines", p.PhysicalLines);
            if (p.PreprocessedLines is { } pre)
            {
                w.WriteNumber("preprocessedCopyLines", pre);
            }

            w.WriteNumber("expandedPhysicalLines", p.ExpandedPhysicalLines);
            WriteArray(w, "copybooksOnlyStarforge", p.OnlyStarforge);
            WriteArray(w, "copybooksOnlyRekt", p.OnlyRekt);
            WriteArray(w, "rektUnresolved", p.RektUnresolved);
            WriteArray(w, "starforgeUnresolved", p.StarforgeUnresolved);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public string Markdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Starforge M1 vs REKT baseline");
        sb.AppendLine();
        var inBaseline = Programs.Where(p => p.Found).ToList();
        sb.AppendLine($"- Programs processed: {Programs.Count}; present in REKT baseline: {inBaseline.Count}.");
        sb.AppendLine($"- `summary.loc` equals Starforge physical line count: {inBaseline.Count(p => p.RektLoc == p.PhysicalLines)}; differs: {inBaseline.Count(p => p.RektLoc is not null && p.RektLoc != p.PhysicalLines)}.");
        sb.AppendLine($"- `summary.loc` equals the line count of REKT's own `.preprocessed/` copy of the program: {inBaseline.Count(p => p.RektLoc is not null && p.RektLoc == p.PreprocessedLines)}.");
        sb.AppendLine($"- REKT `missing-copybooks.txt`: {(MissingListPresent ? (RektMissingCopybooks.Count == 0 ? "present and empty" : string.Join(", ", RektMissingCopybooks)) : "not present")}.");
        sb.AppendLine($"- REKT `unresolvedDiagnostics` entries: {inBaseline.Sum(p => p.RektUnresolved.Count)}; Starforge unresolved inclusions: {Programs.Sum(p => p.StarforgeUnresolved.Count)}.");
        sb.AppendLine();
        sb.AppendLine("## Unresolved copybooks");
        sb.AppendLine();
        var unresolved = Programs.SelectMany(p => p.StarforgeUnresolved.Select(u => (u, p))).GroupBy(x => x.u).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (unresolved.Count == 0)
        {
            sb.AppendLine("Starforge resolved every COPY and EXEC SQL INCLUDE.");
        }

        foreach (var g in unresolved)
        {
            var name = g.Key[(g.Key.IndexOf(':') + 1)..];
            var rektHad = g.Where(x => x.p.RektCopybooks.Contains(name, StringComparer.Ordinal)).Select(x => x.p.Program).ToList();
            var rektMissing = RektMissingCopybooks.Contains(name, StringComparer.Ordinal);
            sb.AppendLine($"- `{g.Key}` in {string.Join(", ", g.Select(x => x.p.Program))}: REKT {(rektMissing ? "also listed it as missing" : rektHad.Count > 0 ? "resolved it (" + string.Join(", ", rektHad) + ")" : "did not report it as COPY or missing")}.");
        }

        sb.AppendLine();
        sb.AppendLine("## Line counts");
        sb.AppendLine();
        sb.AppendLine("REKT `summary.loc` counts the lines of the rewritten copy REKT parses (`<estate>/.preprocessed/<path>`), not the original file:");
        sb.AppendLine("its pre-processor folds `EXEC SQL INCLUDE x END-EXEC` into one `COPY x.` line, drops some debugging lines and inserts");
        sb.AppendLine("`REKT-OPAQUE-*` lines for conditions it cannot parse. Starforge counts the original program (`physical`) and the");
        sb.AppendLine("program plus every copybook instance (`expanded`); neither is forced to match REKT.");
        sb.AppendLine();
        sb.AppendLine("Copybook sets: REKT's `dependency-evidence.json` lists COPY definitions for the program's whole call closure and omits");
        sb.AppendLine("CICS-supplied members it treats as built-in (DFHAID, DFHBMSCA); Starforge lists only what this program's text includes.");
        sb.AppendLine();
        sb.AppendLine("| Program | REKT loc | Starforge physical | REKT preprocessed copy | Starforge expanded | Copybooks only Starforge | Copybooks only REKT |");
        sb.AppendLine("|---|---:|---:|---:|---:|---|---|");
        foreach (var p in Programs)
        {
            sb.AppendLine($"| {p.Program} | {(p.RektLoc?.ToString() ?? "n/a")} | {p.PhysicalLines} | {(p.PreprocessedLines?.ToString() ?? "n/a")} | {p.ExpandedPhysicalLines} | {string.Join(", ", p.OnlyStarforge)} | {string.Join(", ", p.OnlyRekt)} |");
        }

        return sb.ToString();
    }

    private static void WriteArray(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }
}
