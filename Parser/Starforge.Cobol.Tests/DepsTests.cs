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
}
