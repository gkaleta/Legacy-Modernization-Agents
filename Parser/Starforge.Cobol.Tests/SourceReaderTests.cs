using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public sealed class SourceReaderTests
{
    [Fact]
    public void ContinuedAlphanumericLiteralRunsToColumn72AndResumesAfterTheQuote()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("01 X PIC X(90) VALUE 'ABCDEFG"),
            Indicator('-', "    'HIJ'."));

        var literal = "'ABCDEFG" + new string(' ', 36) + "HIJ'";
        r.KindsAndTexts().Should().Equal(
            (TokenKind.LevelNumber, "01"),
            (TokenKind.Word, "X"),
            (TokenKind.Word, "PIC"),
            (TokenKind.PictureString, "X(90)"),
            (TokenKind.Word, "VALUE"),
            (TokenKind.AlphanumericLiteral, literal),
            (TokenKind.Period, "."));
        r.Where(literal).Should().Be((1, 29, 2, 16, "MAIN.cbl"));
        r.Coverage.ByReason.Should().Contain("Skipped(continuation-quote)", 1);
        r.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ContinuedWordJoinsWithoutSeparator()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("MOVE ABC"),
            Indicator('-', "    DEF TO X."));

        r.Texts().Should().Equal("MOVE", "ABCDEF", "TO", "X", ".");
        r.Where("ABCDEF").Should().Be((1, 13, 2, 14, "MAIN.cbl"));
    }

    [Fact]
    public void DebuggingLinesAreCommentsUnlessDebuggingModeIsDeclared()
    {
        using var estate = new TestEstate();
        var plain = estate.Program(
            A("PROCEDURE DIVISION."),
            Indicator('D', "    DISPLAY 'DBG'."),
            B("STOP RUN."));

        plain.Texts().Should().Equal("PROCEDURE", "DIVISION", ".", "STOP", "RUN", ".");
        plain.DebuggingMode.Should().BeFalse();
        plain.Coverage.ByReason.Should().Contain("Comment(debugging-line)", "DISPLAY'DBG'.".Length);

        var debug = estate.Program(
            A("ENVIRONMENT DIVISION."),
            A("CONFIGURATION SECTION."),
            A("SOURCE-COMPUTER. IBM-370 WITH DEBUGGING MODE."),
            A("PROCEDURE DIVISION."),
            Indicator('D', "    DISPLAY 'DBG'."),
            B("STOP RUN."));

        debug.DebuggingMode.Should().BeTrue();
        debug.Where("DISPLAY").Should().Be((5, 12, 5, 18, "MAIN.cbl"));
        debug.Coverage.ByReason.Keys.Should().NotContain("Comment(debugging-line)");
    }

    [Fact]
    public void SequenceAndIdentificationAreasAreExcludedFromTheProgramArea()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            Line("000100", ' ', "IDENTIFICATION DIVISION.", "ABCD1234"),
            Line("000200", '*', "COMMENT", "ABCD1234"),
            Line("000300", ' ', "PROGRAM-ID. T.", "ABCD1234"));

        r.Texts().Should().Equal("IDENTIFICATION", "DIVISION", ".", "PROGRAM-ID", ".", "T", ".");
        r.Coverage.SequenceArea.Should().Be(18);
        r.Coverage.IdentificationArea.Should().Be(24);
        r.Coverage.IndicatorArea.Should().Be(1);
        r.Coverage.Total.Should().Be("IDENTIFICATIONDIVISION.".Length + "COMMENT".Length + "PROGRAM-ID.T.".Length);
        r.Coverage.Count(CoverageClass.Comment).Should().Be(7);
        r.Coverage.Unaccounted.Should().Be(0);
    }

    [Fact]
    public void TabsExpandToEightColumnStopsBeforeColumnRules()
    {
        using var estate = new TestEstate();
        var r = estate.Program("\tMOVE A TO B.");

        r.Where("MOVE").Should().Be((1, 9, 1, 12, "MAIN.cbl"));
        r.Diagnostics.Should().ContainSingle(d => d.Code == "tab-expanded").Which.Severity.Should().Be(DiagnosticSeverity.Info);
    }

    [Fact]
    public void TabLinesThatWouldOverflowColumn72FallBackToOneSpacePerTab()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            Fixed.A("01  REC."),
            "\t\t     05  CUST-FIRST-NAME                         PIC X(25).",
            "\t\t     05  OK PIC X.");

        var warning = r.Diagnostics.Should().ContainSingle(d => d.Code == "tab-expanded-compact").Subject;
        warning.Severity.Should().Be(DiagnosticSeverity.Warning);
        warning.Range!.Value.StartLine.Should().Be(2);
        warning.Fingerprint.Should().Be("tab-expanded-compact");
        r.Diagnostics.Should().NotContain(d => d.Code == "tab-overflow");
        r.Texts().Should().Contain("X(25)");
        r.Where("CUST-FIRST-NAME").Should().Be((2, 12, 2, 26, "MAIN.cbl"));
        r.Where("OK").Should().Be((3, 26, 3, 27, "MAIN.cbl"));
    }

    [Fact]
    public void TabLinesThatOverflowEvenWithOneSpacePerTabAreReported()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            Fixed.A("01  REC."),
            "\t" + new string(' ', 10) + "05  CUST-FIRST-NAME" + new string(' ', 35) + "PIC X(25).");

        r.Diagnostics.Select(d => d.Code).Should().Contain(new[] { "tab-expanded-compact", "tab-overflow" });
        r.Diagnostics.Single(d => d.Code == "tab-overflow").Severity.Should().Be(DiagnosticSeverity.Warning);
        r.Texts().Should().Contain("X(2");
    }

    [Fact]
    public void FreeFormatSourceKeepsTextBeyondColumn72AndDropsFloatingComments()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            ">>SOURCE FORMAT FREE",
            "IDENTIFICATION DIVISION.",
            "PROGRAM-ID. T.",
            "PROCEDURE DIVISION.",
            "*> whole-line comment",
            "DISPLAY 'HELLO' *> trailing comment",
            new string(' ', 80) + "STOP RUN.");

        r.Program.Source.InitialFormat.Should().Be(Source.SourceFormat.Free);
        r.Texts().Should().Equal("IDENTIFICATION", "DIVISION", ".", "PROGRAM-ID", ".", "T", ".", "PROCEDURE", "DIVISION", ".", "DISPLAY", "'HELLO'", "STOP", "RUN", ".");
        r.Where("DISPLAY").Should().Be((6, 1, 6, 7, "MAIN.cbl"));
        r.Where("STOP").Should().Be((7, 81, 7, 84, "MAIN.cbl"));
        r.Coverage.ByReason.Should().Contain("Directive(compiler-directive)", ">>SOURCEFORMATFREE".Length);
        r.Coverage.Count(CoverageClass.Comment).Should().Be("*>whole-linecomment".Length + "*>trailingcomment".Length);
        r.Coverage.Unaccounted.Should().Be(0);
    }

    [Fact]
    public void AuthorCommentEntryWithApostropheIsCommentNotUnterminatedLiteral()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. T."),
            A("AUTHOR. James O'Grady."),
            B("Second line of the entry."),
            A("ENVIRONMENT DIVISION."));

        r.Diagnostics.Should().NotContain(d => d.Severity == DiagnosticSeverity.Error);
        r.KindsAndTexts().Should().Contain((TokenKind.CommentEntry, "James O'Grady.\n    Second line of the entry."));
        r.Coverage.ByReason.Should().Contain("Comment(comment-entry)", "JamesO'Grady.".Length + "Secondlineoftheentry.".Length);
        r.Texts().Should().EndWith(new[] { "ENVIRONMENT", "DIVISION", "." });
    }

    [Fact]
    public void ListingAndCblDirectivesAreDirectivesNotTokens()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            "       CBL SQL",
            A("IDENTIFICATION DIVISION."),
            B("EJECT"),
            Indicator('*', "CONTROL NOSOURCE"),
            A("PROGRAM-ID. T."));

        r.Texts().Should().Equal("IDENTIFICATION", "DIVISION", ".", "PROGRAM-ID", ".", "T", ".");
        r.Coverage.ByReason.Should().Contain("Directive(cbl-process-statement)", "CBLSQL".Length)
            .And.Contain("Directive(listing-directive)", "EJECT".Length)
            .And.Contain("Directive(control-statement)", "CONTROLNOSOURCE".Length);
    }
}
