using Starforge.Cobol.Flow;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public class FlowTests
{
    private static ProcedureFlow Flow(TestEstate estate, params string[] body)
    {
        var result = estate.Program(new[]
        {
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("01  A PIC 9."),
            A("01  B PIC 9."),
            A("PROCEDURE DIVISION."),
        }.Concat(body).ToArray());
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        return FlowBuilder.Build(result.Syntax.Programs[0].Procedure!, result.Tokens);
    }

    private static string Label(FlowNode node) => node.Name.Length == 0 ? node.Type : node.Type + " " + node.Name;

    private static List<(string From, string To)> Edges(ProcedureFlow flow, FlowEdgeKind kind, params string[] types)
    {
        var byId = flow.Root.SelfAndDescendants().ToDictionary(n => n.Id);
        return flow.Edges
            .Where(e => e.Kind == kind)
            .Select(e => (From: byId[e.From], To: byId[e.To]))
            .Where(e => types.Length == 0 || (types.Contains(e.From.Type) && types.Contains(e.To.Type)))
            .Select(e => (Label(e.From), Label(e.To)))
            .ToList();
    }

    private static List<string> Outline(FlowNode node, int depth)
    {
        var lines = new List<string> { Label(node) };
        if (depth > 0)
        {
            lines.AddRange(node.Children.SelectMany(c => Outline(c, depth - 1)).Select(l => "  " + l));
        }

        return lines;
    }

    [Fact]
    public void ParagraphsFallThroughUntilAStatementEndsControl()
    {
        using var estate = new TestEstate();
        var flow = Flow(estate,
            A("P1."),
            B("MOVE 1 TO A."),
            A("P2."),
            B("GO TO P4."),
            A("P3."),
            B("STOP RUN."),
            A("P4."),
            B("GOBACK."));

        Edges(flow, FlowEdgeKind.FollowedBy, "PARAGRAPH").Should().Equal(("PARAGRAPH P1", "PARAGRAPH P2"));
        Edges(flow, FlowEdgeKind.JumpsTo).Should().Equal(("GOTO P4", "PARAGRAPH P4"));
        flow.Unresolved.Should().BeEmpty();
    }

    [Fact]
    public void SectionsFallThroughAndPerformThruJumpsToBothEnds()
    {
        using var estate = new TestEstate();
        var flow = Flow(estate,
            A("MAIN-LOGIC SECTION."),
            A("M1."),
            B("PERFORM P1 THRU P2."),
            B("PERFORM WORK-STEP."),
            A("WORK-STEP SECTION."),
            A("P1."),
            B("MOVE 1 TO A."),
            A("P2."),
            B("EXIT."));

        Outline(flow.Root, 3).Should().Equal(
            "PROCEDURE_DIVISION_BODY PROCEDURE DIVISION",
            "  PARAGRAPHS para-group:",
            "  SECTION MAIN-LOGIC",
            "    SECTION_HEADER MAIN-LOGIC",
            "    PARAGRAPHS para-group:",
            "      PARAGRAPH M1",
            "  SECTION WORK-STEP",
            "    SECTION_HEADER WORK-STEP",
            "    PARAGRAPHS para-group:",
            "      PARAGRAPH P1",
            "      PARAGRAPH P2");
        Edges(flow, FlowEdgeKind.FollowedBy, "SECTION").Should().Equal(("SECTION MAIN-LOGIC", "SECTION WORK-STEP"));
        Edges(flow, FlowEdgeKind.JumpsTo).Should().Equal(
            ("PERFORM P1", "PARAGRAPH P1"),
            ("PERFORM P1", "PARAGRAPH P2"),
            ("PERFORM WORK-STEP", "SECTION WORK-STEP"));
        var perform = flow.Root.SelfAndDescendants().First(n => n.Type == "PERFORM");
        (perform.Target, perform.Thru, perform.Conditional).Should().Be(("P1", "P2", false));
    }

    [Fact]
    public void GoToDependingJumpsToEveryTargetAndFallsThroughWhenOutOfRange()
    {
        using var estate = new TestEstate();
        var flow = Flow(estate,
            A("P0."),
            B("GO TO P1 P2 DEPENDING ON A"),
            B("MOVE 1 TO B."),
            A("P1."),
            B("EXIT."),
            A("P2."),
            B("EXIT."));

        Edges(flow, FlowEdgeKind.JumpsTo).Should().Equal(("GOTO P1", "PARAGRAPH P1"), ("GOTO P1", "PARAGRAPH P2"));
        Edges(flow, FlowEdgeKind.FollowedBy, "GOTO", "MOVE").Should().Equal(("GOTO P1", "MOVE MOVE"));
        Edges(flow, FlowEdgeKind.FollowedBy, "PARAGRAPH").Should().Equal(("PARAGRAPH P0", "PARAGRAPH P1"), ("PARAGRAPH P1", "PARAGRAPH P2"));
    }

    [Fact]
    public void AlterAddsTheNewDestinationToTheAlteredGoTo()
    {
        using var estate = new TestEstate();
        var flow = Flow(estate,
            A("P0."),
            B("ALTER P1 TO PROCEED TO P3."),
            B("ALTER P2 TO PROCEED TO P3."),
            B("PERFORM P1."),
            B("STOP RUN."),
            A("P1."),
            B("GO TO P2."),
            A("P2."),
            B("EXIT."),
            A("P3."),
            B("EXIT."));

        Edges(flow, FlowEdgeKind.JumpsTo).Should().Equal(
            ("PERFORM P1", "PARAGRAPH P1"),
            ("GOTO P2", "PARAGRAPH P2"),
            ("GOTO P2", "PARAGRAPH P3"));
        Edges(flow, FlowEdgeKind.FollowedBy, "PARAGRAPH").Should().Equal(("PARAGRAPH P2", "PARAGRAPH P3"));
        flow.Unresolved.Should().Equal(new UnresolvedProcedure("P2", null, 10, "ALTER"));
    }
}
