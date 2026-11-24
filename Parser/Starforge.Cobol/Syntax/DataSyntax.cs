using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Syntax;

public enum DataSectionKind
{
    File,
    WorkingStorage,
    LocalStorage,
    Linkage,
}

public sealed record DataDivision(
    NodeSpan Span,
    SourceRange Range,
    IReadOnlyList<DataSection> Sections,
    IReadOnlyList<ErrorNode> Errors) : SyntaxNode(Span, Range)
{
    public IEnumerable<ErrorNode> AllErrors()
        => Errors.Concat(Sections.SelectMany(s => s.Errors
            .Concat(s.Files.SelectMany(f => f.Errors.Concat(f.Records.SelectMany(r => r.SelfAndDescendants()).SelectMany(e => e.Errors))))
            .Concat(s.Entries.SelectMany(r => r.SelfAndDescendants()).SelectMany(e => e.Errors))));
}

public sealed record DataSection(
    NodeSpan Span,
    SourceRange Range,
    DataSectionKind Kind,
    IReadOnlyList<FileDescription> Files,
    IReadOnlyList<DataEntry> Entries,
    IReadOnlyList<ExecStatement> Execs,
    IReadOnlyList<ErrorNode> Errors) : SyntaxNode(Span, Range);

public sealed record BlockContains(int? Min, int Max, bool Records);

public sealed record RecordContains(int? Min, int? Max, bool Varying, QualifiedName? DependingOn);

public sealed record FileDescription(NodeSpan Span, SourceRange Range, bool IsSort, string Name) : SyntaxNode(Span, Range)
{
    public bool External { get; init; }

    public bool Global { get; init; }

    public BlockContains? Block { get; init; }

    public RecordContains? Record { get; init; }

    // STANDARD, OMITTED, or the data-names given.
    public IReadOnlyList<string> LabelRecords { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> DataRecords { get; init; } = Array.Empty<string>();

    public IReadOnlyList<KeyValuePair<string, string>> ValueOf { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    public string? RecordingMode { get; init; }

    public string? CodeSet { get; init; }

    public IReadOnlyList<DataEntry> Records { get; init; } = Array.Empty<DataEntry>();

    public IReadOnlyList<ErrorNode> Errors { get; init; } = Array.Empty<ErrorNode>();
}

public enum DataUsage
{
    Display,
    Binary,
    Comp5,
    PackedDecimal,
    Comp1,
    Comp2,
    Index,
    Pointer,
    Pointer32,
    ProcedurePointer,
    FunctionPointer,
    ObjectReference,
    National,
    Display1,
    Utf8,
}

public sealed record SignClause(bool Leading, bool Separate);

public sealed record OccursKey(bool Ascending, QualifiedName Name);

public sealed record OccursClause(int Min, int Max, bool HasTo, QualifiedName? DependingOn, IReadOnlyList<OccursKey> Keys, IReadOnlyList<string> IndexedBy);

public enum SyncKind
{
    Unspecified,
    Left,
    Right,
}

public sealed record ValueLiteral(TokenKind Kind, string Text, FigurativeKind Figurative, bool All);

public sealed record ValueEntry(ValueLiteral From, ValueLiteral? Thru);

public sealed record ValueClause(IReadOnlyList<ValueEntry> Values, ValueLiteral? FalseValue, string Raw);

public sealed record RenamesClause(QualifiedName From, QualifiedName? Thru);

public sealed record PictureClause(string Text, PictureInfo Info);

// One data description entry; clauses may appear in any order. Unit is the source unit of the level number.
public sealed record DataEntry(NodeSpan Span, SourceRange Range, int Level) : SyntaxNode(Span, Range)
{
    public string? Name { get; init; }

    public bool IsFiller { get; init; }

    public int Unit { get; init; }

    public string? Redefines { get; init; }

    public RenamesClause? Renames { get; init; }

    public PictureClause? Picture { get; init; }

    public DataUsage? Usage { get; init; }

    public SignClause? Sign { get; init; }

    public OccursClause? Occurs { get; init; }

    public SyncKind? Sync { get; init; }

    public bool Justified { get; init; }

    public bool BlankWhenZero { get; init; }

    public ValueClause? Value { get; init; }

    public bool External { get; init; }

    public bool Global { get; init; }

    public bool GroupUsageNational { get; init; }

    public IReadOnlyList<DataEntry> Children { get; init; } = Array.Empty<DataEntry>();

    // The entry's own tokens; Span is widened to cover the children of a group.
    public NodeSpan HeaderSpan { get; init; }

    public IReadOnlyList<ErrorNode> Errors { get; init; } = Array.Empty<ErrorNode>();

    public string DisplayName => Name ?? "FILLER";

    public IEnumerable<DataEntry> SelfAndDescendants()
    {
        var stack = new Stack<DataEntry>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            yield return e;
            for (var i = e.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(e.Children[i]);
            }
        }
    }
}
