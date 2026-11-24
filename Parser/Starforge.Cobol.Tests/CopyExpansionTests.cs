using Starforge.Cobol.Copy;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public sealed class CopyExpansionTests
{
    [Fact]
    public void PseudoTextReplacingSwapsMultiTokenTextAndKeepsCopybookOrigin()
    {
        using var estate = new TestEstate();
        estate.Write("copy/CB.cpy",
            A("01 REC."),
            B("05 F1 PIC X(10)."));
        var r = estate.Program(B("COPY CB REPLACING ==PIC X(10)== BY ==PIC X(20)==."));

        r.KindsAndTexts().Should().Equal(
            (TokenKind.LevelNumber, "01"),
            (TokenKind.Word, "REC"),
            (TokenKind.Period, "."),
            (TokenKind.LevelNumber, "05"),
            (TokenKind.Word, "F1"),
            (TokenKind.Word, "PIC"),
            (TokenKind.PictureString, "X(20)"),
            (TokenKind.Period, "."));

        var pic = r.IndexOfText("X(20)");
        var origin = r.Tokens.Origin(pic);
        r.Tokens[pic].FromReplacement.Should().BeTrue();
        origin.FromReplacement.Should().BeTrue();
        (origin.Range.StartLine, origin.Range.StartColumn, origin.Range.EndLine, origin.Range.EndColumn).Should().Be((2, 18, 2, 26));
        Path.GetFileName(origin.Range.File).Should().Be("CB.cpy");
        var replacement = origin.ReplacementRange!.Value;
        Path.GetFileName(replacement.File).Should().Be("MAIN.cbl");
        (replacement.StartLine, replacement.StartColumn, replacement.EndColumn).Should().Be((1, 53, 57));

        r.Where("F1").Should().Be((2, 15, 2, 16, "CB.cpy"));
        r.Tokens[r.IndexOfText("F1")].FromReplacement.Should().BeFalse();
        r.Coverage.ByReason.Should().Contain("Directive(replaced-by-copy-replacing)", "PICX(10)".Length);
        r.Coverage.ByReason.Should().Contain("Directive(copy-statement)", "COPYCBREPLACING==PICX(10)==BY==PICX(20)==.".Length);
        r.Coverage.Unaccounted.Should().Be(0);
        r.Coverage.Conflicts.Should().Be(0);
    }

    [Fact]
    public void LeadingAndTrailingReplaceOnlyPartOfAWord()
    {
        using var estate = new TestEstate();
        estate.Write("copy/CB.cpy",
            B("05 ABC-NAME PIC X."),
            B("05 ABC-CODE PIC X."),
            B("05 XABC-NAME PIC X."));

        var leading = estate.Program(B("COPY CB REPLACING LEADING ==ABC== BY ==XYZ==."));
        leading.Tokens.Where(t => t.Kind == TokenKind.Word && leading.Tokens.Text(t) != "PIC").Select(t => leading.Tokens.Text(t))
            .Should().Equal("XYZ-NAME", "XYZ-CODE", "XABC-NAME");

        var trailing = estate.Program(B("COPY CB REPLACING TRAILING ==-NAME== BY ==-TITLE==."));
        trailing.Tokens.Where(t => t.Kind == TokenKind.Word && trailing.Tokens.Text(t) != "PIC").Select(t => trailing.Tokens.Text(t))
            .Should().Equal("ABC-TITLE", "ABC-CODE", "XABC-TITLE");
        trailing.Where("ABC-TITLE").Should().Be((1, 15, 1, 22, "CB.cpy"));
    }

    [Fact]
    public void ParenthesisedTagInsideAWordIsSplicedByPseudoTextReplacing()
    {
        using var estate = new TestEstate();
        estate.Write("copy/CB.cpy",
            B("05 FLG-(TAG)-OK PIC X."),
            B("MOVE (TAG)C OF (MAP)O TO Y."));
        var r = estate.Program(B("COPY CB REPLACING ==(TAG)== BY ==ACCT== ==(MAP)== BY ==SCRN==."));

        r.Texts().Should().Equal("05", "FLG-ACCT-OK", "PIC", "X", ".", "MOVE", "ACCTC", "OF", "SCRNO", "TO", "Y", ".");
        r.Where("FLG-ACCT-OK").Should().Be((1, 15, 1, 26, "CB.cpy"));
        r.TokenWithText("FLG-ACCT-OK").FromReplacement.Should().BeTrue();
    }

    [Fact]
    public void PseudoTextPatternMayEndWithASeparatorPeriod()
    {
        using var estate = new TestEstate();
        estate.Write("copy/PROC.cpy", B("MOVE 1 TO CNT."));
        var r = estate.Program(B("COPY PROC REPLACING ==CNT.== BY ==CNT IN AREA.==."));

        r.Texts().Should().Equal("MOVE", "1", "TO", "CNT", "IN", "AREA", ".");
    }

    [Fact]
    public void NestedCopyIsExpandedAndACycleIsReportedWithoutStoppingTheParse()
    {
        using var estate = new TestEstate();
        estate.Write("copy/A.cpy",
            A("01 A-REC."),
            B("COPY B."));
        estate.Write("copy/B.cpy",
            B("05 B-FLD PIC X."),
            B("COPY A."));
        var r = estate.Program(
            B("COPY A."),
            A("01 AFTER-CYCLE PIC X."));

        r.Texts().Should().Equal("01", "A-REC", ".", "05", "B-FLD", "PIC", "X", ".", "01", "AFTER-CYCLE", "PIC", "X", ".");
        r.Inclusions.Select(i => (i.Name, i.Status, i.ParentUnitId, i.UnitId)).Should().Equal(
            ("A", CopyStatus.Resolved, 0, 1),
            ("B", CopyStatus.Resolved, 1, 2),
            ("A", CopyStatus.Cycle, 2, -1));
        var cycle = r.Diagnostics.Should().ContainSingle(d => d.Code == "copy-cycle").Subject;
        cycle.Fingerprint.Should().Be("copy-cycle:A");
        Path.GetFileName(cycle.Range!.Value.File).Should().Be("B.cpy");
        (cycle.Range.Value.StartLine, cycle.Range.Value.StartColumn, cycle.Range.Value.EndColumn).Should().Be((2, 12, 18));
        r.Coverage.ByReason.Should().Contain("Unresolved(copy-cycle:A)", "COPYA.".Length);
        r.Coverage.Unaccounted.Should().Be(0);
    }

    [Fact]
    public void MissingCopybookIsUnresolvedAndParsingContinues()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            B("COPY NOPE."),
            A("01 NEXT-ITEM PIC X."));

        var missing = r.Diagnostics.Should().ContainSingle(d => d.Code == "missing-copybook").Subject;
        missing.Fingerprint.Should().Be("missing-copybook:NOPE");
        missing.Range!.Value.Should().Be(new Source.SourceRange(Path.Combine(estate.Root, "src", "MAIN.cbl"), 1, 12, 1, 21));
        r.Inclusions.Should().ContainSingle().Which.Status.Should().Be(CopyStatus.Missing);
        r.Coverage.Count(CoverageClass.Unresolved).Should().Be("COPYNOPE.".Length);
        r.Texts().Should().Equal("01", "NEXT-ITEM", "PIC", "X", ".");
    }

    [Fact]
    public void CopybookExtensionWinsOverAProgramWithTheSameName()
    {
        using var estate = new TestEstate();
        estate.Write("src/DUP.cbl", A("PROGRAM-ID. DUP."));
        estate.Write("copy/DUP.cpy", A("01 DUP-AREA PIC X."));
        var r = estate.Program(B("COPY DUP."));

        var inclusion = r.Inclusions.Should().ContainSingle().Subject;
        inclusion.ResolvedPath.Should().Be(Path.Combine(estate.Root, "copy", "DUP.cpy"));
        inclusion.SearchStage.Should().Be("estate-folder");
        r.Texts().Should().Equal("01", "DUP-AREA", "PIC", "X", ".");
    }

    [Fact]
    public void PreprocessedFolderIsTheLastResortAndCopyLibraryIsRecorded()
    {
        using var estate = new TestEstate();
        estate.Write(".preprocessed/DFHAID.cpy", A("01 DFHAID PIC X."));
        var r = estate.Program(B("COPY DFHAID OF SYSLIB SUPPRESS."));

        var inclusion = r.Inclusions.Should().ContainSingle().Subject;
        inclusion.SearchStage.Should().Be("preprocessed-fallback");
        inclusion.Library.Should().Be("SYSLIB");
        inclusion.Suppress.Should().BeTrue();
        r.Texts().Should().Equal("01", "DFHAID", "PIC", "X", ".");
    }

    [Fact]
    public void ExecSqlIncludeIsExpandedLikeCopy()
    {
        using var estate = new TestEstate();
        estate.Write("copy/SQLX.cpy", A("01 SQLX-AREA PIC X."));
        var r = estate.Program(
            B("EXEC SQL"),
            B("  INCLUDE SQLX"),
            B("END-EXEC."),
            A("01 AFTER-INCLUDE PIC X."));

        r.Texts().Should().Equal("01", "SQLX-AREA", "PIC", "X", ".", "01", "AFTER-INCLUDE", "PIC", "X", ".");
        r.Inclusions.Should().ContainSingle().Which.Kind.Should().Be(CopyStatementKind.SqlInclude);
        r.Coverage.ByReason.Should().Contain("Directive(sql-include)", "EXECSQLINCLUDESQLXEND-EXEC.".Length);
    }

    [Fact]
    public void ReplaceStatementAppliesUntilReplaceOff()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("REPLACE ==OLD-NAME== BY ==NEW-NAME==."),
            A("01 OLD-NAME PIC X."),
            A("REPLACE OFF."),
            A("01 OLD-NAME PIC X."));

        r.Texts().Should().Equal("01", "NEW-NAME", "PIC", "X", ".", "01", "OLD-NAME", "PIC", "X", ".");
        r.Where("NEW-NAME").Should().Be((2, 11, 2, 18, "MAIN.cbl"));
        r.Coverage.ByReason.Should().Contain("Directive(replace-statement)", "REPLACE==OLD-NAME==BY==NEW-NAME==.".Length + "REPLACEOFF.".Length)
            .And.Contain("Directive(replaced-by-replace-statement)", "OLD-NAME".Length);
        r.Coverage.Unaccounted.Should().Be(0);
    }

    [Fact]
    public void ReplaceStatementAlsoAppliesToCopiedText()
    {
        using var estate = new TestEstate();
        estate.Write("copy/CB.cpy", A("01 :PFX:-REC PIC X."));
        var r = estate.Program(
            A("REPLACE ==:PFX:== BY ==CUST==."),
            B("COPY CB."));

        r.Texts().Should().Equal("01", "CUST-REC", "PIC", "X", ".");
        r.Where("CUST-REC").Should().Be((1, 11, 1, 19, "CB.cpy"));
    }
}
