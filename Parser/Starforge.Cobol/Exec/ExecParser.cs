using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Exec;

// Entry point M3 calls for each ExecBlock token. Pure: the same block text always yields the same statement.
public static class ExecParser
{
    public static ExecStatement Parse(TokenStream tokens, int tokenIndex)
    {
        var token = tokens[tokenIndex];
        if (token.Kind != TokenKind.ExecBlock)
        {
            throw new ArgumentException($"Token {tokenIndex} is {token.Kind}, not an EXEC block", nameof(tokenIndex));
        }

        var source = tokens.Source;
        var start = token.Start;
        SourceRange Locate(int s, int e) => source.OriginOf(start + s, start + e).Range;
        return Parse(tokens.Text(token), token.Exec, tokens.Origin(token).Range, start, Locate, (token.Flags & TokenFlags.Unterminated) != 0);
    }

    // blockText runs from EXEC (or EXECUTE) through END-EXEC; locate maps block-relative offsets to source ranges.
    public static ExecStatement Parse(string blockText, ExecKind kind, SourceRange blockRange, int expandedStart = 0, ExecRangeLocator? locate = null, bool unterminated = false)
    {
        var innerStart = SkipWord(blockText, SkipWord(blockText, 0));
        var innerEnd = unterminated ? blockText.Length : EndExecAt(blockText);
        if (innerEnd < innerStart)
        {
            innerEnd = innerStart;
        }

        var ctx = new ExecBlockContext(blockText, kind, blockRange, expandedStart, locate, innerStart, innerEnd);
        var kindWord = blockText[SkipBlanks(blockText, SkipWord(blockText, 0))..innerStart].Trim().ToUpperInvariant();
        if (unterminated)
        {
            // The lexer already reported exec-without-end-exec; the block stays Unsupported under that code.
            return Unknown(ctx, kindWord, "exec-without-end-exec");
        }

        if (innerEnd == blockText.Length && kind != ExecKind.Other)
        {
            ctx.Report("exec-malformed-block", DiagnosticSeverity.Error, "EXEC block does not end with END-EXEC", 0, blockText.Length, "EXEC " + kindWord);
            return Unknown(ctx, kindWord, "exec-malformed-block");
        }

        switch (kind)
        {
            case ExecKind.Cics:
                return CicsParser.Parse(ctx);
            case ExecKind.Sql:
            case ExecKind.SqlIms:
                return SqlParser.Parse(ctx);
            case ExecKind.Dli:
                return DliParser.Parse(ctx);
            default:
                ctx.Report("exec-unknown-kind", DiagnosticSeverity.Error, $"EXEC {kindWord} is not a supported interface", 0, Math.Max(1, innerStart), kindWord);
                return Unknown(ctx, kindWord, "exec-unknown-kind");
        }
    }

    private static UnknownExec Unknown(ExecBlockContext ctx, string name, string reason) => new()
    {
        Kind = ctx.Kind,
        Status = ExecStatus.Unsupported,
        Range = ctx.Range,
        ExpandedStart = ctx.ExpandedStart,
        ExpandedEnd = ctx.ExpandedEnd,
        Diagnostics = ctx.Diagnostics,
        Name = name,
        UnsupportedReason = reason,
    };

    private static int SkipBlanks(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int SkipWord(string text, int i)
    {
        i = SkipBlanks(text, i);
        while (i < text.Length && CobolishTokenizer.IsWordChar(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int EndExecAt(string text)
    {
        var end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        const string word = "END-EXEC";
        return end >= word.Length && text.AsSpan(end - word.Length, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase)
            ? end - word.Length
            : text.Length;
    }
}
