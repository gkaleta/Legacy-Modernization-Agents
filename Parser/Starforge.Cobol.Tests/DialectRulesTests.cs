using System.Text;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Dialect;
using Starforge.Cobol.Emit;
using Starforge.Cobol.Syntax;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

// Taught site rules (Config/starforge-dialect.local.json): they add readings only where Starforge had none.
public sealed class DialectRulesTests : IDisposable
{
    private readonly TestEstate _estate = new();

    public void Dispose() => _estate.Dispose();

    private CompilationResult Process(DialectRules? rules, params string[] lines)
    {
        _estate.Write("src/MAIN.cbl", lines);
        return new CobolFrontEnd(new FrontEndOptions(new[] { _estate.Root }, Rules: rules)).Process(Path.Combine(_estate.Root, "src/MAIN.cbl"));
    }

    private static string[] Procedure(params string[] statements) =>
    [
        A("IDENTIFICATION DIVISION."),
        A("PROGRAM-ID. MAIN."),
        A("DATA DIVISION."),
        A("WORKING-STORAGE SECTION."),
        A("01  WS-A PIC X(4)."),
        A("PROCEDURE DIVISION."),
        .. statements,
        B("GOBACK."),
    ];

    private static DialectRules Statement(string end, StatementEffects effects = StatementEffects.Unknown)
        => DialectRules.Create([new DialectRule("site-trace", DialectRuleKind.Statement, "XTRACE", end, effects)]);

    private static string? ParseDiagnostics(CompilationResult r)
        => RektEmitter.Render(r, "est/src/MAIN.cbl")
            .Where(f => f.RelativePath.EndsWith("parse-diagnostics.json", StringComparison.Ordinal))
            .Select(f => Encoding.UTF8.GetString(f.ToBytes()))
            .SingleOrDefault();

    [Fact]
    public void CopyAliasIncludesTheMemberLikeCopy()
    {
        _estate.Write("copy/PANREC.cpy", A("01  PAN-REC."), B("05  PR-ID PIC 9(4)."));
        string[] program = [A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. MAIN."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
            A("++INCLUDE PANREC"), A("PROCEDURE DIVISION."), B("MOVE 1 TO PR-ID."), B("GOBACK.")];

        Process(null, program).UnreadableSource().Should().ContainSingle();

        var taught = Process(DialectRules.Create([new DialectRule("panvalet", DialectRuleKind.CopyAlias, "++include")]), program);
        taught.UnreadableSource().Should().BeEmpty();
        taught.Programs[0].Items.Select(i => i.Name).Should().Contain("PR-ID");
        taught.TaughtUses.Should().ContainSingle().Which.Should().Match<TaughtUse>(u => u.RuleId == "panvalet" && u.Range.StartLine == 5);
        ParseDiagnostics(taught).Should().BeNull();
    }

    [Fact]
    public void StatementRuleReadsToItsEndWordAndNeverCountsAsParsed()
    {
        var lines = Procedure(B("XTRACE WS-A 'X' END-XTRACE"), B("MOVE 'B' TO WS-A."));
        Process(null, lines).UnreadableSource().Should().NotBeEmpty();

        var r = Process(Statement("END-XTRACE"), lines);

        r.UnreadableSource().Should().BeEmpty();
        var statements = r.Syntax.Programs[0].Procedure!.AllStatements().ToList();
        statements.OfType<TaughtStatement>().Should().ContainSingle().Which.EffectsKnown.Should().BeFalse();
        statements.OfType<MoveStatement>().Should().ContainSingle();
        r.Coverage.ByReason.Should().ContainKey("Unsupported(taught-statement)");
        r.Coverage.Count(CoverageClass.Unaccounted).Should().Be(0);
        ParseDiagnostics(r).Should().Contain("\"taught statement\"").And.Contain("XTRACE (rule site-trace, effects unknown) at MAIN.cbl line 7");
    }

    [Fact]
    public void ConfirmedNoEffectStatementDoesNotMakeEvidencePartial()
        => ParseDiagnostics(Process(Statement("period", StatementEffects.None), Procedure(B("XTRACE WS-A.")))).Should().BeNull();

    [Fact]
    public void PeriodRuleDoesNotSwallowAFollowingStatement()
    {
        var r = Process(Statement("period"), Procedure(B("XTRACE WS-A MOVE 'B' TO WS-A.")));

        r.UnreadableSource().Select(d => d.Code).Should().Equal("taught-statement-boundary");
        r.Syntax.Programs[0].Procedure!.AllStatements().OfType<MoveStatement>().Should().ContainSingle();
        r.TaughtUses.Should().BeEmpty();
    }

    [Fact]
    public void ATaughtStatementIsNotReadAsAReceivingItem()
    {
        var lines = Procedure(B("MOVE 'B' TO WS-A"), B("XTRACE 'X' WS-A."));
        Process(null, lines).UnreadableSource().Should().NotBeEmpty();

        var r = Process(Statement("period"), lines);

        r.UnreadableSource().Should().BeEmpty();
        var statements = r.Syntax.Programs[0].Procedure!.AllStatements().ToList();
        statements.OfType<MoveStatement>().Should().ContainSingle().Which.Targets.Should().ContainSingle();
        statements.OfType<TaughtStatement>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("""{"version":1,"rules":[{"id":"m","kind":"statement","word":"MOVE","end":"period"}]}""", "MOVE is a word Starforge already reads")]
    [InlineData("""{"version":1,"rules":[{"id":"n","kind":"notProgram","paths":["est/*.cbl"]}]}""", "must be an exact path")]
    [InlineData("""{"version":1,"rules":[{"id":"c","kind":"copyAlias","word":"COPY"}]}""", "COPY is a word Starforge already reads")]
    [InlineData("""{"version":2,"rules":[]}""", "\"version\" must be 1")]
    public void InvalidRulesAreRejected(string json, string message)
        => FluentActions.Invoking(() => DialectRules.Parse(json)).Should().Throw<InvalidDataException>().WithMessage($"*{message}*");

    [Fact]
    public void RulesRoundTripWithAStableHash()
    {
        var rules = DialectRules.Parse("""
            {"version":1,"rules":[
              {"id":"tx","kind":"notProgram","paths":["est/tx/B.cbl","est/tx/A.cbl"],"reason":"transaction members"},
              {"id":"site-trace","kind":"statement","word":"xtrace","end":"Period","effects":"none"}]}
            """);

        var again = DialectRules.Parse(rules.ToJson());
        again.ToJson().Should().Be(rules.ToJson());
        again.Sha256.Should().Be(rules.Sha256).And.HaveLength(64);
        again.NotProgram("est\\tx\\A.cbl")!.Id.Should().Be("tx");
        again.Statement("XTRACE")!.End.Should().Be(DialectRule.PeriodEnd);
        again.Without("tx").Rules.Should().ContainSingle();
    }
}
