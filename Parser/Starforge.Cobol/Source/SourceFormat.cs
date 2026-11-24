namespace Starforge.Cobol.Source;

public enum SourceFormat
{
    Fixed,
    Free,
}

public enum SourceLineKind
{
    Blank,
    Code,
    Continuation,
    Comment,
    Debug,
    Directive,
}
