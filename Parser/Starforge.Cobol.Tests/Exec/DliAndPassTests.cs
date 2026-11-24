using Starforge.Cobol.Cli;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Exec;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests.Exec;

public sealed class DliParserTests
{
    [Fact]
    public void GetUniqueWithPcbLiteralSegmentAndWhere()
    {
        var d = ExecText.Dli("GU USING PCB(2) SEGMENT('CUSTSEG') INTO(WS-CUST) WHERE(CUSTNO = WS-KEY) FIELDLENGTH(8)");

        d.Status.Should().Be(ExecStatus.Parsed);
        d.Function.Should().Be("GU");
        d.Pcb!.Text.Should().Be("2");
        d.Segments.Should().ContainSingle();
        d.Segments[0].Name.Should().Be(new ExecTarget("CUSTSEG", true, null));
        d.Segments[0].Options.Select(o => o.Name).Should().Equal("INTO", "WHERE", "FIELDLENGTH");
        d.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void PathCallWithDynamicSegmentName()
    {
        var d = ExecText.Dli("GNP USING PCB(1) SEGMENT(ROOT) SEGMENT((WS-SEG-NAME)) INTO(WS-AREA)");

        d.Segments.Select(s => s.Name.Key).Should().Equal("ROOT", "&WS-SEG-NAME");
    }

    [Theory]
    [InlineData("GET NEXT IN PARENT USING PCB(1) SEGMENT(S1) INTO(A)", "GNP")]
    [InlineData("REPL USING PCB(1) SEGMENT(S1) FROM(A)", "REPL")]
    [InlineData("SCHD PSB((WS-PSB)) NODHABEND", "SCHD")]
    [InlineData("TERM", "TERM")]
    [InlineData("CHKP ID(WS-CHKP-ID)", "CHKP")]
    public void FunctionsInShortAndLongForm(string inner, string function)
    {
        var d = ExecText.Dli(inner);

        d.Status.Should().Be(ExecStatus.Parsed);
        d.Function.Should().Be(function);
        d.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void ScheduleRecordsThePsb()
    {
        ExecText.Dli("SCHD PSB('PAYPSB')").Psb.Should().Be(new ExecTarget("PAYPSB", true, null));
    }

    [Fact]
    public void UnknownFunctionIsUnsupported()
    {
        var s = ExecParser.Parse("EXEC DLI FROB USING PCB(1) END-EXEC", Lexing.ExecKind.Dli, ExecText.Range);

        s.Status.Should().Be(ExecStatus.Unsupported);
        s.Codes().Should().Equal("dli-unknown-command:FROB");
    }
}

public sealed class ExecPassTests
{
    [Fact]
    public void ParsedBlocksMoveFromUnsupportedToParsedAndUnknownOnesKeepAPreciseReason()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("PROCEDURE DIVISION."),
            B("EXEC CICS LINK PROGRAM('SUB1')"),
            B("     COMMAREA(WS-CA)"),
            B("END-EXEC."),
            B("EXEC SQL SELECT A INTO :A FROM T1 -- NOTE"),
            B("END-EXEC."),
            B("EXEC CICS FROBNICATE END-EXEC."),
            B("GOBACK."));
        r.Coverage.ByReason.Should().ContainKey("Unsupported(exec-not-yet-parsed)");

        var exec = ExecPass.Run(r);

        exec.Statements.Select(s => (s.Kind, s.Status, s.Operation)).Should().Equal(
            (Lexing.ExecKind.Cics, ExecStatus.Parsed, "LINK"),
            (Lexing.ExecKind.Sql, ExecStatus.Parsed, "SELECT INTO"),
            (Lexing.ExecKind.Cics, ExecStatus.Unsupported, "FROBNICATE"));
        exec.Statements[0].Range.StartLine.Should().Be(4);
        exec.Statements[0].Range.EndLine.Should().Be(6);
        r.Coverage.ByReason.Keys.Where(k => k.StartsWith("Unsupported", StringComparison.Ordinal))
            .Should().Equal("Unsupported(cics-unknown-command)");
        r.Coverage.ByReason["Unsupported(cics-unknown-command)"].Should().Be("EXECCICSFROBNICATEEND-EXEC".Length);
        r.Coverage.ByReason["Comment(sql-comment)"].Should().Be("--NOTE".Length);
        r.Coverage.Unaccounted.Should().Be(0);
        r.Coverage.Conflicts.Should().Be(0);
        r.Diagnostics.Select(d => d.Fingerprint).Should().Contain("cics-unknown-command:FROBNICATE");
    }

    [Fact]
    public void CoverageJsonCarriesTheExecArray()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("PROCEDURE DIVISION."),
            B("EXEC CICS XCTL PROGRAM(WS-NEXT) END-EXEC."),
            B("EXEC SQL UPDATE T1 SET A = :A END-EXEC."));
        var exec = ExecPass.Run(r);
        using var buffer = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            Emit.JsonReport.WriteCoverage(w, r, new Emit.PathDisplay(new[] { (estate.Root, "e") }), exec);
        }

        using var doc = System.Text.Json.JsonDocument.Parse(buffer.ToArray());
        var root = doc.RootElement;
        root.GetProperty("execSummary").GetProperty("parsed").GetInt32().Should().Be(2);
        var items = root.GetProperty("exec").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("operation").GetString()).Should().Equal("XCTL", "UPDATE");
        var program = items[0].GetProperty("resources")[0];
        (program.GetProperty("kind").GetString(), program.GetProperty("static").GetBoolean(), program.GetProperty("dataName").GetString())
            .Should().Be(("Program", false, "WS-NEXT"));
        items[1].GetProperty("tables")[0].GetProperty("name").GetString().Should().Be("T1");
    }

    [Fact]
    public void CicsDiagnosticsPointAtTheSourceLineOfTheOption()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("PROCEDURE DIVISION."),
            B("EXEC CICS LINK PROGRAM('SUB1')"),
            B("     SHINYNEW(X)"),
            B("END-EXEC."));

        var exec = ExecPass.Run(r);

        var d = exec.Diagnostics.Single();
        d.Fingerprint.Should().Be("cics-unknown-option:LINK SHINYNEW");
        d.Range!.Value.StartLine.Should().Be(5);
        d.Range!.Value.StartColumn.Should().Be(17);
    }
}

public sealed class ExecDiffCommandTests
{
    [Fact]
    public void DiffRektExecComparesResourcesWithTheBaseline()
    {
        using var estate = new TestEstate();
        estate.Write("src/cobol/MAIN.cbl",
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("PROCEDURE DIVISION."),
            B("EXEC CICS LINK PROGRAM('SUB1') END-EXEC."),
            B("EXEC CICS XCTL PROGRAM(WS-NEXT) END-EXEC."),
            B("EXEC SQL DELETE FROM T1 WHERE K = :K END-EXEC."),
            B("GOBACK."));
        var baseline = Path.Combine(estate.Root, "rekt");
        Directory.CreateDirectory(Path.Combine(baseline, "src", "cobol"));
        File.WriteAllText(Path.Combine(baseline, "src", "cobol", "MAIN.cbl-deps.json"), """
            {"dependencyDialectStatements": [
              {"kind": "EXEC_CICS", "operation": "LINK", "resourceReferences": [],
               "resourceBindings": [{"kind": "CICS_PROGRAM", "name": "SUB1", "targetResolution": {"kind": "literal"}}]},
              {"kind": "EXEC_CICS", "operation": "XCTL", "resourceReferences": [],
               "resourceBindings": [{"kind": "CICS_PROGRAM", "name": "SUB9", "targetResolution": {"kind": "immutable-working-storage-literal", "operand": "WS-NEXT"}}]},
              {"kind": "EXEC_CICS", "operation": "DFHRESP", "resourceReferences": [], "resourceBindings": []},
              {"kind": "EXEC_SQL", "operation": "DELETE", "resourceBindings": [],
               "resourceReferences": [{"kind": "SQL_RELATION", "name": "T2", "role": "reference"}, {"kind": "SQL_HOST_VARIABLE", "name": "K"}]}
            ]}
            """);
        var outDir = Path.Combine(estate.Root, "out");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = CliApp.Run(new[] { "diff-rekt", "exec", "--source", Path.Combine(estate.Root, "src"), "--rekt-baseline", baseline, "--out", outDir }, stdout, stderr);

        exit.Should().Be(0, stderr.ToString());
        stdout.ToString().Should().Contain("cics-commands=2/2:2 cics-program=2/2:2 ").And.Contain("sql-tables=0/1:1 ").And.Contain("sql-host-variables=1/1:1 ");
        var md = File.ReadAllText(Path.Combine(outDir, "exec-diff.md"));
        md.Should().Contain("| src/cobol/MAIN.cbl | `DELETE T1` | `DELETE T2` |");
        File.Exists(Path.Combine(outDir, "exec-diff.json")).Should().BeTrue();
    }
}
