using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public sealed class LexerTests
{
    [Fact]
    public void EveryLiteralKindIsRecognised()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("PROCEDURE DIVISION."),
            B("MOVE 'IT''S' \"SAY \"\"HI\"\"\" X'C1F0' N'AB' NX'0041' G'XY'"),
            B("Z'NUL' B'1' BX'F' U'UTF' UX'41' -12 +3.5 .25 1.5E+03 12,"));

        r.KindsAndTexts().Skip(4).Should().Equal(
            (TokenKind.AlphanumericLiteral, "'IT''S'"),
            (TokenKind.AlphanumericLiteral, "\"SAY \"\"HI\"\"\""),
            (TokenKind.HexLiteral, "X'C1F0'"),
            (TokenKind.NationalLiteral, "N'AB'"),
            (TokenKind.NationalHexLiteral, "NX'0041'"),
            (TokenKind.DbcsLiteral, "G'XY'"),
            (TokenKind.NullTerminatedLiteral, "Z'NUL'"),
            (TokenKind.BooleanLiteral, "B'1'"),
            (TokenKind.BooleanHexLiteral, "BX'F'"),
            (TokenKind.Utf8Literal, "U'UTF'"),
            (TokenKind.Utf8HexLiteral, "UX'41'"),
            (TokenKind.IntegerLiteral, "-12"),
            (TokenKind.DecimalLiteral, "+3.5"),
            (TokenKind.DecimalLiteral, ".25"),
            (TokenKind.FloatLiteral, "1.5E+03"),
            (TokenKind.IntegerLiteral, "12"),
            (TokenKind.Comma, ","));
        r.Coverage.Count(CoverageClass.Unsupported).Should().Be(0);
    }

    [Fact]
    public void FigurativeConstantsAndAllLiteral()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("PROCEDURE DIVISION."),
            B("MOVE ZEROES SPACE HIGH-VALUES LOW-VALUE QUOTES NULLS"),
            B("     ALL '*' ALL ZERO."));

        var figuratives = r.Tokens.Where(t => t.Kind == TokenKind.Figurative).Select(t => (t.Figurative, r.Tokens.Text(t))).ToList();
        figuratives.Should().Equal(
            (FigurativeKind.Zero, "ZEROES"),
            (FigurativeKind.Space, "SPACE"),
            (FigurativeKind.HighValue, "HIGH-VALUES"),
            (FigurativeKind.LowValue, "LOW-VALUE"),
            (FigurativeKind.Quote, "QUOTES"),
            (FigurativeKind.Null, "NULLS"),
            (FigurativeKind.All, "ALL"),
            (FigurativeKind.All, "ALL"),
            (FigurativeKind.Zero, "ZERO"));
        r.TokenWithText("'*'").Kind.Should().Be(TokenKind.AlphanumericLiteral);
    }

    [Fact]
    public void PictureStringsKeepInternalPeriodsAndCommasButNotTheTerminator()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("01 A PIC 9(5)."),
            A("01 B PIC 9.99."),
            A("01 C PICTURE IS S9(3)V99, VALUE 1."),
            A("01 D PIC $$,$$9.99."),
            A("01 E PIC X(10) VALUE SPACES."));

        r.Tokens.Where(t => t.Kind == TokenKind.PictureString).Select(t => r.Tokens.Text(t))
            .Should().Equal("9(5)", "9.99", "S9(3)V99", "$$,$$9.99", "X(10)");
        r.Tokens.Count(t => t.Kind == TokenKind.Period).Should().Be(5);
        r.Tokens.Count(t => t.Kind == TokenKind.Comma).Should().Be(1);
        r.Where("9.99").Should().Be((2, 17, 2, 20, "MAIN.cbl"));
    }

    [Fact]
    public void LevelNumbersOnlyInTheDataDivision()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("01 GRP."),
            B("05 FLD PIC 9."),
            B("88 FLD-ON VALUE 1."),
            A("77 CNT PIC 9."),
            A("PROCEDURE DIVISION."),
            B("MOVE 1 TO CNT."),
            B("ADD 05 TO CNT."));

        r.Tokens.Where(t => t.Kind == TokenKind.LevelNumber).Select(t => r.Tokens.Text(t)).Should().Equal("01", "05", "88", "77");
        r.Tokens.Where(t => t.Kind == TokenKind.IntegerLiteral).Select(t => r.Tokens.Text(t)).Should().Equal("1", "1", "05");
    }

    [Fact]
    public void OperatorsAndReferenceModificationColon()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("PROCEDURE DIVISION."),
            B("COMPUTE X = A ** 2 + B * (C - D) / E."),
            B("IF X >= 1 AND X <= 2 AND X <> 3 AND X > 4 AND X < 5"),
            B("MOVE Y(1:2) TO Z; END-IF."));

        r.Tokens.Where(t => t.Kind is not (TokenKind.Word or TokenKind.IntegerLiteral or TokenKind.Period))
            .Select(t => t.Kind).Should().Equal(
                TokenKind.Equal, TokenKind.Power, TokenKind.Plus, TokenKind.Asterisk, TokenKind.LeftParen, TokenKind.Minus,
                TokenKind.RightParen, TokenKind.Slash,
                TokenKind.GreaterOrEqual, TokenKind.LessOrEqual, TokenKind.NotEqual, TokenKind.Greater, TokenKind.Less,
                TokenKind.LeftParen, TokenKind.Colon, TokenKind.RightParen, TokenKind.Semicolon);
        r.Texts().Should().ContainInOrder("Y", "(", "1", ":", "2", ")");
    }

    [Fact]
    public void ExecBlocksAreSingleTokensAcrossLinesAndIgnoreEndExecInsideLiteralsAndSqlComments()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("PROCEDURE DIVISION."),
            B("EXEC CICS SEND TEXT FROM('END-EXEC')"),
            B("     LENGTH(8)"),
            B("END-EXEC."),
            B("EXEC SQL SELECT A -- END-EXEC in a comment"),
            B("  INTO :H FROM T END-EXEC"),
            B("GOBACK."));

        var execs = r.Tokens.Where(t => t.Kind == TokenKind.ExecBlock).ToList();
        execs.Select(t => t.Exec).Should().Equal(ExecKind.Cics, ExecKind.Sql);
        var cics = r.Tokens.Origin(execs[0]).Range;
        (cics.StartLine, cics.StartColumn, cics.EndLine, cics.EndColumn).Should().Be((2, 12, 4, 19));
        var sql = r.Tokens.Origin(execs[1]).Range;
        (sql.StartLine, sql.StartColumn, sql.EndLine, sql.EndColumn).Should().Be((5, 12, 6, 36));
        r.Texts().Should().EndWith(new[] { "GOBACK", "." });
        r.Texts().Count(t => t == ".").Should().Be(3);

        var cicsChars = "EXECCICSSENDTEXTFROM('END-EXEC')LENGTH(8)END-EXEC".Length;
        var sqlChars = "EXECSQLSELECTA--END-EXECinacommentINTO:HFROMTEND-EXEC".Length;
        r.Coverage.ByReason.Should().Contain("Unsupported(exec-not-yet-parsed)", cicsChars + sqlChars);
    }

    [Fact]
    public void ReadyTraceIsADirective()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("PROCEDURE DIVISION."),
            B("READY TRACE."),
            B("RESET TRACE."));

        r.Tokens.Count(t => t.Kind == TokenKind.CompilerDirective).Should().Be(2);
        r.Coverage.ByReason.Should().Contain("Directive(trace-statement)", "READYTRACE".Length + "RESETTRACE".Length);
    }

    [Fact]
    public void WordsAreCaseInsensitiveButKeepTheirOriginalText()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("procedure division."),
            B("Move ws_count-1 to Out-Fld."));

        r.Texts().Should().Equal("procedure", "division", ".", "Move", "ws_count-1", "to", "Out-Fld", ".");
        r.Tokens.Is(r.Tokens[3], "MOVE").Should().BeTrue();
        r.Tokens.All(t => t.Kind is TokenKind.Word or TokenKind.Period).Should().BeTrue();
    }
}
