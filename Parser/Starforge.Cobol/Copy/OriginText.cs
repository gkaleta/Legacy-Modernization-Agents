using System.Text;

namespace Starforge.Cobol.Copy;

[Flags]
public enum SegmentFlags : byte
{
    None = 0,
    FromReplacement = 1,
    Synthetic = 2,
}

// Maps [Start, Start+Length) of an expanded text to [SourceOffset, ...) of one unit's logical text.
// Replacement segments do not advance: Unit/SourceOffset/AnchorLength name the replaced text they stand in for,
// while ReplacementUnit/ReplacementOffset point at the operand (BY text) the characters were copied from.
public readonly record struct TextSegment(
    int Start,
    int Length,
    int Unit,
    int SourceOffset,
    SegmentFlags Flags,
    int AnchorLength = 0,
    int ReplacementUnit = -1,
    int ReplacementOffset = 0)
{
    public int End => Start + Length;

    public bool IsSynthetic => (Flags & SegmentFlags.Synthetic) != 0;

    public bool IsFromReplacement => (Flags & SegmentFlags.FromReplacement) != 0;
}

// Text whose every character remembers which unit (program or copybook instance) and offset it came from.
// Segments are contiguous and cover the whole text, so lookups never fall into gaps.
public sealed class OriginText
{
    private readonly TextSegment[] _segments;

    internal OriginText(string text, TextSegment[] segments)
    {
        Text = text;
        _segments = segments;
    }

    public string Text { get; }

    public int Length => Text.Length;

    public IReadOnlyList<TextSegment> Segments => _segments;

    public static OriginText ForUnit(int unit, string text)
        => new(text, text.Length == 0 ? Array.Empty<TextSegment>() : new[] { new TextSegment(0, text.Length, unit, 0, SegmentFlags.None) });

    public int FindSegment(int offset)
    {
        var lo = 0;
        var hi = _segments.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var s = _segments[mid];
            if (offset < s.Start)
            {
                hi = mid - 1;
            }
            else if (offset >= s.End)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    public TextSegment SegmentAt(int index) => _segments[index];
}

internal sealed class OriginTextBuilder
{
    private readonly StringBuilder _text;
    private readonly List<TextSegment> _segments = new();

    public OriginTextBuilder(int capacity = 1024)
    {
        _text = new StringBuilder(capacity);
    }

    public int Length => _text.Length;

    public char LastChar => _text.Length == 0 ? '\n' : _text[^1];

    public void Append(OriginText source, int start, int end)
    {
        if (end <= start)
        {
            return;
        }

        var segIndex = source.FindSegment(start);
        var pos = start;
        while (pos < end && segIndex >= 0 && segIndex < source.Segments.Count)
        {
            var seg = source.SegmentAt(segIndex);
            var pieceEnd = Math.Min(end, seg.End);
            var delta = pos - seg.Start;
            if (seg.IsFromReplacement)
            {
                AddSegment(seg with { Length = pieceEnd - pos, ReplacementOffset = seg.ReplacementOffset + delta });
            }
            else
            {
                AddSegment(seg with { Length = pieceEnd - pos, SourceOffset = seg.SourceOffset + delta });
            }

            pos = pieceEnd;
            segIndex++;
        }

        _text.Append(source.Text, start, end - start);
    }

    // Appends replacement text anchored to the replaced range [anchorStart, anchorEnd) of anchorSource.
    public void AppendReplacement(OriginText replacement, int start, int end, OriginText anchorSource, int anchorStart, int anchorEnd)
    {
        if (end <= start)
        {
            return;
        }

        var (unit, offset, length) = Anchor(anchorSource, anchorStart, anchorEnd);
        var segIndex = replacement.FindSegment(start);
        var pos = start;
        while (pos < end && segIndex >= 0 && segIndex < replacement.Segments.Count)
        {
            var seg = replacement.SegmentAt(segIndex);
            var pieceEnd = Math.Min(end, seg.End);
            var delta = pos - seg.Start;
            var replUnit = seg.IsFromReplacement ? seg.ReplacementUnit : seg.Unit;
            var replOffset = seg.IsFromReplacement ? seg.ReplacementOffset + delta : seg.SourceOffset + delta;
            var anchored = unit >= 0 ? (unit, offset, length) : (replUnit, replOffset, pieceEnd - pos);
            AddSegment(new TextSegment(0, pieceEnd - pos, anchored.Item1, anchored.Item2, SegmentFlags.FromReplacement, anchored.Item3, replUnit, replOffset));
            pos = pieceEnd;
            segIndex++;
        }

        _text.Append(replacement.Text, start, end - start);
    }

    public void AppendSynthetic(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        AddSegment(new TextSegment(0, text.Length, -1, 0, SegmentFlags.Synthetic));
        _text.Append(text);
    }

    public OriginText Build() => new(_text.ToString(), _segments.ToArray());

    private static (int Unit, int Offset, int Length) Anchor(OriginText source, int start, int end)
    {
        var index = source.FindSegment(start);
        while (index >= 0 && index < source.Segments.Count && source.SegmentAt(index).IsSynthetic && source.SegmentAt(index).End < end)
        {
            index++;
        }

        if (index < 0 || index >= source.Segments.Count || source.SegmentAt(index).IsSynthetic)
        {
            return (-1, 0, 0);
        }

        var seg = source.SegmentAt(index);
        if (seg.IsFromReplacement)
        {
            return (seg.Unit, seg.SourceOffset, seg.AnchorLength);
        }

        var from = Math.Max(start, seg.Start);
        return (seg.Unit, seg.SourceOffset + (from - seg.Start), Math.Min(end, seg.End) - from);
    }

    private void AddSegment(TextSegment segment)
    {
        segment = segment with { Start = _text.Length };
        if (_segments.Count > 0)
        {
            var last = _segments[^1];
            var mergeable = last.Unit == segment.Unit && last.Flags == segment.Flags
                && (segment.IsSynthetic || (!segment.IsFromReplacement && last.SourceOffset + last.Length == segment.SourceOffset));
            if (mergeable)
            {
                _segments[^1] = last with { Length = last.Length + segment.Length };
                return;
            }
        }

        _segments.Add(segment);
    }
}
