using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Doctor;

// STARFORGE_TEACH picks how ./doctor.sh teach-parser run teaches: auto (default), review or off.
public sealed class DoctorTeachParserTests : IDisposable
{
    private readonly DoctorFixture _doctor = new();

    public DoctorTeachParserTests()
    {
        _doctor.AddCheckoutFile("Config/load-config.sh");
        _doctor.AddCheckoutFile("Config/ai-config.env.example");
        File.WriteAllText(Path.Join(_doctor.Root, "Config", "ai-config.local.env"),
            "AZURE_OPENAI_SERVICE_TYPE=GitHubCopilot\nAISETTINGS__MODELID=test-model\n");
        var program = Path.Join(_doctor.Root, "source", "estate", "PROG.cbl");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, "       IDENTIFICATION DIVISION.\n       PROGRAM-ID. PROG.\n");
    }

    public void Dispose() => _doctor.Dispose();

    private IReadOnlyList<string> TeachCall() =>
        _doctor.Calls("dotnet").Should().ContainSingle(call => call.Contains("teach-parser")).Subject;

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("auto", false, false)]
    [InlineData("review", true, false)]
    [InlineData("sometimes", true, true)]
    public void RunTeachesAutomaticallyUnlessReviewIsChosen(string? mode, bool review, bool warns)
    {
        if (mode is not null)
        {
            _doctor.Env["STARFORGE_TEACH"] = mode;
        }

        var result = _doctor.Run("teach-parser", "run");

        result.ShouldSucceed();
        var call = TeachCall();
        call.Contains("--review").Should().Be(review);
        DoctorFixture.After(call, "--source").Should().Contain("/source");
        result.StdErr.Contains("STARFORGE_TEACH='sometimes' is not auto, review or off").Should().Be(warns);
    }

    [Fact]
    public void ShareNeedsNoAiSettingsAndPassesTheEstateForTheNameCheck()
    {
        File.Delete(Path.Join(_doctor.Root, "Config", "ai-config.local.env"));

        var result = _doctor.Run("teach-parser", "share", "copy-include", "--github");

        result.ShouldSucceed();
        var call = TeachCall();
        call.Should().ContainInOrder("teach-parser", "share", "--source").And.ContainInOrder("copy-include", "--github", "--rules");
    }
}
