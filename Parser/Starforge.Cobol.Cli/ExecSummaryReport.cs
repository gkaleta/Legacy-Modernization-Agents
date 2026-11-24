using System.Text;
using System.Text.Json;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Exec;

namespace Starforge.Cobol.Cli;

// What the coverage summary keeps of one program's EXEC pass.
internal sealed record ExecTally(IReadOnlyList<(string Kind, string Operation, bool Parsed, string? Reason)> Blocks, IReadOnlyList<string> UnsupportedFingerprints)
{
    public static ExecTally From(ExecPassResult exec) => new(
        exec.Statements.Select(s => (ExecJson.KindName(s.Kind), s.Operation, s.Status == ExecStatus.Parsed, s.UnsupportedReason)).ToList(),
        exec.Statements
            .Where(s => s.Status == ExecStatus.Unsupported)
            .Select(s => s.Diagnostics.FirstOrDefault(d => d.Code == s.UnsupportedReason)?.Fingerprint ?? s.UnsupportedReason ?? ExecPass.PendingReason)
            .ToList());
}

internal static class ExecSummaryReport
{
    public static void WriteJson(Utf8JsonWriter w, IReadOnlyList<ProgramSummary> programs)
    {
        w.WriteStartObject("exec");
        foreach (var g in Estates(programs))
        {
            var list = g.ToList();
            var blocks = list.SelectMany(p => p.Exec?.Blocks ?? Array.Empty<(string, string, bool, string?)>()).ToList();
            w.WriteStartObject(g.Key);
            w.WriteNumber("total", blocks.Count);
            w.WriteNumber("parsed", blocks.Count(b => b.Parsed));
            w.WriteNumber("unsupported", blocks.Count(b => !b.Parsed));
            foreach (var k in blocks.GroupBy(b => b.Kind).OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                w.WriteStartObject(k.Key);
                w.WriteNumber("total", k.Count());
                w.WriteNumber("parsed", k.Count(b => b.Parsed));
                w.WriteNumber("unsupported", k.Count(b => !b.Parsed));
                w.WriteEndObject();
            }

            w.WriteStartObject("unsupportedFingerprints");
            foreach (var f in Fingerprints(list))
            {
                w.WriteNumber(f.Key, f.Count());
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    public static string Markdown(IReadOnlyList<ProgramSummary> programs)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## EXEC blocks (M4)");
        sb.AppendLine();
        sb.AppendLine("| Estate | Kind | Blocks | Parsed | Unsupported |");
        sb.AppendLine("|---|---|---:|---:|---:|");
        foreach (var g in Estates(programs))
        {
            var blocks = g.SelectMany(p => p.Exec?.Blocks ?? Array.Empty<(string, string, bool, string?)>()).ToList();
            foreach (var k in blocks.GroupBy(b => b.Kind).OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"| {g.Key} | {k.Key} | {k.Count()} | {k.Count(b => b.Parsed)} | {k.Count(b => !b.Parsed)} |");
            }

            sb.AppendLine($"| {g.Key} | **all** | {blocks.Count} | {blocks.Count(b => b.Parsed)} | {blocks.Count(b => !b.Parsed)} |");
        }

        sb.AppendLine();
        sb.AppendLine("| Estate | Parsed % | Unsupported % |");
        sb.AppendLine("|---|---:|---:|");
        foreach (var g in Estates(programs))
        {
            var total = g.Sum(p => p.Total);
            sb.AppendLine($"| {g.Key} | {CoverageCommand.Pct(g.Sum(p => p.Count(CoverageClass.Parsed)), total)} | {CoverageCommand.Pct(g.Sum(p => p.Count(CoverageClass.Unsupported)), total)} |");
        }

        sb.AppendLine();
        sb.AppendLine("### Unsupported EXEC fingerprints");
        sb.AppendLine();
        var all = Fingerprints(programs).ToList();
        if (all.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var f in all)
        {
            sb.AppendLine($"- {f.Count()} × `{f.Key}`");
        }

        return sb.ToString();
    }

    private static IEnumerable<IGrouping<string, ProgramSummary>> Estates(IReadOnlyList<ProgramSummary> programs)
        => programs.GroupBy(p => p.Estate).OrderBy(g => g.Key, StringComparer.Ordinal).Concat(programs.GroupBy(_ => "all"));

    private static IEnumerable<IGrouping<string, string>> Fingerprints(IEnumerable<ProgramSummary> programs)
        => programs.SelectMany(p => p.Exec?.UnsupportedFingerprints ?? Array.Empty<string>())
            .GroupBy(f => f)
            .OrderByDescending(f => f.Count())
            .ThenBy(f => f.Key, StringComparer.Ordinal);
}
