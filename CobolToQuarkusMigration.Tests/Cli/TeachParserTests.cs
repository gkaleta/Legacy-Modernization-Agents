using System.CommandLine.Parsing;
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

    private string NotesDir => Path.Combine(_root, "notes");

    private string ConsentPath => Path.Combine(_root, "Config", "consent.local.json");

    private TeachRunOptions Review(string? journalRoot = null) => new(JournalRoot: journalRoot, Notes: new TeachNotes(NotesDir), SensitiveTermsPath: null);

    private TeachRunOptions Automatic(bool canAsk = true, string? journalRoot = null)
        => new(Automatic: true, Consent: new TeachConsent(ConsentPath), CanAsk: canAsk, JournalRoot: journalRoot, Notes: new TeachNotes(NotesDir), SensitiveTermsPath: null);

    private async Task<(string Output, FakeClient Client, bool Created)> Run(string answers, string aiAnswer, string? journalRoot = null,
        TeachRunOptions? options = null, int exitCode = 0)
    {
        var client = new FakeClient(aiAnswer);
        var created = false;
        var output = new StringWriter();
        var exit = await new TeachParser(new StringReader(answers), output).RunAsync([_estate], _rulesPath, TeachParserCommand.LoadRules(_rulesPath),
            () => new TeachAiEndpoint("AzureOpenAI", "https://example.invalid", "test-model", () => { created = true; return client; }),
            options ?? Review(journalRoot));
        exit.Should().Be(exitCode, output.ToString());
        return (output.ToString(), client, created);
    }

    private const string IncludeAnswer = """{"category":"dialect-rule","explanation":"Panvalet include.","rule":{"kind":"copyAlias"}}""";

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

    private (int Exit, string Output, List<string> Posted) Share(DialectRules rules, string id, string answers = "", bool github = false,
        string? sensitiveTerms = null)
    {
        var posted = new List<string>();
        var output = new StringWriter();
        var exit = new TeachParser(new StringReader(answers), output).Share(rules, id, new TeachNotes(NotesDir), EstateNames.From([_estate], sensitiveTerms),
            github, post: (path, title) => { posted.Add(title + "\n" + File.ReadAllText(path)); return (0, "https://example.invalid/issues/1"); });
        return (exit, output.ToString(), posted);
    }

    [Fact]
    public void ShareWritesANoteWithOnlyTheGenericRuleAndAMadeUpExampleAndSendsNothing()
    {
        IncludeProgram();
        var rules = DialectRules.Create([new DialectRule("copy-include", DialectRuleKind.CopyAlias, "++INCLUDE", Reason: "seen in ORDERS")]);

        var (exit, output, posted) = Share(rules, "copy-include");

        exit.Should().Be(0, output);
        posted.Should().BeEmpty();
        output.Should().Contain("Nothing has been sent");
        var note = File.ReadAllText(Path.Combine(NotesDir, "copy-include.md"));
        note.Should().Contain("\"word\": \"++INCLUDE\"").And.Contain("++INCLUDE EXAMPLEC").And.NotContainAny("ORDERS", "CUSTREC", "seen in");
    }

    [Fact]
    public void ARuleWordThatIsAlsoAnEstateNameIsReplacedInTheNote()
    {
        IncludeProgram();
        var rules = DialectRules.Create([new DialectRule("stmt-cust-rec", DialectRuleKind.Statement, "CUST-REC", DialectRule.PeriodEnd)]);

        var (exit, output, _) = Share(rules, "stmt-cust-rec");

        exit.Should().Be(0, output);
        output.Should().Contain("CUST-REC is also a name in your estate");
        File.ReadAllText(Path.Combine(NotesDir, "stmt-cust-rec.md")).Should().Contain("SITEWORD EX-FIELD.").And.NotContain("CUST-REC");
    }

    [Fact]
    public void ANoteWithASensitiveTermIsNotShared()
    {
        IncludeProgram();
        var terms = Path.Combine(_root, "terms.txt");
        File.WriteAllText(terms, "# site terms\nEXAMPLEC\n");
        var rules = DialectRules.Create([new DialectRule("copy-include", DialectRuleKind.CopyAlias, "++INCLUDE")]);

        var (exit, output, _) = Share(rules, "copy-include", sensitiveTerms: terms);

        exit.Should().Be(1);
        output.Should().Contain("Not shared").And.Contain("EXAMPLEC");
        Directory.Exists(NotesDir).Should().BeFalse();
    }

    [Fact]
    public void NotProgramRulesAreNeverShared()
    {
        var rules = DialectRules.Create([new DialectRule("not-programs", DialectRuleKind.NotProgram, Paths: ["est/cobol/TXDEFS.cbl"])]);

        var (exit, output, _) = Share(rules, "not-programs");

        exit.Should().Be(1);
        output.Should().Contain("never shared");
        Directory.Exists(NotesDir).Should().BeFalse();
    }

    [Theory]
    [InlineData("n\n", false)]
    [InlineData("y\n", true)]
    public void PostingToGitHubHappensOnlyAfterTheNoteIsShownAndConfirmed(string answer, bool posts)
    {
        var rules = DialectRules.Create([new DialectRule("copy-include", DialectRuleKind.CopyAlias, "++INCLUDE")]);

        var (exit, output, posted) = Share(rules, "copy-include", answer, github: true);

        exit.Should().Be(0, output);
        output.Should().Contain("│ ## Made-up example");
        posted.Should().HaveCount(posts ? 1 : 0);
        if (posts)
        {
            posted[0].Should().StartWith("Starforge dialect: include word ++INCLUDE\n# Starforge dialect");
        }
    }

    [Theory]
    [InlineData("report")]
    [InlineData("run")]
    [InlineData("share", "copy-include")]
    public void AMissingEstateFolderIsRefusedInsteadOfReadAsEmpty(params string[] command)
    {
        var missing = Path.Combine(_root, "typo");

        var result = new Parser(TeachParserCommand.Build(() => throw new InvalidOperationException("no AI call expected")))
            .Parse([.. command, "--source", missing, "--rules", _rulesPath]);

        result.Errors.Select(e => e.Message).Should().ContainSingle().Which.Should().Be($"No folder '{missing}'.");
    }

    [Fact]
    public async Task AnAutomaticRunAsksOnceThenSendsChecksAndSavesWithoutFurtherQuestions()
    {
        IncludeProgram();

        var (first, firstClient, _) = await Run("n\n", IncludeAnswer, options: Automatic());
        first.Should().Contain("Send automatically, now and in later runs?").And.Contain("Nothing sent.");
        firstClient.Prompts.Should().BeEmpty();
        File.Exists(ConsentPath).Should().BeFalse();

        var journalRoot = Path.Combine(_root, "journal");
        var (second, secondClient, _) = await Run("y\n", IncludeAnswer, options: Automatic(journalRoot: journalRoot));
        secondClient.Prompts.Should().ContainSingle();
        second.Should().NotContain("Save rule").And.Contain("It read every place in every program.");
        TeachParserCommand.LoadRules(_rulesPath).Rules.Should().ContainSingle().Which.Id.Should().Be("copy-include");
        Journal(journalRoot)["mode"]!.GetValue<string>().Should().Be("automatic");

        TeachParser.Save(_rulesPath, DialectRules.Empty);
        var (third, thirdClient, _) = await Run(string.Empty, IncludeAnswer, options: Automatic(canAsk: false));
        third.Should().NotContain("Send automatically");
        thirdClient.Prompts.Should().ContainSingle();
    }

    [Fact]
    public async Task WithoutAgreementAndWithoutATerminalNothingIsSent()
    {
        IncludeProgram();

        var (output, _, created) = await Run(string.Empty, IncludeAnswer, options: Automatic(canAsk: false), exitCode: 2);

        created.Should().BeFalse();
        output.Should().Contain("Nothing was sent").And.Contain("--agree");
        File.Exists(_rulesPath).Should().BeFalse();
    }

    [Fact]
    public async Task AnAutomaticRunSavesStatementsAsEffectsUnknownAndNeverSavesARuleThatFailsItsCheck()
    {
        Write("cobol/LOG.cbl", A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. LOG."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
            A("01  WS-A PIC X."), A("PROCEDURE DIVISION."), B("SITELOG WS-A."), B("GOBACK."));
        Write("cobol/TRC.cbl", A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. TRC."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
            A("01  WS-A PIC X."), A("PROCEDURE DIVISION."), B("XTRACE WS-A MOVE 'B' TO WS-A."), B("GOBACK."));
        new TeachConsent(ConsentPath).Give([_estate], new TeachAiEndpoint("AzureOpenAI", "https://example.invalid", "other-model", () => null!));

        var (output, _, _) = await Run(string.Empty, """{"category":"dialect-rule","explanation":"Site statement.","rule":{"kind":"statement","end":"period"}}""",
            options: Automatic(canAsk: false));

        output.Should().Contain("Kept as effects unknown").And.Contain("Not saved");
        TeachParserCommand.LoadRules(_rulesPath).Rules.Should().ContainSingle()
            .Which.Should().Match<DialectRule>(r => r.Word == "SITELOG" && r.Effects != StatementEffects.None);
    }

    [Fact]
    public async Task AParserGapLeavesAMaskedNoteAndSendsNothingFurther()
    {
        Write("copy/XTRACE.cpy", A("01  XT-REC PIC X."));
        Write("cobol/TRC.cbl", A("IDENTIFICATION DIVISION."), A("PROGRAM-ID. TRC."), A("DATA DIVISION."), A("WORKING-STORAGE SECTION."),
            A("01  WS-A PIC X."), A("PROCEDURE DIVISION."), B("XTRACE WS-A."), B("GOBACK."));
        var journalRoot = Path.Combine(_root, "journal");

        var (output, _, _) = await Run("y\n", """{"category":"parser-gap","explanation":"XTRACE looks like standard COBOL."}""", journalRoot);

        output.Should().Contain("teach-parser share gap-expected-statement").And.Contain("name(s) from your estate masked");
        var note = File.ReadAllText(Path.Combine(NotesDir, "gap-expected-statement.md"));
        note.Should().StartWith("# Starforge parser gap").And.Contain(">>").And.Contain("NAME1").And.NotContainAny("XTRACE", "WS-A", "TRC");
        Journal(journalRoot)["groups"]![0]!["note"]!.GetValue<string>().Should().Be("gap-expected-statement");
        File.Exists(_rulesPath).Should().BeFalse();
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
