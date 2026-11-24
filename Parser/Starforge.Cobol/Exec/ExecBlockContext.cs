using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Exec;

// Maps a range of the EXEC block text (offsets from the "EXEC" keyword) to the original source.
public delegate SourceRange ExecRangeLocator(int blockStart, int blockEnd);

// Per-block parsing state shared by the CICS, SQL and DL/I parsers; never shared between blocks.
internal sealed class ExecBlockContext
{
    public ExecBlockContext(string text, ExecKind kind, SourceRange range, int expandedStart, ExecRangeLocator? locate, int innerStart, int innerEnd)
    {
        Text = text;
        Kind = kind;
        Range = range;
        ExpandedStart = expandedStart;
        Locate = locate;
        InnerStart = innerStart;
        InnerEnd = innerEnd;
    }

    public string Text { get; }

    public ExecKind Kind { get; }

    public SourceRange Range { get; }

    public int ExpandedStart { get; }

    public int ExpandedEnd => ExpandedStart + Text.Length;

    public ExecRangeLocator? Locate { get; }

    // The command text between "EXEC <kind>" and "END-EXEC".
    public int InnerStart { get; }

    public int InnerEnd { get; }

    public List<Diagnostic> Diagnostics { get; } = new();

    public SourceRange RangeOf(int start, int end) => Locate is null || end <= start ? Range : Locate(start, end);

    public Diagnostic Report(string code, DiagnosticSeverity severity, string message, int start, int end, string shape)
    {
        var d = Diagnostic.Create(code, severity, message, RangeOf(start, end), shape);
        Diagnostics.Add(d);
        return d;
    }

    public string Snippet(int start, int end, int max = 60)
    {
        var s = ExecArguments.Collapse(Text, start, end);
        return s.Length <= max ? s : s[..max] + "...";
    }
}
