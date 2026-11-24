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
    public void DiffRektIsAStubUntilM5()
    {
        var (exit, _, stderr) = Run("diff-rekt");

        exit.Should().Be(2);
        stderr.Should().Contain("not implemented until M5");
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
    public void UnknownOptionIsAUsageError()
    {
        var (exit, _, stderr) = Run("coverage", "--source", "x", "--out", "y", "--bogus");

        exit.Should().Be(2);
        stderr.Should().Contain("--bogus");
    }
}
