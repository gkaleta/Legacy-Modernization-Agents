namespace Starforge.Cobol.Source;

public sealed record SourceLine(int Number, string Text, SourceFormat Format, SourceLineKind Kind, bool HadTabs, string? Reason = null)
{
    public const int ProgramAreaStart = 8;
    public const int ProgramAreaEnd = 72;

    public char Indicator => Format == SourceFormat.Fixed && Text.Length >= 7 ? Text[6] : ' ';

    public string SequenceArea => Format == SourceFormat.Fixed ? Columns(1, 6) : string.Empty;

    public string AreaA => Format == SourceFormat.Fixed ? Columns(8, 11) : string.Empty;

    public string AreaB => Format == SourceFormat.Fixed ? Columns(12, 72) : string.Empty;

    public string IdentificationArea => Format == SourceFormat.Fixed ? Columns(73, int.MaxValue) : string.Empty;

    public string ProgramArea => Format == SourceFormat.Fixed ? Columns(ProgramAreaStart, ProgramAreaEnd) : Text;

    public string Columns(int from, int to)
    {
        var start = from - 1;
        if (start >= Text.Length)
        {
            return string.Empty;
        }

        var end = Math.Min(Text.Length, to);
        return Text.Substring(start, end - start);
    }
}
