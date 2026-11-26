using CobolToQuarkusMigration.Helpers;
using CobolToQuarkusMigration.Models;
using CobolToQuarkusMigration.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Helpers;

public sealed class EstateEvidenceScopeTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(".test-artifacts", $"estate-scope-{Guid.NewGuid():N}"));
    private string SourceRoot => Path.Combine(_root, "source");
    private string FactsRoot => Path.Combine(_root, "output", "rekt");
    private string Estate => Path.Combine(SourceRoot, "bank");

    [Fact]
    public void EstateParsedWithItsParent_IsConvertedFromTheParentWithOnlyItsPrograms()
    {
        Source("bank/cobol/MAIN.cbl");
        Source("bank/cobol/SUB.cbl");
        Source("other/cobol/MAIN.cbl");
        DependencyEvidenceFixture.Write(_root, SourceRoot, "bank/cobol/MAIN.cbl");
        DependencyEvidenceFixture.Write(_root, SourceRoot, "bank/cobol/SUB.cbl");
        DependencyEvidenceFixture.Write(_root, SourceRoot, "other/cobol/MAIN.cbl");

        var scope = EstateEvidenceScope.Resolve(Estate, FactsRoot, null);

        scope.Widened.Should().BeTrue();
        scope.Estate.Should().Be("bank");
        scope.SourceRoot.Should().Be(SourceRoot);
        scope.Selection.Should().BeEquivalentTo("bank/cobol/MAIN.cbl", "bank/cobol/SUB.cbl");

        // The strict gate now finds the evidence the parse wrote; before, every program was "missing".
        var files = ProgramSelection.Apply(Scan(scope.SourceRoot), scope.Selection, out _);
        var verify = () => ConversionEligibility.Select(files, scope.SourceRoot, FactsRoot,
            Path.Combine(_root, "out"), new() { DependencyMode = "strict" }, null);
        verify.Should().NotThrow();
        files.Select(file => file.FilePath).Should().OnlyContain(path => path.StartsWith(Estate));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/jcl/elsewhere")]
    public void TheRunMovesToTheParentButReadsJclFromTheEstateOnly(string? configuredJcl)
    {
        Source("bank/cobol/MAIN.cbl");
        DependencyEvidenceFixture.Write(_root, SourceRoot, "bank/cobol/MAIN.cbl");
        var settings = new AppSettings();
        settings.ApplicationSettings.CobolSourceFolder = Estate;
        settings.JclJobs.SourceFolder = configuredJcl;

        EstateEvidenceScope.Resolve(Estate, FactsRoot, null).ApplyTo(settings);

        settings.ApplicationSettings.CobolSourceFolder.Should().Be(SourceRoot);
        settings.JclJobs.SourceFolder.Should().Be(configuredJcl ?? Estate);
    }

    [Fact]
    public void ANameSelectionStaysInsideTheEstate_AndAnUnknownNameIsPassedOn()
    {
        Source("bank/cobol/MAIN.cbl");
        Source("other/cobol/MAIN.cbl");
        DependencyEvidenceFixture.Write(_root, SourceRoot, "bank/cobol/MAIN.cbl");

        var scope = EstateEvidenceScope.Resolve(Estate, FactsRoot, ["MAIN", "NOPE"]);

        scope.Selection.Should().Equal("bank/cobol/MAIN.cbl", "NOPE");
    }

    [Fact]
    public void EstateParsedOnItsOwn_IsLeftAlone()
    {
        Source("bank/cobol/MAIN.cbl");
        DependencyEvidenceFixture.Write(_root, Estate, "cobol/MAIN.cbl");

        var scope = EstateEvidenceScope.Resolve(Estate, FactsRoot, ["MAIN"]);

        scope.Widened.Should().BeFalse();
        scope.SourceRoot.Should().Be(Estate);
        scope.Selection.Should().Equal("MAIN");
    }

    [Fact]
    public void EvidenceForAnotherFileWithTheSamePath_DoesNotMoveTheRun()
    {
        Source("bank/cobol/MAIN.cbl");
        var evidence = DependencyEvidenceFixture.Write(_root, SourceRoot, "bank/cobol/MAIN.cbl");
        DependencyEvidenceFixture.Save(_root, evidence with { OriginalSourcePath = Path.Combine(_root, "elsewhere", "MAIN.cbl") });

        EstateEvidenceScope.Resolve(Estate, FactsRoot, null).Widened.Should().BeFalse();
    }

    private void Source(string relative)
    {
        var path = Path.Combine(SourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "       PROCEDURE DIVISION.\n           STOP RUN.");
    }

    private static List<CobolFile> Scan(string root) => SourceTypeRegistry.EnumerateProgramFiles(root)
        .Select(path => new CobolFile { FileName = Path.GetFileName(path), FilePath = path, Content = File.ReadAllText(path) })
        .ToList();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
