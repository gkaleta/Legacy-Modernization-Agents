using Starforge.Cobol.Cli;
using Starforge.Cobol.ModelDiff;
using ModelDiffProgram = Starforge.Cobol.ModelDiff.Program;

namespace Starforge.Cobol.Contract.Tests;

// Runs the model diff in-process; the CLI's diff-rekt --model only launches this entry point.
public sealed class ModelDiffTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "model-diff", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string A(string text) => "       " + text;

    private static string B(string text) => "           " + text;

    private static async Task<(int Exit, string Out, string Err)> Diff(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await ModelDiffProgram.Run(args, stdout, stderr);
        return (exit, stdout.ToString().Replace("\r\n", "\n"), stderr.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task OneSourcePerEstateGivesTheSameDiffAsTheirParent()
    {
        foreach (var name in new[] { "ALPHA", "BETA" })
        {
            var path = Path.Combine(_root, name.ToLowerInvariant(), "cobol", name + ".cbl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join("\n",
                A("IDENTIFICATION DIVISION."), A($"PROGRAM-ID. {name}."), A("PROCEDURE DIVISION."), B("GOBACK.")) + "\n");
        }

        var alpha = Path.Combine(_root, "alpha");
        var beta = Path.Combine(_root, "beta");
        var emitDir = Path.Combine(_root, "emit");
        CliApp.Run(["emit", "--source", alpha, "--source", beta, "--out", emitDir], new StringWriter(), new StringWriter())
            .Should().Be(0);
        var perEstate = Path.Combine(_root, "per-estate");
        var parent = Path.Combine(_root, "parent");

        var multi = await Diff("--rekt", emitDir, "--starforge", emitDir, "--source", alpha, "--source", beta, "--out", perEstate);
        var single = await Diff("--rekt", emitDir, "--starforge", emitDir, "--source", _root, "--out", parent);

        multi.Exit.Should().Be(0, multi.Err);
        single.Exit.Should().Be(0, single.Err);
        multi.Out.Should().StartWith("programs=2 unexplained=0\n");
        File.ReadAllBytes(Path.Combine(perEstate, "model-diff.json"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(parent, "model-diff.json")));
    }

    // The REKT side shows its stored gate record; so does a PARSER_ENGINE=starforge tree given as --starforge-evidence.
    [Theory]
    [InlineData(true, "complete", 0, "evidence.coverage                matched=5 onlyStarforge=0 onlyRekt=0 mismatch=0")]
    [InlineData(false, "partial", 0, "evidence.coverage                matched=0 onlyStarforge=0 onlyRekt=0 mismatch=5")]
    [InlineData(true, "partial", 1, "evidence.coverage                matched=0 onlyStarforge=0 onlyRekt=0 mismatch=5")]
    public async Task StarforgeEvidenceTreeIsComparedByItsStoredGateRecord(bool analysisComplete, string coverage, int exit, string tally)
    {
        var path = Path.Combine(_root, "alpha", "cobol", "ALPHA.cbl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n",
            A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. ALPHA."), A("PROCEDURE DIVISION."), B("GOBACK.")) + "\n");
        var alpha = Path.Combine(_root, "alpha");
        var emitDir = Path.Combine(_root, "emit");
        CliApp.Run(["emit", "--source", alpha, "--out", emitDir], new StringWriter(), new StringWriter()).Should().Be(0);
        static string Evidence(string status) =>
            "{\"coverage\": {" + string.Join(", ", new[] { "CALL", "COPY", "EXEC_CICS", "EXEC_SQL", "FILE_IO" }.Select(k => $"\"{k}\": \"{status}\""))
            + "}, \"definitions\": [], \"unresolvedDiagnostics\": []}";
        var rekt = Path.Combine(_root, "rekt");
        var evidenceTree = Path.Combine(_root, "starforge-run");
        foreach (var file in Directory.EnumerateFiles(emitDir, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(rekt, Path.GetRelativePath(emitDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        foreach (var (tree, status) in new[] { (rekt, "complete"), (evidenceTree, coverage) })
        {
            Directory.CreateDirectory(Path.Combine(tree, "alpha", "cobol", "ALPHA.cbl.report"));
            File.WriteAllText(Path.Combine(tree, "alpha", "cobol", "ALPHA.cbl.report", "dependency-evidence.json"), Evidence(status));
        }
        File.WriteAllText(Path.Combine(evidenceTree, "alpha", "cobol", "ALPHA.cbl-deps.json"),
            $"{{\"dependencyProducer\": \"starforge\", \"dependencyAnalysisComplete\": {(analysisComplete ? "true" : "false")}}}");

        var result = await Diff("--rekt", rekt, "--starforge", emitDir, "--source", alpha, "--out", Path.Combine(_root, "out"),
            "--starforge-evidence", evidenceTree);

        result.Exit.Should().Be(exit, result.Err);
        result.Out.Split('\n').Should().Contain(tally);
    }

    private string Deps(string name, params (string Program, string Kind)[] bindings)
    {
        var items = bindings.Select(b =>
            $"{{\"kind\": \"CICS_PROGRAM\", \"name\": \"{b.Program}\", \"targetResolution\": {{\"kind\": \"{b.Kind}\", \"operand\": \"WS-NEXT\"}}}}");
        var path = Path.Combine(_root, name + "-deps.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, $"{{\"dependencyDialectStatements\": [{{\"operation\": \"XCTL\", \"resourceBindings\": [{string.Join(", ", items)}]}}]}}");
        return path;
    }

    [Fact]
    public void ReachingProofInsideTheSlotUniverseNarrowsRektsExtraTargets()
    {
        const string slot = "closed-world-commarea-slot";
        const string reaching = "structured-reaching-assignments";
        var rekt = Deps("rekt", ("PGMA", slot), ("PGMB", slot), ("PGMC", slot));

        var (names, closed) = ArtifactDiff.NarrowedTargets(rekt, Deps("sf", ("PGMA", reaching)));

        names.Order(StringComparer.Ordinal).Should().Equal("PGMB", "PGMC");
        closed.Should().BeTrue();
    }

    [Fact]
    public void ReachingProofOutsideTheSlotUniverseNarrowsNothing()
    {
        const string slot = "closed-world-commarea-slot";
        var rekt = Deps("rekt", ("PGMA", slot), ("PGMB", slot));

        var sf = Deps("sf", ("PGMA", "structured-reaching-assignments"), ("PGMZ", "structured-reaching-assignments"));

        var (names, closed) = ArtifactDiff.NarrowedTargets(rekt, sf);

        names.Should().BeEmpty();
        closed.Should().BeFalse();
    }
}
