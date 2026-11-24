using Starforge.Cobol.Copy;
using Starforge.Cobol.Deps;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public class DepsTests
{
    private static ProgramDependencies Deps(TestEstate estate, params string[] body)
    {
        var result = estate.Program(new[]
        {
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("01  WS-FIXED PIC X(8) VALUE 'FIXEDPGM'."),
            A("01  WS-DYN PIC X(8) VALUE 'FIRSTPGM'."),
            A("01  WS-TRAN PIC X(4)."),
            A("PROCEDURE DIVISION."),
        }.Concat(body).ToArray());
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        return DependencyExtractor.Extract(result);
    }

    [Fact]
    public void CallIsStaticForALiteralOrAnUnchangedConstantAndDynamicOtherwise()
    {
        using var estate = new TestEstate();
        var deps = Deps(estate,
            B("CALL 'LITPGM'."),
            B("CALL WS-FIXED."),
            B("MOVE 'OTHERPGM' TO WS-DYN."),
            B("CALL WS-DYN."),
            B("GOBACK."));

        deps.Programs.Should().Equal(
            new ProgramReference(DependencyKind.Call, "LITPGM", true, null, 9),
            new ProgramReference(DependencyKind.Call, "FIXEDPGM", true, "WS-FIXED", 10),
            new ProgramReference(DependencyKind.Call, "WS-DYN", false, null, 12));
        deps.Presence["CALL"].Should().Be(3);
    }

    [Fact]
    public void CicsLinkXctlAndTransactionTransfersKeepLiteralsStaticAndDataNamesDynamic()
    {
        using var estate = new TestEstate();
        var deps = Deps(estate,
            B("EXEC CICS LINK PROGRAM('LNKPGM') END-EXEC."),
            B("EXEC CICS LINK PROGRAM(WS-FIXED) END-EXEC."),
            B("MOVE 'NEXTPGM' TO WS-DYN."),
            B("EXEC CICS XCTL PROGRAM(WS-DYN) END-EXEC."),
            B("MOVE 'T001' TO WS-TRAN."),
            B("EXEC CICS RUN TRANSID(WS-TRAN) END-EXEC."),
            B("EXEC CICS RETURN TRANSID('T002') END-EXEC."));

        deps.Programs.Should().Equal(
            new ProgramReference(DependencyKind.CicsLink, "LNKPGM", true, null, 9),
            new ProgramReference(DependencyKind.CicsLink, "FIXEDPGM", true, "WS-FIXED", 10),
            new ProgramReference(DependencyKind.CicsXctl, "WS-DYN", false, null, 12));
        deps.Transfers.Should().Equal(
            new ControlTransfer("XCTL", "WS-DYN", false, null, 12),
            new ControlTransfer("RUN", "WS-TRAN", false, null, 14),
            new ControlTransfer("RETURN", "T002", true, null, 15));
    }

    [Fact]
    public void CopybooksAreNamedByStemWithALowerCaseCpyExtension()
    {
        using var estate = new TestEstate();
        estate.Write("copy/CPYA.CPY", A("01  CPYA-REC PIC X."));
        var result = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("COPY CPYA."),
            A("COPY NOSUCH."),
            A("PROCEDURE DIVISION."),
            B("GOBACK."));

        DependencyExtractor.Extract(result).Copybooks.Select(c => (c.Name, c.Member, c.Kind, c.Status, c.Line)).Should().Equal(
            ("CPYA.cpy", "CPYA", DependencyKind.Copy, CopyStatus.Resolved, 5),
            ("NOSUCH.cpy", "NOSUCH", DependencyKind.Copy, CopyStatus.Missing, 6));
    }

    private static string[] RunLoop(params string[] write) => new[]
    {
        A("IDENTIFICATION DIVISION."),
        A("PROGRAM-ID. MAIN."),
        A("DATA DIVISION."),
        A("WORKING-STORAGE SECTION."),
        A("01  WS-TRAN PIC X(4)."),
        A("01  WS-I PIC 9 VALUE 0."),
        A("PROCEDURE DIVISION."),
        B("PERFORM VARYING WS-I FROM 1 BY 1 UNTIL WS-I > 3"),
    }.Concat(write).Concat(new[]
    {
        B("    EXEC CICS RUN TRANSID(WS-TRAN) END-EXEC"),
        B("END-PERFORM."),
        B("GOBACK."),
    }).ToArray();

    private static CicsDefinitions Definitions(TestEstate estate)
    {
        estate.Write("cics/defs.yaml",
            "resourceDefinitions:",
            "  - transaction:",
            "      name: OCR1",
            "      program: AGENCY1",
            "  - transaction:",
            "      name: OCR2",
            "      group: G",
            "      program: AGENCY2",
            "  - program:",
            "      name: AGENCY3");
        estate.Write("cics/defs.csd",
            "DEFINE TRANSACTION(OCR3) GROUP(G)",
            "       PROGRAM(AGENCY3) DESCRIPTION(THIRD)",
            "DEFINE PROGRAM(AGENCY3) GROUP(G)",
            "DEFINE TRANSACTION(OCR2) GROUP(H) PROGRAM(OTHER)");
        return CicsDefinitions.Discover(estate.Root);
    }

    [Fact]
    public void CicsDefinitionsReadCsdAndYamlAndNeverResolveAConflictingTransaction()
    {
        using var estate = new TestEstate();
        var cics = Definitions(estate);

        cics.Count.Should().Be(3);
        cics.Transaction("OCR1").Should().Be(new CicsDefinitions.Binding("AGENCY1", Path.Combine(estate.Root, "cics", "defs.yaml")));
        cics.Transaction("OCR3").Should().Be(new CicsDefinitions.Binding("AGENCY3", Path.Combine(estate.Root, "cics", "defs.csd")));
        cics.Transaction("OCR2").Should().BeNull();
    }

    [Fact]
    public void RunTransidFromABoundedStringLoopResolvesEachTransactionThroughTheDefinitions()
    {
        using var estate = new TestEstate();
        var cics = Definitions(estate);
        var result = estate.Program(RunLoop(
            B("    STRING 'OCR' DELIMITED BY SIZE WS-I DELIMITED BY SIZE"),
            B("        INTO WS-TRAN")));

        DependencyExtractor.Extract(result, cics).Programs.Should().Equal(
            new ProgramReference(DependencyKind.CicsRun, "AGENCY1", true, "WS-TRAN", 11),
            new ProgramReference(DependencyKind.CicsRun, "AGENCY3", true, "WS-TRAN", 11));
    }

    [Fact]
    public void RunTransidStaysUnresolvedWhenAnotherStatementAlsoWritesTheOperand()
    {
        using var estate = new TestEstate();
        var cics = Definitions(estate);
        var result = estate.Program(RunLoop(
            B("    STRING 'OCR' DELIMITED BY SIZE WS-I DELIMITED BY SIZE"),
            B("        INTO WS-TRAN"),
            B("    MOVE 'OCR1' TO WS-TRAN")));

        DependencyExtractor.Extract(result, cics).Programs.Should().BeEmpty();
    }
}
