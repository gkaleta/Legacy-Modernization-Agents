using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Syntax;

// Token indices [FirstToken, EndToken) in the TokenStream and character offsets [Start, End) in the expanded source.
public readonly record struct NodeSpan(int FirstToken, int EndToken, int Start, int End)
{
    public static readonly NodeSpan Empty = new(0, 0, 0, 0);

    public int TokenCount => EndToken - FirstToken;

    public bool IsEmpty => EndToken <= FirstToken;
}

public abstract record SyntaxNode(NodeSpan Span, SourceRange Range);

// Text the parser could not interpret. The skipped tokens are accounted as Unsupported, never Parsed.
public sealed record ErrorNode(NodeSpan Span, SourceRange Range, Diagnostic Diagnostic) : SyntaxNode(Span, Range);

public sealed record QualifiedName(string Name, IReadOnlyList<string> Qualifiers)
{
    public static QualifiedName Simple(string name) => new(name, Array.Empty<string>());

    public override string ToString() => Qualifiers.Count == 0 ? Name : Name + " OF " + string.Join(" OF ", Qualifiers);

    // COBOL names are case-insensitive; compare qualifiers by content rather than list identity.
    public bool Equals(QualifiedName? other)
        => other is not null
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
            && Qualifiers.SequenceEqual(other.Qualifiers, StringComparer.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}

public sealed record CompilationUnitSyntax(
    NodeSpan Span,
    SourceRange Range,
    IReadOnlyList<ProgramUnit> Programs,
    IReadOnlyList<ErrorNode> Errors) : SyntaxNode(Span, Range)
{
    // Every program in source order, nested ones directly after their parent.
    public IEnumerable<ProgramUnit> AllPrograms() => Programs.SelectMany(p => p.SelfAndNested());
}

[Flags]
public enum ProgramAttributes
{
    None = 0,
    Initial = 1,
    Common = 2,
    Recursive = 4,
}

public sealed record ProgramUnit(
    NodeSpan Span,
    SourceRange Range,
    IdentificationDivision? Identification,
    EnvironmentDivision? Environment,
    DataDivision? Data,
    ProcedureDivision? Procedure,
    IReadOnlyList<ProgramUnit> Nested,
    string? EndProgramName,
    IReadOnlyList<ErrorNode> Errors) : SyntaxNode(Span, Range)
{
    public string? Name => Identification?.ProgramName;

    public IEnumerable<ProgramUnit> SelfAndNested()
    {
        yield return this;
        foreach (var n in Nested.SelectMany(n => n.SelfAndNested()))
        {
            yield return n;
        }
    }

    // Error nodes of this program's own divisions (nested programs report their own).
    public IEnumerable<ErrorNode> AllErrors()
        => Errors
            .Concat(Identification?.Errors ?? Array.Empty<ErrorNode>())
            .Concat(Environment?.Errors ?? Array.Empty<ErrorNode>())
            .Concat(Data?.AllErrors() ?? Enumerable.Empty<ErrorNode>())
            .Concat(Procedure?.Errors ?? Array.Empty<ErrorNode>());
}

public sealed record CommentParagraph(NodeSpan Span, SourceRange Range, string Name) : SyntaxNode(Span, Range);

public sealed record IdentificationDivision(
    NodeSpan Span,
    SourceRange Range,
    string? ProgramName,
    bool ProgramNameIsLiteral,
    ProgramAttributes Attributes,
    IReadOnlyList<CommentParagraph> Paragraphs,
    IReadOnlyList<ErrorNode> Errors) : SyntaxNode(Span, Range);

public sealed record EnvironmentDivision(
    NodeSpan Span,
    SourceRange Range,
    ConfigurationSection? Configuration,
    InputOutputSection? InputOutput,
    IReadOnlyList<ErrorNode> Errors) : SyntaxNode(Span, Range);

public sealed record ConfigurationSection(
    NodeSpan Span,
    SourceRange Range,
    string? SourceComputer,
    bool DebuggingMode,
    string? ObjectComputer,
    SpecialNamesParagraph? SpecialNames) : SyntaxNode(Span, Range);

public sealed record CurrencySign(string Literal, string? PictureSymbol);

public sealed record ValueRange(string From, string? Thru);

public sealed record ClassDefinition(string Name, IReadOnlyList<ValueRange> Values);

public sealed record SymbolicCharacter(string Name, int Ordinal, string? Alphabet);

public sealed record MnemonicName(string EnvironmentName, string? Mnemonic, string? OnStatus, string? OffStatus);

public sealed record AlphabetDefinition(string Name, string Specification);

public sealed record SpecialNamesParagraph(
    NodeSpan Span,
    SourceRange Range,
    bool DecimalPointIsComma,
    IReadOnlyList<CurrencySign> Currencies,
    IReadOnlyList<ClassDefinition> Classes,
    IReadOnlyList<SymbolicCharacter> SymbolicCharacters,
    IReadOnlyList<MnemonicName> Mnemonics,
    IReadOnlyList<AlphabetDefinition> Alphabets) : SyntaxNode(Span, Range);

public sealed record AlternateKey(QualifiedName Key, bool WithDuplicates);

public sealed record FileControlEntry(NodeSpan Span, SourceRange Range, string FileName) : SyntaxNode(Span, Range)
{
    public bool Optional { get; init; }

    public IReadOnlyList<string> Assign { get; init; } = Array.Empty<string>();

    // SEQUENTIAL, INDEXED, RELATIVE or LINE SEQUENTIAL; null means the default (sequential).
    public string? Organization { get; init; }

    public string? AccessMode { get; init; }

    public QualifiedName? RecordKey { get; init; }

    public IReadOnlyList<AlternateKey> AlternateKeys { get; init; } = Array.Empty<AlternateKey>();

    public QualifiedName? RelativeKey { get; init; }

    public QualifiedName? FileStatus { get; init; }

    public QualifiedName? VsamStatus { get; init; }

    public int? ReserveAreas { get; init; }

    public string? Padding { get; init; }

    public string? RecordDelimiter { get; init; }

    public string? Password { get; init; }
}

public sealed record IoControlEntry(NodeSpan Span, SourceRange Range, string Kind, IReadOnlyList<string> Operands) : SyntaxNode(Span, Range);

public sealed record InputOutputSection(
    NodeSpan Span,
    SourceRange Range,
    IReadOnlyList<FileControlEntry> Files,
    IReadOnlyList<IoControlEntry> IoControl) : SyntaxNode(Span, Range);

public enum PassingMode
{
    Reference,
    Value,
    Content,
}

public sealed record UsingParameter(PassingMode Mode, QualifiedName Name);

// Body is the token range after the header; Errors holds header and body error nodes (statement-level ones included).
public sealed record ProcedureDivision(
    NodeSpan Span,
    SourceRange Range,
    IReadOnlyList<UsingParameter> Using,
    QualifiedName? Returning,
    NodeSpan Body,
    IReadOnlyList<ErrorNode> Errors) : SyntaxNode(Span, Range)
{
    public IReadOnlyList<ProcedureSection> Declaratives { get; init; } = Array.Empty<ProcedureSection>();

    // The first section has a null Name when the body (or its start) is not inside a SECTION.
    public IReadOnlyList<ProcedureSection> Sections { get; init; } = Array.Empty<ProcedureSection>();

    public IReadOnlyList<DataRef> DataReferences { get; init; } = Array.Empty<DataRef>();

    public IReadOnlyList<ProcedureRef> ProcedureReferences { get; init; } = Array.Empty<ProcedureRef>();

    public IEnumerable<ProcedureSection> AllSections() => Declaratives.Concat(Sections);

    public IEnumerable<Paragraph> AllParagraphs() => AllSections().SelectMany(s => s.Paragraphs);

    public IEnumerable<Statement> AllStatements()
        => AllSections().SelectMany(s => s.Leading.Concat(s.Paragraphs.SelectMany(p => p.Sentences)))
            .SelectMany(s => s.Statements).SelectMany(Statement.SelfAndNested);
}

// EXEC ... END-EXEC kept opaque; its inner text is parsed by Starforge.Cobol.Exec.
public sealed record ExecStatement(NodeSpan Span, SourceRange Range, ExecKind Kind) : SyntaxNode(Span, Range);
