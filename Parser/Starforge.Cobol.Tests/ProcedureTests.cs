using FluentAssertions;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Syntax;
using Xunit;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public class ProcedureTests
{
    private static readonly string[] Storage =
    {
        A("01  A PIC 9(4)."),
        A("01  B PIC 9(4)."),
        A("01  C PIC 9(4)."),
        A("01  D PIC 9(4)."),
        A("01  X PIC X(10)."),
        A("01  Y PIC X(10)."),
        A("01  Z PIC X(10)."),
        A("01  PTR USAGE POINTER."),
        A("01  FLAG PIC X."),
        B("88  DONE VALUE 'Y'."),
        A("01  TBL."),
        B("05  ENT OCCURS 10 ASCENDING KEY ENT-KEY INDEXED BY IX."),
        B("    10  ENT-KEY PIC 9(4)."),
        B("    10  ENT-VAL PIC X(5)."),
        A("01  GRP1."),
        B("05  F1 PIC X."),
        A("01  GRP2."),
        B("05  F1 PIC X."),
    };

    private static CompilationResult Program(TestEstate estate, params string[] body)
        => estate.Program(new[]
        {
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
        }.Concat(Storage).Append(A("PROCEDURE DIVISION.")).Concat(body).ToArray());

    private static ProcedureDivision Proc(CompilationResult r) => r.Syntax.Programs[0].Procedure!;

    private static List<Statement> TopLevel(CompilationResult r)
        => Proc(r).AllSections().SelectMany(s => s.Leading.Concat(s.Paragraphs.SelectMany(p => p.Sentences))).SelectMany(s => s.Statements).ToList();

    private static T Single<T>(CompilationResult r)
        where T : Statement
        => TopLevel(r).Should().ContainSingle().Which.Should().BeOfType<T>().Subject;

    private static List<string> Problems(CompilationResult r)
        => r.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Select(d => d.Fingerprint).ToList();

    private static void FullyParsed(CompilationResult r)
    {
        Problems(r).Should().BeEmpty();
        r.Coverage.Count(CoverageClass.Lexed).Should().Be(0);
        r.Coverage.Count(CoverageClass.Unsupported).Should().Be(0);
    }

    [Theory]
    [InlineData("A = 1 OR 2 AND > B", "((A = 1) OR ((A = 2) AND (A > B)))")]
    [InlineData("A > B AND NOT > C OR D", "(((A > B) AND (A <= C)) OR (A <= D))")]
    [InlineData("NOT A = B OR C", "(NOT (A = B) OR (A = C))")]
    [InlineData("A NOT = 1 AND 2", "((A <> 1) AND (A <> 2))")]
    [InlineData("A EQUAL TO 1 OR GREATER THAN OR EQUAL TO 5", "((A = 1) OR (A >= 5))")]
    [InlineData("A IS LESS THAN B AND C IS NOT GREATER THAN D", "((A < B) AND (C <= D))")]
    [InlineData("A = B AND C = D OR DONE", "(((A = B) AND (C = D)) OR DONE)")]
    [InlineData("A = 1 OR 2 OR DONE", "(((A = 1) OR (A = 2)) OR DONE)")]
    [InlineData("NOT DONE AND NOT A = 1", "(NOT DONE AND NOT (A = 1))")]
    [InlineData("X NUMERIC AND A NOT NEGATIVE OR B IS ZERO", "(((X NUMERIC) AND (A NOT NEGATIVE)) OR (B ZERO))")]
    [InlineData("X IS NOT ALPHABETIC-UPPER", "(X NOT ALPHABETIC-UPPER)")]
    [InlineData("(A = 1 OR A = 2) AND B > 0", "(((A = 1) OR (A = 2)) AND (B > 0))")]
    [InlineData("A + 1 * B ** 2 > C - -1", "((A + (1 * (B ** 2))) > (C - -1))")]
    [InlineData("A - B - C = 2 ** 3 ** 2", "(((A - B) - C) = ((2 ** 3) ** 2))")]
    [InlineData("(A + 1) * 2 = -B", "(((A + 1) * 2) = -B)")]
    [InlineData("B -1 > A", "((B - 1) > A)")]
    [InlineData("FUNCTION LENGTH(X) > 3 AND X(1:2) = 'AB'", "((FUNCTION LENGTH(X) > 3) AND (X(1:2) = 'AB'))")]
    [InlineData("ENT-KEY(IX) = A AND ENT-VAL OF ENT(1) NOT = SPACES", "((ENT-KEY(IX) = A) AND (ENT-VAL OF ENT(1) <> SPACE))")]
    [InlineData("X = ALL '*' OR LENGTH OF X > 0", "((X = ALL '*') OR (LENGTH OF X > 0))")]
    [InlineData("A = (1 OR 2)", "((A = 1) OR (A = 2))")]
    [InlineData("X(1:2) = ('AB' OR 'CD' OR 'EF')", "(((X(1:2) = 'AB') OR (X(1:2) = 'CD')) OR (X(1:2) = 'EF'))")]
    [InlineData("A = (B OR C) AND D > 0", "(((A = B) OR (A = C)) AND (D > 0))")]
    [InlineData("A NOT = (1 AND 2)", "((A <> 1) AND (A <> 2))")]
    [InlineData("A = (B + 1)", "(A = (B + 1))")]
    public void ConditionsFollowPrecedenceAndExpandAbbreviations(string condition, string expected)
    {
        using var estate = new TestEstate();
        var r = Program(estate, B($"IF {condition}"), B("    CONTINUE"), B("END-IF."));

        FullyParsed(r);
        ProcedureText.Of(Single<IfStatement>(r).Condition).Should().Be(expected);
    }

    [Fact]
    public void AbbreviatedRelationsAreFlagged()
    {
        using var estate = new TestEstate();
        var r = Program(estate, B("IF A = 1 OR 2 CONTINUE END-IF."));

        var or = Single<IfStatement>(r).Condition.Should().BeOfType<OrCondition>().Subject;
        ((RelationCondition)or.Left).Abbreviated.Should().BeFalse();
        ((RelationCondition)or.Right).Abbreviated.Should().BeTrue();
    }

    [Fact]
    public void SectionsAndParagraphsCarryTheirLines()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            B("DISPLAY 'START'."),
            A("MAIN-SECTION SECTION."),
            A("FIRST-PARA."),
            B("PERFORM SECOND-PARA."),
            B("STOP RUN."),
            A("SECOND-PARA."),
            B("MOVE 1 TO A"),
            B("    B."),
            A("OTHER-SECTION SECTION 50."),
            A("THIRD-PARA."),
            B("EXIT."));

        FullyParsed(r);
        var proc = Proc(r);
        var first = 24;
        proc.Sections.Select(s => (s.Name, s.Priority, s.StartLine, s.EndLine)).Should().Equal(
            (null, null, first, first),
            ("MAIN-SECTION", null, first + 1, first + 7),
            ("OTHER-SECTION", 50, first + 8, first + 10));
        proc.Sections[0].Leading.Should().ContainSingle();
        proc.AllParagraphs().Select(p => (p.Name, p.StartLine, p.EndLine, p.Sentences.Count)).Should().Equal(
            ("FIRST-PARA", first + 2, first + 4, 2),
            ("SECOND-PARA", first + 5, first + 7, 1),
            ("THIRD-PARA", first + 9, first + 10, 1));
    }

    [Fact]
    public void ParagraphsFromACopybookTakeTheLinesOfTheCopyStatement()
    {
        using var estate = new TestEstate();
        estate.Write("cpy/PARAS.cpy", A("COPIED-PARA."), B("DISPLAY 'IN COPY'."), A("COPIED-EXIT."), B("EXIT."));
        var r = Program(estate, A("MAIN-PARA."), B("PERFORM COPIED-PARA THRU COPIED-EXIT."), B("GOBACK."), B("COPY PARAS."), A("LAST-PARA."), B("EXIT."));

        FullyParsed(r);
        Proc(r).AllParagraphs().Select(p => (p.Name, p.StartLine, p.EndLine)).Should().Equal(
            ("MAIN-PARA", 24, 26),
            ("COPIED-PARA", 27, 27),
            ("COPIED-EXIT", 27, 27),
            ("LAST-PARA", 28, 29));
        r.Bindings[0].TargetOf(Proc(r).ProcedureReferences[1]).Should().BeOfType<Paragraph>().Which.Name.Should().Be("COPIED-EXIT");
    }

    [Fact]
    public void DeclarativesHoldUseSections()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            A("DECLARATIVES."),
            A("ERR-SECTION SECTION."),
            B("USE AFTER STANDARD ERROR PROCEDURE ON INPUT."),
            A("ERR-PARA."),
            B("DISPLAY 'IO ERROR'."),
            A("END DECLARATIVES."),
            A("MAIN SECTION."),
            B("GOBACK."));

        FullyParsed(r);
        var proc = Proc(r);
        proc.Declaratives.Should().ContainSingle().Which.Use!.Text.Should().Be("USE AFTER STANDARD ERROR PROCEDURE ON INPUT");
        proc.Declaratives[0].Paragraphs.Select(p => p.Name).Should().Equal("ERR-PARA");
        proc.Sections.Select(s => s.Name).Should().Equal("MAIN");
    }

    [Fact]
    public void ProcedureHeaderUsingAndReturning()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. SUB."),
            A("DATA DIVISION."),
            A("LINKAGE SECTION."),
            A("01  P1 PIC X."),
            A("01  P2 PIC 9."),
            A("01  RES PIC 9."),
            A("PROCEDURE DIVISION USING BY REFERENCE P1 BY VALUE P2"),
            B("RETURNING RES."),
            B("GOBACK."));

        FullyParsed(r);
        Proc(r).Using.Select(u => (u.Name.Name, u.Mode)).Should().Equal(("P1", PassingMode.Reference), ("P2", PassingMode.Value));
        Proc(r).Returning!.Name.Should().Be("RES");
    }

    [Fact]
    public void MoveAndArithmeticForms()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            B("MOVE CORRESPONDING GRP1 TO GRP2"),
            B("MOVE X(2:3) TO Y Z"),
            B("ADD A B TO C D ROUNDED"),
            B("ADD A TO 5 GIVING C"),
            B("SUBTRACT 1 FROM A ON SIZE ERROR DISPLAY 'E'"),
            B("    NOT ON SIZE ERROR CONTINUE END-SUBTRACT"),
            B("MULTIPLY A BY B GIVING C ROUNDED MODE IS NEAREST-EVEN"),
            B("DIVIDE A INTO B GIVING C REMAINDER D"),
            B("DIVIDE 10 BY 3 GIVING C"),
            B("COMPUTE A ROUNDED B = (C + 1) / 2"),
            B("    SIZE ERROR MOVE 0 TO A END-COMPUTE."));

        FullyParsed(r);
        var s = TopLevel(r);
        s.Select(x => x.GetType().Name).Should().Equal(
            "MoveStatement", "MoveStatement", "ArithmeticStatement", "ArithmeticStatement", "ArithmeticStatement",
            "ArithmeticStatement", "ArithmeticStatement", "ArithmeticStatement", "ComputeStatement");
        var corr = (MoveStatement)s[0];
        corr.Corresponding.Should().BeTrue();
        ((MoveStatement)s[1]).Targets.Select(ProcedureText.Of).Should().Equal("Y", "Z");
        ProcedureText.Of(((MoveStatement)s[1]).Source).Should().Be("X(2:3)");
        var add = (ArithmeticStatement)s[2];
        (add.Operands.Count, add.Preposition, add.Giving).Should().Be((2, "TO", false));
        add.Targets.Select(t => (ProcedureText.Of(t.Target), t.Rounded)).Should().Equal(("C", false), ("D", true));
        var addGiving = (ArithmeticStatement)s[3];
        (ProcedureText.Of(addGiving.Second!), addGiving.Giving).Should().Be(("5", true));
        var sub = (ArithmeticStatement)s[4];
        sub.Handlers.Select(h => (h.Kind, h.Not, h.Statements.Count)).Should().Equal((HandlerKind.SizeError, false, 1), (HandlerKind.SizeError, true, 1));
        ((ArithmeticStatement)s[5]).Targets.Single().RoundingMode.Should().Be("NEAREST-EVEN");
        var div = (ArithmeticStatement)s[6];
        (div.Preposition, ProcedureText.Of(div.Second!), ProcedureText.Of(div.Remainder!)).Should().Be(("INTO", "B", "D"));
        ((ArithmeticStatement)s[7]).Preposition.Should().Be("BY");
        var compute = (ComputeStatement)s[8];
        compute.Targets.Select(t => t.Rounded).Should().Equal(true, false);
        ProcedureText.Of(compute.Value).Should().Be("((C + 1) / 2)");
        compute.Handlers.Single().Statements.Single().Should().BeOfType<MoveStatement>();
    }

    [Fact]
    public void IfNestingWithPeriodsAndScopeTerminators()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            B("IF A = 1"),
            B("    IF B = 2"),
            B("        MOVE 1 TO C"),
            B("    ELSE"),
            B("        MOVE 2 TO C."),
            B("IF DONE NEXT SENTENCE ELSE MOVE 3 TO C END-IF"),
            B("DISPLAY C."));

        FullyParsed(r);
        var sentences = Proc(r).Sections[0].Leading;
        sentences.Select(s => s.Statements.Count).Should().Equal(1, 2);
        var outer = (IfStatement)sentences[0].Statements[0];
        (outer.Else, outer.HasEndIf).Should().Be((null, false));
        var inner = outer.Then.Should().ContainSingle().Which.Should().BeOfType<IfStatement>().Subject;
        inner.Else.Should().ContainSingle().Which.Should().BeOfType<MoveStatement>();
        var second = (IfStatement)sentences[1].Statements[0];
        second.Then.Should().ContainSingle().Which.Should().BeOfType<NextSentenceStatement>();
        second.HasEndIf.Should().BeTrue();
        sentences[1].Statements[1].Should().BeOfType<DisplayStatement>();
    }

    [Fact]
    public void EvaluateWithAlsoThruAnyAndOther()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            B("EVALUATE TRUE ALSO A"),
            B("    WHEN DONE ALSO 1 THRU 5"),
            B("    WHEN A > B ALSO ANY"),
            B("        DISPLAY 'ONE'"),
            B("    WHEN FALSE ALSO NOT 9"),
            B("        CONTINUE"),
            B("    WHEN OTHER"),
            B("        DISPLAY 'OTHER'"),
            B("END-EVALUATE."));

        FullyParsed(r);
        var e = Single<EvaluateStatement>(r);
        e.Subjects.Select(s => s.Kind).Should().Equal(EvaluateOperandKind.True, EvaluateOperandKind.Expression);
        e.Whens.Select(w => w.Alternatives.Count).Should().Equal(2, 1);
        var firstAlt = e.Whens[0].Alternatives[0];
        ProcedureText.Of(firstAlt[0].Condition!).Should().Be("DONE");
        (ProcedureText.Of(firstAlt[1].Value!), ProcedureText.Of(firstAlt[1].Thru!)).Should().Be(("1", "5"));
        e.Whens[0].Alternatives[1][1].Kind.Should().Be(EvaluateOperandKind.Any);
        e.Whens[1].Alternatives[0].Select(o => (o.Kind, o.Not)).Should().Equal((EvaluateOperandKind.False, false), (EvaluateOperandKind.Expression, true));
        e.Other.Should().ContainSingle().Which.Should().BeOfType<DisplayStatement>();
    }

    [Fact]
    public void PerformForms()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            A("MAIN-PARA."),
            B("PERFORM P1 THRU P2"),
            B("PERFORM P1 3 TIMES"),
            B("PERFORM P1 ENT-KEY(1) TIMES"),
            B("PERFORM P1 WITH TEST AFTER UNTIL DONE"),
            B("PERFORM VARYING A FROM 1 BY 1 UNTIL A > 10"),
            B("        AFTER B FROM A BY 2 UNTIL B > 5"),
            B("    DISPLAY A B"),
            B("END-PERFORM"),
            B("PERFORM A TIMES CONTINUE END-PERFORM"),
            B("PERFORM UNTIL DONE SET DONE TO TRUE END-PERFORM."),
            A("P1."),
            B("EXIT."),
            A("P2."),
            B("EXIT."));

        FullyParsed(r);
        var p = TopLevel(r).OfType<PerformStatement>().ToList();
        p.Select(x => (x.Target?.Name, x.Thru?.Name, x.Kind, x.Inline?.Count)).Should().Equal(
            ("P1", "P2", PerformKind.Once, null),
            ("P1", null, PerformKind.Times, null),
            ("P1", null, PerformKind.Times, null),
            ("P1", null, PerformKind.Until, null),
            (null, null, PerformKind.Varying, 1),
            (null, null, PerformKind.Times, 1),
            (null, null, PerformKind.Until, 1));
        ProcedureText.Of(p[2].Times!).Should().Be("ENT-KEY(1)");
        p[3].TestAfter.Should().BeTrue();
        p[4].Varying.Select(v => (ProcedureText.Of(v.Variable), ProcedureText.Of(v.From), ProcedureText.Of(v.Until))).Should().Equal(
            ("A", "1", "(A > 10)"), ("B", "A", "(B > 5)"));
        Proc(r).ProcedureReferences.Select(x => (x.Name, x.FromParagraph, x.Line)).Should().Equal(
            ("P1", "MAIN-PARA", 25), ("P2", "MAIN-PARA", 25), ("P1", "MAIN-PARA", 26), ("P1", "MAIN-PARA", 27), ("P1", "MAIN-PARA", 28));
    }

    [Fact]
    public void GoToAlterAndExitForms()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            A("P1."),
            B("GO TO P2 P3 DEPENDING ON A"),
            B("ALTER P4 TO PROCEED TO P3"),
            B("GO P2."),
            A("P2."),
            B("EXIT PARAGRAPH."),
            A("P3."),
            B("EXIT PROGRAM."),
            A("P4."),
            B("GO TO."));

        FullyParsed(r);
        var s = Proc(r).AllStatements().ToList();
        var dependingOn = (GoToStatement)s[0];
        dependingOn.Targets.Select(t => t.Name).Should().Equal("P2", "P3");
        ProcedureText.Of(dependingOn.DependingOn!).Should().Be("A");
        ((AlterStatement)s[1]).Alterations.Select(a => (a.From.Name, a.To.Name)).Should().Equal(("P4", "P3"));
        ((GoToStatement)s[2]).Targets.Select(t => t.Name).Should().Equal("P2");
        s.OfType<ExitStatement>().Select(e => e.Kind).Should().Equal("PARAGRAPH", "PROGRAM");
        ((GoToStatement)s[5]).Targets.Should().BeEmpty();
    }

    [Fact]
    public void CallCancelAndProgramEnds()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            B("CALL 'SUBPROG' USING BY REFERENCE X"),
            B("    BY CONTENT 'LIT' LENGTH OF Y BY VALUE A OMITTED"),
            B("    RETURNING B"),
            B("    ON EXCEPTION DISPLAY 'NO PROG' NOT ON EXCEPTION CONTINUE"),
            B("END-CALL"),
            B("CALL X USING Y ON OVERFLOW CONTINUE END-CALL"),
            B("CANCEL 'SUBPROG' X"),
            B("ENTRY 'ALTENTRY' USING X"),
            B("GOBACK"),
            B("STOP RUN."));

        FullyParsed(r);
        var s = TopLevel(r);
        var call = (CallStatement)s[0];
        (call.IsStatic, call.Program).Should().Be((true, "SUBPROG"));
        call.Using.Select(u => (u.Mode, u.Value is null ? "OMITTED" : ProcedureText.Of(u.Value))).Should().Equal(
            (PassingMode.Reference, "X"), (PassingMode.Content, "'LIT'"), (PassingMode.Content, "LENGTH OF Y"), (PassingMode.Value, "A"), (PassingMode.Value, "OMITTED"));
        ProcedureText.Of(call.Returning!).Should().Be("B");
        call.Handlers.Select(h => (h.Kind, h.Not)).Should().Equal((HandlerKind.Exception, false), (HandlerKind.Exception, true));
        var dynamic = (CallStatement)s[1];
        (dynamic.IsStatic, ProcedureText.Of(dynamic.Target)).Should().Be((false, "X"));
        dynamic.Handlers.Single().Kind.Should().Be(HandlerKind.Overflow);
        ((CancelStatement)s[2]).Programs.Select(ProcedureText.Of).Should().Equal("'SUBPROG'", "X");
        ((EntryStatement)s[3]).Name.Should().Be("ALTENTRY");
        s[4].Should().BeOfType<GobackStatement>();
        ((StopStatement)s[5]).Run.Should().BeTrue();
    }

    [Fact]
    public void InitializeInspectStringAndUnstring()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            B("INITIALIZE X Y REPLACING NUMERIC DATA BY 0"),
            B("    ALPHANUMERIC BY SPACES"),
            B("INSPECT X TALLYING A FOR ALL 'A' BEFORE INITIAL 'B'"),
            B("    B FOR CHARACTERS"),
            B("    REPLACING LEADING SPACE BY ZERO"),
            B("INSPECT X CONVERTING 'abc' TO 'ABC' AFTER '.'"),
            B("STRING X DELIMITED BY SPACE 'LIT' Y DELIMITED SIZE"),
            B("    INTO Z WITH POINTER A"),
            B("    ON OVERFLOW CONTINUE END-STRING"),
            B("UNSTRING X DELIMITED BY ',' OR ALL SPACE"),
            B("    INTO Y DELIMITER IN Z COUNT IN A, B"),
            B("    WITH POINTER C TALLYING IN D END-UNSTRING."));

        FullyParsed(r);
        var s = TopLevel(r);
        var init = (InitializeStatement)s[0];
        init.Targets.Select(ProcedureText.Of).Should().Equal("X", "Y");
        init.Replacing.Select(x => (x.Category, ProcedureText.Of(x.Value))).Should().Equal(("NUMERIC", "0"), ("ALPHANUMERIC", "SPACE"));
        var tally = (InspectStatement)s[1];
        tally.Tallying.Select(t => (t.Mode, t.Pattern is null ? null : ProcedureText.Of(t.Pattern), ProcedureText.Of(t.Counter!), t.Boundaries.Count)).Should().Equal(
            ("ALL", "'A'", "A", 1), ("CHARACTERS", null, "B", 0));
        tally.Replacing.Single().Mode.Should().Be("LEADING");
        ((InspectStatement)s[2]).Converting!.Boundaries.Single().Before.Should().BeFalse();
        var str = (StringStatement)s[3];
        str.Sources.Select(x => (x.Values.Count, x.DelimitedBySize)).Should().Equal((1, false), (2, true));
        ProcedureText.Of(str.Pointer!).Should().Be("A");
        str.Handlers.Single().Kind.Should().Be(HandlerKind.Overflow);
        var unstr = (UnstringStatement)s[4];
        unstr.Delimiters.Select(d => d.All).Should().Equal(false, true);
        unstr.Targets.Select(t => (ProcedureText.Of(t.Into), t.DelimiterIn is null, t.CountIn is null)).Should().Equal(("Y", false, false), ("B", true, true));
        (ProcedureText.Of(unstr.Pointer!), ProcedureText.Of(unstr.Tallying!)).Should().Be(("C", "D"));
    }

    [Fact]
    public void SetAndSearchForms()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            B("SET DONE TO TRUE"),
            B("SET IX UP BY 1"),
            B("SET IX TO 1"),
            B("SET PTR TO ADDRESS OF X"),
            B("SEARCH ENT VARYING A"),
            B("    AT END DISPLAY 'NONE'"),
            B("    WHEN ENT-KEY(IX) = 5 DISPLAY 'FIVE'"),
            B("    WHEN ENT-VAL(IX) = 'X' NEXT SENTENCE"),
            B("END-SEARCH"),
            B("SEARCH ALL ENT WHEN ENT-KEY(IX) = A CONTINUE."));

        FullyParsed(r);
        var s = TopLevel(r);
        s.Take(4).Cast<SetStatement>().Select(x => x.Kind).Should().Equal(SetKind.ToTrue, SetKind.UpBy, SetKind.To, SetKind.ToAddress);
        var search = (SearchStatement)s[4];
        (search.All, ProcedureText.Of(search.Table), ProcedureText.Of(search.Varying!)).Should().Be((false, "ENT", "A"));
        search.AtEnd.Should().ContainSingle();
        search.Whens.Select(w => ProcedureText.Of(w.Condition)).Should().Equal("(ENT-KEY(IX) = 5)", "(ENT-VAL(IX) = 'X')");
        ((SearchStatement)s[5]).All.Should().BeTrue();
        r.Bindings[0].ImplicitKindOf(Proc(r).DataReferences.First(d => d.Name.Name == "IX")).Should().Be("index");
    }

    [Fact]
    public void FileInputOutputSortAndConsole()
    {
        using var estate = new TestEstate();
        var r = estate.Program(
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("ENVIRONMENT DIVISION."),
            A("INPUT-OUTPUT SECTION."),
            A("FILE-CONTROL."),
            B("SELECT IN-FILE ASSIGN TO INFILE ORGANIZATION INDEXED"),
            B("    RECORD KEY IN-KEY."),
            B("SELECT OUT-FILE ASSIGN TO OUTFILE."),
            B("SELECT SORT-FILE ASSIGN TO SORTWK."),
            A("DATA DIVISION."),
            A("FILE SECTION."),
            A("FD  IN-FILE."),
            A("01  IN-REC."),
            B("05  IN-KEY PIC X(4)."),
            A("FD  OUT-FILE."),
            A("01  OUT-REC PIC X(80)."),
            A("SD  SORT-FILE."),
            A("01  SORT-REC."),
            B("05  SORT-KEY PIC X(4)."),
            A("WORKING-STORAGE SECTION."),
            A("01  WS-REC PIC X(80)."),
            A("01  WS-DATE PIC 9(8)."),
            A("PROCEDURE DIVISION."),
            B("OPEN INPUT IN-FILE OUTPUT OUT-FILE"),
            B("READ IN-FILE NEXT RECORD INTO WS-REC AT END DISPLAY 'EOF'"),
            B("    NOT AT END CONTINUE END-READ"),
            B("READ IN-FILE KEY IS IN-KEY INVALID KEY CONTINUE END-READ"),
            B("START IN-FILE KEY IS NOT LESS THAN IN-KEY"),
            B("    INVALID KEY CONTINUE END-START"),
            B("WRITE OUT-REC FROM WS-REC AFTER ADVANCING 2 LINES"),
            B("REWRITE IN-REC INVALID KEY CONTINUE END-REWRITE"),
            B("DELETE IN-FILE RECORD INVALID KEY CONTINUE END-DELETE"),
            B("SORT SORT-FILE ON ASCENDING KEY SORT-KEY WITH DUPLICATES"),
            B("    INPUT PROCEDURE IS FEED-SORT THRU FEED-EXIT"),
            B("    GIVING OUT-FILE"),
            B("ACCEPT WS-DATE FROM DATE YYYYMMDD"),
            B("DISPLAY 'DATE ' WS-DATE UPON CONSOLE WITH NO ADVANCING"),
            B("CLOSE IN-FILE OUT-FILE"),
            B("GOBACK."),
            A("FEED-SORT."),
            B("RELEASE SORT-REC FROM WS-REC"),
            B("RETURN SORT-FILE INTO WS-REC AT END CONTINUE END-RETURN."),
            A("FEED-EXIT."),
            B("EXIT."));

        FullyParsed(r);
        var s = Proc(r).AllStatements().Where(x => x is not (DisplayStatement or ContinueStatement)).ToList();
        ((OpenStatement)s[0]).Files.Select(f => (f.Mode, f.File.Name.Name)).Should().Equal(("INPUT", "IN-FILE"), ("OUTPUT", "OUT-FILE"));
        var files = s.OfType<FileStatement>().ToList();
        files.Select(f => (f.Verb, f.Target.Name.Name, f.Handlers.Count)).Should().Equal(
            ("READ", "IN-FILE", 2), ("READ", "IN-FILE", 1), ("START", "IN-FILE", 1), ("WRITE", "OUT-REC", 0),
            ("REWRITE", "IN-REC", 1), ("DELETE", "IN-FILE", 1), ("RELEASE", "SORT-REC", 0), ("RETURN", "SORT-FILE", 1));
        files[0].Next.Should().BeTrue();
        files[2].KeyOperator.Should().Be(RelationalOperator.GreaterOrEqual);
        (files[3].Advancing, ProcedureText.Of(files[3].AdvancingAmount!)).Should().Be(("AFTER", "2"));
        var sort = s.OfType<SortStatement>().Single();
        (sort.InputProcedure!.Value.From.Name, sort.InputProcedure.Value.Thru!.Name, sort.Duplicates).Should().Be(("FEED-SORT", "FEED-EXIT", true));
        sort.Giving.Select(g => g.Name.Name).Should().Equal("OUT-FILE");
        s.OfType<AcceptStatement>().Single().Special.Should().Be("DATE YYYYMMDD");
        var display = Proc(r).AllStatements().OfType<DisplayStatement>().Last();
        (display.Values.Count, display.Upon, display.NoAdvancing).Should().Be((2, "CONSOLE", true));
        s.OfType<CloseStatement>().Single().Files.Should().HaveCount(2);
    }

    [Fact]
    public void ExecBlocksInTheProcedureShareTheExecParserResult()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            B("EXEC CICS RETURN END-EXEC"),
            B("IF EIBCALEN = 0"),
            B("    EXEC CICS SEND TEXT FROM(X) END-EXEC"),
            B("END-IF."));

        Problems(r).Should().BeEmpty();
        r.Coverage.Count(CoverageClass.Lexed).Should().Be(0);
        var execs = Proc(r).AllStatements().OfType<ExecStatementSyntax>().ToList();
        execs.Select(e => (e.Kind, e.Parsed.Operation)).Should().Equal((ExecKind.Cics, "RETURN"), (ExecKind.Cics, "SEND TEXT"));
        r.ParsedExecs.Values.Should().HaveCount(2);
        execs.Select(e => r.ParsedExecs[e.Span.FirstToken]).Should().Equal(execs.Select(e => e.Parsed), (a, b) => ReferenceEquals(a, b));
    }

    [Fact]
    public void InvalidStatementsBecomeErrorNodesAndParsingResumes()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            A("P1."),
            B("MOVE TO X."),
            B("MOVE 1 TO A."),
            A("P2."),
            B("ADD 1 B C DISPLAY 'AFTER'"),
            B("IF A = THEN DISPLAY 'X' END-IF"),
            B("XML GENERATE X FROM Y END-XML"),
            B("DISPLAY 'END'."),
            A("P3."),
            B("EXIT."));

        var proc = Proc(r);
        proc.AllParagraphs().Select(p => p.Name).Should().Equal("P1", "P2", "P3");
        proc.AllStatements().Select(s => s.GetType().Name).Should().Equal(
            "ErrorStatement", "MoveStatement", "ErrorStatement", "DisplayStatement", "ErrorStatement", "ErrorStatement", "DisplayStatement",
            "ExitStatement");
        proc.Errors.Select(e => e.Diagnostic.Fingerprint).Should().Equal(
            "invalid-statement:MOVE TO X", "invalid-statement:ADD LIT B", "invalid-statement:IF A EQUAL", "unsupported-statement:XML GENERATE X");
        r.Coverage.Count(CoverageClass.Lexed).Should().Be(0);
        r.Coverage.Count(CoverageClass.Unsupported).Should().Be(
            "MOVETOX".Length + "ADD1BC".Length + "IFA=THENDISPLAY'X'END-IF".Length + "XMLGENERATEXFROMYEND-XML".Length);
    }

    [Fact]
    public void UnresolvedAndAmbiguousNamesAreWarnings()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            A("P1."),
            B("MOVE NOPE TO A"),
            B("MOVE F1 TO X"),
            B("MOVE F1 OF GRP2 TO X"),
            B("MOVE RETURN-CODE TO A"),
            B("PERFORM MISSING-PARA"),
            B("PERFORM P1."));

        Problems(r).Should().Equal("unresolved-data-name:NOPE", "ambiguous-data-name:F9", "unresolved-procedure-name:MISSING-PARA");
        var refs = Proc(r).DataReferences;
        r.Bindings[0].ItemOf(refs.Single(d => d.Name.Qualifiers.Count == 1))!.Path.Should().Be("GRP2.F1");
        r.Bindings[0].ImplicitKindOf(refs.Single(d => d.Name.Name == "RETURN-CODE")).Should().Be("special-register");
    }

    [Fact]
    public void DuplicateParagraphNamesResolveWithinTheReferencingSection()
    {
        using var estate = new TestEstate();
        var r = Program(estate,
            A("S1 SECTION."),
            A("START-IT."),
            B("PERFORM WORK."),
            A("WORK."),
            B("EXIT."),
            A("S2 SECTION."),
            A("WORK."),
            B("PERFORM WORK OF S1."),
            B("GO TO WORK."));

        FullyParsed(r);
        var refs = Proc(r).ProcedureReferences;
        refs.Select(x => (x.Name, x.Qualifier, x.FromSection)).Should().Equal(("WORK", null, "S1"), ("WORK", "S1", "S2"), ("WORK", null, "S2"));
        refs.Select(x => ((Paragraph)r.Bindings[0].TargetOf(x)!).StartLine).Should().Equal(27, 27, 30);
    }
}
