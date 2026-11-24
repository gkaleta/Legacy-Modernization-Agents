using CobolToQuarkusMigration.Helpers;
using Starforge.Cobol.Emit;

namespace Starforge.Cobol.Contract.Tests;

// Emits a small program with Starforge and reads it back through the application's RektContextLoader.
public sealed class EmitRoundTripTests : IDisposable
{
    private readonly string _repo = Path.Combine(AppContext.BaseDirectory, "round-trip", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_repo))
        {
            Directory.Delete(_repo, recursive: true);
        }
    }

    private static string A(string text) => "       " + text;

    private static string B(string text) => "           " + text;

    private RektContext EmitAndLoad()
    {
        var estate = Path.Combine(_repo, "source", "est");
        Write(Path.Combine(estate, "copy", "CPYA.cpy"), A("01  CPYA-REC PIC X(4)."));
        var program = Write(Path.Combine(estate, "cobol", "MAIN.cbl"),
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
            A("COPY CPYA."),
            A("01  WS-REC."),
            B("05  WS-ID PIC 9(4) COMP VALUE 0."),
            B("05  WS-TAB PIC X(2) OCCURS 3."),
            A("01  WS-ALT REDEFINES WS-REC PIC X(10)."),
            A("PROCEDURE DIVISION."),
            A("MAIN-LOGIC SECTION."),
            A("M1."),
            B("PERFORM P1 UNTIL WS-ID > 3."),
            B("PERFORM P1."),
            B("CALL 'LITPGM'."),
            B("EXEC SQL SELECT 1 INTO :WS-ID FROM CUSTOMER END-EXEC."),
            B("GOBACK."),
            A("WORK-STEP SECTION."),
            A("P1."),
            B("ADD 1 TO WS-ID."));

        var rekt = Path.Combine(_repo, "rekt");
        var result = new CobolFrontEnd(new FrontEndOptions(new[] { estate })).Process(program);
        foreach (var file in RektEmitter.Render(result, "est/cobol/MAIN.cbl", new PathDisplay(new[] { (estate, "est") })))
        {
            var target = Path.Combine(rekt, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.ToBytes());
        }

        return new RektContextLoader(_repo, rekt).Load("est/cobol/MAIN.cbl", "source");
    }

    private static string Write(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void LoaderReadsTheEmittedTreeAsTheProgramsStructure()
    {
        var ctx = EmitAndLoad();

        ctx.Sections.Select(s => (s.Name, s.StartLine, s.EndLine, string.Join(",", s.Paragraphs.Select(p => $"{p.Name}:{p.StartLine}-{p.EndLine}"))))
            .Should().Equal(("MAIN-LOGIC", 11, 17, "M1:12-17"), ("WORK-STEP", 18, 20, "P1:19-20"));
        // The loader records a PERFORM once from its SENTENCE text and once from the PERFORM node, for REKT trees too.
        ctx.PerformGraph.Select(e => (e.From, e.To, e.Conditional)).Should().Equal(
            ("MAIN-LOGIC", "P1", true),
            ("MAIN-LOGIC", "P1", true),
            ("MAIN-LOGIC", "P1", false),
            ("MAIN-LOGIC", "P1", false));
        ctx.CallTargets.Select(c => (c.TargetProgram, c.IsDynamic, c.LineNumber)).Should().Equal(("LITPGM", false, 15));
        ctx.SqlStatements.Select(s => (s.Operation, string.Join(",", s.Tables), s.LineNumber, s.Excerpt))
            .Should().Equal(("SELECT", "CUSTOMER", 16, "EXEC SQL SELECT 1 INTO :WS-ID FROM CUSTOMER END-EXEC"));
        ctx.CopybookUsage.Should().Equal("CPYA.cpy");
        ctx.LineCount.Should().Be(20);
    }

    [Fact]
    public void LoaderReadsTheEmittedDataLayout()
    {
        var ctx = EmitAndLoad();

        static IEnumerable<string> Flatten(IEnumerable<RektDataItem> items, string indent) => items.SelectMany(d =>
            new[] { $"{indent}{d.Level} {d.Name} pic={d.PicClause} usage={d.Usage} redefines={d.Redefines} occurs={d.Occurs} {d.SourceSection}" }
                .Concat(Flatten(d.Children, indent + "  ")));

        Flatten(ctx.DataStructure, "").Should().Equal(
            "1 CPYA-REC pic=PIC X(4). usage= redefines= occurs= WORKING_STORAGE",
            "1 WS-REC pic= usage= redefines= occurs= WORKING_STORAGE",
            "  5 WS-ID pic=PIC 9(4) usage=COMP redefines= occurs= WORKING_STORAGE",
            "  5 WS-TAB pic=PIC X(2) usage= redefines= occurs=3 WORKING_STORAGE",
            "1 WS-ALT pic=PIC X(10). usage= redefines=WS-REC occurs= WORKING_STORAGE");
    }
}
