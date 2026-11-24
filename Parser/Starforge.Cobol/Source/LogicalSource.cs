using Starforge.Cobol.Coverage;

namespace Starforge.Cobol.Source;

// The program-area text of one file after comment removal and continuation joining.
// Every non-synthetic character maps back to exactly one physical (line, column).
public sealed class LogicalSource
{
    private readonly int[] _runStart;
    private readonly int[] _runLine;
    private readonly int[] _runColumn;
    private readonly int[] _runLength;

    internal LogicalSource(
        SourceFile file,
        SourceFormat initialFormat,
        IReadOnlyList<SourceLine> lines,
        string text,
        List<(int Start, int Line, int Column, int Length)> runs,
        IReadOnlyList<ReaderMark> marks,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        File = file;
        InitialFormat = initialFormat;
        Lines = lines;
        Text = text;
        _runStart = new int[runs.Count];
        _runLine = new int[runs.Count];
        _runColumn = new int[runs.Count];
        _runLength = new int[runs.Count];
        for (var i = 0; i < runs.Count; i++)
        {
            _runStart[i] = runs[i].Start;
            _runLine[i] = runs[i].Line;
            _runColumn[i] = runs[i].Column;
            _runLength[i] = runs[i].Length;
        }

        Marks = marks;
        Diagnostics = diagnostics;
    }

    public SourceFile File { get; }

    public string Path => File.Path;

    public SourceFormat InitialFormat { get; }

    public IReadOnlyList<SourceLine> Lines { get; }

    public string Text { get; }

    // Coverage the reader decided on its own: comment lines, directives, sequence/identification areas.
    public IReadOnlyList<ReaderMark> Marks { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public int RunCount => _runStart.Length;

    public bool IsSynthetic(int offset) => FindRun(offset) < 0;

    public SourceLocation? Locate(int offset)
    {
        var run = FindRun(offset);
        if (run < 0)
        {
            return null;
        }

        return new SourceLocation(Path, _runLine[run], _runColumn[run] + (offset - _runStart[run]));
    }

    // Synthetic characters (line joins, literal padding) resolve to the closest real character before them.
    public SourceLocation LocateNearest(int offset, bool preferForward = false)
    {
        var exact = Locate(offset);
        if (exact is not null)
        {
            return exact.Value;
        }

        var run = LastRunStartingAtOrBefore(offset);
        if (preferForward && run + 1 < _runStart.Length)
        {
            return new SourceLocation(Path, _runLine[run + 1], _runColumn[run + 1]);
        }

        if (run >= 0)
        {
            return new SourceLocation(Path, _runLine[run], _runColumn[run] + _runLength[run] - 1);
        }

        if (_runStart.Length > 0)
        {
            return new SourceLocation(Path, _runLine[0], _runColumn[0]);
        }

        return new SourceLocation(Path, 1, 1);
    }

    public SourceRange RangeOf(int start, int endExclusive)
    {
        if (endExclusive <= start)
        {
            endExclusive = start + 1;
        }

        var s = LocateNearest(start, preferForward: true);
        var e = LocateNearest(endExclusive - 1);
        if (e.Line < s.Line || (e.Line == s.Line && e.Column < s.Column))
        {
            e = s;
        }

        return SourceRange.At(s, e);
    }

    private int FindRun(int offset)
    {
        var run = LastRunStartingAtOrBefore(offset);
        if (run < 0 || offset >= _runStart[run] + _runLength[run])
        {
            return -1;
        }

        return run;
    }

    private int LastRunStartingAtOrBefore(int offset)
    {
        var lo = 0;
        var hi = _runStart.Length - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_runStart[mid] <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }
}

public enum ReaderArea : byte
{
    Program,
    Sequence,
    Indicator,
    Identification,
}

public sealed record ReaderMark(CoverageClass Class, string Reason, ReaderArea Area, int Line, int StartColumn, int EndColumn, int Characters);
