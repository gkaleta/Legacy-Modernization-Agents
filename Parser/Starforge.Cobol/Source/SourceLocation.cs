namespace Starforge.Cobol.Source;

public readonly record struct SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}:{Line}:{Column}";
}

// Columns are 1-based and EndColumn is inclusive, matching how COBOL areas are described (Area B = 12-72).
public readonly record struct SourceRange(string File, int StartLine, int StartColumn, int EndLine, int EndColumn)
{
    public SourceLocation Start => new(File, StartLine, StartColumn);

    public SourceLocation End => new(File, EndLine, EndColumn);

    public static SourceRange At(SourceLocation start, SourceLocation end) => new(start.File, start.Line, start.Column, end.Line, end.Column);

    public override string ToString() => StartLine == EndLine
        ? $"{File}:{StartLine}:{StartColumn}-{EndColumn}"
        : $"{File}:{StartLine}:{StartColumn}-{EndLine}:{EndColumn}";
}
