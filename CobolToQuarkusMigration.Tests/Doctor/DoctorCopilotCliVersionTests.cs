using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Doctor;

// The SDK starts the CLI it bundled at build time. An older installed CLI, or COPILOT_CLI_PATH
// pointing elsewhere (a Copilot CLI session sets it), ends in "SDK protocol version mismatch".
public sealed class DoctorCopilotCliVersionTests : IDisposable
{
    private readonly DoctorFixture _doctor = new();

    public DoctorCopilotCliVersionTests()
    {
        _doctor.AddCheckoutFile("Config/load-config.sh");
        _doctor.AddCheckoutFile("Config/ai-config.env.example");
        File.WriteAllText(Path.Join(_doctor.Root, "Config", "ai-config.local.env"),
            "AZURE_OPENAI_SERVICE_TYPE=GitHubCopilot\nAISETTINGS__MODELID=test-model\n");
        File.WriteAllText(Path.Join(_doctor.Root, "CobolToQuarkusMigration.csproj"),
            """<Project><ItemGroup><PackageReference Include="Microsoft.Copilot.SDK" Version="9.9.9" /></ItemGroup></Project>""");

        var packages = Path.Join(_doctor.Root, "nuget");
        var build = Path.Join(packages, "microsoft.copilot.sdk", "9.9.9", "build");
        Directory.CreateDirectory(build);
        File.WriteAllText(Path.Join(build, "Microsoft.Copilot.SDK.props"),
            "<Project><PropertyGroup>\n    <CopilotCliVersion>1.0.112</CopilotCliVersion>\n</PropertyGroup></Project>\n");
        _doctor.Env["NUGET_PACKAGES"] = DoctorFixture.Posix(packages);
    }

    public void Dispose() => _doctor.Dispose();

    private void InstalledCli(string version) =>
        _doctor.WriteExecutable("copilot", $"#!/bin/bash\necho \"GitHub Copilot CLI {version}.\"\n");

    [Fact]
    public void OlderInstalledCli_AsksForAnUpdate()
    {
        InstalledCli("1.0.93");

        var output = _doctor.Run("doctor").Output;

        output.Should().Contain("Copilot CLI version: 1.0.93 (this app is built for 1.0.112)");
        output.Should().Contain("copilot update");
        output.Should().NotContain("COPILOT_CLI_PATH is set");
    }

    [Fact]
    public void CopilotCliPathToAnotherVersion_IsReported()
    {
        InstalledCli("1.0.112");
        _doctor.WriteExecutable("session-copilot", "#!/bin/bash\necho \"GitHub Copilot CLI 1.0.114.\"\n");
        _doctor.Env["COPILOT_CLI_PATH"] = DoctorFixture.Posix(Path.Join(_doctor.Bin, "session-copilot"));

        var output = _doctor.Run("doctor").Output;

        output.Should().Contain("COPILOT_CLI_PATH is set, so AI calls use Copilot CLI 1.0.114");
        output.Should().Contain("unset COPILOT_CLI_PATH");
        output.Should().NotContain("older than the version");
    }

    [Fact]
    public void MatchingCli_HasNoWarning()
    {
        InstalledCli("1.0.112");

        var output = _doctor.Run("doctor").Output;

        output.Should().Contain("Copilot CLI version: 1.0.112 (this app is built for 1.0.112)");
        output.Should().NotContain("copilot update");
        output.Should().NotContain("COPILOT_CLI_PATH is set");
    }
}
