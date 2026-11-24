using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Doctor;

// Drives doctor.sh rekt-parse with each PARSER_ENGINE; Starforge and the evidence writer are the real CLIs.
public sealed class DoctorParserEngineTests : IDisposable
{
    private const string Program = """
               IDENTIFICATION DIVISION.
               PROGRAM-ID. PROG.
               DATA DIVISION.
               WORKING-STORAGE SECTION.
               COPY REAL.
               PROCEDURE DIVISION.
               MAIN-PARA.
                   CALL 'SUBPROG'.
                   GOBACK.
        """;

    private const string Subprogram = """
               IDENTIFICATION DIVISION.
               PROGRAM-ID. SUBPROG.
               PROCEDURE DIVISION.
                   GOBACK.
        """;

    private readonly DoctorFixture _doctor = new();

    public DoctorParserEngineTests()
    {
        _doctor.AddCheckoutFile("tools/preprocess-for-rekt.sh");
        Write("estate/PROG.cbl", Program + "\n");
        Write("estate/SUBPROG.cbl", Subprogram + "\n");
        Write("estate/copy/REAL.cpy", "       01 REAL-FIELD PIC X.\n");
        _doctor.Env["_REKT_PROGRAM_FILTER"] = "estate/PROG.cbl";
        _doctor.Env["_PROGRAM_FACTS"] = "false";
    }

    public void Dispose() => _doctor.Dispose();

    private string RektOutput => Path.Join(_doctor.Root, "output", "rekt");

    [Fact]
    public void Starforge_ParsesWithoutDocker_IntoTheRektOutputLayout()
    {
        var starforge = Path.Join(AppContext.BaseDirectory, "starforge.dll");
        _doctor.Env["PARSER_ENGINE"] = "starforge";
        _doctor.Env["STARFORGE_CLI_ASSEMBLY"] = starforge;
        _doctor.Env["STARFORGE_TEST_DLL"] = starforge;

        var result = _doctor.Run("rekt-parse");

        result.ShouldSucceed();
        result.StdOut.Should().Contain("Parser engine: starforge (PARSER_ENGINE)");
        _doctor.Calls("docker").Should().BeEmpty();
        _doctor.Calls("dotnet").Count(call => call[0] == starforge && call[1] == "emit").Should().Be(1);
        var export = JsonNode.Parse(File.ReadAllText(Path.Join(RektOutput, "estate/PROG.cbl-deps.json")))!;
        export["dependencyProducer"]!.GetValue<string>().Should().Be("starforge");
        export["dependencies"]!.AsArray().Select(dependency => dependency!["callTarget"]!["name"]!.GetValue<string>())
            .Should().Equal("REAL.cpy", "SUBPROG");
        var report = Path.Join(RektOutput, "estate/PROG.cbl.report");
        Directory.GetFiles(report, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(report, file).Replace('\\', '/')).Order(StringComparer.Ordinal)
            .Should().Equal("cfg/cfg-PROG.cbl.json", "data_structures/PROG.cbl-data.json",
                "dependency-evidence.json", "flow_ast/flow-ast-PROG.cbl.json");
        var evidence = JsonNode.Parse(File.ReadAllText(Path.Join(report, "dependency-evidence.json")))!;
        evidence["relativePath"]!.GetValue<string>().Should().Be("estate/PROG.cbl");
        evidence["exportExitCode"]!.GetValue<int>().Should().Be(0);
        File.Exists(Path.Join(RektOutput, "estate/SUBPROG.cbl-deps.json")).Should().BeFalse("the filter selected PROG only");
        File.ReadAllText(Path.Join(RektOutput, ".work/parser-engine")).Should().Be("starforge\n");
        Directory.GetDirectories(Path.Join(RektOutput, ".work"), "starforge-*").Should().BeEmpty();
    }

    [Fact]
    public void InvalidEngine_FailsBeforeParsing()
    {
        _doctor.Env["PARSER_ENGINE"] = "java";

        var result = _doctor.Run("rekt-parse");

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("PARSER_ENGINE must be rekt, starforge or both (got 'java').");
        _doctor.Calls("docker").Should().BeEmpty();
        Directory.Exists(RektOutput).Should().BeFalse();
    }

    [Fact]
    public void UnsetEngine_ParsesWithTheRektContainer()
    {
        _doctor.UseParserContainer("#!/bin/bash\nexit 0\n");

        var result = _doctor.Run("rekt-parse");

        result.StdOut.Should().Contain("Parser engine: rekt (PARSER_ENGINE)");
        _doctor.Calls("docker").Should().Contain(call => call.Count > 0 && call[0] == "exec" && call.Contains("java"));
        _doctor.Calls("dotnet").Should().NotContain(call => call.Contains("emit"));
    }

    private void Write(string relative, string text)
    {
        var path = Path.Join(_doctor.Root, "source", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
