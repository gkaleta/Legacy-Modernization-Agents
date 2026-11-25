using Starforge.Cobol.Source;

namespace Starforge.Cobol.Copy;

public enum SourceUnitKind
{
    Program,
    Copybook,
    SqlInclude,
}

// One instance of a file in the compilation: the program itself, or one inclusion of a copybook.
public sealed record SourceUnit(
    int Id,
    SourceUnitKind Kind,
    LogicalSource Source,
    int ParentId,
    string? Name,
    string? Library,
    SourceRange? IncludedAt,
    int Depth,
    bool Replacing)
{
    public string Path => Source.Path;
}

public enum CopyStatus
{
    Resolved,
    Missing,
    Cycle,
    DepthExceeded,
    Malformed,
    ReadError,
}

public enum CopyStatementKind
{
    Copy,
    SqlInclude,
}

public sealed record CopyInclusion(
    CopyStatementKind Kind,
    string Name,
    string? Library,
    bool Suppress,
    int ReplacingPairs,
    int ParentUnitId,
    int UnitId,
    SourceRange StatementRange,
    CopyStatus Status,
    string? ResolvedPath,
    string? SearchStage,
    IReadOnlyList<string> OtherCandidates,
    bool Idms = false);
