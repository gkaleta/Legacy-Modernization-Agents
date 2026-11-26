using System.Text.Json.Nodes;
using CobolToQuarkusMigration.Cli;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Starforge.Cobol.Dialect;
using Xunit;

namespace CobolToQuarkusMigration.Tests.Cli;

// Every estate here is made up; the include word follows the public Panvalet ++INCLUDE form.
public sealed class TeachParserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "teach-" + Guid.NewGuid().ToString("N"));
    private readonly string _estate;
    private readonly string _rulesPath;

    public TeachParserTests()
    {
        _estate = Path.Combine(_root, "est");
        _rulesPath = Path.Combine(_root, "Config", "starforge-dialect.local.json");
        Directory.CreateDirectory(_estate);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string A(string t) => "       " + t;

    private static string B(string t) => "           " + t;

    private void Write(string rel, params string[] lines)
    {
        var path = Path.Combine(_estate, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }

    private void IncludeProgram()
    {
        Write("copy/CUSTREC.cpy", A("01  CUST-REC."), B("05  CUST-BALANCE PIC S9(7)V99 COMP-3."));
        Write("cobol/ORDERS.cbl", A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. ORDERS."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
            A("01  WS-SECRET-NAME PIC X(20) VALUE 'ACME HOLDINGS'."), "      *    PRIVATE NOTE ABOUT THE CUSTOMER",
            A("++INCLUDE CUSTREC"), A("PROCEDURE DIVISION."), B("MOVE 0 TO CUST-BALANCE."), B("GOBACK."));
    }

    private sealed class FakeClient(string answer) : IChatClient
    {
        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Prompts.Add(messages.Last().Text);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private async Task<(string Output, FakeClient Client, bool Created)> Run(string answers, string aiAnswer, string? journalRoot = null)
    {
        var client = new FakeClient(aiAnswer);
        var created = false;
        var output = new StringWriter();
        var exit = await new TeachParser(new StringReader(answers), output).RunAsync([_estate], _rulesPath, DialectRules.Empty,
            () => new TeachAiEndpoint("AzureOpenAI", "https://example.invalid", "test-model", () => { created = true; return client; }),
            journalRoot: journalRoot);
        exit.Should().Be(0, output.ToString());
        return (output.ToString(), client, created);
    }

    [Fact]
    public async Task ConsentedCopyAliasIsVerifiedAndSaved()
    {
        IncludeProgram();

        var (output, client, _) = await Run("y\ny\n", """{"category":"dialect-rule","explanation":"Panvalet include.","rule":{"kind":"copyAlias"}}""");

        var prompt = client.Prompts.Should().ContainSingle().Subject;
        prompt.Should().Contain("++INCLUDE W").And.Contain("PIC X(20) VALUE '...'").And.Contain("(comment removed)");
        prompt.Should().NotContainAny("CUSTREC", "WS-SECRET-NAME", "ACME", "PRIVATE", "ORDERS");
        output.Should().Contain("Fixes 1 of 1 place(s).").And.Contain("Nothing else changed.").And.Contain("It read every place in every program.");
        var rule = TeachParserCommand.LoadRules(_rulesPath).Rules.Should().ContainSingle().Subject;
        rule.Should().Match<DialectRule>(r => r.Kind == DialectRuleKind.CopyAlias && r.Word == "++INCLUDE" && r.Id == "copy-include");
    }

    [Fact]
    public async Task SkippingSendsNothing()
    {
        IncludeProgram();

        var (output, client, created) = await Run("s\n", "{}");

        created.Should().BeFalse();
        client.Prompts.Should().BeEmpty();
        output.Should().Contain("No rules saved.");
        File.Exists(_rulesPath).Should().BeFalse();
    }

    [Fact]
    public async Task ASourceErrorIsNotTurnedIntoARule()
    {
        Write("cobol/BAD.cbl", A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. BAD."), A("PROCEDURE DIVISION."), B("DISPLAY 'OPEN"), B("GOBACK."));

        var (output, _, _) = await Run("y\n", """{"category":"source-error","explanation":"The literal has no closing quote."}""");

        output.Should().Contain("Fix the source; no rule is saved.");
        File.Exists(_rulesPath).Should().BeFalse();
    }

    [Fact]
    public async Task AStatementRuleThatSwallowsTheNextStatementIsNotSaved()
    {
        Write("cobol/TRC.cbl", A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. TRC."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
            A("01  WS-A PIC X."), A("PROCEDURE DIVISION."), B("XTRACE WS-A MOVE 'B' TO WS-A."), B("GOBACK."));

        var (output, _, _) = await Run("y\ny\nu\ny\n", """{"category":"dialect-rule","explanation":"Site trace.","rule":{"kind":"statement","end":"period"}}""");

        output.Should().Contain("Still not read: 1").And.Contain("Not saved");
        File.Exists(_rulesPath).Should().BeFalse();
    }

    [Fact]
    public async Task NotCobolOnlyExcludesFilesThatFailAtTheirFirstLine()
    {
        Write("cobol/TXDEFS.cbl", A("TRANSACTION-TABLE ROWS 12"), A("ENTRY T001 HANDLER-A"));

        var (output, _, _) = await Run("y\ny\n", """{"category":"not-cobol","explanation":"A table definition."}""");

        TeachParserCommand.LoadRules(_rulesPath).NotProgram("est/cobol/TXDEFS.cbl").Should().NotBeNull();
        output.Should().Contain("Excludes 1 file(s) as not programs.");
    }

    private JsonNode Journal(string root)
        => JsonNode.Parse(File.ReadAllText(Directory.GetFiles(root, TeachJournal.FileName, SearchOption.AllDirectories).Should().ContainSingle().Subject))!;

    [Fact]
    public async Task TheJournalRecordsWhatWasSentTheAnswerTheCheckAndTheChoice()
    {
        IncludeProgram();
        var journalRoot = Path.Combine(_root, "journal");

        var (_, client, _) = await Run("y\ny\n", """{"category":"dialect-rule","explanation":"Panvalet include.","rule":{"kind":"copyAlias"}}""", journalRoot);

        var doc = Journal(journalRoot);
        doc["activityKind"]!.GetValue<string>().Should().Be("teach-parser");
        doc["status"]!.GetValue<string>().Should().Be("completed");
        doc["endpoint"]!["model"]!.GetValue<string>().Should().Be("test-model");
        doc["before"]!["places"]!.GetValue<int>().Should().Be(1);
        doc["after"]!["places"]!.GetValue<int>().Should().Be(0);
        var group = doc["groups"]!.AsArray().Should().ContainSingle().Subject!;
        group["prompt"]!.GetValue<string>().Should().Be(client.Prompts.Single());
        group["decision"]!.GetValue<string>().Should().Be("sent");
        group["category"]!.GetValue<string>().Should().Be("dialect-rule");
        group["ruleId"]!.GetValue<string>().Should().Be("copy-include");
        group["check"]!["passed"]!.GetValue<bool>().Should().BeTrue();
        group["saved"]!.GetValue<bool>().Should().BeTrue();
        doc["savedRules"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("copy-include");
    }

    [Fact]
    public async Task QuittingIsRecordedAsStoppedWithNothingSent()
    {
        IncludeProgram();
        var journalRoot = Path.Combine(_root, "journal");

        await Run("q\n", "{}", journalRoot);

        var doc = Journal(journalRoot);
        doc["status"]!.GetValue<string>().Should().Be("stopped");
        var group = doc["groups"]!.AsArray().Should().ContainSingle().Subject!;
        group["decision"]!.GetValue<string>().Should().Be("quit");
        group["answer"].Should().BeNull();
    }

    [Fact]
    public void ShareHoldsOnlyTheGenericRuleAndAMadeUpExample()
    {
        var rules = DialectRules.Create([new DialectRule("copy-include", DialectRuleKind.CopyAlias, "++INCLUDE", Reason: "seen in ORDERS")]);
        var output = new StringWriter();

        new TeachParser(new StringReader(string.Empty), output).Share(rules, "copy-include").Should().Be(0);

        output.ToString().Should().Contain("Nothing has been sent").And.Contain("\"word\": \"++INCLUDE\"").And.Contain("++INCLUDE EXAMPLEC")
            .And.NotContain("ORDERS");
    }

    [Fact]
    public async Task AStatementWordAfterAnUnendedStatementIsNamedByItsPlaceholder()
    {
        Write("cobol/TRC.cbl", A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. TRC."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
            A("01  WS-A PIC X."), A("PROCEDURE DIVISION."), B("MOVE 'B' TO WS-A"), B("XTRACE 'SECRET' WS-A."), B("GOBACK."));

        var (output, client, _) = await Run("y\nu\ny\n",
            """{"category":"dialect-rule","explanation":"Site trace.","rule":{"kind":"statement","end":"period","word":"W2"}}""");

        client.Prompts.Should().ContainSingle().Which.Should().Contain("W2 '...' W1.").And.NotContainAny("XTRACE", "SECRET");
        output.Should().Contain("Fixes 1 of 1 place(s).").And.Contain("It read every place in every program.");
        TeachParserCommand.LoadRules(_rulesPath).Rules.Should().ContainSingle()
            .Which.Should().Match<DialectRule>(r => r.Kind == DialectRuleKind.Statement && r.Word == "XTRACE" && r.Id == "stmt-xtrace");
    }

    [Theory]
    [InlineData("W9")]
    [InlineData("PERFORM")]
    [InlineData(null)]
    public void ARuleWordMustComeFromTheExcerpt(string? word)
    {
        var group = new TeachGroup("expected-statement", "'...'", [new TeachSite("est/cobol/TRC.cbl", "est/cobol/TRC.cbl", 8, 19, "expected-statement", "'...'")]);
        var answer = new TeachAnswer("dialect-rule", "x", "statement", "period", word);

        TeachParser.Candidate(group, answer, DialectRules.Empty, new Dictionary<string, string> { ["WS-A"] = "W1", ["XTRACE"] = "W2" }).Should().BeNull();
    }

    [Theory]
    [InlineData("Sure! ```json\n{\"category\":\"parser-gap\",\"explanation\":\"x\"}\n```", "parser-gap")]
    [InlineData("{\"category\":\"dialect-rule\",\"explanation\":\"x\",\"rule\":{\"kind\":\"statement\",\"end\":\"END-XTRACE\"}}", "dialect-rule")]
    public void AnswersAreReadFromTheFirstJsonObject(string text, string category)
        => TeachParser.ParseAnswer(text).Category.Should().Be(category);

    [Fact]
    public void AnUnknownCategoryIsRejected()
        => FluentActions.Invoking(() => TeachParser.ParseAnswer("""{"category":"rewrite-it"}""")).Should().Throw<InvalidDataException>();
}
