using Starforge.Cobol.Copy;

namespace Starforge.Cobol.Lexing;

public enum TokenKind : byte
{
    Word,
    Figurative,
    IntegerLiteral,
    DecimalLiteral,
    FloatLiteral,
    AlphanumericLiteral,
    HexLiteral,
    NullTerminatedLiteral,
    NationalLiteral,
    NationalHexLiteral,
    DbcsLiteral,
    BooleanLiteral,
    BooleanHexLiteral,
    Utf8Literal,
    Utf8HexLiteral,
    PictureString,
    LevelNumber,
    Period,
    Comma,
    Semicolon,
    LeftParen,
    RightParen,
    Colon,
    Plus,
    Minus,
    Asterisk,
    Slash,
    Power,
    Equal,
    Greater,
    Less,
    GreaterOrEqual,
    LessOrEqual,
    NotEqual,
    Ampersand,
    PseudoTextDelimiter,
    ExecBlock,
    CompilerDirective,
    CommentEntry,
    Unknown,
}

public enum FigurativeKind : byte
{
    None,
    Zero,
    Space,
    HighValue,
    LowValue,
    Quote,
    Null,
    All,
}

public enum ExecKind : byte
{
    None,
    Cics,
    Sql,
    SqlIms,
    Dli,
    Other,
}

[Flags]
public enum TokenFlags : byte
{
    None = 0,
    FromReplacement = 1,
    Unterminated = 2,
}

// Kept as a small struct so 500k-line programs do not allocate millions of objects; text and origin
// are resolved through the owning TokenStream.
public readonly record struct Token(TokenKind Kind, int Start, int Length, byte Detail, TokenFlags Flags)
{
    public int End => Start + Length;

    public bool FromReplacement => (Flags & TokenFlags.FromReplacement) != 0;

    public FigurativeKind Figurative => Kind == TokenKind.Figurative ? (FigurativeKind)Detail : FigurativeKind.None;

    public ExecKind Exec => Kind == TokenKind.ExecBlock ? (ExecKind)Detail : ExecKind.None;

    public bool IsLiteral => Kind is >= TokenKind.IntegerLiteral and <= TokenKind.Utf8HexLiteral;
}

public sealed class TokenStream : IReadOnlyList<Token>
{
    private readonly Token[] _tokens;

    internal TokenStream(ExpandedSource source, Token[] tokens)
    {
        Source = source;
        _tokens = tokens;
    }

    public ExpandedSource Source { get; }

    public int Count => _tokens.Length;

    public Token this[int index] => _tokens[index];

    public string Text(Token token) => Source.Content.Substring(token.Start, token.Length);

    public string Text(int index) => Text(_tokens[index]);

    public ReadOnlySpan<char> Span(Token token) => Source.Content.AsSpan(token.Start, token.Length);

    public bool Is(Token token, string word) => Span(token).Equals(word, StringComparison.OrdinalIgnoreCase);

    public TextOrigin Origin(Token token) => Source.OriginOf(token.Start, token.End);

    public TextOrigin Origin(int index) => Origin(_tokens[index]);

    public IEnumerator<Token> GetEnumerator() => ((IEnumerable<Token>)_tokens).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _tokens.GetEnumerator();
}
