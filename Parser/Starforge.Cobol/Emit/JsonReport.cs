using System.Text.Encodings.Web;
using System.Text.Json;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Emit;

// Deterministic output: no timestamps, absolute paths shown relative to a display root with '/' separators.
public sealed class PathDisplay
{
    private readonly IReadOnlyList<(string Root, string Label)> _roots;

    public PathDisplay(IEnumerable<(string Root, string Label)> roots)
    {
        _roots = roots
            .Select(r => (Path.GetFullPath(r.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, r.Label))
            .OrderByDescending(r => r.Item1.Length)
            .ToList();
    }

    public string Show(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var full = Path.GetFullPath(path);
        foreach (var (root, label) in _roots)
        {
            if (full.StartsWith(root, StringComparison.Ordinal))
            {
                var rel = full[root.Length..].Replace(Path.DirectorySeparatorChar, '/');
                return label.Length == 0 ? rel : label + "/" + rel;
            }
        }

        return full.Replace(Path.DirectorySeparatorChar, '/');
    }

    public string Show(SourceRange range) => $"{Show(range.File)}:{range.StartLine}:{range.StartColumn}-{range.EndLine}:{range.EndColumn}";
}

public static class JsonReport
{
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void WriteCoverage(Utf8JsonWriter w, CompilationResult r, PathDisplay paths, ExecPassResult? exec = null)
    {
        var cov = r.Coverage;
        w.WriteStartObject();
        w.WriteString("program", paths.Show(r.Program.Path));
        w.WriteString("format", r.Program.Source.InitialFormat.ToString());
        w.WriteString("encoding", r.Program.Source.File.Encoding);
        w.WriteBoolean("debuggingMode", r.DebuggingMode);
        w.WriteBoolean("decimalPointIsComma", r.DecimalPointIsComma);
        w.WriteStartObject("lines");
        w.WriteNumber("physical", r.PhysicalLines);
        w.WriteNumber("expandedPhysical", r.ExpandedPhysicalLines);
        w.WriteNumber("expandedCode", r.ExpandedCodeLines);
        w.WriteEndObject();
        w.WriteNumber("units", r.Units.Count);
        w.WriteNumber("tokens", r.Tokens.Count);
        WriteCoverageSummary(w, cov);
        WriteUnresolved(w, r);

        w.WriteStartArray("inclusions");
        foreach (var inc in r.Inclusions)
        {
            w.WriteStartObject();
            w.WriteString("kind", inc.Kind.ToString());
            w.WriteString("name", inc.Name);
            if (inc.Library is not null)
            {
                w.WriteString("library", inc.Library);
            }

            if (inc.Suppress)
            {
                w.WriteBoolean("suppress", true);
            }

            if (inc.ReplacingPairs > 0)
            {
                w.WriteNumber("replacingPairs", inc.ReplacingPairs);
            }

            w.WriteNumber("parentUnit", inc.ParentUnitId);
            w.WriteNumber("unit", inc.UnitId);
            w.WriteString("at", paths.Show(inc.StatementRange));
            w.WriteString("status", inc.Status.ToString());
            if (inc.ResolvedPath is not null)
            {
                w.WriteString("resolved", paths.Show(inc.ResolvedPath));
                w.WriteString("stage", inc.SearchStage);
            }

            if (inc.OtherCandidates.Count > 0)
            {
                w.WriteStartArray("otherCandidates");
                foreach (var c in inc.OtherCandidates)
                {
                    w.WriteStringValue(paths.Show(c));
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartArray("diagnostics");
        foreach (var d in DistinctDiagnostics(r.Diagnostics))
        {
            w.WriteStartObject();
            w.WriteString("code", d.Code);
            w.WriteString("severity", d.Severity.ToString());
            w.WriteString("fingerprint", d.Fingerprint);
            w.WriteString("message", d.Message);
            if (d.Range is { } range)
            {
                w.WriteString("range", paths.Show(range));
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();

        w.WriteStartArray("ranges");
        foreach (var e in cov.Ranges)
        {
            w.WriteStartObject();
            w.WriteString("class", e.Class.ToString());
            w.WriteString("reason", e.Reason);
            w.WriteString("range", paths.Show(e.Range));
            w.WriteNumber("characters", e.Characters);
            if (e.Occurrences > 1)
            {
                w.WriteNumber("occurrences", e.Occurrences);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        if (exec is not null)
        {
            ExecJson.Write(w, exec, paths);
        }

        w.WriteEndObject();
    }

    public static void WriteTokens(Utf8JsonWriter w, CompilationResult r, PathDisplay paths)
    {
        w.WriteStartObject();
        w.WriteString("program", paths.Show(r.Program.Path));
        w.WriteNumber("tokenCount", r.Tokens.Count);
        WriteCoverageSummary(w, r.Coverage);
        WriteUnresolved(w, r);
        w.WriteStartArray("tokens");
        var tokens = r.Tokens;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var origin = tokens.Origin(t);
            w.WriteStartObject();
            w.WriteString("kind", t.Kind.ToString());
            w.WriteString("text", tokens.Text(t));
            if (t.Kind == TokenKind.Figurative)
            {
                w.WriteString("figurative", t.Figurative.ToString());
            }
            else if (t.Kind == TokenKind.ExecBlock)
            {
                w.WriteString("exec", t.Exec.ToString());
            }
            else if (t.Kind == TokenKind.LevelNumber)
            {
                w.WriteNumber("level", t.Detail);
            }

            w.WriteString("origin", paths.Show(origin.Range));
            if (origin.FromReplacement)
            {
                w.WriteBoolean("fromReplacement", true);
                if (origin.ReplacementRange is { } rr)
                {
                    w.WriteString("replacementText", paths.Show(rr));
                }
            }

            if ((t.Flags & TokenFlags.Unterminated) != 0)
            {
                w.WriteBoolean("unterminated", true);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    public static void WriteCoverageSummary(Utf8JsonWriter w, CoverageReport cov)
    {
        w.WriteStartObject("coverage");
        w.WriteNumber("total", cov.Total);
        w.WriteStartObject("characters");
        foreach (var cls in Enum.GetValues<CoverageClass>())
        {
            w.WriteNumber(cls.ToString(), cov.Count(cls));
        }

        w.WriteEndObject();
        w.WriteStartObject("percent");
        foreach (var cls in Enum.GetValues<CoverageClass>())
        {
            w.WriteNumber(cls.ToString(), Math.Round(cov.Percent(cls), 3));
        }

        w.WriteEndObject();
        w.WriteStartObject("byReason");
        foreach (var (k, v) in cov.ByReason.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            w.WriteNumber(k, v);
        }

        w.WriteEndObject();
        w.WriteStartObject("outsideProgramArea");
        w.WriteNumber("sequence", cov.SequenceArea);
        w.WriteNumber("indicator", cov.IndicatorArea);
        w.WriteNumber("identification", cov.IdentificationArea);
        w.WriteEndObject();
        w.WriteNumber("conflicts", cov.Conflicts);
        w.WriteEndObject();
    }

    public static IReadOnlyList<string> UnresolvedNames(CompilationResult r)
        => r.Inclusions
            .Where(i => i.Status is CopyStatus.Missing or CopyStatus.Cycle or CopyStatus.DepthExceeded or CopyStatus.ReadError)
            .Select(i => $"{StatusCode(i.Status)}:{i.Name.ToUpperInvariant()}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

    private static void WriteUnresolved(Utf8JsonWriter w, CompilationResult r)
    {
        w.WriteStartArray("unresolved");
        foreach (var u in UnresolvedNames(r))
        {
            w.WriteStringValue(u);
        }

        w.WriteEndArray();
    }

    private static string StatusCode(CopyStatus status) => status switch
    {
        CopyStatus.Missing => "missing-copybook",
        CopyStatus.Cycle => "copy-cycle",
        CopyStatus.DepthExceeded => "copy-depth-exceeded",
        _ => "copybook-read-error",
    };

    // A copybook included several times reports its own reader diagnostics once per instance; show each once.
    private static IEnumerable<Diagnostic> DistinctDiagnostics(IEnumerable<Diagnostic> diagnostics)
        => diagnostics
            .Distinct()
            .OrderBy(d => d.Range?.File ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Range?.StartLine ?? 0)
            .ThenBy(d => d.Range?.StartColumn ?? 0)
            .ThenBy(d => d.Code, StringComparer.Ordinal);
}
