using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Syntax;

// Expressions and conditions carry only token spans; statements, paragraphs, sections and names also carry source ranges.
public abstract record Expr(NodeSpan Span);

public enum RefKind
{
    Data,
    File,
    Mnemonic,
}

public sealed record DataRef(NodeSpan Span, QualifiedName Name, IReadOnlyList<Expr> Subscripts, Expr? RefStart, Expr? RefLength) : Expr(Span)
{
    public RefKind Kind { get; init; }

    public bool AllSubscript { get; init; }

    public SourceRange Range { get; init; }
}

public sealed record AllSubscript(NodeSpan Span) : Expr(Span);

public sealed record LiteralExpr(NodeSpan Span, TokenKind Kind, string Text) : Expr(Span);

// ALL literal is Figurative.All with Of set to the repeated literal.
public sealed record FigurativeExpr(NodeSpan Span, FigurativeKind Figurative, Expr? Of) : Expr(Span);

public sealed record FunctionCall(NodeSpan Span, string Name, IReadOnlyList<Expr> Arguments, Expr? RefStart, Expr? RefLength) : Expr(Span);

public enum SpecialOperator
{
    LengthOf,
    AddressOf,
}

public sealed record SpecialRef(NodeSpan Span, SpecialOperator Operator, Expr Operand) : Expr(Span);

// DFHRESP(NORMAL) / DFHVALUE(...) are translated by the CICS precompiler to constants.
public sealed record CicsValue(NodeSpan Span, string Function, string Argument) : Expr(Span);

public sealed record UnaryExpr(NodeSpan Span, char Operator, Expr Operand) : Expr(Span);

public enum ArithmeticOperator
{
    Add,
    Subtract,
    Multiply,
    Divide,
    Power,
}

public sealed record BinaryExpr(NodeSpan Span, ArithmeticOperator Operator, Expr Left, Expr Right) : Expr(Span);

public abstract record Condition(NodeSpan Span);

public enum RelationalOperator
{
    Equal,
    NotEqual,
    Greater,
    Less,
    GreaterOrEqual,
    LessOrEqual,
}

// Abbreviated is true when the subject (and possibly the operator) was implied from an earlier relation.
public sealed record RelationCondition(NodeSpan Span, Expr Left, RelationalOperator Operator, Expr Right, bool Abbreviated) : Condition(Span);

// NUMERIC, ALPHABETIC, ALPHABETIC-LOWER, ALPHABETIC-UPPER, DBCS, KANJI, OMITTED or a SPECIAL-NAMES class name.
public sealed record ClassCondition(NodeSpan Span, Expr Subject, string Class, bool Not) : Condition(Span);

// POSITIVE, NEGATIVE or ZERO.
public sealed record SignCondition(NodeSpan Span, Expr Subject, string Sign, bool Not) : Condition(Span);

public sealed record ConditionNameCondition(NodeSpan Span, DataRef Name) : Condition(Span);

public sealed record NotCondition(NodeSpan Span, Condition Operand) : Condition(Span);

public sealed record AndCondition(NodeSpan Span, Condition Left, Condition Right) : Condition(Span);

public sealed record OrCondition(NodeSpan Span, Condition Left, Condition Right) : Condition(Span);

// A procedure name as written (Qualifier is the OF/IN section); Line is the program-file line of the reference.
public sealed record ProcedureRef(NodeSpan Span, string Name, string? Qualifier, int Line)
{
    // Where the reference appears; null outside any section/paragraph.
    public string? FromSection { get; init; }

    public SourceRange Range { get; init; }

    public string? FromParagraph { get; init; }
}

public abstract record Statement(NodeSpan Span, SourceRange Range) : SyntaxNode(Span, Range)
{
    // Pre-order: the statement, then every statement nested in its branches, handlers and inline bodies.
    public static IEnumerable<Statement> SelfAndNested(Statement statement)
    {
        yield return statement;
        foreach (var child in statement.Children().SelectMany(SelfAndNested))
        {
            yield return child;
        }
    }

    public IEnumerable<Statement> Children()
    {
        IEnumerable<Statement> handlers = HandlersOf(this).SelectMany(h => h.Statements);
        return this switch
        {
            IfStatement s => s.Then.Concat(s.Else ?? Array.Empty<Statement>()),
            EvaluateStatement s => s.Whens.SelectMany(w => w.Statements).Concat(s.Other ?? Array.Empty<Statement>()),
            PerformStatement s => s.Inline ?? Array.Empty<Statement>(),
            SearchStatement s => (s.AtEnd ?? Array.Empty<Statement>()).Concat(s.Whens.SelectMany(w => w.Statements)),
            _ => handlers,
        };
    }

    private static IReadOnlyList<ConditionalPhrase> HandlersOf(Statement s) => s switch
    {
        ComputeStatement c => c.Handlers,
        ArithmeticStatement c => c.Handlers,
        CallStatement c => c.Handlers,
        StringStatement c => c.Handlers,
        UnstringStatement c => c.Handlers,
        FileStatement c => c.Handlers,
        AcceptStatement c => c.Handlers,
        _ => Array.Empty<ConditionalPhrase>(),
    };
}

public enum HandlerKind
{
    AtEnd,
    InvalidKey,
    SizeError,
    Exception,
    Overflow,
    EndOfPage,
}

public sealed record ConditionalPhrase(HandlerKind Kind, bool Not, IReadOnlyList<Statement> Statements);

public sealed record ErrorStatement(NodeSpan Span, SourceRange Range, ErrorNode Error) : Statement(Span, Range);

public sealed record MoveStatement(NodeSpan Span, SourceRange Range, bool Corresponding, Expr Source, IReadOnlyList<Expr> Targets) : Statement(Span, Range);

public sealed record ArithmeticTarget(Expr Target, bool Rounded, string? RoundingMode);

public sealed record ComputeStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<ArithmeticTarget> Targets, Expr Value, IReadOnlyList<ConditionalPhrase> Handlers) : Statement(Span, Range);

public enum ArithmeticVerb
{
    Add,
    Subtract,
    Multiply,
    Divide,
}

// Operands precede the preposition (TO/FROM/BY/INTO); Second is the operand after it in the GIVING forms.
public sealed record ArithmeticStatement(
    NodeSpan Span,
    SourceRange Range,
    ArithmeticVerb Verb,
    bool Corresponding,
    IReadOnlyList<Expr> Operands,
    string? Preposition,
    Expr? Second,
    bool Giving,
    IReadOnlyList<ArithmeticTarget> Targets,
    Expr? Remainder,
    IReadOnlyList<ConditionalPhrase> Handlers) : Statement(Span, Range);

// Else is null when there is no ELSE phrase; NEXT SENTENCE appears as a NextSentenceStatement.
public sealed record IfStatement(NodeSpan Span, SourceRange Range, Condition Condition, IReadOnlyList<Statement> Then, IReadOnlyList<Statement>? Else, bool HasEndIf) : Statement(Span, Range);

public enum EvaluateOperandKind
{
    Expression,
    Condition,
    True,
    False,
    Any,
}

public sealed record EvaluateOperand(EvaluateOperandKind Kind, Expr? Value, Expr? Thru, Condition? Condition, bool Not);

public sealed record WhenClause(IReadOnlyList<IReadOnlyList<EvaluateOperand>> Alternatives, IReadOnlyList<Statement> Statements);

public sealed record EvaluateStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<EvaluateOperand> Subjects, IReadOnlyList<WhenClause> Whens, IReadOnlyList<Statement>? Other) : Statement(Span, Range);

public sealed record VaryingPhrase(Expr Variable, Expr From, Expr By, Condition Until);

public enum PerformKind
{
    Once,
    Times,
    Until,
    Varying,
}

public sealed record PerformStatement(
    NodeSpan Span,
    SourceRange Range,
    ProcedureRef? Target,
    ProcedureRef? Thru,
    PerformKind Kind,
    Expr? Times,
    bool TestAfter,
    Condition? Until,
    IReadOnlyList<VaryingPhrase> Varying,
    IReadOnlyList<Statement>? Inline) : Statement(Span, Range);

public sealed record GoToStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<ProcedureRef> Targets, Expr? DependingOn) : Statement(Span, Range);

public sealed record AlterStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<(ProcedureRef From, ProcedureRef To)> Alterations) : Statement(Span, Range);

public sealed record CallArgument(PassingMode Mode, Expr? Value)
{
    public bool Omitted => Value is null;
}

// Static when the target is a literal (Program holds its text); dynamic when it is an identifier.
public sealed record CallStatement(
    NodeSpan Span,
    SourceRange Range,
    Expr Target,
    string? Program,
    IReadOnlyList<CallArgument> Using,
    Expr? Returning,
    IReadOnlyList<ConditionalPhrase> Handlers) : Statement(Span, Range)
{
    public bool IsStatic => Program is not null;
}

public sealed record CancelStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<Expr> Programs) : Statement(Span, Range);

// Kind is empty for a plain EXIT, otherwise PROGRAM, PARAGRAPH, SECTION, PERFORM, PERFORM CYCLE or METHOD.
public sealed record ExitStatement(NodeSpan Span, SourceRange Range, string Kind) : Statement(Span, Range);

public sealed record GobackStatement(NodeSpan Span, SourceRange Range) : Statement(Span, Range);

public sealed record StopStatement(NodeSpan Span, SourceRange Range, Expr? Literal) : Statement(Span, Range)
{
    public bool Run => Literal is null;
}

public sealed record ContinueStatement(NodeSpan Span, SourceRange Range) : Statement(Span, Range);

public sealed record NextSentenceStatement(NodeSpan Span, SourceRange Range) : Statement(Span, Range);

public sealed record InitializeReplacing(string Category, Expr Value);

public sealed record InitializeStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<Expr> Targets, bool WithFiller, string? AllOrCategory, bool ToValue, bool ToDefault, IReadOnlyList<InitializeReplacing> Replacing) : Statement(Span, Range);

// One TALLYING/REPLACING/CONVERTING operation; Mode is CHARACTERS, ALL, LEADING, FIRST or (for CONVERTING) empty.
public sealed record InspectOperation(string Mode, Expr? Pattern, Expr? Replacement, Expr? Counter, IReadOnlyList<(bool Before, bool Initial, Expr Delimiter)> Boundaries);

public sealed record InspectStatement(NodeSpan Span, SourceRange Range, Expr Subject, IReadOnlyList<InspectOperation> Tallying, IReadOnlyList<InspectOperation> Replacing, InspectOperation? Converting) : Statement(Span, Range);

// Delimiter null means DELIMITED BY SIZE.
public sealed record StringSource(IReadOnlyList<Expr> Values, Expr? Delimiter, bool DelimitedBySize);

public sealed record StringStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<StringSource> Sources, Expr Into, Expr? Pointer, IReadOnlyList<ConditionalPhrase> Handlers) : Statement(Span, Range);

public sealed record UnstringTarget(Expr Into, Expr? DelimiterIn, Expr? CountIn);

public sealed record UnstringStatement(
    NodeSpan Span,
    SourceRange Range,
    Expr Source,
    IReadOnlyList<(bool All, Expr Delimiter)> Delimiters,
    IReadOnlyList<UnstringTarget> Targets,
    Expr? Pointer,
    Expr? Tallying,
    IReadOnlyList<ConditionalPhrase> Handlers) : Statement(Span, Range);

public enum SetKind
{
    To,
    UpBy,
    DownBy,
    ToTrue,
    ToFalse,
    ToOnOff,
    ToAddress,
}

// Value is null for TO TRUE/FALSE/ON/OFF; OnOff holds ON or OFF for mnemonic switches.
public sealed record SetStatement(NodeSpan Span, SourceRange Range, SetKind Kind, IReadOnlyList<Expr> Targets, Expr? Value, string? OnOff) : Statement(Span, Range);

public sealed record SearchWhen(Condition Condition, IReadOnlyList<Statement> Statements);

public sealed record SearchStatement(NodeSpan Span, SourceRange Range, bool All, DataRef Table, Expr? Varying, IReadOnlyList<Statement>? AtEnd, IReadOnlyList<SearchWhen> Whens) : Statement(Span, Range);

public sealed record OpenFile(string Mode, DataRef File);

public sealed record OpenStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<OpenFile> Files) : Statement(Span, Range);

public sealed record CloseStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<DataRef> Files) : Statement(Span, Range);

// Verb is READ, WRITE, REWRITE, DELETE, START, RETURN or RELEASE; Record is the file (READ/DELETE/START/RETURN) or record name.
public sealed record FileStatement(
    NodeSpan Span,
    SourceRange Range,
    string Verb,
    DataRef Target,
    Expr? Into,
    Expr? From,
    Expr? Key,
    RelationalOperator? KeyOperator,
    string? Advancing,
    Expr? AdvancingAmount,
    IReadOnlyList<ConditionalPhrase> Handlers) : Statement(Span, Range)
{
    public bool Next { get; init; }
}

// Device is the UPON/FROM mnemonic or environment name; Special is DATE/DAY/TIME/DAY-OF-WEEK and their YYYY variants.
public sealed record AcceptStatement(NodeSpan Span, SourceRange Range, Expr Target, string? Device, string? Special, IReadOnlyList<ConditionalPhrase> Handlers) : Statement(Span, Range);

public sealed record DisplayStatement(NodeSpan Span, SourceRange Range, IReadOnlyList<Expr> Values, string? Upon, bool NoAdvancing) : Statement(Span, Range);

public sealed record SortKey(bool Ascending, IReadOnlyList<Expr> Keys);

// SORT and MERGE; Input/Output procedures are ProcedureRef ranges, Using/Giving are files.
public sealed record SortStatement(
    NodeSpan Span,
    SourceRange Range,
    string Verb,
    DataRef File,
    IReadOnlyList<SortKey> Keys,
    bool Duplicates,
    IReadOnlyList<DataRef> Using,
    IReadOnlyList<DataRef> Giving,
    (ProcedureRef From, ProcedureRef? Thru)? InputProcedure,
    (ProcedureRef From, ProcedureRef? Thru)? OutputProcedure) : Statement(Span, Range);

public sealed record EntryStatement(NodeSpan Span, SourceRange Range, string Name, IReadOnlyList<CallArgument> Using) : Statement(Span, Range);

// EXEC ... END-EXEC in the procedure body; Parsed is the shared Starforge.Cobol.Exec result for the block token.
public sealed record ExecStatementSyntax(NodeSpan Span, SourceRange Range, ExecKind Kind, Exec.ExecStatement Parsed) : Statement(Span, Range);

public sealed record Sentence(NodeSpan Span, IReadOnlyList<Statement> Statements);

// StartLine/EndLine are program-file lines; code from a procedure-division COPY maps to the COPY statement's lines.
public sealed record Paragraph(NodeSpan Span, SourceRange Range, string Name, IReadOnlyList<Sentence> Sentences, int StartLine, int EndLine) : SyntaxNode(Span, Range);

public sealed record UseStatement(NodeSpan Span, string Text);

// Sentences before the first paragraph of a section (or of a section-less body) live in Leading.
public sealed record ProcedureSection(
    NodeSpan Span,
    SourceRange Range,
    string? Name,
    int? Priority,
    UseStatement? Use,
    IReadOnlyList<Sentence> Leading,
    IReadOnlyList<Paragraph> Paragraphs,
    int StartLine,
    int EndLine) : SyntaxNode(Span, Range);
