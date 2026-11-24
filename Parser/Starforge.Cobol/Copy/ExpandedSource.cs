using Starforge.Cobol.Source;

namespace Starforge.Cobol.Copy;

public readonly record struct TextOrigin(SourceRange Range, int Unit, bool FromReplacement, SourceRange? ReplacementRange);

// The compilation unit after COPY and REPLACE processing, with a way back to original files.
public sealed class ExpandedSource
{
    internal ExpandedSource(OriginText text, IReadOnlyList<SourceUnit> units)
    {
        Text = text;
        Units = units;
    }

    public OriginText Text { get; }

    public IReadOnlyList<SourceUnit> Units { get; }

    public string Content => Text.Text;

    public TextOrigin OriginOf(int start, int endExclusive)
    {
        if (endExclusive <= start)
        {
            endExclusive = start + 1;
        }

        var segs = Text.Segments;
        if (segs.Count == 0)
        {
            return new TextOrigin(new SourceRange(Units.Count > 0 ? Units[0].Path : string.Empty, 1, 1, 1, 1), 0, false, null);
        }

        var first = Text.FindSegment(Math.Min(start, Text.Length - 1));
        var last = Text.FindSegment(Math.Min(endExclusive - 1, Text.Length - 1));
        while (first < last && segs[first].IsSynthetic)
        {
            first++;
        }

        while (last > first && segs[last].IsSynthetic)
        {
            last--;
        }

        // A range made only of synthetic text borrows the nearest real character before it.
        while (first > 0 && segs[first].IsSynthetic)
        {
            first--;
            last = first;
        }

        var fs = segs[first];
        if (fs.IsSynthetic)
        {
            return new TextOrigin(new SourceRange(Units[0].Path, 1, 1, 1, 1), 0, false, null);
        }

        while (last > first && (segs[last].IsSynthetic || segs[last].Unit != fs.Unit))
        {
            last--;
        }

        var ls = segs[last];
        var unit = Units[fs.Unit].Source;
        var startLoc = Locate(unit, fs, Math.Max(start, fs.Start), atStart: true);
        var endLoc = Locate(unit, ls, Math.Min(endExclusive - 1, ls.End - 1), atStart: false);
        if (endLoc.Line < startLoc.Line || (endLoc.Line == startLoc.Line && endLoc.Column < startLoc.Column))
        {
            endLoc = startLoc;
        }

        var fromReplacement = false;
        SourceRange? replacementRange = null;
        for (var i = first; i <= Text.FindSegment(Math.Min(endExclusive - 1, Text.Length - 1)); i++)
        {
            if (segs[i].IsFromReplacement)
            {
                fromReplacement = true;
                if (replacementRange is null && segs[i].ReplacementUnit >= 0)
                {
                    var r = segs[i];
                    var from = Math.Max(start, r.Start) - r.Start;
                    var to = Math.Min(endExclusive, r.End) - r.Start;
                    replacementRange = Units[r.ReplacementUnit].Source.RangeOf(r.ReplacementOffset + from, r.ReplacementOffset + to);
                }
            }
        }

        return new TextOrigin(SourceRange.At(startLoc, endLoc), fs.Unit, fromReplacement, replacementRange);
    }

    private static SourceLocation Locate(LogicalSource source, TextSegment seg, int offset, bool atStart)
    {
        if (seg.IsFromReplacement)
        {
            var anchor = atStart ? seg.SourceOffset : seg.SourceOffset + Math.Max(0, seg.AnchorLength - 1);
            return source.LocateNearest(anchor, preferForward: atStart);
        }

        return source.LocateNearest(seg.SourceOffset + (offset - seg.Start), preferForward: atStart);
    }
}
