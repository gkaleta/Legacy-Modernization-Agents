using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Exec;

public enum ExecStatus
{
    Parsed,
    Unsupported,
}

public enum ExecArgumentKind
{
    Literal,
    Numeric,
    Figurative,
    DataRef,
    LengthOf,
    AddressOf,
    Function,
    Expression,
    Empty,
}

// Text is the argument with whitespace collapsed; Value is the literal content (quotes removed) for literals;
// DataName is the leading data name of a reference (subscripts/qualifiers stripped), the dependency key M5 resolves.
public sealed record ExecArgument(ExecArgumentKind Kind, string Text, string? Value, string? DataName)
{
    public bool IsStatic => Kind is ExecArgumentKind.Literal or ExecArgumentKind.Numeric;
}

public sealed record ExecOption(string Name, ExecArgument? Argument, bool Known, SourceRange Range);

public enum ExecResourceKind
{
    Program,
    TransId,
    File,
    TsQueue,
    TdQueue,
    Map,
    Channel,
    Container,
    Commarea,
    Label,
    Psb,
}

// Name is the literal value for static targets and the reference text for dynamic ones.
public sealed record ExecTarget(string Name, bool IsStatic, string? DataName)
{
    // CICS and DL/I names are blank-padded fixed-length fields, so 'INQCUST ' names the same resource as 'INQCUST'.
    public static ExecTarget From(ExecArgument arg)
        => arg.IsStatic
            ? new ExecTarget((arg.Value ?? arg.Text).TrimEnd(' '), true, null)
            : new ExecTarget(arg.Text, false, arg.DataName);

    // Stable comparison key: literal value, or '&' + data name for a target only known at run time.
    public string Key => IsStatic ? Name.ToUpperInvariant() : "&" + (DataName ?? Name).ToUpperInvariant();
}

// Qualifier carries the second half of two-part resources: MAPSET of a map, CHANNEL of a container, LENGTH of a COMMAREA.
public sealed record ExecResource(ExecResourceKind Kind, string Option, ExecTarget Target, ExecTarget? Qualifier = null, bool QualifierImplied = false);

// Offsets are relative to the EXEC block text (the block starts at "EXEC").
public readonly record struct ExecSpan(int Start, int End);

public abstract class ExecStatement
{
    public required ExecKind Kind { get; init; }

    public required ExecStatus Status { get; init; }

    public required SourceRange Range { get; init; }

    public required int ExpandedStart { get; init; }

    public required int ExpandedEnd { get; init; }

    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    public IReadOnlyList<ExecResource> Resources { get; init; } = Array.Empty<ExecResource>();

    public IReadOnlyList<ExecSpan> Comments { get; init; } = Array.Empty<ExecSpan>();

    // Coverage reason for an Unsupported block: the code of the diagnostic that explains it.
    public string? UnsupportedReason { get; init; }

    // Upper-case command or statement name: "SEND MAP", "SELECT INTO", "GU".
    public abstract string Operation { get; }
}

public sealed class CicsCommand : ExecStatement
{
    public required string Verb { get; init; }

    public required IReadOnlyList<ExecOption> Options { get; init; }

    public override string Operation => Verb;

    public ExecOption? Option(string name) => Options.FirstOrDefault(o => o.Name == name);

    public IEnumerable<ExecOption> UnknownOptions => Options.Where(o => !o.Known);
}

public enum SqlStatementKind
{
    Unknown,
    SelectInto,
    Select,
    DeclareCursor,
    DeclareTable,
    DeclareGlobalTemporaryTable,
    DeclareStatement,
    DeclareVariable,
    Open,
    Fetch,
    Close,
    Insert,
    Update,
    Delete,
    Merge,
    Truncate,
    Commit,
    Rollback,
    Savepoint,
    ReleaseSavepoint,
    Whenever,
    Call,
    Set,
    ValuesInto,
    Values,
    Prepare,
    Execute,
    ExecuteImmediate,
    Describe,
    LockTable,
    Connect,
    Release,
    Include,
    BeginDeclareSection,
    EndDeclareSection,
    GetDiagnostics,
    Signal,
    AssociateLocators,
    AllocateCursor,
    FreeLocator,
    HoldLocator,
    Create,
    Drop,
    Alter,
    Grant,
    Revoke,
    Comment,
    Label,
    Explain,
    Refresh,
}

public enum SqlTableOperation
{
    Select,
    Insert,
    Update,
    Delete,
    Merge,
    Declare,
    Lock,
    Truncate,
    Ddl,
}

// Name is the full name as written (schema/location qualified, upper-cased); Table is its last part.
public sealed record SqlTableReference(string Name, string? Schema, string Table, string? Alias, SqlTableOperation Operation);

public enum SqlHostVariableRole
{
    Input,
    Output,
}

// Name is the host identifier after ':' including any '.'-qualification; Field is its last part.
public sealed record SqlHostVariable(string Name, string? Qualifier, string Field, string? Indicator, SqlHostVariableRole Role);

public sealed class SqlStatement : ExecStatement
{
    public required SqlStatementKind StatementKind { get; init; }

    // First words as written; the only description an Unknown statement has.
    public required string Leading { get; init; }

    public IReadOnlyList<SqlTableReference> Tables { get; init; } = Array.Empty<SqlTableReference>();

    // The statement's tokens (offsets into the EXEC block text) and, per entry of Tables, its [First, End) token span.
    internal IReadOnlyList<SToken> Tokens { get; init; } = Array.Empty<SToken>();

    internal IReadOnlyList<(int First, int End)> TableTokens { get; init; } = Array.Empty<(int, int)>();

    public IReadOnlyList<string> CommonTableExpressions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<SqlHostVariable> HostVariables { get; init; } = Array.Empty<SqlHostVariable>();

    public string? Cursor { get; init; }

    public string? StatementName { get; init; }

    public bool WhereCurrentOf { get; init; }

    public IReadOnlyList<string> CursorAttributes { get; init; } = Array.Empty<string>();

    // The named object a statement acts on when it is not a table or cursor: CALL routine, WHENEVER label,
    // INCLUDE member, CONNECT location, SET/GET DIAGNOSTICS target, SAVEPOINT name.
    public string? Target { get; init; }

    public override string Operation => OperationOf(StatementKind);

    public static string OperationOf(SqlStatementKind kind) => kind switch
    {
        SqlStatementKind.Unknown => "UNKNOWN",
        SqlStatementKind.SelectInto => "SELECT INTO",
        SqlStatementKind.DeclareCursor => "DECLARE CURSOR",
        SqlStatementKind.DeclareTable => "DECLARE TABLE",
        SqlStatementKind.DeclareGlobalTemporaryTable => "DECLARE GLOBAL TEMPORARY TABLE",
        SqlStatementKind.DeclareStatement => "DECLARE STATEMENT",
        SqlStatementKind.DeclareVariable => "DECLARE VARIABLE",
        SqlStatementKind.ReleaseSavepoint => "RELEASE SAVEPOINT",
        SqlStatementKind.ValuesInto => "VALUES INTO",
        SqlStatementKind.ExecuteImmediate => "EXECUTE IMMEDIATE",
        SqlStatementKind.LockTable => "LOCK TABLE",
        SqlStatementKind.BeginDeclareSection => "BEGIN DECLARE SECTION",
        SqlStatementKind.EndDeclareSection => "END DECLARE SECTION",
        SqlStatementKind.GetDiagnostics => "GET DIAGNOSTICS",
        SqlStatementKind.AssociateLocators => "ASSOCIATE LOCATORS",
        SqlStatementKind.AllocateCursor => "ALLOCATE CURSOR",
        SqlStatementKind.FreeLocator => "FREE LOCATOR",
        SqlStatementKind.HoldLocator => "HOLD LOCATOR",
        _ => kind.ToString().ToUpperInvariant(),
    };
}

// One level of a DL/I path call: SEGMENT(name) or SEGMENT((data-name)) plus the options that qualify that level.
public sealed record DliSegment(ExecTarget Name, IReadOnlyList<ExecOption> Options);

public sealed class DliCommand : ExecStatement
{
    public required string Function { get; init; }

    public ExecArgument? Pcb { get; init; }

    public ExecTarget? Psb { get; init; }

    public IReadOnlyList<DliSegment> Segments { get; init; } = Array.Empty<DliSegment>();

    // Options that precede the first SEGMENT (USING PCB, KEYFEEDBACK, FEEDBACKLEN, ID, ...).
    public required IReadOnlyList<ExecOption> Options { get; init; }

    public override string Operation => Function;
}

public sealed class UnknownExec : ExecStatement
{
    public required string Name { get; init; }

    public override string Operation => Name;
}
