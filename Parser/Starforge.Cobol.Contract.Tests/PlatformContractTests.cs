using System.Reflection;
using System.Text.Json.Nodes;
using CobolToQuarkusMigration.Agents.Infrastructure.Facts;
using CobolToQuarkusMigration.Agents.Infrastructure.Rekt;
using Starforge.Cobol.Emit;

namespace Starforge.Cobol.Contract.Tests;

// Binds DFHAID usages to the app's own generated symbolic projection and reads the export back through the app's platform reader.
public sealed class PlatformContractTests : IDisposable
{
    private readonly string _estate = Path.Combine(AppContext.BaseDirectory, "platform", Guid.NewGuid().ToString("N"), "est");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_estate)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string A(string text) => "       " + text;

    private static string B(string text) => "           " + text;

    private JsonObject Emit(params string[] procedure)
    {
        var events = new JsonObject { ["DFHENTER"] = "ENTER", ["DFHCLEAR"] = "CLEAR" };
        for (var i = 1; i <= 3; i++)
        {
            events[$"DFHPA{i}"] = $"PA{i}";
        }

        for (var i = 1; i <= 24; i++)
        {
            events[$"DFHPF{i}"] = $"PF{i}";
        }

        var profile = Path.Combine(Path.GetDirectoryName(_estate)!, "platform-contracts.json");
        Directory.CreateDirectory(Path.Combine(_estate, ".preprocessed"));
        File.WriteAllText(profile, new JsonObject
        {
            ["schemaVersion"] = 1, ["profileKind"] = "platform-replacement", ["profileName"] = "test-terminal-events",
            ["copybooks"] = new JsonObject
            {
                ["DFHAID"] = new JsonObject
                {
                    ["recipe"] = "symbolic-terminal-events-v1", ["selector"] = "EIBAID", ["symbols"] = events,
                    ["documentation"] = new JsonArray("https://www.ibm.com/docs/en/cics-ts/6.x"),
                    ["targetSemantics"] = "named-events-preserve-source-branches",
                },
            },
        }.ToJsonString());
        PlatformCopybookContracts.Generate(_estate, Path.Combine(_estate, ".preprocessed"), profile, TextWriter.Null);

        var program = Path.Combine(_estate, "cobol", "MAIN.cbl");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, string.Join("\n", new[]
        {
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("01  WS-KEY PIC X."),
            A("COPY DFHAID."),
            A("PROCEDURE DIVISION."),
        }.Concat(procedure).Append(B("GOBACK.")).Append("")));

        var result = new CobolFrontEnd(new FrontEndOptions(new[] { _estate })).Process(program);
        var deps = RektEmitter.Render(result, "est/cobol/MAIN.cbl", new RektEmitOptions(PlatformContracts: profile))
            .Single(f => f.RelativePath.EndsWith("-deps.json", StringComparison.Ordinal));
        var path = Path.Combine(Path.GetDirectoryName(_estate)!, "MAIN.cbl-deps.json");
        File.WriteAllBytes(path, deps.ToBytes());
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private IReadOnlyList<string> ReadProjections()
    {
        var reader = typeof(DependencyEvidenceGate).GetMethod("ReadPlatformProjectionNames", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return (IReadOnlyList<string>)reader.Invoke(null, [Path.Combine(Path.GetDirectoryName(_estate)!, "MAIN.cbl-deps.json"), "est/cobol/MAIN.cbl"])!;
        }
        catch (TargetInvocationException e)
        {
            return ["failed: " + e.InnerException!.Message.Replace("\r\n", "\n")];
        }
    }

    private static JsonNode Platform(JsonObject deps) =>
        deps["dependencies"]!.AsArray().Single(d => (string?)d!["dependencyKind"] == "PLATFORM_COPY")!;

    [Fact]
    public void ConditionsOnEibaidBindToTheSymbolicProjectionTheAppAccepts()
    {
        var deps = Emit(
            B("IF EIBAID = DFHENTER"),
            B("    MOVE 'E' TO WS-KEY"),
            B("END-IF."),
            B("EVALUATE EIBAID"),
            B("    WHEN DFHPF3 MOVE '3' TO WS-KEY"),
            B("    WHEN OTHER MOVE 'X' TO WS-KEY"),
            B("END-EVALUATE."));

        var node = Platform(deps);
        ((bool)node["runtimeContractBindingComplete"]!).Should().BeTrue();
        node["runtimeContract"]!["usages"]!.AsArray()
            .Select(u => ((string?)u!["grammarRule"], (string?)u["conditionRole"], (string?)u["sourceText"], (int)u["line"]!))
            .Should().Equal(
                ("ConditionContext", null, "EIBAID = DFHENTER", 38),
                ("EvaluateSelectContext", "evaluate-subject", "EIBAID", 41),
                ("EvaluateWhenContext", "evaluate-when", "WHEN DFHPF3", 42));
        ReadProjections().Should().Equal("DFHAID");
    }

    [Fact]
    public void AnUnprovenUseFailsClosedWithADiagnosticInsteadOfAContract()
    {
        var deps = Emit(B("MOVE DFHENTER TO WS-KEY."));

        var node = Platform(deps);
        ((bool)node["runtimeContractBindingComplete"]!).Should().BeFalse();
        node["runtimeContract"].Should().BeNull();
        node["dependencyDiagnostics"]!.AsArray().Select(d => (string?)d).Should().Equal("starforge: DFHAID symbolic replacement not bound: DFHENTER is used outside an EIBAID comparison (line 8)");
        ReadProjections().Should().Equal("failed: unresolved parser dependency diagnostics at depth 1: [\n        \"starforge: DFHAID symbolic replacement not bound: DFHENTER is used outside an EIBAID comparison (line 8)\"\n      ]");
    }
}
