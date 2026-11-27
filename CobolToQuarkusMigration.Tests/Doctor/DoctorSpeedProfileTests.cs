using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Doctor;

// The speed profile menu once exported CODEX_* names the app had stopped reading, so every
// profile silently ran with the appsettings.json defaults.
public sealed class DoctorSpeedProfileTests
{
    [Fact]
    public void EverySpeedProfileVariable_IsOneTheAppReads()
    {
        var doctor = File.ReadAllText(Path.Join(DoctorFixture.RepoRoot, "doctor.sh"));
        var program = File.ReadAllText(Path.Join(DoctorFixture.RepoRoot, "Program.cs"));

        var start = doctor.IndexOf("select_speed_profile() {", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var end = doctor.IndexOf("\n}\n", start, StringComparison.Ordinal);
        var body = doctor[start..end];
        var exported = Regex.Matches(body, @"export\s+([A-Z_]+)=")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        // A variable the user exported before running doctor.sh wins over the profile's value.
        Regex.Matches(body, @"export\s+([A-Z_]+)=""\$\{\1:-[^}]*\}""").Count
            .Should().Be(Regex.Matches(body, @"export\s+[A-Z_]+=").Count);

        exported.Should().Contain("AI_MAX_PARALLEL_CONVERSION");
        exported.Where(name => !program.Contains($"GetEnvironmentVariable(\"{name}\")", StringComparison.Ordinal))
            .Should().BeEmpty();
    }
}
