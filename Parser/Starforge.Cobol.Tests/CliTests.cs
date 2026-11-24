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
    public void UnknownOptionIsAUsageError()
    {
        var (exit, _, stderr) = Run("coverage", "--source", "x", "--out", "y", "--bogus");

        exit.Should().Be(2);
        stderr.Should().Contain("--bogus");
    }
}
