using Starforge.Cobol.Cli;
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
        return (exit, stdout.ToString(), stderr.ToString());
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
}
