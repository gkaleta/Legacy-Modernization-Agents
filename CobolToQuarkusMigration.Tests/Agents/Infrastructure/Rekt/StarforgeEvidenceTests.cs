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

    private JsonObject Export()
    {
        var document = NativeExporter.Export(new NativeExportOptions(ProgramPath, Path.Join(Source, "estate")));
        Save(ExportPath, document);
        return document;
    }

    private JsonObject Gate(string engine = NativeProducer.Starforge) =>
        RektEvidence.BuildEvidence(Source, Relative, Output, 0, engine: engine);

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
}
