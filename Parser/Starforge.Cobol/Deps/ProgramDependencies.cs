using Starforge.Cobol.Copy;

namespace Starforge.Cobol.Deps;

public enum DependencyKind
{
    Copy,
    PlatformCopy,
    Call,
    CicsLink,
    CicsXctl,
    CicsRun,
    IdmsTransfer,
}

// Name is the copybook file stem plus a lower-case ".cpy" whatever the extension on disk; platform copybooks use the bare member.
public sealed record CopybookReference(string Name, string Member, DependencyKind Kind, CopyStatus Status, string? ResolvedPath, int Line);

// IsStatic: the program is known at compile time, from a literal or from a data name whose VALUE never changes (ResolvedFrom).
public sealed record ProgramReference(DependencyKind Kind, string Name, bool IsStatic, string? ResolvedFrom, int Line);

// CICS RETURN, XCTL, START and RUN, IDMS "TRANSFER RETURN|NORETURN" and "DC RETURN"; Target is the program, TRANSID
// or next task code, null for a bare RETURN.
public sealed record ControlTransfer(string Operation, string? Target, bool IsStatic, string? ResolvedFrom, int Line);

// Files, DB2 tables, queues, maps, channels, containers, COMMAREAs and PSBs a program names; Operation is the verb.
public sealed record ResourceReference(string Kind, string Name, bool IsStatic, string Operation, int Line);

public sealed record ProgramDependencies(
    IReadOnlyList<CopybookReference> Copybooks,
    IReadOnlyList<ProgramReference> Programs,
    IReadOnlyList<ControlTransfer> Transfers,
    IReadOnlyList<ResourceReference> Resources,
    IReadOnlyDictionary<string, int> Presence)
{
    public IEnumerable<CopybookReference> MissingCopybooks => Copybooks.Where(c => c.Status != CopyStatus.Resolved);
}
