using Starforge.Cobol.Exec;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Source;

namespace Starforge.Cobol.Tests.Exec;

internal static class ExecText
{
    public static readonly SourceRange Range = new("T.cbl", 1, 12, 1, 40);

    public static T Parse<T>(ExecKind kind, string block)
        where T : ExecStatement
        => ExecParser.Parse(block, kind, Range).Should().BeOfType<T>().Subject;

    public static CicsCommand Cics(string inner) => Parse<CicsCommand>(ExecKind.Cics, "EXEC CICS " + inner + " END-EXEC");

    public static SqlStatement Sql(string inner) => Parse<SqlStatement>(ExecKind.Sql, "EXEC SQL " + inner + " END-EXEC");

    public static DliCommand Dli(string inner) => Parse<DliCommand>(ExecKind.Dli, "EXEC DLI " + inner + " END-EXEC");

    public static List<string> Codes(this ExecStatement s) => s.Diagnostics.Select(d => d.Fingerprint).ToList();
}

public sealed class CicsParserTests
{
    [Fact]
    public void LinkWithLiteralProgramAndCommareaLength()
    {
        var c = ExecText.Cics("LINK PROGRAM('PAYCALC') COMMAREA(WS-AREA) LENGTH(LENGTH OF WS-AREA)");

        c.Status.Should().Be(ExecStatus.Parsed);
        c.Verb.Should().Be("LINK");
        c.Options.Select(o => o.Name).Should().Equal("PROGRAM", "COMMAREA", "LENGTH");
        c.Option("LENGTH")!.Argument.Should().Be(new ExecArgument(ExecArgumentKind.LengthOf, "LENGTH OF WS-AREA", null, "WS-AREA"));
        c.Resources.Should().Equal(
            new ExecResource(ExecResourceKind.Program, "PROGRAM", new ExecTarget("PAYCALC", true, null)),
            new ExecResource(ExecResourceKind.Commarea, "COMMAREA", new ExecTarget("WS-AREA", false, "WS-AREA"), new ExecTarget("LENGTH OF WS-AREA", false, "WS-AREA")));
        c.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void DynamicProgramRecordsTheDataNameWithoutSubscripts()
    {
        var c = ExecText.Cics("XCTL PROGRAM(PGM-TABLE (WS-IX)) COMMAREA(CA)");

        c.Resources[0].Should().Be(new ExecResource(ExecResourceKind.Program, "PROGRAM", new ExecTarget("PGM-TABLE (WS-IX)", false, "PGM-TABLE")));
        c.Resources[0].Target.Key.Should().Be("&PGM-TABLE");
    }

    [Fact]
    public void BlankPaddedLiteralNamesTheSameProgram()
    {
        var c = ExecText.Cics("LINK PROGRAM('ABC     ')");

        c.Resources.Single().Target.Key.Should().Be("ABC");
        c.Option("PROGRAM")!.Argument!.Value.Should().Be("ABC     ");
    }

    [Fact]
    public void NestedParenthesesAndArithmeticStayInOneArgument()
    {
        var c = ExecText.Cics("SEND TEXT FROM(WS-MSG(1:WS-LEN + 2)) LENGTH((WS-LEN + 2) * 1) ERASE");

        c.Verb.Should().Be("SEND TEXT");
        c.Option("FROM")!.Argument.Should().Be(new ExecArgument(ExecArgumentKind.DataRef, "WS-MSG(1:WS-LEN + 2)", null, "WS-MSG"));
        c.Option("LENGTH")!.Argument!.Kind.Should().Be(ExecArgumentKind.Expression);
        c.Option("LENGTH")!.Argument!.Text.Should().Be("(WS-LEN + 2) * 1");
        c.Option("ERASE")!.Argument.Should().BeNull();
        c.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void RespResp2AndNohandleAreAcceptedOnAnyCommand()
    {
        var c = ExecText.Cics("DELAY INTERVAL(000010) RESP(WS-RESP) RESP2(WS-RESP2) NOHANDLE");

        c.Status.Should().Be(ExecStatus.Parsed);
        c.Options.Select(o => (o.Name, o.Known)).Should().Equal(("INTERVAL", true), ("RESP", true), ("RESP2", true), ("NOHANDLE", true));
        c.Option("INTERVAL")!.Argument!.Kind.Should().Be(ExecArgumentKind.Numeric);
        c.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void MultiLineBlockWithTwoWordVerb()
    {
        var c = ExecText.Cics("""
            SEND
                     MAP('MENU1')
                     MAPSET('MENUSET')
                     FROM(MENU1O)
                     ERASE
            """);

        c.Verb.Should().Be("SEND MAP");
        c.Resources.Single().Should().Be(new ExecResource(ExecResourceKind.Map, "MAP", new ExecTarget("MENU1", true, null), new ExecTarget("MENUSET", true, null)));
        c.Option("MAP")!.Argument!.Value.Should().Be("MENU1");
    }

    [Fact]
    public void MapWithoutMapsetUsesTheMapNameAsImpliedMapset()
    {
        var c = ExecText.Cics("RECEIVE MAP(WS-MAP) INTO(WS-IN)");

        c.Verb.Should().Be("RECEIVE MAP");
        c.Resources.Single().Should().Be(new ExecResource(ExecResourceKind.Map, "MAP", new ExecTarget("WS-MAP", false, "WS-MAP"), new ExecTarget("WS-MAP", false, "WS-MAP"), true));
    }

    [Theory]
    [InlineData("SYNCPOINT ROLLBACK", "SYNCPOINT ROLLBACK")]
    [InlineData("SYNCPOINT", "SYNCPOINT")]
    [InlineData("HANDLE CONDITION NOTFND(PARA-NF) ERROR(PARA-ERR)", "HANDLE CONDITION")]
    [InlineData("WRITEQ TS QUEUE('Q1') FROM(X)", "WRITEQ TS")]
    [InlineData("WRITEQ QUEUE('Q1') FROM(X)", "WRITEQ TS")]
    [InlineData("DELETEQ TD QUEUE('CSMT')", "DELETEQ TD")]
    [InlineData("INQUIRE PROGRAM('P1') STATUS(WS-ST)", "INQUIRE PROGRAM")]
    [InlineData("BIF DEEDIT FIELD(WS-AMT) LENGTH(10)", "BIF DEEDIT")]
    [InlineData("GET CONTAINER('C1') CHANNEL('CH') INTO(WS-C)", "GET CONTAINER")]
    [InlineData("ASSIGN APPLID(WS-APPLID) SYSID(WS-SYSID)", "ASSIGN")]
    [InlineData("SEND CONTROL ERASE FREEKB", "SEND CONTROL")]
    public void VerbsResolveToTheirCommandTableEntry(string inner, string verb)
    {
        var c = ExecText.Cics(inner);

        c.Status.Should().Be(ExecStatus.Parsed);
        c.Verb.Should().Be(verb);
        c.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void QualifierWordMayFollowOptions()
    {
        var c = ExecText.Cics("READQ QUEUE(WS-Q) INTO(WS-REC) TS ITEM(1)");

        c.Verb.Should().Be("READQ TS");
        c.Resources.Single().Should().Be(new ExecResource(ExecResourceKind.TsQueue, "QUEUE", new ExecTarget("WS-Q", false, "WS-Q")));
    }

    [Fact]
    public void FileCommandsRecordTheDatasetTarget()
    {
        var c = ExecText.Cics("READ DATASET(WS-FILE) INTO(REC) RIDFLD(KEY) UPDATE");

        c.Resources.Single().Should().Be(new ExecResource(ExecResourceKind.File, "DATASET", new ExecTarget("WS-FILE", false, "WS-FILE")));
    }

    [Fact]
    public void StartAndReturnRecordTransactionIds()
    {
        ExecText.Cics("START TRANSID('TX01') INTERVAL(0) FROM(D)").Resources.Single()
            .Should().Be(new ExecResource(ExecResourceKind.TransId, "TRANSID", new ExecTarget("TX01", true, null)));
        ExecText.Cics("RETURN TRANSID(WS-TRAN) COMMAREA(CA)").Resources.Select(r => (r.Kind, r.Target.Key))
            .Should().Equal((ExecResourceKind.TransId, "&WS-TRAN"), (ExecResourceKind.Commarea, "&CA"));
    }

    [Fact]
    public void HandleAbendLabelIsALabelResource()
    {
        var c = ExecText.Cics("HANDLE ABEND LABEL(ABEND-PARA)");

        c.Verb.Should().Be("HANDLE ABEND");
        c.Resources.Single().Should().Be(new ExecResource(ExecResourceKind.Label, "LABEL", new ExecTarget("ABEND-PARA", false, "ABEND-PARA")));
    }

    [Fact]
    public void UnknownVerbMakesTheBlockUnsupported()
    {
        var s = ExecParser.Parse("EXEC CICS FROBNICATE WIDGET(X) END-EXEC", ExecKind.Cics, ExecText.Range);

        s.Status.Should().Be(ExecStatus.Unsupported);
        s.UnsupportedReason.Should().Be("cics-unknown-command");
        s.Codes().Should().Equal("cics-unknown-command:FROBNICATE");
        s.Diagnostics.Single().Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [Fact]
    public void UnknownOptionIsAWarningAndStaysParsed()
    {
        var c = ExecText.Cics("LINK PROGRAM('P1') SHINYNEW(X)");

        c.Status.Should().Be(ExecStatus.Parsed);
        c.UnknownOptions.Select(o => o.Name).Should().Equal("SHINYNEW");
        c.Codes().Should().Equal("cics-unknown-option:LINK SHINYNEW");
        c.Diagnostics.Single().Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [Fact]
    public void UnbalancedParenthesesAreUnsupported()
    {
        var s = ExecParser.Parse("EXEC CICS LINK PROGRAM('P1' END-EXEC", ExecKind.Cics, ExecText.Range);

        s.Status.Should().Be(ExecStatus.Unsupported);
        s.UnsupportedReason.Should().Be("cics-unbalanced-parentheses");
    }

    [Fact]
    public void QuotedParenthesisDoesNotCloseTheArgument()
    {
        var c = ExecText.Cics("WRITE OPERATOR TEXT('DONE (OK)') TEXTLENGTH(9)");

        c.Verb.Should().Be("WRITE OPERATOR");
        c.Option("TEXT")!.Argument!.Value.Should().Be("DONE (OK)");
    }
}
