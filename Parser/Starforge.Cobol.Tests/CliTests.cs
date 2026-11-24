using Starforge.Cobol.Cli;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public sealed class CliTests
{
    private static (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = CliApp.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void CoverageWritesSummaryAndPerProgramReports()
    {
        using var estate = new TestEstate();
        estate.Write("cobol/MAIN.cbl",
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            B("COPY CB."),
            A("PROCEDURE DIVISION."),
            B("GOBACK."));
        estate.Write("copy/CB.cpy", A("01 CB-AREA PIC X."));
        estate.Write(".preprocessed/cobol/MAIN.cbl", A("IGNORED."));
        var outDir = Path.Combine(estate.Root, "out");

        var (exit, stdout, stderr) = Run("coverage", "--source", estate.Root, "--out", outDir);

        exit.Should().Be(0, stderr);
        stdout.Should().StartWith("programs=1 exceptions=0 unaccounted=0 conflicts=0 ");
        File.Exists(Path.Combine(outDir, "summary.json")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "summary.md")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "programs", Path.GetFileName(estate.Root), "cobol", "MAIN.cbl.json")).Should().BeTrue();
    }

    [Fact]
    public void ParseWritesTokenJson()
    {
        using var estate = new TestEstate();
        var program = estate.Write("MAIN.cbl", A("PROCEDURE DIVISION."), B("GOBACK."));

        var (exit, stdout, _) = Run("parse", program);

        exit.Should().Be(0);
        stdout.Should().Contain("\"GOBACK\"");
    }

    [Fact]
    public void ParseReportsWarningsOnStderrAndInTheDiagnosticsArray()
    {
        using var estate = new TestEstate();
        var program = estate.Write("MAIN.cbl", A("PROCEDURE DIVISION."), Indicator('Q', "    GOBACK."));

        var (exit, stdout, stderr) = Run("parse", program);

        var label = Path.GetFileName(estate.Root);
        exit.Should().Be(0);
        stderr.Should().Be(
            $"{label}/MAIN.cbl:2:7-2:7: warning: invalid-indicator: Column 7 holds 'Q', which is not a valid indicator; the line is read as code\n" +
            $"{label}/MAIN.cbl:1:8-1:16: warning: missing-identification-division: Program has no IDENTIFICATION DIVISION\n");
        using var json = System.Text.Json.JsonDocument.Parse(stdout);
        json.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Select(d => (d.GetProperty("code").GetString(), d.GetProperty("severity").GetString(), d.GetProperty("range").GetString()))
            .Should().Equal(
                ("missing-identification-division", "Warning", $"{label}/MAIN.cbl:1:8-1:16"),
                ("invalid-indicator", "Warning", $"{label}/MAIN.cbl:2:7-2:7"));
    }

    [Fact]
    public void EmitWritesTheReportTreeOfEveryProgram()
    {
        using var estate = new TestEstate();
        estate.Write("cobol/MAIN.cbl",
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("PROCEDURE DIVISION."),
            B("CALL 'SUB'."),
            B("GOBACK."));
        var outDir = Path.Combine(estate.Root, "out");
        var label = Path.GetFileName(estate.Root);

        var (exit, stdout, stderr) = Run("emit", "--source", estate.Root, "--out", outDir);

        exit.Should().Be(0, stderr);
        stdout.Should().StartWith("programs=1 files=4 exceptions=0 ");
        Directory.GetFiles(outDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(outDir, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .Should().Equal(
                $"{label}/cobol/MAIN.cbl-deps.json",
                $"{label}/cobol/MAIN.cbl.report/cfg/cfg-MAIN.cbl.json",
                $"{label}/cobol/MAIN.cbl.report/data_structures/MAIN.cbl-data.json",
                $"{label}/cobol/MAIN.cbl.report/flow_ast/flow-ast-MAIN.cbl.json");
    }

    [Fact]
    public void DiffRektModelAcceptsOneSourcePerEstate()
    {
        using var estate = new TestEstate();
        foreach (var name in new[] { "ALPHA", "BETA" })
        {
            estate.Write($"{name.ToLowerInvariant()}/cobol/{name}.cbl",
                A("IDENTIFICATION DIVISION."),
                A($"PROGRAM-ID. {name}."),
                A("PROCEDURE DIVISION."),
                B("GOBACK."));
        }

        var alpha = Path.Combine(estate.Root, "alpha");
        var beta = Path.Combine(estate.Root, "beta");
        var emitDir = Path.Combine(estate.Root, "emit");
        Run("emit", "--source", alpha, "--source", beta, "--out", emitDir).Exit.Should().Be(0);
        var perEstate = Path.Combine(estate.Root, "per-estate");
        var parent = Path.Combine(estate.Root, "parent");

        var multi = Run("diff-rekt", "--model", "--rekt", emitDir, "--starforge", emitDir,
            "--source", alpha, "--source", beta, "--out", perEstate);
        var single = Run("diff-rekt", "--model", "--rekt", emitDir, "--starforge", emitDir,
            "--source", estate.Root, "--out", parent);

        multi.Exit.Should().Be(0, multi.Err);
        single.Exit.Should().Be(0, single.Err);
        multi.Out.Should().StartWith("programs=2 unexplained=0\n");
        File.ReadAllBytes(Path.Combine(perEstate, "model-diff.json"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(parent, "model-diff.json")));
    }

    [Fact]
    public void EmitRejectsAPositionalArgument()
    {
        var (exit, _, stderr) = Run("emit", "estate", "--out", "x");

        exit.Should().Be(2);
        stderr.Should().Contain("unexpected argument 'estate' (repeat --source for each estate)");
    }

    [Fact]
    public void DiffRektWithoutAComparisonIsAUsageError()
    {
        var (exit, _, stderr) = Run("diff-rekt");

        exit.Should().Be(2);
        stderr.Should().Contain("diff-rekt needs a comparison: --layouts, --procedure, --model or exec");
    }

    [Fact]
    public void DiffRektLayoutsComparesItemsAndCommareaLengths()
    {
        using var estate = new TestEstate();
        estate.Write("MAIN.cbl",
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("01 REC."),
            B("05 F1 PIC X(4)."),
            B("05 FILLER REDEFINES F1 PIC 9(4)."),
            B("05 F2 PIC S9(4) COMP."),
            A("77 SOLO PIC X."),
            A("PROCEDURE DIVISION."),
            B("GOBACK."));
        var rekt = Path.Combine(estate.Root, "rekt");
        var report = Path.Combine(rekt, Path.GetFileName(estate.Root), "MAIN.cbl.report", "data_structures");
        Directory.CreateDirectory(report);
        File.WriteAllText(Path.Combine(report, "MAIN.cbl-data.json"), """
            {"name":"[ROOT]","levelNumber":0,"children":[
              {"name":"REC","levelNumber":1,"rawText":"01 REC.","redefines":"","dataType":"OBJECT","sourceSection":"WORKING_STORAGE","children":[
                {"name":"F1","levelNumber":5,"rawText":"05 F1 PIC X(5).","redefines":"","dataType":"STRING","sourceSection":"WORKING_STORAGE","children":[]},
                {"name":"FIL-R001","levelNumber":5,"rawText":"05 FIL-R001 REDEFINES F1 PIC 9(4).","redefines":"F1","dataType":"NUMBER","sourceSection":"WORKING_STORAGE","children":[]},
                {"name":"F3","levelNumber":5,"rawText":"05 F3 PIC X.","redefines":"","dataType":"STRING","sourceSection":"WORKING_STORAGE","children":[]}]},
              {"name":"WHEN-COMPILED","levelNumber":1,"rawText":"","redefines":"","dataType":"STRING","sourceSection":"PROCEDURE_DIVISION","children":[]}]}
            """);
        File.WriteAllText(Path.Combine(rekt, Path.GetFileName(estate.Root), "MAIN.cbl-deps.json"), """
            {"commareaInventory":{"records":[{"name":"REC","slots":{"F1":{"length":4},"F2":{"length":4}}}]}}
            """);
        var outDir = Path.Combine(estate.Root, "out");

        var (exit, stdout, stderr) = Run("diff-rekt", "--layouts", "--rekt", rekt, "--source", estate.Root, "--out", outDir);

        exit.Should().Be(0, stderr);
        stdout.Should().StartWith("programs=1 noBaseline=0 exceptions=0 rektItems=4 starforgeItems=5 matched=3 onlyInStarforge=2 onlyInRekt=1 ");
        stdout.Should().Contain(" picMismatches=1 redefinesMismatches=0 ");
        stdout.Should().Contain(" commarea=1/2 ");
        File.ReadAllText(Path.Combine(outDir, "layout-diff.md")).Should().Contain("level-77 item (REKT exports no level 77)");
    }

    [Fact]
    public void DiffRektProcedureComparesParagraphsAndEdgesWithLines()
    {
        using var estate = new TestEstate();
        estate.Write("MAIN.cbl",
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("PROCEDURE DIVISION."),
            A("MAIN-PARA."),
            B("PERFORM SUB-PARA."),
            B("CALL 'PGM1'."),
            B("GO TO END-PARA."),
            A("SUB-PARA."),
            B("DISPLAY 'X'."),
            A("END-PARA."),
            B("GOBACK."));
        var rekt = Path.Combine(estate.Root, "rekt");
        var flow = Path.Combine(rekt, Path.GetFileName(estate.Root), "MAIN.cbl.report", "flow_ast");
        Directory.CreateDirectory(flow);
        File.WriteAllText(Path.Combine(flow, "flow-ast-MAIN.cbl.json"), """
            {"type":"PROCEDURE_DIVISION_BODY","name":"body","originalText":"MAIN-PARA.","children":[
              {"type":"PARAGRAPHS","name":"g","originalText":"","children":[
                {"type":"PARAGRAPH","name":"MAIN-PARA","originalText":"MAIN-PARA.\n    PERFORM SUB-PARA.\n    CALL 'PGM2'.\n    GO TO END-PARA.","children":[
                  {"type":"PERFORM","name":"p","originalText":"PERFORM SUB-PARA","children":[]},
                  {"type":"CALL","name":"c","originalText":"CALL 'PGM2'","children":[]},
                  {"type":"GOTO","name":"g","originalText":"GO TO END-PARA","children":[]}]},
                {"type":"PARAGRAPH","name":"SUB-PARA","originalText":"SUB-PARA.\n    DISPLAY 'X'.","children":[]},
                {"type":"PARAGRAPH","name":"END-PARA","originalText":"END-PARA.\n    GOBACK.","children":[]}]}]}
            """);
        var outDir = Path.Combine(estate.Root, "out");

        var (exit, stdout, stderr) = Run("diff-rekt", "--procedure", "--rekt", rekt, "--source", estate.Root, "--out", outDir);

        exit.Should().Be(0, stderr);
        stdout.Should().StartWith("programs=1 noBaseline=0 exceptions=0 sections=0/0:0~0 paragraphs=3/3:3~0 perform=1/1:1~0 call=0/1:1~0 goto=1/1:1~0\n");
        var md = File.ReadAllText(Path.Combine(outDir, "procedure-diff.md"));
        md.Should().Contain("| call | only Starforge | `MAIN-PARA -> 'PGM1'` | 6 | |");
        md.Should().Contain("| call | only REKT | `MAIN-PARA -> 'PGM2'` | | (statement text not aligned) |");
    }

    [Fact]
    public void UnknownOptionIsAUsageError()
    {
        var (exit, _, stderr) = Run("coverage", "--source", "x", "--out", "y", "--bogus");

        exit.Should().Be(2);
        stderr.Should().Contain("--bogus");
    }
}
