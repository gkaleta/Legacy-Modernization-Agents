using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Emit;
using Starforge.Cobol.Exec;

namespace Starforge.Cobol.Cli;

internal sealed record ProgramSummary(
    string Program,
    string Estate,
    int PhysicalLines,
    int ExpandedPhysicalLines,
    int ExpandedCodeLines,
    int Units,
    int Tokens,
    IReadOnlyDictionary<CoverageClass, long> Characters,
    long Total,
    int Conflicts,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> ResolvedCopybooks,
    int Errors,
    int Warnings,
    IReadOnlyList<string> Fingerprints,
    int? PreprocessedLines,
    string? Exception,
    long ProcedureLexed = 0,
    IReadOnlyList<string>? ErrorNodes = null,
    int SyntaxPrograms = 0,
    int DataItems = 0,
    ExecTally? Exec = null)
{
    public long Count(CoverageClass cls) => Characters.TryGetValue(cls, out var n) ? n : 0;

    public IReadOnlyList<string> ErrorNodeFingerprints => ErrorNodes ?? Array.Empty<string>();

    // Code characters of IDENTIFICATION/ENVIRONMENT/DATA: parsed, rejected by the parser, or still only lexed (EXEC blocks).
    public long DivisionScope => Count(CoverageClass.Parsed) + SyntaxErrorCharacters + (Count(CoverageClass.Lexed) - ProcedureLexed);

    public long SyntaxErrorCharacters { get; init; }
}

internal static class CoverageCommand
{
    private static readonly string[] ProgramExtensions = { ".cbl", ".cob" };

    public static int Run(CommandLine cmd, TextWriter stdout, TextWriter stderr)
    {
        cmd.RejectUnknown("source", "out", "rekt-baseline", "rules");
        var roots = cmd.All("source").Select(s => Path.GetFullPath(s)).ToList();
        if (roots.Count == 0)
        {
            throw new UsageException("coverage needs at least one --source <estate-dir>");
        }

        foreach (var r in roots.Where(r => !Directory.Exists(r)))
        {
            stderr.WriteLine($"error: source directory not found: {r}");
            return 2;
        }

        var outDir = Path.GetFullPath(cmd.Required("out"));
        var baseline = cmd.Single("rekt-baseline") is { } b ? Path.GetFullPath(b) : null;
        var display = new PathDisplay(roots.Select(r => (r, Path.GetFileName(r))));
        var rules = CliApp.LoadRules(cmd);
        var discovered = roots
            .SelectMany(r => DiscoverPrograms(r).Select(p => (Root: r, Path: p)))
            .OrderBy(p => display.Show(p.Path), StringComparer.Ordinal)
            .ToList();
        var programs = discovered.Where(p => rules.NotProgram(display.Show(p.Path)) is null).ToList();

        Directory.CreateDirectory(outDir);
        var frontEnd = new CobolFrontEnd(new FrontEndOptions(roots, Rules: rules));
        var summaries = new ConcurrentBag<ProgramSummary>();
        var clock = Stopwatch.StartNew();
        Parallel.ForEach(programs, p => summaries.Add(ProcessOne(frontEnd, p.Root, p.Path, display, outDir)));
        clock.Stop();

        var ordered = summaries.OrderBy(s => s.Program, StringComparer.Ordinal).ToList();
        var rekt = baseline is null ? null : RektComparison.Build(baseline, ordered);
        WriteSummaryJson(Path.Combine(outDir, "summary.json"), ordered, rekt, rules,
            discovered.Select(p => display.Show(p.Path)).Where(p => rules.NotProgram(p) is not null).ToList());
        File.WriteAllText(Path.Combine(outDir, "summary.md"), SummaryMarkdown(ordered, rekt), new UTF8Encoding(false));
        if (rekt is not null)
        {
            File.WriteAllText(Path.Combine(outDir, "rekt-comparison.md"), rekt.Markdown(), new UTF8Encoding(false));
        }

        var exceptions = ordered.Count(s => s.Exception is not null);
        var unaccounted = ordered.Sum(s => s.Count(CoverageClass.Unaccounted));
        var conflicts = ordered.Sum(s => s.Conflicts);
        var total = ordered.Sum(s => s.Total);
        stdout.WriteLine($"programs={ordered.Count} exceptions={exceptions} unaccounted={unaccounted} conflicts={conflicts} " +
            $"parsed={Pct(ordered.Sum(s => s.Count(CoverageClass.Parsed)), total)} id-env-data-parsed={Pct(ordered.Sum(s => s.Count(CoverageClass.Parsed)), ordered.Sum(s => s.DivisionScope))} error-nodes={ordered.Sum(s => s.ErrorNodeFingerprints.Count)} " +
            $"lexed={Pct(ordered.Sum(s => s.Count(CoverageClass.Lexed)), total)} comment={Pct(ordered.Sum(s => s.Count(CoverageClass.Comment)), total)} " +
            $"unsupported={Pct(ordered.Sum(s => s.Count(CoverageClass.Unsupported)), total)} unresolved-copybooks={ordered.SelectMany(s => s.Unresolved).Distinct().Count()} " +
            $"elapsed={clock.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s" +
            (rules.IsEmpty ? string.Empty : $" excluded={discovered.Count - programs.Count} rules={rules.Sha256[..12]}"));
        stdout.WriteLine($"report: {Path.Combine(outDir, "summary.md")}");
        foreach (var s in ordered.Where(s => s.Exception is not null))
        {
            stderr.WriteLine($"{s.Program}: {s.Exception}");
        }

        return exceptions == 0 && unaccounted == 0 && conflicts == 0 ? 0 : 1;
    }

    internal static IEnumerable<string> DiscoverPrograms(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // Dot folders hold generated output (.preprocessed, .previous snapshots), never source programs.
                if (!Path.GetFileName(sub).StartsWith('.'))
                {
                    stack.Push(sub);
                }
            }

            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (ProgramExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                {
                    yield return f;
                }
            }
        }
    }

    private static ProgramSummary ProcessOne(CobolFrontEnd frontEnd, string root, string path, PathDisplay display, string outDir)
    {
        var name = display.Show(path);
        var estate = Path.GetFileName(root);
        try
        {
            var r = frontEnd.Process(path);
            var exec = ExecPass.Run(r);
            var target = Path.Combine(outDir, "programs", name + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var stream = File.Create(target))
            using (var writer = new Utf8JsonWriter(stream, JsonReport.WriterOptions))
            {
                JsonReport.WriteCoverage(writer, r, display, exec);
            }

            var diags = r.Diagnostics.Distinct().ToList();
            var errorNodes = r.Syntax.Errors.Concat(r.Syntax.AllPrograms().SelectMany(p => p.AllErrors())).Select(e => e.Diagnostic.Fingerprint).ToList();
            return new ProgramSummary(
                name,
                estate,
                r.PhysicalLines,
                r.ExpandedPhysicalLines,
                r.ExpandedCodeLines,
                r.Units.Count,
                r.Tokens.Count,
                r.Coverage.ProgramArea,
                r.Coverage.Total,
                r.Coverage.Conflicts,
                JsonReport.UnresolvedNames(r),
                r.Inclusions.Where(i => i.Status == CopyStatus.Resolved).Select(i => i.Name.ToUpperInvariant()).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList(),
                diags.Count(d => d.Severity == DiagnosticSeverity.Error),
                diags.Count(d => d.Severity == DiagnosticSeverity.Warning),
                diags.Where(d => d.Severity != DiagnosticSeverity.Info).Select(d => d.Fingerprint).ToList(),
                PreprocessedLineCount(root, path),
                null,
                r.CountInProcedureBodies(CoverageClass.Lexed),
                errorNodes,
                r.Programs.Count,
                r.Programs.Sum(p => p.Items.Count),
                ExecTally.From(exec))
            {
                SyntaxErrorCharacters = r.Coverage.ByReason.TryGetValue("Unsupported(syntax-error)", out var se) ? se : 0,
            };
        }
        catch (Exception ex)
        {
            return new ProgramSummary(name, estate, 0, 0, 0, 0, 0, new Dictionary<CoverageClass, long>(), 0, 0,
                Array.Empty<string>(), Array.Empty<string>(), 0, 0, Array.Empty<string>(), null, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // REKT parses <estate>/.preprocessed/<path>, so its line counts refer to that rewritten copy.
    private static int? PreprocessedLineCount(string root, string path)
    {
        var candidate = Path.Combine(root, CopybookResolver.PreprocessedFolder, Path.GetRelativePath(root, path));
        return File.Exists(candidate) ? Starforge.Cobol.Source.SourceFile.Load(candidate).Lines.Count : null;
    }

    private static void WriteSummaryJson(string path, IReadOnlyList<ProgramSummary> programs, RektComparison? rekt,
        Dialect.DialectRules rules, IReadOnlyList<string> excluded)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, JsonReport.WriterOptions);
        w.WriteStartObject();
        w.WriteString("milestone", "M3");
        if (!rules.IsEmpty)
        {
            w.WriteString("rulesSha256", rules.Sha256);
            w.WriteStartArray("excludedNotPrograms");
            foreach (var p in excluded)
            {
                w.WriteStringValue(p);
            }

            w.WriteEndArray();
        }

        WriteTotals(w, "totals", programs);
        w.WriteStartObject("estates");
        foreach (var g in programs.GroupBy(p => p.Estate).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            WriteTotals(w, g.Key, g.ToList());
        }

        w.WriteEndObject();

        w.WriteStartObject("unresolvedCopybooks");
        foreach (var g in programs.SelectMany(p => p.Unresolved.Select(u => (u, p.Program))).GroupBy(x => x.u).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            w.WriteStartArray(g.Key);
            foreach (var x in g.OrderBy(x => x.Program, StringComparer.Ordinal))
            {
                w.WriteStringValue(x.Program);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();

        w.WriteStartObject("fingerprints");
        foreach (var g in programs.SelectMany(p => p.Fingerprints).GroupBy(f => f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            w.WriteNumber(g.Key, g.Count());
        }

        w.WriteEndObject();

        w.WriteStartObject("errorNodes");
        foreach (var g in programs.SelectMany(p => p.ErrorNodeFingerprints.Select(f => (f, p.Program))).GroupBy(x => x.f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            w.WriteStartArray(g.Key);
            foreach (var prog in g.Select(x => x.Program).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                w.WriteStringValue(prog);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();

        w.WriteStartArray("programs");
        foreach (var p in programs)
        {
            w.WriteStartObject();
            w.WriteString("program", p.Program);
            if (p.Exception is not null)
            {
                w.WriteString("exception", p.Exception);
            }

            w.WriteNumber("physicalLines", p.PhysicalLines);
            w.WriteNumber("expandedPhysicalLines", p.ExpandedPhysicalLines);
            w.WriteNumber("expandedCodeLines", p.ExpandedCodeLines);
            w.WriteNumber("units", p.Units);
            w.WriteNumber("tokens", p.Tokens);
            w.WriteNumber("total", p.Total);
            w.WriteStartObject("characters");
            foreach (var cls in Enum.GetValues<CoverageClass>())
            {
                w.WriteNumber(cls.ToString(), p.Count(cls));
            }

            w.WriteEndObject();
            w.WriteNumber("conflicts", p.Conflicts);
            w.WriteNumber("divisionScope", p.DivisionScope);
            w.WriteNumber("divisionParsedPercent", PctNum(p.Count(CoverageClass.Parsed), p.DivisionScope));
            w.WriteNumber("syntaxPrograms", p.SyntaxPrograms);
            w.WriteNumber("dataItems", p.DataItems);
            w.WriteNumber("errorNodes", p.ErrorNodeFingerprints.Count);
            w.WriteNumber("errors", p.Errors);
            w.WriteNumber("warnings", p.Warnings);
            w.WriteStartArray("unresolved");
            foreach (var u in p.Unresolved)
            {
                w.WriteStringValue(u);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        ExecSummaryReport.WriteJson(w, programs);
        rekt?.WriteJson(w);
        w.WriteEndObject();
    }

    private static void WriteTotals(Utf8JsonWriter w, string name, IReadOnlyList<ProgramSummary> programs)
    {
        var total = programs.Sum(p => p.Total);
        w.WriteStartObject(name);
        w.WriteNumber("programs", programs.Count);
        w.WriteNumber("exceptions", programs.Count(p => p.Exception is not null));
        w.WriteNumber("physicalLines", programs.Sum(p => p.PhysicalLines));
        w.WriteNumber("expandedPhysicalLines", programs.Sum(p => p.ExpandedPhysicalLines));
        w.WriteNumber("total", total);
        foreach (var cls in Enum.GetValues<CoverageClass>())
        {
            var n = programs.Sum(p => p.Count(cls));
            w.WriteStartObject(cls.ToString());
            w.WriteNumber("characters", n);
            w.WriteNumber("percent", total == 0 ? 0 : Math.Round(100.0 * n / total, 3));
            w.WriteEndObject();
        }

        w.WriteNumber("conflicts", programs.Sum(p => p.Conflicts));
        var scope = programs.Sum(p => p.DivisionScope);
        w.WriteStartObject("identificationEnvironmentData");
        w.WriteNumber("characters", scope);
        w.WriteNumber("parsed", programs.Sum(p => p.Count(CoverageClass.Parsed)));
        w.WriteNumber("syntaxErrors", programs.Sum(p => p.SyntaxErrorCharacters));
        w.WriteNumber("parsedPercent", PctNum(programs.Sum(p => p.Count(CoverageClass.Parsed)), scope));
        w.WriteEndObject();
        w.WriteNumber("procedureBodyLexed", programs.Sum(p => p.ProcedureLexed));
        w.WriteNumber("errorNodes", programs.Sum(p => p.ErrorNodeFingerprints.Count));
        w.WriteNumber("dataItems", programs.Sum(p => p.DataItems));
        w.WriteNumber("unresolvedCopybooks", programs.SelectMany(p => p.Unresolved).Distinct().Count());
        w.WriteEndObject();
    }

    private static string SummaryMarkdown(IReadOnlyList<ProgramSummary> programs, RektComparison? rekt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Starforge M3 coverage");
        sb.AppendLine();
        sb.AppendLine("Non-blank program-area characters (fixed format columns 8-72) of each program and every copybook instance it expands.");
        sb.AppendLine("`Parsed` is consumed by the division parsers (IDENTIFICATION/ENVIRONMENT/DATA/PROCEDURE) or the EXEC parsers; `Lexed` is tokenised but claimed by no parser (expected 0). Statements the parser cannot handle are error nodes and count as `Unsupported`.");
        sb.AppendLine("`ID+ENV+DATA parsed %` = Parsed / (Parsed + parser-rejected + Lexed outside PROCEDURE DIVISION bodies).");
        sb.AppendLine();
        sb.AppendLine("| Estate | Programs | Exceptions | Total chars | Parsed % | ID+ENV+DATA parsed % | Error nodes | Lexed % | Comment % | Directive % | Unsupported % | Unresolved % | Skipped % | Unaccounted | Conflicts | Unresolved copybooks |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in programs.GroupBy(p => p.Estate).OrderBy(g => g.Key, StringComparer.Ordinal).Append(programs.GroupBy(_ => "**all**").Single()))
        {
            var list = g.ToList();
            var total = list.Sum(p => p.Total);
            string P(CoverageClass c) => Pct(list.Sum(p => p.Count(c)), total);
            sb.AppendLine($"| {g.Key} | {list.Count} | {list.Count(p => p.Exception is not null)} | {total} | {P(CoverageClass.Parsed)} | {Pct(list.Sum(p => p.Count(CoverageClass.Parsed)), list.Sum(p => p.DivisionScope))} | {list.Sum(p => p.ErrorNodeFingerprints.Count)} | {P(CoverageClass.Lexed)} | {P(CoverageClass.Comment)} | {P(CoverageClass.Directive)} | {P(CoverageClass.Unsupported)} | {P(CoverageClass.Unresolved)} | {P(CoverageClass.Skipped)} | {list.Sum(p => p.Count(CoverageClass.Unaccounted))} | {list.Sum(p => p.Conflicts)} | {list.SelectMany(p => p.Unresolved).Distinct().Count()} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Unresolved copybooks");
        sb.AppendLine();
        var unresolved = programs.SelectMany(p => p.Unresolved.Select(u => (u, p.Program))).GroupBy(x => x.u).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (unresolved.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var g in unresolved)
        {
            sb.AppendLine($"- `{g.Key}`: {string.Join(", ", g.Select(x => x.Program).OrderBy(x => x, StringComparer.Ordinal))}");
        }

        sb.AppendLine();
        sb.AppendLine("## Top diagnostic fingerprints (errors and warnings)");
        sb.AppendLine();
        var fps = programs.SelectMany(p => p.Fingerprints).GroupBy(f => f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(20).ToList();
        if (fps.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var g in fps)
        {
            sb.AppendLine($"- {g.Count()} × `{g.Key}`");
        }

        sb.AppendLine();
        sb.AppendLine("## Error nodes by fingerprint");
        sb.AppendLine();
        var errorNodes = programs.SelectMany(p => p.ErrorNodeFingerprints.Select(f => (f, p.Program))).GroupBy(x => x.f).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (errorNodes.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var g in errorNodes)
        {
            sb.AppendLine($"- {g.Count()} × `{g.Key}`: {string.Join(", ", g.Select(x => x.Program).Distinct().OrderBy(x => x, StringComparer.Ordinal))}");
        }

        sb.AppendLine();
        sb.AppendLine("## Programs");
        sb.AppendLine();
        sb.AppendLine("| Program | Physical lines | Expanded lines | Units | Tokens | Total chars | Parsed % | ID+ENV+DATA parsed % | Data items | Error nodes | Lexed % | Comment % | Unsupported % | Unaccounted | Errors | Unresolved |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var p in programs)
        {
            if (p.Exception is not null)
            {
                sb.AppendLine($"| {p.Program} | EXCEPTION: {p.Exception.Split('\n')[0]} |||||||||||||||");
                continue;
            }

            sb.AppendLine($"| {p.Program} | {p.PhysicalLines} | {p.ExpandedPhysicalLines} | {p.Units} | {p.Tokens} | {p.Total} | {Pct(p.Count(CoverageClass.Parsed), p.Total)} | {Pct(p.Count(CoverageClass.Parsed), p.DivisionScope)} | {p.DataItems} | {p.ErrorNodeFingerprints.Count} | {Pct(p.Count(CoverageClass.Lexed), p.Total)} | {Pct(p.Count(CoverageClass.Comment), p.Total)} | {Pct(p.Count(CoverageClass.Unsupported), p.Total)} | {p.Count(CoverageClass.Unaccounted)} | {p.Errors} | {string.Join(", ", p.Unresolved)} |");
        }

        sb.Append(ExecSummaryReport.Markdown(programs));
        if (rekt is not null)
        {
            sb.AppendLine();
            sb.AppendLine("See `rekt-comparison.md` for the comparison with the REKT baseline.");
        }

        return sb.ToString();
    }

    private static double PctNum(long n, long total) => total == 0 ? 0 : Math.Round(100.0 * n / total, 3);

    internal static string Pct(long n, long total) => total == 0 ? "0.00" : (100.0 * n / total).ToString("F2", CultureInfo.InvariantCulture);
}
