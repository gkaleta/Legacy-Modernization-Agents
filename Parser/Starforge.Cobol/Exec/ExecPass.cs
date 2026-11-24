using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;

namespace Starforge.Cobol.Exec;

public sealed record ExecPassResult(IReadOnlyList<ExecStatement> Statements, IReadOnlyList<Diagnostic> Diagnostics)
{
    public int Parsed => Statements.Count(s => s.Status == ExecStatus.Parsed);

    public int Unsupported => Statements.Count(s => s.Status == ExecStatus.Unsupported);
}

// Standalone stand-in for the M3 statement parser: parses every EXEC block of a compilation, moves the characters of
// understood blocks from Unsupported to Parsed, and re-labels the rest with the precise reason. Run once per result.
public static class ExecPass
{
    public const string PendingReason = "exec-not-yet-parsed";

    public const string SqlCommentReason = "sql-comment";

    public static ExecPassResult Run(CompilationResult result)
    {
        var tokens = result.Tokens;
        var statements = new List<ExecStatement>();
        var diagnostics = new List<Diagnostic>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind != TokenKind.ExecBlock)
            {
                continue;
            }

            var statement = ExecParser.Parse(tokens, i);
            statements.Add(statement);
            diagnostics.AddRange(statement.Diagnostics);
            Account(result, token, statement);
        }

        result.AddDiagnostics(diagnostics);
        result.RebuildCoverage();
        return new ExecPassResult(statements, diagnostics);
    }

    private static void Account(CompilationResult result, Token token, ExecStatement statement)
    {
        if (statement.Status == ExecStatus.Parsed)
        {
            foreach (var c in statement.Comments)
            {
                Reclassify(result, token.Start + c.Start, token.Start + c.End, CoverageClass.Comment, SqlCommentReason);
            }

            Reclassify(result, token.Start, token.End, CoverageClass.Parsed, string.Empty);
        }
        else
        {
            Reclassify(result, token.Start, token.End, CoverageClass.Unsupported, statement.UnsupportedReason ?? PendingReason);
        }
    }

    // Same segment walk as TokenAccounting.MarkExpanded, so exactly the characters it claimed are moved.
    private static void Reclassify(CompilationResult result, int start, int end, CoverageClass to, string reason)
    {
        var text = result.Expanded.Text;
        var segIndex = text.FindSegment(start);
        var pos = start;
        while (segIndex >= 0 && segIndex < text.Segments.Count && pos < end)
        {
            var seg = text.SegmentAt(segIndex);
            var pieceEnd = Math.Min(end, seg.End);
            if (!seg.IsSynthetic && !seg.IsFromReplacement && pieceEnd > pos)
            {
                var srcStart = seg.SourceOffset + (pos - seg.Start);
                result.Accounting.Reclassify(seg.Unit, srcStart, srcStart + (pieceEnd - pos), CoverageClass.Unsupported, PendingReason, to, reason);
            }

            pos = Math.Max(pos, pieceEnd);
            segIndex++;
        }
    }
}
