using System.Text.Json.Nodes;
using CobolToQuarkusMigration.Agents.Infrastructure.Rekt;
using FluentAssertions;
using Starforge.Cobol.Native;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Agents.Infrastructure.Rekt;

// Starforge's real native export judged by the real evidence gate with the starforge producer rules.
[Collection(RektEvidenceCollection.Name)]
public sealed class StarforgeEvidenceTests : RektEvidenceFixture
{
    private const string Batch = """
               IDENTIFICATION DIVISION.
               PROGRAM-ID. PROG.
               ENVIRONMENT DIVISION.
               INPUT-OUTPUT SECTION.
               FILE-CONTROL.
                   SELECT IN-FILE ASSIGN TO INFILE.
               DATA DIVISION.
               FILE SECTION.
               FD IN-FILE.
               01 IN-REC PIC X(10).
               WORKING-STORAGE SECTION.
               COPY REAL.
               01 WS-TARGET PIC X(8) VALUE 'SUBPROG'.
               PROCEDURE DIVISION.
                   OPEN INPUT IN-FILE.
                   READ IN-FILE AT END CONTINUE END-READ.
                   CLOSE IN-FILE.
                   CALL 'SUBPROG' USING IN-REC.
                   EXEC CICS RETURN END-EXEC.
        """;

    public StarforgeEvidenceTests()
    {
        File.Delete(ExportPath);
        Write("estate/PROG.cbl", Batch + "\n");
        Write("estate/SUBPROG.cbl", "       IDENTIFICATION DIVISION.\n       PROGRAM-ID. SUBPROG.\n"
            + "       PROCEDURE DIVISION.\n           GOBACK.\n");
    }

    private const string SqlProgram = """
               IDENTIFICATION DIVISION.
               PROGRAM-ID. PROG.
               DATA DIVISION.
               WORKING-STORAGE SECTION.
               01 WS-ID PIC X(8).
               PROCEDURE DIVISION.
                   EXEC SQL DELETE FROM CONTROL WHERE ID = :WS-ID END-EXEC.
                   GOBACK.
        """;

    private const string SqlCatalog = """
                   EXEC SQL DECLARE CONTROL TABLE
                   ( ID CHAR(8) NOT NULL )
                   END-EXEC.
        """;

    private JsonObject Export(IReadOnlyList<string>? sqlDeclarations = null, IReadOnlyList<string>? cicsDefinitions = null,
        string? profile = null)
    {
        var document = NativeExporter.Export(new NativeExportOptions(ProgramPath, Path.Join(Source, "estate"),
            ProfilePath: profile, CicsDefinitions: cicsDefinitions, SqlDeclarations: sqlDeclarations));
        Save(ExportPath, document);
        return document;
    }

    private JsonObject Gate(string engine = NativeProducer.Starforge, string? sqlDeclarations = null, string? profile = null) =>
        RektEvidence.BuildEvidence(Source, Relative, Output, 0, dependencyProfile: profile, sqlDeclarations: sqlDeclarations,
            engine: engine);

    private static List<string> Problems(JsonObject evidence) => Strings(evidence["unresolvedDiagnostics"]);

    private static JsonObject FirstStatement(JsonObject document, string operation) =>
        Objects(document["dependencyDialectStatements"]).First(item => (string?)item["operation"] == operation);

    [Fact]
    public void RoundTrip_BatchProgram_IsCompleteForEveryDomain()
    {
        var document = Export();

        Strings(document["dependencyDiagnostics"]).Should().BeEmpty();
        Objects(document["dependencyDialectStatements"]).Select(item => (string?)item["operation"])
            .Should().Equal("OPEN", "READ", "CLOSE", "RETURN");
        var evidence = Gate();

        Problems(evidence).Should().BeEmpty();
        evidence["coverage"]!.AsObject().Select(pair => (pair.Key, (string?)pair.Value))
            .Should().OnlyContain(pair => pair.Item2 == "complete");
        Objects(evidence["definitions"]).Select(item => ((string?)item["kind"], (string?)item["name"]))
            .Should().Contain([("COPY", "REAL"), ("CALL", "SUBPROG")]);
    }

    [Fact]
    public void SyntaxRoots_AreSlicesOfTheProgramTheyClaim()
    {
        var read = FirstStatement(Export(), "READ")["syntax"]!.AsObject();
        var range = read["range"]!;

        range["path"]!.GetValue<string>().Should().Be(ProgramPath);
        RektEvidence.SourceSlice(File.ReadAllText(ProgramPath), range["start"]![0]!.GetValue<int>(), range["start"]![1]!.GetValue<int>(),
            range["end"]![0]!.GetValue<int>(), range["end"]![1]!.GetValue<int>())
            .Should().Be("READ IN-FILE");
    }

    [Fact]
    public void TamperedProgramHash_IsRejected()
    {
        var document = Export();
        document["sourceSha256"] = new string('0', 64);
        Save(ExportPath, document);

        Problems(Gate()).Should().Contain("native-export-source-unverified:native parser source identity/hash mismatch");
    }

    [Fact]
    public void TamperedRootHash_IsRejected()
    {
        var document = Export();
        FirstStatement(document, "READ")["syntax"]!["textSha256"] = new string('0', 64);
        Save(ExportPath, document);

        Problems(Gate()).Should().Contain("starforge-syntax-root-hash-mismatch:FileStatement");
    }

    [Fact]
    public void RootWhoseTokensAreNotInItsSlice_IsRejected()
    {
        var document = Export();
        var syntax = FirstStatement(document, "CLOSE")["syntax"]!.AsObject();
        syntax["children"]!.AsArray().Add(new JsonObject { ["token"] = "DELETE" });
        Save(ExportPath, document);

        Problems(Gate()).Should().Contain("starforge-syntax-root-text-mismatch:CloseStatement");
    }

    [Fact]
    public void RootOutsideTheExportedInputs_IsRejected()
    {
        var document = Export();
        var other = Write("estate/OTHER.cbl", File.ReadAllText(ProgramPath));
        FirstStatement(document, "OPEN")["syntax"]!["range"]!["path"] = other;
        Save(ExportPath, document);

        Problems(Gate()).Should().Contain("starforge-syntax-root-outside-inputs:OpenStatement");
    }

    [Fact]
    public void StatementWithoutRange_IsRejected()
    {
        var document = Export();
        var syntax = FirstStatement(document, "OPEN")["syntax"]!.AsObject();
        syntax.Remove("range");
        syntax.Remove("textSha256");
        Save(ExportPath, document);

        Problems(Gate()).Should().Contain("starforge-syntax-root-unbound:syntax");
    }

    [Fact]
    public void MissingPresenceEvidence_IsRejected()
    {
        var document = Export();
        document["dependencyPresenceEvidence"]!.AsObject().Remove("FILE_IO");
        Save(ExportPath, document);

        var evidence = Gate();

        Problems(evidence).Should().Equal("native-absence-proof-missing:FILE_IO");
        evidence["coverage"]!["FILE_IO"]!.GetValue<string>().Should().Be("partial");
    }

    [Fact]
    public void UnresolvedDynamicCall_IsRejected()
    {
        Write("estate/PROG.cbl", Batch.Replace("CALL 'SUBPROG' USING IN-REC.", "CALL WS-TARGET USING IN-REC.") + "\n");
        var document = Export();

        Strings(document["dependencyDiagnostics"]).Should().Equal("dynamic-call-unsupported:WS-TARGET");
        Problems(Gate()).Should().Contain("dynamic-CALL-target-unresolved");
    }

    [Fact]
    public void UnknownProducer_IsRejected()
    {
        var document = Export();
        document["dependencyProducer"] = "javacc";
        Save(ExportPath, document);

        Problems(Gate()).Should().Contain("dependency-export-invalid:unknown-native-producer:javacc");
    }

    [Fact]
    public void StarforgeExportUnderRektEngine_IsRejected()
    {
        Export();

        Problems(Gate(NativeProducer.Rekt)).Should().Contain("dependency-export-invalid:native-producer-mismatch:starforge!=rekt");
    }

    [Fact]
    public void AntlrShapedSyntaxUnderStarforge_IsRejected()
    {
        var document = Export();
        FirstStatement(document, "OPEN")["syntax"] = O("""
            {"rule": "OpenStatementContext", "children": [{"token": "OPEN"}, {"token": "INPUT"}, {"token": "IN-FILE"}]}
            """);
        Save(ExportPath, document);

        Problems(Gate()).Should().Contain("starforge-syntax-root-foreign:syntax");
    }

    [Fact]
    public void SqlRelationBoundToConfiguredCatalog_IsComplete()
    {
        Write("estate/PROG.cbl", SqlProgram + "\n");
        var catalog = Write("sql/CONTROL.cpy", SqlCatalog + "\n");
        var document = Export([catalog]);
        var binding = Objects(FirstStatement(document, "DELETE")["resourceBindings"])
            .Single(item => (string?)item["kind"] == "SQL_RELATION");

        Strings(document["dependencyDiagnostics"]).Should().BeEmpty();
        ((string?)binding["definitionScope"]).Should().Be("explicit-sql-catalog");
        ((string?)binding["sourcePath"]).Should().Be(catalog);
        Problems(Gate(sqlDeclarations: catalog)).Should().BeEmpty();
    }

    [Fact]
    public void SqlCatalogThatIsNotConfigured_IsRejected()
    {
        Write("estate/PROG.cbl", SqlProgram + "\n");
        Export([Write("sql/CONTROL.cpy", SqlCatalog + "\n")]);

        Problems(Gate()).Should().Equal(
            "native-sql-catalog-unverified:SQL declaration does not match an explicit native catalog input");
    }

    [Fact]
    public void SqlCatalogRootPointingAtAnotherFile_IsRejected()
    {
        Write("estate/PROG.cbl", SqlProgram + "\n");
        var catalog = Write("sql/CONTROL.cpy", SqlCatalog + "\n");
        var document = Export([catalog]);
        var other = Write("sql/OTHER.cpy", SqlCatalog + "\n");
        Objects(FirstStatement(document, "DELETE")["resourceBindings"])
            .Single(item => (string?)item["kind"] == "SQL_RELATION")["definitionSyntax"]!["range"]!["path"] = other;
        Save(ExportPath, document);

        Problems(Gate(sqlDeclarations: catalog)).Should().Equal("starforge-syntax-root-outside-inputs:SqlDeclareTable");
    }

    private const string RunProgram = """
               IDENTIFICATION DIVISION.
               PROGRAM-ID. PROG.
               DATA DIVISION.
               WORKING-STORAGE SECTION.
               01 WS-TRANSID PIC X(4) VALUE SPACES.
               01 WS-CNT PIC 9 VALUE 0.
               01 WS-TOKEN PIC X(16).
               PROCEDURE DIVISION.
                   PERFORM VARYING WS-CNT FROM 1 BY 1 UNTIL WS-CNT > 2
                       STRING 'OCR' DELIMITED BY SIZE
                              WS-CNT DELIMITED BY SIZE
                           INTO WS-TRANSID
                       END-STRING
                       EXEC CICS RUN TRANSID(WS-TRANSID) CHILD(WS-TOKEN)
                       END-EXEC
                   END-PERFORM.
                   EXEC CICS RETURN END-EXEC.
        """;

    private const string RunDefinitions = """
        DEFINE TRANSACTION(OCR1) GROUP(G) PROGRAM(AGY1) DYNAMIC(NO) ROUTABLE(NO) STATUS(ENABLED)
        DEFINE TRANSACTION(OCR2) GROUP(G) PROGRAM(AGY2) DYNAMIC(NO) ROUTABLE(NO) STATUS(ENABLED)
        DEFINE PROGRAM(AGY1) GROUP(G) LANGUAGE(COBOL) DYNAMIC(NO)
        DEFINE PROGRAM(AGY2) GROUP(G) LANGUAGE(COBOL) DYNAMIC(NO)
        """;

    private JsonObject ExportRun()
    {
        Write("estate/PROG.cbl", RunProgram + "\n");
        foreach (var name in new[] { "AGY1", "AGY2" })
            Write($"estate/{name}.cbl", $"       IDENTIFICATION DIVISION.\n       PROGRAM-ID. {name}.\n       PROCEDURE DIVISION.\n           GOBACK.\n");
        return Export(cicsDefinitions: [Write("csd/DEFS.csd", RunDefinitions + "\n")]);
    }

    private static List<JsonObject> Transactions(JsonObject document) =>
        Objects(FirstStatement(document, "RUN")["resourceBindings"]).Where(item => (string?)item["kind"] == "CICS_TRANSACTION").ToList();

    [Fact]
    public void RunTransidFromBoundedStringLoop_IsComplete()
    {
        var document = ExportRun();

        Strings(document["dependencyDiagnostics"]).Should().BeEmpty();
        Transactions(document).Select(item => (string?)item["name"]).Should().Equal("OCR1", "OCR2");
        var evidence = Gate();

        Problems(evidence).Should().BeEmpty();
        evidence["coverage"]!["EXEC_CICS"]!.GetValue<string>().Should().Be("complete");
    }

    [Fact]
    public void RunTransidProofClaimingAnotherTransaction_IsRejected()
    {
        var document = ExportRun();
        foreach (var transaction in Transactions(document))
            transaction["targetResolution"]!["transactionNames"] = new JsonArray("OCR1", "OCR3");
        Save(ExportPath, document);

        Problems(Gate()).Should().Equal("native-absence-proof-missing:EXEC_CICS");
    }

    [Fact]
    public void RunTransidLoopHeaderWithWiderBound_IsRejected()
    {
        var document = ExportRun();
        foreach (var transaction in Transactions(document))
        {
            var header = transaction["targetResolution"]!["loopHeaderSyntax"]!["children"]!.AsArray();
            header[^1] = new JsonObject { ["token"] = "3" };
        }
        Save(ExportPath, document);

        Problems(Gate()).Should().Equal("starforge-syntax-root-text-mismatch:PerformPhrase", "starforge-syntax-root-text-mismatch:PerformPhrase",
            "native-absence-proof-missing:EXEC_CICS");
    }

    private const string ReplacingProgram = """
               IDENTIFICATION DIVISION.
               PROGRAM-ID. PROG.
               DATA DIVISION.
               WORKING-STORAGE SECTION.
               COPY NAMES REPLACING ==:P:== BY ==WS==.
               PROCEDURE DIVISION.
                   EXEC CICS XCTL PROGRAM(WS-NEXT) END-EXEC.
        """;

    private JsonObject ExportReplacing()
    {
        Write("estate/PROG.cbl", ReplacingProgram + "\n");
        Write("estate/NAMES.cpy", "       01 :P:-NEXT PIC X(8) VALUE 'SUBPROG'.\n");
        return Export(cicsDefinitions: [Write("csd/DEFS.csd", "DEFINE PROGRAM(SUBPROG) GROUP(G) LANGUAGE(COBOL) DYNAMIC(NO)\n")]);
    }

    private static JsonObject ReplacingPiece(JsonObject document) =>
        Objects(FirstStatement(document, "XCTL")["resourceBindings"]).Single(item => (string?)item["kind"] == "CICS_PROGRAM")
            ["targetResolution"]!["definitionSyntax"]!["replacing"]!["statement"]![0]!.AsObject();

    [Fact]
    public void XctlTargetFromReplacedCopybookEntry_IsComplete()
    {
        var document = ExportReplacing();

        Strings(document["dependencyDiagnostics"]).Should().BeEmpty();
        Problems(Gate()).Should().BeEmpty();
    }

    [Fact]
    public void ReplacingProofWithTamperedStatementHash_IsRejected()
    {
        var document = ExportReplacing();
        ReplacingPiece(document)["textSha256"] = new string('0', 64);
        Save(ExportPath, document);

        Problems(Gate()).Should().Equal("starforge-syntax-root-replacing-invalid:DataEntry");
    }

    [Fact]
    public void ReplacingProofNamingAnotherCopybook_IsRejected()
    {
        var document = ExportReplacing();
        var piece = ReplacingPiece(document);
        piece["path"] = Write("estate/OTHER.cbl", File.ReadAllText(ProgramPath));
        Save(ExportPath, document);

        Problems(Gate()).Should().Equal("starforge-syntax-root-replacing-invalid:DataEntry");
    }

    private const string DynamicCallProgram = """
               IDENTIFICATION DIVISION.
               PROGRAM-ID. PROG.
               DATA DIVISION.
               WORKING-STORAGE SECTION.
               01 WS-PGM PIC X(8) VALUE 'DSNTIAC'.
               01 WS-A PIC X(4).
               PROCEDURE DIVISION.
                   CALL WS-PGM USING WS-A WS-A WS-A WS-A WS-A.
                   GOBACK.
        """;

    // DSNTIAC is one of the documented services the gate knows; the leaf must carry its exact documented contract.
    private const string DsntiacProfile = """
        {"schemaVersion": 1, "profileKind": "target-port", "profileName": "test-target", "runtimeContracts": {"DSNTIAC": {
          "kind": "documented-platform-service", "platform": "IBM Db2 for z/OS",
          "documentation": ["https://www.ibm.com/docs/en/db2-for-zos/13.0.0?topic=statements-handling-sql-error-codes-in-cobol-applications"],
          "parameters": [
            {"name": "eib", "passing": "BY REFERENCE", "direction": "in", "representation": "test"},
            {"name": "commarea", "passing": "BY REFERENCE", "direction": "in", "representation": "test"},
            {"name": "sqlca", "passing": "BY REFERENCE", "direction": "in", "representation": "test"},
            {"name": "message", "passing": "BY REFERENCE", "direction": "out", "representation": "test"},
            {"name": "lrecl", "passing": "BY REFERENCE", "direction": "in", "representation": "test"}],
          "semantics": {"effect": "formats the SQLCA message"}}}}
        """;

    [Fact]
    public void DynamicCallOfImmutableRuntimeContractConstant_IsComplete()
    {
        var profile = Write("profile.json", DsntiacProfile + "\n");
        Write("estate/PROG.cbl", DynamicCallProgram + "\n");
        var document = Export(profile: profile);

        Strings(document["dependencyDiagnostics"]).Should().BeEmpty();
        var evidence = Gate(profile: profile);

        Problems(evidence).Should().BeEmpty();
        Objects(evidence["definitions"]).Select(item => ((string?)item["kind"], (string?)item["name"]))
            .Should().Contain(("RUNTIME_CONTRACT", "DSNTIAC"));
    }

    [Fact]
    public void DynamicCallResolutionNamingAnotherOperand_IsRejected()
    {
        var profile = Write("profile.json", DsntiacProfile + "\n");
        Write("estate/PROG.cbl", DynamicCallProgram + "\n");
        var document = Export(profile: profile);
        Objects(document["dependencies"]).Single()["runtimeContract"]!["targetResolution"]!["operand"] = "WS-A";
        Save(ExportPath, document);

        Problems(Gate(profile: profile)).Should().Equal("dynamic-CALL-target-unresolved",
            "native-runtime-contract-unverified:DSNTIAC:invalid documented runtime-contract leaf");
    }
}
