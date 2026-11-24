using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Syntax;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public sealed class SyntaxTests
{
    [Fact]
    public void IdentificationAttributesAndCommentParagraphs()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. 'PAYROLL' IS RECURSIVE PROGRAM."),
            A("AUTHOR. SOMEONE, SOMEWHERE."),
            A("DATE-WRITTEN. July 2022"),
            A("DATE-COMPILED."),
            A("    Today."),
            A("PROCEDURE DIVISION."));

        var id = r.Syntax.Programs.Single().Identification!;
        id.ProgramName.Should().Be("PAYROLL");
        id.ProgramNameIsLiteral.Should().BeTrue();
        id.Attributes.Should().Be(ProgramAttributes.Recursive);
        id.Paragraphs.Select(p => p.Name).Should().Equal("AUTHOR", "DATE-WRITTEN", "DATE-COMPILED");
        r.Tokens.Count(t => t.Kind == TokenKind.CommentEntry).Should().Be(3);
        r.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Should().BeEmpty();
    }

    [Fact]
    public void SpecialNamesClauses()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("ENVIRONMENT DIVISION."),
            A("CONFIGURATION SECTION."),
            A("SOURCE-COMPUTER. IBM-370 WITH DEBUGGING MODE."),
            A("OBJECT-COMPUTER. IBM-370."),
            A("SPECIAL-NAMES."),
            B("C01 IS TOP-OF-PAGE"),
            B("UPSI-0 ON STATUS IS SW-ON OFF STATUS IS SW-OFF"),
            B("CURRENCY SIGN IS 'E'"),
            B("DECIMAL-POINT IS COMMA"),
            B("CLASS HEXDIGIT IS '0' THRU '9' 'A' THRU 'F'"),
            B("SYMBOLIC CHARACTERS NUL IS 1 BEL TAB ARE 8 10."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("01  AMT PIC EEE9,99."),
            A("PROCEDURE DIVISION."));

        var config = r.Syntax.Programs.Single().Environment!.Configuration!;
        config.SourceComputer.Should().Be("IBM-370");
        config.DebuggingMode.Should().BeTrue();
        var sn = config.SpecialNames!;
        sn.DecimalPointIsComma.Should().BeTrue();
        sn.Currencies.Should().Equal(new CurrencySign("E", null));
        sn.Classes.Should().ContainSingle().Which.Values.Should().Equal(new ValueRange("'0'", "'9'"), new ValueRange("'A'", "'F'"));
        sn.SymbolicCharacters.Should().Equal(new SymbolicCharacter("NUL", 1, null), new SymbolicCharacter("BEL", 8, null), new SymbolicCharacter("TAB", 10, null));
        sn.Mnemonics.Should().Equal(new MnemonicName("C01", "TOP-OF-PAGE", null, null), new MnemonicName("UPSI-0", null, "SW-ON", "SW-OFF"));
        var amt = r.Programs.Single().Items.Single();
        amt.Length.Should().Be(7);
        amt.Scale.Should().Be(2);
        r.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Should().BeEmpty();
    }

    [Fact]
    public void SelectWithAllKeyClauses()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("ENVIRONMENT DIVISION."),
            A("INPUT-OUTPUT SECTION."),
            A("FILE-CONTROL."),
            B("SELECT OPTIONAL CUST-FILE ASSIGN TO CUSTDD"),
            B("    ORGANIZATION IS INDEXED"),
            B("    ACCESS MODE IS DYNAMIC"),
            B("    RECORD KEY IS CUST-ID"),
            B("    ALTERNATE RECORD KEY IS CUST-NAME WITH DUPLICATES"),
            B("    ALTERNATE KEY CUST-MAIL"),
            B("    FILE STATUS IS WS-STAT WS-VSAM."),
            B("SELECT REL-FILE ASSIGN 'REL.DAT' ORGANIZATION RELATIVE"),
            B("    ACCESS RANDOM RELATIVE KEY REL-KEY STATUS REL-STAT."),
            A("I-O-CONTROL."),
            B("SAME RECORD AREA FOR CUST-FILE REL-FILE."),
            A("PROCEDURE DIVISION."));

        var io = r.Syntax.Programs.Single().Environment!.InputOutput!;
        var cust = io.Files[0];
        cust.FileName.Should().Be("CUST-FILE");
        cust.Optional.Should().BeTrue();
        cust.Assign.Should().Equal("CUSTDD");
        cust.Organization.Should().Be("INDEXED");
        cust.AccessMode.Should().Be("DYNAMIC");
        cust.RecordKey!.Name.Should().Be("CUST-ID");
        cust.AlternateKeys.Select(k => (k.Key.Name, k.WithDuplicates)).Should().Equal(("CUST-NAME", true), ("CUST-MAIL", false));
        cust.FileStatus!.Name.Should().Be("WS-STAT");
        cust.VsamStatus!.Name.Should().Be("WS-VSAM");
        var rel = io.Files[1];
        rel.Assign.Should().Equal("REL.DAT");
        rel.Organization.Should().Be("RELATIVE");
        rel.AccessMode.Should().Be("RANDOM");
        rel.RelativeKey!.Name.Should().Be("REL-KEY");
        rel.FileStatus!.Name.Should().Be("REL-STAT");
        io.IoControl.Should().ContainSingle().Which.Kind.Should().Be("SAME");
        r.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Should().BeEmpty();
    }

    [Fact]
    public void FileDescriptionWithRecordVarying()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("ENVIRONMENT DIVISION."),
            A("INPUT-OUTPUT SECTION."),
            A("FILE-CONTROL."),
            B("SELECT VFILE ASSIGN TO VDD."),
            A("DATA DIVISION."),
            A("FILE SECTION."),
            A("FD  VFILE IS EXTERNAL"),
            B("RECORDING MODE IS V"),
            B("BLOCK CONTAINS 0 RECORDS"),
            B("RECORD IS VARYING IN SIZE FROM 10 TO 200 CHARACTERS"),
            B("    DEPENDING ON WS-LEN"),
            B("LABEL RECORDS ARE STANDARD"),
            B("DATA RECORD IS V-REC."),
            A("01  V-REC."),
            B("05  V-LEN PIC 9(4) COMP."),
            B("05  V-DATA PIC X(198)."),
            A("WORKING-STORAGE SECTION."),
            A("01  WS-LEN PIC 9(4) COMP."),
            A("PROCEDURE DIVISION."));

        var fd = r.Syntax.Programs.Single().Data!.Sections[0].Files.Single();
        fd.Name.Should().Be("VFILE");
        fd.External.Should().BeTrue();
        fd.RecordingMode.Should().Be("V");
        fd.Block.Should().Be(new BlockContains(null, 0, true));
        fd.Record!.Varying.Should().BeTrue();
        (fd.Record.Min, fd.Record.Max, fd.Record.DependingOn!.Name).Should().Be((10, 200, "WS-LEN"));
        fd.LabelRecords.Should().Equal("STANDARD");
        fd.DataRecords.Should().Equal("V-REC");
        var file = r.Programs.Single().Files.Single();
        file.Select!.FileName.Should().Be("VFILE");
        file.Records.Single().Length.Should().Be(200);
        file.Records.Single().FileName.Should().Be("VFILE");
        r.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Should().BeEmpty();
    }

    [Fact]
    public void ErrorRecoveryResyncsAtNextLevelNumber()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("01  REC."),
            B("05  GOOD-1 PIC X(2)."),
            B("05  BAD-1 PIC X(3) FROBNICATE 12 WIDGETS."),
            B("05  GOOD-2 PIC X(4)."),
            A("PROCEDURE DIVISION."));

        var error = r.Syntax.Programs.Single().AllErrors().Should().ContainSingle().Subject;
        error.Diagnostic.Code.Should().Be("unexpected-data-clause");
        error.Diagnostic.Fingerprint.Should().Be("unexpected-data-clause:FROBNICATE LIT WIDGETS");
        r.Tokens.Text(error.Span.FirstToken).Should().Be("FROBNICATE");
        error.Span.TokenCount.Should().Be(3);
        (error.Range.StartLine, error.Range.StartColumn).Should().Be((7, 31));
        var data = r.Programs.Single();
        data.Items.Select(i => i.Path).Should().Equal("REC", "REC.GOOD-1", "REC.BAD-1", "REC.GOOD-2");
        data.Items.Single(i => i.Path == "REC.GOOD-2").Offset.Should().Be(5);
        r.Coverage.Count(CoverageClass.Unsupported).Should().Be("FROBNICATE12WIDGETS".Length);
        r.Coverage.ByReason["Unsupported(syntax-error)"].Should().Be("FROBNICATE12WIDGETS".Length);
    }

    [Fact]
    public void ErrorRecoveryResyncsAtNextSectionHeader()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("GARBAGE HERE."),
            A("LINKAGE SECTION."),
            A("01  LK PIC X."),
            A("PROCEDURE DIVISION USING LK."));

        r.Syntax.Programs.Single().AllErrors().Should().ContainSingle().Which.Span.TokenCount.Should().Be(2);
        r.Programs.Single().Items.Single().Section.Should().Be(DataSectionKind.Linkage);
        r.Syntax.Programs.Single().Procedure!.Using.Should().Equal(new UsingParameter(PassingMode.Reference, QualifiedName.Simple("LK")));
    }

    [Fact]
    public void NestedProgramsAndEndProgram()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. OUTER."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("01  SHARED PIC X(4) GLOBAL."),
            A("PROCEDURE DIVISION."),
            B("CALL 'INNER'."),
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. INNER IS COMMON."),
            A("DATA DIVISION."),
            A("LOCAL-STORAGE SECTION."),
            A("01  OWN PIC 9."),
            A("PROCEDURE DIVISION RETURNING OWN."),
            B("MOVE SHARED TO SHARED."),
            A("END PROGRAM INNER."),
            A("END PROGRAM OUTER."),
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. SECOND."),
            A("PROCEDURE DIVISION."),
            B("GOBACK."),
            A("END PROGRAM SECOND."));

        r.Syntax.Programs.Select(p => p.Name).Should().Equal("OUTER", "SECOND");
        var outer = r.Syntax.Programs[0];
        outer.Nested.Should().ContainSingle().Which.Name.Should().Be("INNER");
        outer.EndProgramName.Should().Be("OUTER");
        outer.Nested[0].Identification!.Attributes.Should().Be(ProgramAttributes.Common);
        outer.Nested[0].Procedure!.Returning!.Name.Should().Be("OWN");
        r.Programs.Select(p => (p.Name, p.Parent?.Name)).Should().Equal(("OUTER", null), ("INNER", "OUTER"), ("SECOND", null));
        var inner = r.Programs[1];
        inner.Symbols.Resolve("SHARED").Item!.Path.Should().Be("SHARED");
        inner.Items.Single().Section.Should().Be(DataSectionKind.LocalStorage);
        r.Programs[2].Symbols.Resolve("SHARED").Status.Should().Be(Semantics.LookupStatus.NotFound);
        r.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Should().BeEmpty();
        r.Coverage.Count(CoverageClass.Lexed).Should().Be(0);
    }

    [Fact]
    public void EndProgramNameMismatchIsReported()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. ONE."),
            A("PROCEDURE DIVISION."),
            B("GOBACK."),
            A("END PROGRAM TWO."));

        r.Diagnostics.Should().ContainSingle(d => d.Code == "end-program-name-mismatch");
    }

    [Fact]
    public void ExecBlocksInWorkingStorageStayOpaque()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            B("EXEC SQL BEGIN DECLARE SECTION END-EXEC."),
            A("01  REC PIC X."),
            A("PROCEDURE DIVISION."));

        r.Syntax.Programs.Single().Data!.Sections.Single().Execs.Should().ContainSingle().Which.Kind.Should().Be(ExecKind.Sql);
        r.Programs.Single().Items.Single().Path.Should().Be("REC");
        r.Syntax.Programs.Single().AllErrors().Should().BeEmpty();
    }
}
