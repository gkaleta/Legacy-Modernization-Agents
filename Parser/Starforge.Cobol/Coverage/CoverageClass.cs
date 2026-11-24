using Starforge.Cobol.Source;

namespace Starforge.Cobol.Coverage;

// Unaccounted must stay 0 so a freshly allocated class array means "nobody claimed this character".
public enum CoverageClass : byte
{
    Unaccounted = 0,
    Parsed,
    Lexed,
    Comment,
    Directive,
    Unsupported,
    Unresolved,
    Skipped,
}

public sealed record CoverageMark(CoverageClass Class, string Reason, SourceRange Range, int Characters);
