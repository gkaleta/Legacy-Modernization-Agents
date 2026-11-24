using Starforge.Cobol.Copy;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Coverage;

public sealed record CoverageRangeEntry(CoverageClass Class, string Reason, SourceRange Range, int Characters, int Occurrences);

public sealed class CoverageReport
{
    internal CoverageReport(
        IReadOnlyDictionary<CoverageClass, long> programArea,
        IReadOnlyDictionary<string, long> byReason,
        long sequenceArea,
        long indicatorArea,
        long identificationArea,
        int conflicts,
        IReadOnlyList<CoverageRangeEntry> ranges)
    {
        ProgramArea = programArea;
        ByReason = byReason;
        SequenceArea = sequenceArea;
        IndicatorArea = indicatorArea;
        IdentificationArea = identificationArea;
        Conflicts = conflicts;
        Ranges = ranges;
    }

    // Non-blank characters of the program area (columns 8-72 in fixed format) across the program and every copybook instance.
    public IReadOnlyDictionary<CoverageClass, long> ProgramArea { get; }

    public IReadOnlyDictionary<string, long> ByReason { get; }

    public long SequenceArea { get; }

    public long IndicatorArea { get; }

    public long IdentificationArea { get; }

    public int Conflicts { get; }

    // Every program-area range that is not Parsed/Lexed, including unaccounted ones.
    public IReadOnlyList<CoverageRangeEntry> Ranges { get; }

    public long Total => ProgramArea.Values.Sum();

    public long Count(CoverageClass cls) => ProgramArea.TryGetValue(cls, out var n) ? n : 0;

    public long Unaccounted => Count(CoverageClass.Unaccounted);

    public double Percent(CoverageClass cls) => Total == 0 ? 0 : 100.0 * Count(cls) / Total;
}

// Tracks, per unit instance, which class claimed each logical character. A character may be claimed once;
// a second, different claim is a pipeline bug and is counted as a conflict.
public sealed class TokenAccounting
{
    private readonly List<SourceUnit> _units = new();
    private readonly List<byte[]> _classes = new();
    private readonly List<(int Unit, int Start, int End, CoverageClass Class, string Reason, int Chars)> _marks = new();
    private readonly Dictionary<(CoverageClass, string), long> _reasonCounts = new();
    private int _conflicts;

    public int Conflicts => _conflicts;

    public IReadOnlyList<SourceUnit> Units => _units;

    public void Register(SourceUnit unit)
    {
        if (unit.Id != _units.Count)
        {
            throw new InvalidOperationException($"Unit ids must be dense; expected {_units.Count}, got {unit.Id}");
        }

        _units.Add(unit);
        _classes.Add(new byte[unit.Source.Text.Length]);
    }

    public CoverageClass ClassAt(int unitId, int offset) => (CoverageClass)_classes[unitId][offset];

    public void Mark(int unitId, int start, int end, CoverageClass cls, string reason)
    {
        var arr = _classes[unitId];
        var text = _units[unitId].Source.Text;
        var count = 0;
        end = Math.Min(end, arr.Length);
        for (var i = Math.Max(0, start); i < end; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            var existing = arr[i];
            if (existing == 0)
            {
                arr[i] = (byte)cls;
                count++;
            }
            else if (existing != (byte)cls)
            {
                _conflicts++;
            }
        }

        // Lexed/Parsed are totals only; listing them would repeat the whole program.
        if (count == 0 || cls is CoverageClass.Lexed or CoverageClass.Parsed)
        {
            return;
        }

        var key = (cls, reason);
        _reasonCounts[key] = _reasonCounts.TryGetValue(key, out var n) ? n + count : count;
        _marks.Add((unitId, start, end, cls, reason, count));
    }

    // Promotes characters already claimed as Lexed (used when a later stage consumes lexed tokens).
    public void Promote(int unitId, int start, int end, CoverageClass from, CoverageClass to, string reason)
    {
        var arr = _classes[unitId];
        var count = 0;
        end = Math.Min(end, arr.Length);
        for (var i = Math.Max(0, start); i < end; i++)
        {
            if (arr[i] == (byte)from)
            {
                arr[i] = (byte)to;
                count++;
            }
        }

        if (count > 0 && to is not (CoverageClass.Lexed or CoverageClass.Parsed))
        {
            var key = (to, reason);
            _reasonCounts[key] = _reasonCounts.TryGetValue(key, out var n) ? n + count : count;
            _marks.Add((unitId, start, end, to, reason, count));
        }
    }

    // Moves characters a previous stage claimed under (fromClass, fromReason) to (toClass, toReason), keeping the
    // per-reason totals and listed ranges consistent; Promote alone would leave the old reason counted.
    public int Reclassify(int unitId, int start, int end, CoverageClass fromClass, string fromReason, CoverageClass toClass, string toReason)
    {
        var arr = _classes[unitId];
        var text = _units[unitId].Source.Text;
        start = Math.Max(0, start);
        end = Math.Min(end, arr.Length);
        if (end <= start)
        {
            return 0;
        }

        var moved = new bool[end - start];
        var total = 0;
        for (var m = 0; m < _marks.Count; m++)
        {
            var mark = _marks[m];
            if (mark.Unit != unitId || mark.Class != fromClass || mark.Reason != fromReason || mark.End <= start || mark.Start >= end)
            {
                continue;
            }

            var taken = 0;
            for (var i = Math.Max(start, mark.Start); i < Math.Min(end, mark.End); i++)
            {
                if (!moved[i - start] && arr[i] == (byte)fromClass && !char.IsWhiteSpace(text[i]))
                {
                    moved[i - start] = true;
                    arr[i] = (byte)toClass;
                    taken++;
                }
            }

            taken = Math.Min(taken, mark.Chars);
            if (taken == 0)
            {
                continue;
            }

            total += taken;
            _marks[m] = mark with { Chars = mark.Chars - taken };
            var key = (fromClass, fromReason);
            _reasonCounts[key] -= taken;
        }

        _marks.RemoveAll(m => m.Chars == 0);
        if (total > 0 && toClass is not (CoverageClass.Lexed or CoverageClass.Parsed))
        {
            var key = (toClass, toReason);
            _reasonCounts[key] = _reasonCounts.TryGetValue(key, out var n) ? n + total : total;
            _marks.Add((unitId, start, end, toClass, toReason, total));
        }

        return total;
    }

    // Replacement text is owned by the directive that supplied it, so only original characters are claimed here.
    public void MarkExpanded(OriginText text, int start, int end, CoverageClass cls, string reason)
    {
        if (end <= start)
        {
            return;
        }

        var segIndex = text.FindSegment(start);
        var pos = start;
        while (segIndex >= 0 && segIndex < text.Segments.Count && pos < end)
        {
            var seg = text.SegmentAt(segIndex);
            var pieceEnd = Math.Min(end, seg.End);
            if (!seg.IsSynthetic && !seg.IsFromReplacement)
            {
                var srcStart = seg.SourceOffset + (pos - seg.Start);
                Mark(seg.Unit, srcStart, srcStart + (pieceEnd - pos), cls, reason);
            }

            pos = pieceEnd;
            segIndex++;
        }
    }

    // Same mapping as MarkExpanded, but only re-classifies characters currently claimed as 'from'.
    public void PromoteExpanded(OriginText text, int start, int end, CoverageClass from, CoverageClass to, string reason)
    {
        if (end <= start)
        {
            return;
        }

        var segIndex = text.FindSegment(start);
        var pos = start;
        while (segIndex >= 0 && segIndex < text.Segments.Count && pos < end)
        {
            var seg = text.SegmentAt(segIndex);
            var pieceEnd = Math.Min(end, seg.End);
            if (!seg.IsSynthetic && !seg.IsFromReplacement && pieceEnd > pos)
            {
                var srcStart = seg.SourceOffset + (pos - seg.Start);
                Promote(seg.Unit, srcStart, srcStart + (pieceEnd - pos), from, to, reason);
            }

            pos = Math.Max(pos, pieceEnd);
            segIndex++;
        }
    }

    // Counts original (non-replacement) characters of an expanded range that currently have the given class.
    public long CountExpanded(OriginText text, int start, int end, CoverageClass cls)
    {
        long count = 0;
        var segIndex = end > start ? text.FindSegment(start) : -1;
        var pos = start;
        while (segIndex >= 0 && segIndex < text.Segments.Count && pos < end)
        {
            var seg = text.SegmentAt(segIndex);
            var pieceEnd = Math.Min(end, seg.End);
            if (!seg.IsSynthetic && !seg.IsFromReplacement && pieceEnd > pos)
            {
                var arr = _classes[seg.Unit];
                var srcStart = seg.SourceOffset + (pos - seg.Start);
                var srcEnd = Math.Min(arr.Length, srcStart + (pieceEnd - pos));
                for (var i = Math.Max(0, srcStart); i < srcEnd; i++)
                {
                    if (arr[i] == (byte)cls)
                    {
                        count++;
                    }
                }
            }

            pos = Math.Max(pos, pieceEnd);
            segIndex++;
        }

        return count;
    }

    public CoverageReport BuildReport()
    {
        var counts = new Dictionary<CoverageClass, long>();
        foreach (CoverageClass c in Enum.GetValues<CoverageClass>())
        {
            counts[c] = 0;
        }

        var byReason = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, value) in _reasonCounts)
        {
            if (value > 0)
            {
                byReason[$"{key.Item1}({key.Item2})"] = value;
            }
        }

        long seq = 0, ind = 0, ident = 0;
        var ranges = new Dictionary<(CoverageClass, string, SourceRange), (int Chars, int Occurrences)>();

        void AddRange(CoverageClass cls, string reason, SourceRange range, int chars)
        {
            var key = (cls, reason, range);
            ranges[key] = ranges.TryGetValue(key, out var v) ? (v.Chars, v.Occurrences + 1) : (chars, 1);
        }

        for (var u = 0; u < _units.Count; u++)
        {
            var unit = _units[u];
            var source = unit.Source;

            // Reader marks: merge consecutive whole-line marks of the same class/reason into one listed range.
            ReaderMark? open = null;
            var openStartLine = 0;
            var openChars = 0;
            foreach (var m in source.Marks)
            {
                switch (m.Area)
                {
                    case ReaderArea.Sequence:
                        seq += m.Characters;
                        continue;
                    case ReaderArea.Indicator:
                        ind += m.Characters;
                        continue;
                    case ReaderArea.Identification:
                        ident += m.Characters;
                        continue;
                }

                counts[m.Class] += m.Characters;
                var reasonKey = $"{m.Class}({m.Reason})";
                byReason[reasonKey] = byReason.TryGetValue(reasonKey, out var rn) ? rn + m.Characters : m.Characters;

                if (open is not null && open.Class == m.Class && open.Reason == m.Reason && m.Line == open.Line + 1)
                {
                    open = m with { StartColumn = Math.Min(open.StartColumn, m.StartColumn) };
                    openChars += m.Characters;
                    continue;
                }

                if (open is not null)
                {
                    AddRange(open.Class, open.Reason, new SourceRange(source.Path, openStartLine, open.StartColumn, open.Line, open.EndColumn), openChars);
                }

                open = m;
                openStartLine = m.Line;
                openChars = m.Characters;
            }

            if (open is not null)
            {
                AddRange(open.Class, open.Reason, new SourceRange(source.Path, openStartLine, open.StartColumn, open.Line, open.EndColumn), openChars);
            }

            var arr = _classes[u];
            var text = source.Text;
            var unaccountedStart = -1;
            for (var i = 0; i <= arr.Length; i++)
            {
                var isUnaccounted = i < arr.Length && arr[i] == 0 && !char.IsWhiteSpace(text[i]);
                if (i < arr.Length && !char.IsWhiteSpace(text[i]))
                {
                    counts[(CoverageClass)arr[i]]++;
                }

                if (isUnaccounted && unaccountedStart < 0)
                {
                    unaccountedStart = i;
                }
                else if (!isUnaccounted && unaccountedStart >= 0 && (i == arr.Length || !char.IsWhiteSpace(text[i])))
                {
                    var chars = 0;
                    for (var k = unaccountedStart; k < i; k++)
                    {
                        chars += char.IsWhiteSpace(text[k]) ? 0 : 1;
                    }

                    AddRange(CoverageClass.Unaccounted, "unaccounted", source.RangeOf(unaccountedStart, TrimEnd(text, unaccountedStart, i)), chars);
                    unaccountedStart = -1;
                }
            }
        }

        foreach (var m in _marks)
        {
            var source = _units[m.Unit].Source;
            AddRange(m.Class, m.Reason, source.RangeOf(m.Start, TrimEnd(source.Text, m.Start, m.End)), m.Chars);
        }

        var list = ranges
            .Select(kv => new CoverageRangeEntry(kv.Key.Item1, kv.Key.Item2, kv.Key.Item3, kv.Value.Chars, kv.Value.Occurrences))
            .OrderBy(r => r.Range.File, StringComparer.Ordinal)
            .ThenBy(r => r.Range.StartLine)
            .ThenBy(r => r.Range.StartColumn)
            .ThenBy(r => r.Class)
            .ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();

        return new CoverageReport(counts, byReason, seq, ind, ident, _conflicts, list);
    }

    private static int TrimEnd(string text, int start, int end)
    {
        while (end > start + 1 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end;
    }
}
