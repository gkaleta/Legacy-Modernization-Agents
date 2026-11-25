using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Starforge.Cobol.Native;
using Xunit;

namespace Starforge.Cobol.Tests;

// The native dependency exporter per evidence family: one proof it builds from Starforge's own parse, and one
// construct it must refuse with a named diagnostic instead of guessing.
public sealed class NativeExportTests : IDisposable
{
    private const string Csd = """
        DEFINE TRANSACTION(OCR1) GROUP(G) PROGRAM(AGY1) DYNAMIC(NO) ROUTABLE(NO) STATUS(ENABLED)
        DEFINE TRANSACTION(OCR2) GROUP(G) PROGRAM(AGY2) DYNAMIC(NO) ROUTABLE(NO) STATUS(ENABLED)
        DEFINE PROGRAM(AGY1) GROUP(G) LANGUAGE(COBOL) DYNAMIC(NO)
        DEFINE PROGRAM(AGY2) GROUP(G) LANGUAGE(COBOL) DYNAMIC(NO)
        DEFINE PROGRAM(SUB) GROUP(G) LANGUAGE(COBOL) DYNAMIC(NO)
        DEFINE FILE(CUSTF) GROUP(G) DSNAME(A.CUST) RECORDSIZE(80) KEYLENGTH(8)
        """;

    private readonly TestEstate _estate = new();

    public NativeExportTests()
    {
        foreach (var name in new[] { "SUB", "AGY1", "AGY2" })
        {
            _estate.Write($"src/{name}.cbl", Fixed.A("IDENTIFICATION DIVISION."), Fixed.A($"PROGRAM-ID. {name}."),
                Fixed.A("PROCEDURE DIVISION."), Fixed.B("GOBACK."));
        }

        _estate.Write("defs.csd", Csd);
        _estate.Write("decl/CONTDECL.cpy", Fixed.A("EXEC SQL DECLARE CONTROL TABLE"), Fixed.A("( ID CHAR(8) NOT NULL )"), Fixed.A("END-EXEC."));
        _estate.Write("profile.json", """{"profileName":"t","profileKind":"k","runtimeContracts":{"DSNTIAC":{"kind":"documented-platform-service"}}}""");
    }

    public void Dispose() => _estate.Dispose();

    private string ProgramPath => Path.Join(_estate.Root, "src", "MAIN.cbl");

    private void Main(string[] workingStorage, string[] procedure, string[]? linkage = null)
    {
        var lines = new List<string>
        {
            Fixed.A("IDENTIFICATION DIVISION."), Fixed.A("PROGRAM-ID. MAIN."), Fixed.A("DATA DIVISION."), Fixed.A("WORKING-STORAGE SECTION."),
        };
        lines.AddRange(workingStorage);
        if (linkage is not null)
        {
            lines.Add(Fixed.A("LINKAGE SECTION."));
            lines.AddRange(linkage);
        }

        lines.Add(Fixed.A("PROCEDURE DIVISION."));
        lines.AddRange(procedure.Select(Fixed.B));
        _estate.Write("src/MAIN.cbl", lines.ToArray());
    }

    private JsonObject Export(bool profile = false, string? closure = null) => NativeExporter.Export(new NativeExportOptions(
        ProgramPath,
        Path.Join(_estate.Root, "src"),
        ProfilePath: profile ? Path.Join(_estate.Root, "profile.json") : null,
        CicsDefinitions: [Path.Join(_estate.Root, "defs.csd")],
        SqlDeclarations: [Path.Join(_estate.Root, "decl", "CONTDECL.cpy")],
        CommareaClosure: closure));

    private static List<string> Diagnostics(JsonObject document) =>
        document["dependencyDiagnostics"]!.AsArray().Select(d => (string)d!).ToList();

    private static JsonObject Statement(JsonObject document, string operation) =>
        document["dependencyDialectStatements"]!.AsArray().Select(s => s!.AsObject()).Single(s => (string?)s["operation"] == operation);

    private static JsonObject Statement(JsonObject document, string operation, int index) =>
        document["dependencyDialectStatements"]!.AsArray().Select(s => s!.AsObject()).Where(s => (string?)s["operation"] == operation).ElementAt(index);

    private static List<string> Kinds(JsonObject document) =>
        document["dependencies"]!.AsArray().Select(d => (string)d!["dependencyKind"]!).Where(k => k != "COPY").ToList();

    private static List<JsonObject> Bindings(JsonObject statement, string kind) =>
        statement["resourceBindings"]!.AsArray().Select(b => b!.AsObject()).Where(b => (string?)b["kind"] == kind).ToList();

    private static List<string> Names(IEnumerable<JsonObject> bindings) => bindings.Select(b => (string)b["name"]!).ToList();

    private static List<string> Callees(JsonObject document) =>
        document["dependencies"]!.AsArray().Select(d => (string)d!["callTarget"]!["name"]!).ToList();

    private static string Text(JsonNode? node)
    {
        var tokens = new List<string>();
        void Walk(JsonNode? current)
        {
            if (current is not JsonObject obj)
            {
                return;
            }

            if (obj["token"] is JsonValue token)
            {
                tokens.Add((string)token!);
            }

            foreach (var child in obj["children"]?.AsArray() ?? [])
            {
                Walk(child);
            }
        }

        Walk(node);
        return string.Join(" ", tokens);
    }

    private static readonly string[] RunStorage =
    [
        Fixed.A("01 WS-TRANSID PIC X(4) VALUE SPACES."), Fixed.A("01 WS-CNT PIC 9 VALUE 0."), Fixed.A("01 WS-TOKEN PIC X(16)."),
    ];

    private static readonly string[] RunLoop =
    [
        "PERFORM VARYING WS-CNT FROM 1 BY 1 UNTIL WS-CNT > 2",
        "    STRING 'OCR' DELIMITED BY SIZE",
        "           WS-CNT DELIMITED BY SIZE",
        "        INTO WS-TRANSID",
        "    END-STRING",
        "    EXEC CICS RUN TRANSID(WS-TRANSID) CHILD(WS-TOKEN)",
        "    END-EXEC",
        "END-PERFORM.",
        "EXEC CICS RETURN END-EXEC.",
    ];

    [Fact]
    public void RunTransid_FromBoundedStringLoop_BindsEveryTransactionAndStartsItsProgram()
    {
        Main(RunStorage, RunLoop);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        var transactions = Bindings(Statement(document, "RUN"), "CICS_TRANSACTION");
        Names(transactions).Should().Equal("OCR1", "OCR2");
        transactions.Select(t => (string)t["programBinding"]!["name"]!).Should().Equal("AGY1", "AGY2");
        var proof = transactions[0]["targetResolution"]!;
        ((string)proof["kind"]!).Should().Be("bounded-varying-string");
        ((string)proof["counter"]!).Should().Be("WS-CNT");
        proof["transactionNames"]!.AsArray().Select(n => (string)n!).Should().Equal("OCR1", "OCR2");
        Text(proof["loopHeaderSyntax"]).Should().Be("VARYING WS-CNT FROM 1 BY 1 UNTIL WS-CNT > 2");
        Text(proof["stringSyntax"]).Should().Be("STRING 'OCR' DELIMITED BY SIZE WS-CNT DELIMITED BY SIZE INTO WS-TRANSID END-STRING");
        JsonNode.DeepEquals(transactions[1]["targetResolution"], proof).Should().BeTrue();
        Callees(document).Should().Equal("AGY1", "AGY2");
        Kinds(document).Should().Equal("CICS_RUN", "CICS_RUN");
    }

    [Fact]
    public void RunTransid_WrittenOutsideTheLoop_IsRefused()
    {
        Main(RunStorage, ["MOVE 'OCR9' TO WS-TRANSID.", .. RunLoop]);

        var document = Export();

        Diagnostics(document).Should().Equal("starforge-unsupported:cics:RUN:run-transid-written:WS-TRANSID");
        ((string)document["dependencyCoverage"]!["EXEC_CICS"]!).Should().Be("unavailable");
    }

    [Fact]
    public void DynamicCall_OfReadOnlyConstantNamingARuntimeContract_CarriesItsTargetResolution()
    {
        Main([Fixed.A("01 WS-PGM PIC X(8) VALUE 'DSNTIAC'."), Fixed.A("01 WS-A PIC X(4).")], ["CALL WS-PGM USING WS-A.", "GOBACK."]);

        var document = Export(profile: true);

        Diagnostics(document).Should().BeEmpty();
        var leaf = document["dependencies"]!.AsArray().Single()!;
        ((string)leaf["callTarget"]!["name"]!).Should().Be("DSNTIAC");
        ((string)leaf["dependencyKind"]!).Should().Be("EXTERNAL_RUNTIME");
        var resolution = leaf["runtimeContract"]!["targetResolution"]!;
        ((string)resolution["operand"]!).Should().Be("WS-PGM");
        ((string)resolution["targetResolution"]!["kind"]!).Should().Be("immutable-working-storage-literal");
        Text(resolution["callSyntax"]).Should().Be("CALL WS-PGM USING WS-A");
    }

    [Fact]
    public void DynamicCall_OfWrittenDataName_IsRefused()
    {
        Main([Fixed.A("01 WS-PGM PIC X(8) VALUE 'DSNTIAC'."), Fixed.A("01 WS-A PIC X(4).")],
            ["MOVE 'OTHER' TO WS-PGM.", "CALL WS-PGM USING WS-A.", "GOBACK."]);

        Diagnostics(Export(profile: true)).Should().Equal("dynamic-call-unsupported:WS-PGM");
    }

    [Fact]
    public void CicsLink_LiteralProgram_BindsDefinitionAndExportsTheProgram()
    {
        Main([Fixed.A("01 WS-A PIC X(4).")], ["EXEC CICS LINK PROGRAM('SUB') COMMAREA(WS-A) END-EXEC.", "EXEC CICS RETURN END-EXEC."]);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        var program = Bindings(Statement(document, "LINK"), "CICS_PROGRAM").Single();
        ((string)program["name"]!).Should().Be("SUB");
        ((string)program["targetResolution"]!["kind"]!).Should().Be("literal");
        ((string)program["programSourcePath"]!).Should().Be(Path.Join(_estate.Root, "src", "SUB.cbl"));
        Callees(document).Should().Equal("SUB");
        Kinds(document).Should().Equal("CICS_LINK");
    }

    [Fact]
    public void CicsReturn_IsRecordedAsAControlTransfer_InEveryExportedProgram()
    {
        Main([Fixed.A("01 WS-A PIC X(4).")],
        [
            "EXEC CICS LINK PROGRAM('SUB') COMMAREA(WS-A) END-EXEC.",
            "EXEC CICS RETURN TRANSID('OCR1')",
            "     COMMAREA(WS-A) END-EXEC.",
            "EXEC CICS RETURN END-EXEC.",
        ]);
        _estate.Write("src/SUB.cbl", Fixed.A("IDENTIFICATION DIVISION."), Fixed.A("PROGRAM-ID. SUB."),
            Fixed.A("PROCEDURE DIVISION."), Fixed.B("EXEC CICS RETURN END-EXEC."));

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        var transfers = document["dependencyControlTransfers"]!.AsArray().Select(t => t!.AsObject()).ToList();
        transfers.Select(t => (string)t["variant"]!).Should().Equal("options", "bare");
        transfers.Should().OnlyContain(t => (string)t["kind"]! == "EXEC_CICS" && (string)t["operation"]! == "RETURN"
            && (string)t["controlEffect"]! == "return-to-cics");
        ((string)transfers[0]["sourceText"]!).Should().StartWith("RETURN TRANSID('OCR1')");
        ((string)transfers[1]["line"]!).Should().Be(Statement(document, "RETURN", 1)["line"]!.ToString());
        var linked = document["dependencies"]!.AsArray().Single()!;
        linked["dependencyControlTransfers"]!.AsArray().Select(t => (string)t!["variant"]!).Should().Equal("bare");
    }

    [Fact]
    public void CicsLink_WithSysid_IsRefused()
    {
        Main([], ["EXEC CICS LINK PROGRAM('SUB') SYSID('X') END-EXEC.", "EXEC CICS RETURN END-EXEC."]);

        Diagnostics(Export()).Should().Equal("starforge-unsupported:cics:LINK:link-option");
    }

    private static readonly string[] JumpStorage = [Fixed.A("01 WS-NEXT PIC X(8)."), Fixed.A("01 WS-FLAG PIC X VALUE 'A'.")];

    [Fact]
    public void XctlTarget_ReachedThroughForwardGoTo_IsTheSetOfReachingMoves()
    {
        Main(JumpStorage,
        [
            "P0.", "    IF WS-FLAG = 'A'", "        MOVE 'SUB' TO WS-NEXT", "        GO TO P2", "    END-IF.", "    MOVE 'AGY1' TO WS-NEXT.",
            "P2.", "    EXEC CICS XCTL PROGRAM(WS-NEXT) END-EXEC.",
        ]);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        var programs = Bindings(Statement(document, "XCTL"), "CICS_PROGRAM");
        Names(programs).Should().Equal("SUB", "AGY1");
        programs.Select(p => (string)p["targetResolution"]!["kind"]!).Should().Equal("structured-reaching-assignments", "structured-reaching-assignments");
    }

    [Fact]
    public void XctlTarget_InProgramWithAlter_IsRefused()
    {
        Main(JumpStorage,
        [
            "P0.", "    IF WS-FLAG = 'A'", "        MOVE 'SUB' TO WS-NEXT", "        GO TO P2", "    END-IF.",
            "    ALTER P9 TO PROCEED TO P2.", "    MOVE 'AGY1' TO WS-NEXT.", "P9.", "    GO TO P2.",
            "P2.", "    EXEC CICS XCTL PROGRAM(WS-NEXT) END-EXEC.",
        ]);

        Diagnostics(Export()).Should().Equal("starforge-unsupported:cics:XCTL:reaching-flow:flow:AlterStatement");
    }

    [Fact]
    public void ReplacedCopybookEntry_ProvesItsTextWithTheCopyStatement()
    {
        _estate.Write("src/NAMES.cpy", Fixed.A("01 :P:-NEXT PIC X(8) VALUE 'SUB'."));
        const string copy = "COPY NAMES REPLACING ==:P:== BY ==WS==.";
        Main([Fixed.A(copy)], ["EXEC CICS XCTL PROGRAM(WS-NEXT) END-EXEC."]);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        var definition = Bindings(Statement(document, "XCTL"), "CICS_PROGRAM").Single()["targetResolution"]!["definitionSyntax"]!;
        Text(definition).Should().Be("01 WS-NEXT PIC X(8) VALUE 'SUB' .");
        ((string)definition["range"]!["path"]!).Should().Be(Path.Join(_estate.Root, "src", "NAMES.cpy"));
        var piece = definition["replacing"]!["statement"]!.AsArray().Single()!;
        ((string)piece["path"]!).Should().Be(ProgramPath);
        piece["start"]!.AsArray().Select(n => (int)n!).Should().Equal(5, 8);
        piece["end"]!.AsArray().Select(n => (int)n!).Should().Equal(5, 7 + copy.Length);
        ((string)piece["textSha256"]!).Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(copy))));
    }

    [Fact]
    public void ReplacedCopybookEntry_WhoseCopyStatementHasATab_IsRefused()
    {
        _estate.Write("src/NAMES.cpy", Fixed.A("01 :P:-NEXT PIC X(8) VALUE 'SUB'."));
        Main([Fixed.A("COPY NAMES REPLACING\t==:P:== BY ==WS==.")], ["EXEC CICS XCTL PROGRAM(WS-NEXT) END-EXEC."]);

        Diagnostics(Export()).Should().Equal("syntax-range-unavailable:data:WS-NEXT");
    }

    private static readonly string[] CommareaStorage =
    [
        Fixed.A("01 WS-COMM."), Fixed.B("05 WS-NEXT-PGM PIC X(8)."), Fixed.B("05 WS-DATA PIC X(20)."),
    ];

    [Fact]
    public void CommareaInventory_RecordsTheSentRecordAndItsSlotWrites()
    {
        Main(CommareaStorage, ["MOVE 'SUB' TO WS-NEXT-PGM.", "EXEC CICS XCTL PROGRAM('SUB') COMMAREA(WS-COMM)", "END-EXEC."]);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        var record = document["commareaInventory"]!["records"]!.AsArray().Single()!;
        ((string)record["name"]!).Should().Be("WS-COMM");
        record["layout"]!.AsArray().Select(l => (string)l!).Should().Equal("01WS-COMM.", "05WS-NEXT-PGMPICX(8).", "05WS-DATAPICX(20).");
        ((bool)record["loaded"]!).Should().BeFalse();
        var slot = record["slots"]!["WS-NEXT-PGM"]!;
        slot["values"]!.AsArray().Select(v => (string?)v).Should().Equal(null, "SUB");
        ((bool)slot["entry"]!).Should().BeFalse();
        var send = document["commareaInventory"]!["sends"]!.AsArray().Single()!;
        ((string)send["command"]!).Should().Be("XCTL");
        ((string)send["record"]!).Should().Be("WS-COMM");
    }

    private void EntrySlotProgram() => Main(
        CommareaStorage,
        [
            "IF EIBCALEN > 0", "    MOVE DFHCOMMAREA(1:EIBCALEN) TO WS-COMM", "ELSE", "    MOVE 'SUB' TO WS-NEXT-PGM", "END-IF.",
            "EXEC CICS XCTL PROGRAM(WS-NEXT-PGM)", "    COMMAREA(WS-COMM) END-EXEC.",
        ],
        [Fixed.A("01 DFHCOMMAREA PIC X(28).")]);

    private string Closure(params string?[] values)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ProgramPath)));
        var slots = new JsonObject { ["WS-NEXT-PGM"] = new JsonArray(values.Select(v => (JsonNode?)v).ToArray()) };
        var closure = new JsonObject { ["programs"] = new JsonObject { [sha] = new JsonObject { ["slots"] = slots } } };
        return _estate.Write("closure.json", closure.ToJsonString());
    }

    [Fact]
    public void CommareaEntrySlot_WithEstateClosure_TargetsTheClosureUniverse()
    {
        EntrySlotProgram();

        var document = Export(closure: Closure(null, "AGY1", "SUB"));

        Diagnostics(document).Should().BeEmpty();
        var programs = Bindings(Statement(document, "XCTL"), "CICS_PROGRAM");
        Names(programs).Should().Equal("AGY1", "SUB");
        var proof = programs[0]["targetResolution"]!;
        ((string)proof["kind"]!).Should().Be("closed-world-commarea-slot");
        ((string)proof["usageProof"]!).Should().Be("estate-closed-world:flow-insensitive-commarea-slot-closure");
        proof["universe"]!.AsArray().Select(v => (string?)v).Should().Equal(null, "AGY1", "SUB");
    }

    [Fact]
    public void CommareaEntrySlot_WithoutClosure_IsPending()
    {
        EntrySlotProgram();

        Diagnostics(Export()).Should().Equal("cics-commarea-closure-pending:WS-NEXT-PGM");
    }

    [Fact]
    public void CommareaEntrySlot_WhoseClosureMissesALocalValue_IsRefused()
    {
        EntrySlotProgram();

        Diagnostics(Export(closure: Closure(null, "AGY1"))).Should().Equal("starforge-unsupported:cics:XCTL:commarea-closure-mismatch:WS-NEXT-PGM");
    }

    private static readonly string[] FileStorage = [Fixed.A("01 WS-REC PIC X(80)."), Fixed.A("01 WS-KEY PIC X(8).")];

    [Fact]
    public void CicsWriteFile_BindsTheCsdFileDefinition()
    {
        Main(FileStorage, ["EXEC CICS WRITE FILE('CUSTF') FROM(WS-REC)", "    RIDFLD(WS-KEY) END-EXEC.", "EXEC CICS RETURN END-EXEC."]);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        var statement = Statement(document, "WRITE");
        Names(Bindings(statement, "CICS_DATA")).Should().Equal("WS-REC", "WS-KEY");
        var file = Bindings(statement, "CICS_FILE").Single();
        ((string)file["name"]!).Should().Be("CUSTF");
        JsonNode.DeepEquals(file["attributes"], JsonNode.Parse("""{"dsname":"A.CUST","recordsize":80,"keylength":8}""")).Should().BeTrue();
        ((string)file["sourcePath"]!).Should().Be(Path.Join(_estate.Root, "defs.csd"));
    }

    [Fact]
    public void CicsWriteFile_WithoutDefinition_IsRefused()
    {
        Main(FileStorage, ["EXEC CICS WRITE FILE('NOPE') FROM(WS-REC)", "    RIDFLD(WS-KEY) END-EXEC.", "EXEC CICS RETURN END-EXEC."]);

        Diagnostics(Export()).Should().Equal("starforge-unsupported:cics:WRITE:file-definition:NOPE");
    }

    private static readonly string[] TimeStorage = [Fixed.A("01 WS-T PIC S9(15) COMP-3."), Fixed.A("01 WS-D PIC X(10).")];

    [Fact]
    public void AsktimeAndFormattime_BindTheirDataOperands()
    {
        Main(TimeStorage,
            ["EXEC CICS ASKTIME ABSTIME(WS-T) END-EXEC.", "EXEC CICS FORMATTIME ABSTIME(WS-T)", "    YYYYMMDD(WS-D) END-EXEC.", "EXEC CICS RETURN END-EXEC."]);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        Names(Bindings(Statement(document, "ASKTIME"), "CICS_DATA")).Should().Equal("WS-T");
        Names(Bindings(Statement(document, "FORMATTIME"), "CICS_DATA")).Should().Equal("WS-T", "WS-D");
        ((string)Statement(document, "FORMATTIME")["bindingBasis"]!).Should().Be("parsed-current-task-operation");
    }

    [Fact]
    public void Asktime_OfUndeclaredData_IsRefused()
    {
        Main(TimeStorage, ["EXEC CICS ASKTIME ABSTIME(WS-NONE) END-EXEC.", "EXEC CICS RETURN END-EXEC."]);

        Diagnostics(Export()).Should().Equal("starforge-unsupported:cics:ASKTIME:data:WS-NONE");
    }

    [Fact]
    public void SqlDelete_BindsTheCatalogRelationAndHostVariable()
    {
        Main([Fixed.A("01 WS-ID PIC X(8).")], ["EXEC SQL DELETE FROM CONTROL WHERE ID = :WS-ID END-EXEC.", "GOBACK."]);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        var statement = Statement(document, "DELETE");
        var relation = Bindings(statement, "SQL_RELATION").Single();
        ((string)relation["name"]!).Should().Be("CONTROL");
        ((string)relation["definitionScope"]!).Should().Be("explicit-sql-catalog");
        Names(Bindings(statement, "SQL_HOST_VARIABLE")).Should().Equal("WS-ID");
    }

    [Fact]
    public void SqlDelete_FromUndeclaredTable_IsRefused()
    {
        Main([Fixed.A("01 WS-ID PIC X(8).")], ["EXEC SQL DELETE FROM OTHERTAB WHERE ID = :WS-ID END-EXEC.", "GOBACK."]);

        Diagnostics(Export()).Should().Equal("starforge-unsupported:sql:DELETE:relation:OTHERTAB");
    }

    private static readonly string[] CursorStorage =
        [Fixed.A("01 WS-ID PIC X(8)."), Fixed.A("EXEC SQL DECLARE C1 CURSOR FOR SELECT ID FROM CONTROL"), Fixed.A("END-EXEC.")];

    [Fact]
    public void SqlCursor_OpenFetchClose_BindTheDeclaredCursor()
    {
        Main(CursorStorage, ["EXEC SQL OPEN C1 END-EXEC.", "EXEC SQL FETCH C1 INTO :WS-ID END-EXEC.", "EXEC SQL CLOSE C1 END-EXEC.", "GOBACK."]);

        var document = Export();

        Diagnostics(document).Should().BeEmpty();
        foreach (var operation in new[] { "OPEN", "FETCH", "CLOSE" })
        {
            Names(Bindings(Statement(document, operation), "SQL_CURSOR")).Should().Equal("C1");
        }

        Names(Bindings(Statement(document, "DECLARE"), "SQL_RELATION")).Should().Equal("CONTROL");
    }

    [Fact]
    public void SqlCursor_OpenOfUndeclaredCursor_IsRefused()
    {
        Main(CursorStorage, ["EXEC SQL OPEN C2 END-EXEC.", "GOBACK."]);

        Diagnostics(Export()).Should().Equal("starforge-unsupported:sql:OPEN:cursor:C2");
    }
}
