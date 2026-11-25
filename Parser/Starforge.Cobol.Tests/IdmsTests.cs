using System.Text.Json.Nodes;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Deps;
using Starforge.Cobol.Flow;
using Starforge.Cobol.Native;
using Starforge.Cobol.Syntax;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

// CA IDMS precompiler source: COPY IDMS, the IDMS-CONTROL/SCHEMA/MAP sections and DML statements.
// Every record here is synthetic; no vendor copybook layout is reproduced.
public sealed class IdmsTests : IDisposable
{
    private readonly TestEstate _estate = new();

    public IdmsTests()
    {
        _estate.Write("copy/EMPREC.cpy", B("05  EMP-ID PIC 9(4)."), B("05  EMP-NAME PIC X(20)."));
        _estate.Write("src/SUBPGM.cbl", A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. SUBPGM."), A("PROCEDURE DIVISION."), B("GOBACK."));
    }

    public void Dispose() => _estate.Dispose();

    private static string[] Program(string[] procedure, bool idms = true, params string[] storage) =>
    [
        A("IDENTIFICATION DIVISION."),
        A("PROGRAM-ID. MAIN."),
        A("DATA DIVISION."),
        A("WORKING-STORAGE SECTION."),
        A("01  WS-NEXT PIC X(8) VALUE 'SUBPGM'."),
        A("01  WS-STATUS PIC X(4)."),
        B("88  DB-REC-NOT-FOUND VALUE '0326'."),
        .. storage,
        .. idms ? [A("01  COPY IDMS EMPREC.")] : Array.Empty<string>(),
        A("PROCEDURE DIVISION."),
        .. procedure,
    ];

    private CompilationResult Parse(string[] procedure, bool idms = true, params string[] storage)
    {
        _estate.Write("src/MAIN.cbl", Program(procedure, idms, storage));
        return _estate.Process("src/MAIN.cbl");
    }

    private static List<Statement> Statements(CompilationResult r) => r.Syntax.Programs[0].Procedure!.AllStatements().ToList();

    private static void FullyParsed(CompilationResult r)
    {
        r.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Select(d => d.Fingerprint).Should().BeEmpty();
        r.Coverage.Count(CoverageClass.Unsupported).Should().Be(0);
        r.Coverage.Count(CoverageClass.Lexed).Should().Be(0);
    }

    [Fact]
    public void CopyIdms_WithALevel_RebuildsTheRecordEntryAroundAFieldsOnlyCopybook()
    {
        var r = Parse([B("GOBACK.")]);

        FullyParsed(r);
        r.Inclusions.Should().ContainSingle().Which.Should().Match<CopyInclusion>(i => i.Idms && i.Status == CopyStatus.Resolved && i.Name == "EMPREC");
        var record = r.Syntax.Programs[0].Data!.Sections[0].Entries.Single(e => e.Name == "EMPREC");
        record.Level.Should().Be(1);
        record.Children.Select(c => c.Name).Should().Equal("EMP-ID", "EMP-NAME");
    }

    [Fact]
    public void IdmsSections_AreParsedIntoTheirOwnNodes()
    {
        _estate.Write("src/MAIN.cbl",
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("ENVIRONMENT DIVISION."),
            A("IDMS-CONTROL SECTION."),
            A("PROTOCOL.    MODE IS IDMS-DC DEBUG"),
            A("             IDMS-RECORDS MANUAL."),
            A("DATA DIVISION."),
            A("SCHEMA SECTION."),
            A("DB SUBSCH1 WITHIN SCHEMA1 VERSION 100."),
            A("MAP SECTION."),
            A("MAX FIELD LIST IS 10."),
            A("MAP MAP1 VERSION 1 TYPE IS EXTENDED PAGING."),
            A("WORKING-STORAGE SECTION."),
            A("01  COPY IDMS EMPREC."),
            A("PROCEDURE DIVISION."),
            B("GOBACK."));

        var r = _estate.Process("src/MAIN.cbl");

        FullyParsed(r);
        var program = r.Syntax.Programs[0];
        program.Environment!.IdmsControl.Should().Match<IdmsControlSection>(c => c.Mode == "IDMS-DC" && c.Debug && c.Records == "MANUAL");
        program.Data!.IdmsSchema.Should().Match<IdmsSchemaSection>(s => s.Subschema == "SUBSCH1" && s.Schema == "SCHEMA1" && s.Version == 100);
        program.Data.IdmsMaps!.Should().ContainSingle().Which.Should().Match<IdmsMapDeclaration>(m => m.Name == "MAP1" && m.Version == 1);
    }

    [Fact]
    public void DmlStatements_EndAtPeriodsSemicolonsAndTheNextStatement_AndKeepTheirOnClause()
    {
        var r = Parse(
        [
            A("P1."),
            B("BIND RUN-UNIT; READY USAGE-MODE IS UPDATE."),
            B("OBTAIN CALC EMPREC"),
            B("    ON DB-REC-NOT-FOUND GO TO P9."),
            B("IF EMP-SET IS NOT EMPTY"),
            B("    MODIFY EMPREC"),
            B("END-IF."),
            B("FINISH."),
            A("P9."),
            B("COMMIT."),
            B("GOBACK."),
        ]);

        FullyParsed(r);
        r.Syntax.Programs[0].Procedure!.AllParagraphs().Select(p => p.Name).Should().Equal("P1", "P9");
        var statements = Statements(r);
        statements.OfType<IdmsStatement>().Select(s => s.Form).Should().Equal("BIND RUN-UNIT", "READY", "OBTAIN", "MODIFY", "FINISH", "COMMIT");
        statements.OfType<IdmsStatement>().First().Kind.Should().Be(IdmsKind.BindRunUnit);
        var obtain = statements.OfType<IdmsStatement>().Single(s => s.Form == "OBTAIN");
        obtain.On!.Condition.Should().Be("DB-REC-NOT-FOUND");
        obtain.On.Statements.Should().ContainSingle().Which.Should().BeOfType<GoToStatement>();
        var test = statements.OfType<IfStatement>().Single().Condition.Should().BeOfType<IdmsSetCondition>().Subject;
        test.Should().Be(new IdmsSetCondition(test.Span, "EMP-SET", true, true));
    }

    [Fact]
    public void LineVerbs_AreStatementsOnlyAtTheStartOfASourceLine()
    {
        var r = Parse([B("STARTPAGE SESSION ENDPAGE."), B("ENDPAGE SESSION."), B("GOBACK.")]);

        Statements(r).OfType<IdmsStatement>().Select(s => s.Form).Should().Equal("STARTPAGE", "ENDPAGE");
    }

    [Fact]
    public void WithoutCopyIdms_DmlWordsStayOrdinaryDataNames()
    {
        var r = Parse([B("MOVE 'A' TO OBTAIN."), B("DISPLAY FINISH."), B("GOBACK.")], idms: false,
            A("01  OBTAIN PIC X."), A("01  FINISH PIC X."));

        FullyParsed(r);
        Statements(r).Select(s => s.GetType().Name).Should().Equal("MoveStatement", "DisplayStatement", "GobackStatement");
    }

    private static readonly string[] Transfers =
    [
        A("P1."),
        B("TRANSFER CONTROL TO 'SUBPGM' LINK USING EMPREC."),
        B("TRANSFER CONTROL TO WS-NEXT RETURN."),
        B("DC RETURN NEXT TASK CODE 'TSK1'."),
        A("P2."),
        B("TRANSFER TO 'SUBPGM' XCTL."),
    ];

    [Fact]
    public void TransferAndDcReturn_AreProgramReferencesAndControlTransfers()
    {
        var r = Parse(Transfers);

        FullyParsed(r);
        var deps = DependencyExtractor.Extract(r);
        deps.Programs.Should().Equal(
            new ProgramReference(DependencyKind.IdmsTransfer, "SUBPGM", true, null, 11),
            new ProgramReference(DependencyKind.IdmsTransfer, "SUBPGM", true, "WS-NEXT", 12),
            new ProgramReference(DependencyKind.IdmsTransfer, "SUBPGM", true, null, 15));
        deps.Transfers.Should().Equal(
            new ControlTransfer("TRANSFER RETURN", "SUBPGM", true, null, 11),
            new ControlTransfer("TRANSFER RETURN", "SUBPGM", true, "WS-NEXT", 12),
            new ControlTransfer("DC RETURN", "TSK1", true, null, 13),
            new ControlTransfer("TRANSFER NORETURN", "SUBPGM", true, null, 15));
    }

    [Fact]
    public void Flow_DcReturnAndNoReturnTransferEndThePath_AndAnOnClauseBranches()
    {
        var r = Parse(
        [
            A("P1."),
            B("OBTAIN CALC EMPREC ON DB-REC-NOT-FOUND GO TO P3."),
            B("DC RETURN."),
            A("P2."),
            B("TRANSFER TO 'SUBPGM' NORETURN."),
            A("P3."),
            B("GOBACK."),
        ]);

        FullyParsed(r);
        var flow = FlowBuilder.Build(r.Syntax.Programs[0].Procedure!, r.Tokens);
        var nodes = flow.Root.SelfAndDescendants().ToList();
        nodes.Select(n => n.Type).Should().Contain(["DIALECT", "ON_CLAUSE", "ON_CLAUSE_ACTION", "TRANSFER"]);
        var byId = nodes.ToDictionary(n => n.Id);
        flow.Edges.Where(e => e.Kind == FlowEdgeKind.FollowedBy && byId[e.From].Type == "PARAGRAPH")
            .Select(e => (byId[e.From].Name, byId[e.To].Name)).Should().BeEmpty();
        flow.Edges.Where(e => e.Kind == FlowEdgeKind.JumpsTo).Select(e => byId[e.To].Name).Should().Equal("P3");
    }

    [Fact]
    public void NativeExport_LinksAStaticTransferAndListsEveryIdmsControlTransfer()
    {
        _estate.Write("src/MAIN.cbl", Program(Transfers));

        var document = NativeExporter.Export(new NativeExportOptions(Path.Join(_estate.Root, "src", "MAIN.cbl"), _estate.Root));

        document["dependencyDiagnostics"]!.AsArray().Select(d => (string)d!).Should().Equal("dynamic-transfer-unsupported:WS-NEXT");
        document["dependencies"]!.AsArray().Select(d => ((string)d!["dependencyKind"]!, (string)d["callTarget"]!["name"]!))
            .Should().Equal(("COPY", "EMPREC.cpy"), ("IDMS_TRANSFER", "SUBPGM"));
        var transfers = document["dependencyControlTransfers"]!.AsArray().Select(t => t!.AsObject()).ToList();
        transfers.Select(t => ((string)t["kind"]!, (string)t["operation"]!, (string)t["controlEffect"]!)).Should().Equal(
            ("IDMS_DC", "TRANSFER CONTROL", "link-and-return"),
            ("IDMS_DC", "TRANSFER CONTROL", "link-and-return"),
            ("IDMS_DC", "DC RETURN", "return-to-idms-dc"),
            ("IDMS_DC", "TRANSFER CONTROL", "transfer-no-return"));
    }
}
