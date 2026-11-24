using Starforge.Cobol.Coverage;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;
using static Starforge.Cobol.Tests.Fixed;

namespace Starforge.Cobol.Tests;

public sealed class DataLayoutTests
{
    private static CompilationResult WorkingStorage(TestEstate estate, params string[] entries)
        => estate.Program(new[]
        {
            A("IDENTIFICATION DIVISION."),
            A("PROGRAM-ID. MAIN."),
            A("DATA DIVISION."),
            A("WORKING-STORAGE SECTION."),
        }.Concat(entries).Append(A("PROCEDURE DIVISION.")).Append(B("GOBACK.")).ToArray());

    private static DataItem Item(CompilationResult r, string path)
        => r.Programs.Single().Items.Single(i => i.Path == path);

    private static (int Offset, int Length) At(CompilationResult r, string path)
    {
        var item = Item(r, path);
        return (item.Offset, item.Length);
    }

    [Theory]
    [InlineData("PIC 9(5)", 5, DataUsage.Display)]
    [InlineData("PIC S9(5)", 5, DataUsage.Display)]
    [InlineData("PIC S9(5) SIGN LEADING", 5, DataUsage.Display)]
    [InlineData("PIC S9(5) SIGN IS TRAILING SEPARATE CHARACTER", 6, DataUsage.Display)]
    [InlineData("PIC S9(5) SIGN LEADING SEPARATE", 6, DataUsage.Display)]
    [InlineData("PIC S9(4) COMP", 2, DataUsage.Binary)]
    [InlineData("PIC 9(5) COMPUTATIONAL", 4, DataUsage.Binary)]
    [InlineData("PIC S9(9) BINARY", 4, DataUsage.Binary)]
    [InlineData("PIC S9(10) COMP-4", 8, DataUsage.Binary)]
    [InlineData("PIC S9(18) USAGE IS COMP-5", 8, DataUsage.Comp5)]
    [InlineData("PIC 9(3) COMP-5", 2, DataUsage.Comp5)]
    [InlineData("PIC S9(7) COMP-3", 4, DataUsage.PackedDecimal)]
    [InlineData("PIC S9(6)V99 PACKED-DECIMAL", 5, DataUsage.PackedDecimal)]
    [InlineData("PIC 9 COMP-3", 1, DataUsage.PackedDecimal)]
    [InlineData("COMP-1", 4, DataUsage.Comp1)]
    [InlineData("COMP-2", 8, DataUsage.Comp2)]
    [InlineData("INDEX", 4, DataUsage.Index)]
    [InlineData("USAGE POINTER", 4, DataUsage.Pointer)]
    [InlineData("PROCEDURE-POINTER", 8, DataUsage.ProcedurePointer)]
    [InlineData("FUNCTION-POINTER", 4, DataUsage.FunctionPointer)]
    [InlineData("PIC N(5) USAGE NATIONAL", 10, DataUsage.National)]
    [InlineData("PIC N(3)", 6, DataUsage.National)]
    [InlineData("PIC G(4) DISPLAY-1", 8, DataUsage.Display1)]
    [InlineData("PIC X(7)", 7, DataUsage.Display)]
    [InlineData("PIC ZZ9.99-", 7, DataUsage.Display)]
    public void ElementaryLengthFollowsUsageAndSign(string clauses, int length, DataUsage usage)
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(estate, A("01  REC."), B($"05  FLD {clauses}."), B("05  NEXT-FLD PIC X."));

        var fld = Item(r, "REC.FLD");
        fld.Length.Should().Be(length);
        fld.Usage.Should().Be(usage);
        At(r, "REC.NEXT-FLD").Should().Be((length, 1));
        At(r, "REC").Should().Be((0, length + 1));
        r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void GroupUsageAppliesToChildrenAndSignIsRecorded()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  REC USAGE COMP-3."),
            B("05  A1 PIC S9(5)V99."),
            B("05  A2 PIC 9(2)."),
            A("01  REC2 SIGN LEADING SEPARATE."),
            B("05  B1 PIC S9(3)."));

        At(r, "REC.A1").Should().Be((0, 4));
        At(r, "REC.A2").Should().Be((4, 2));
        Item(r, "REC.A1").Usage.Should().Be(DataUsage.PackedDecimal);
        Item(r, "REC.A1").Digits.Should().Be(7);
        Item(r, "REC.A1").Scale.Should().Be(2);
        Item(r, "REC.A1").Signed.Should().BeTrue();
        Item(r, "REC2.B1").SignLeading.Should().BeTrue();
        Item(r, "REC2.B1").SignSeparate.Should().BeTrue();
        At(r, "REC2.B1").Should().Be((0, 4));
    }

    [Fact]
    public void NestedGroupsWithOccurs()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  TABLE-REC."),
            B("05  HDR PIC X(4)."),
            B("05  ROW OCCURS 3 TIMES INDEXED BY ROW-IX."),
            B("    10  CELL OCCURS 2 PIC S9(3) COMP-3."),
            B("    10  TAG PIC X."),
            B("05  TRAILER PIC X(2)."));

        At(r, "TABLE-REC.ROW").Should().Be((4, 5));
        Item(r, "TABLE-REC.ROW").TotalLength.Should().Be(15);
        At(r, "TABLE-REC.ROW.CELL").Should().Be((4, 2));
        At(r, "TABLE-REC.ROW.TAG").Should().Be((8, 1));
        At(r, "TABLE-REC.TRAILER").Should().Be((19, 2));
        At(r, "TABLE-REC").Should().Be((0, 21));
        r.Programs.Single().Symbols.IndexOwner("ROW-IX")!.Path.Should().Be("TABLE-REC.ROW");
    }

    [Fact]
    public void OccursDependingOnUsesTheMaximum()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  ODO-REC."),
            B("05  CNT PIC S9(4) COMP."),
            B("05  ITEMS OCCURS 1 TO 50 TIMES DEPENDING ON CNT"),
            B("        ASCENDING KEY IS ITEM-KEY INDEXED BY IX1 IX2."),
            B("    10  ITEM-KEY PIC X(3)."),
            B("    10  ITEM-AMT PIC 9(5)."));

        var items = Item(r, "ODO-REC.ITEMS");
        items.Occurs!.Min.Should().Be(1);
        items.Occurs.Max.Should().Be(50);
        items.Occurs.DependingOn!.Name.Should().Be("CNT");
        items.Occurs.Keys.Select(k => (k.Ascending, k.Name.Name)).Should().Equal((true, "ITEM-KEY"));
        items.Occurs.IndexedBy.Should().Equal("IX1", "IX2");
        At(r, "ODO-REC.ITEMS").Should().Be((2, 8));
        At(r, "ODO-REC").Should().Be((0, 402));
    }

    [Fact]
    public void RedefinesSmallerAndLargerThanTarget()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  REC."),
            B("05  DATE-X PIC X(8)."),
            B("05  DATE-N REDEFINES DATE-X."),
            B("    10  YYYY PIC 9(4)."),
            B("    10  MMDD PIC 9(4)."),
            B("05  SHORT-V REDEFINES DATE-X PIC X(2)."),
            B("05  LONG-V REDEFINES DATE-X PIC X(12)."),
            B("05  AFTER-F PIC X."));

        At(r, "REC.DATE-N").Should().Be((0, 8));
        At(r, "REC.DATE-N.MMDD").Should().Be((4, 4));
        At(r, "REC.SHORT-V").Should().Be((0, 2));
        At(r, "REC.LONG-V").Should().Be((0, 12));
        Item(r, "REC.LONG-V").Redefines!.Path.Should().Be("REC.DATE-X");
        At(r, "REC.AFTER-F").Should().Be((12, 1));
        At(r, "REC").Should().Be((0, 13));
        r.Diagnostics.Should().ContainSingle(d => d.Code == "redefines-larger");
    }

    [Fact]
    public void RecordLevelRedefinesStartsAtZero()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  REC-A PIC X(10)."),
            A("01  REC-B REDEFINES REC-A."),
            B("05  B1 PIC X(4)."),
            B("05  B2 PIC 9(6)."));

        At(r, "REC-B").Should().Be((0, 10));
        At(r, "REC-B.B2").Should().Be((4, 6));
        Item(r, "REC-B").Redefines!.Path.Should().Be("REC-A");
    }

    [Fact]
    public void RenamesThruProducesAnAliasRange()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  REC."),
            B("05  F1 PIC X(2)."),
            B("05  F2 PIC X(3)."),
            B("05  F3 PIC X(4)."),
            B("05  F4 PIC X(5)."),
            A("66  MIDDLE RENAMES F2 THRU F3."),
            A("66  ONLY-F4 RENAMES F4."));

        var middle = Item(r, "REC.MIDDLE");
        middle.IsRenames.Should().BeTrue();
        middle.RenamesFrom!.Path.Should().Be("REC.F2");
        middle.RenamesThru!.Path.Should().Be("REC.F3");
        At(r, "REC.MIDDLE").Should().Be((2, 7));
        At(r, "REC.ONLY-F4").Should().Be((9, 5));
        At(r, "REC").Should().Be((0, 14));
    }

    [Fact]
    public void ConditionNamesWithMultipleValuesAndThru()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  REC."),
            B("05  STATUS-CODE PIC X(2) VALUE '00'."),
            B("    88  OK VALUE '00'."),
            B("    88  RETRYABLE VALUES ARE '10' '20' THRU '29', '35'."),
            B("    88  BAD VALUE IS '90' THROUGH '99'"),
            B("        WHEN SET TO FALSE IS '00'."),
            B("05  NEXT-F PIC X."));

        var retry = Item(r, "REC.STATUS-CODE.RETRYABLE");
        retry.IsCondition.Should().BeTrue();
        retry.HasStorage.Should().BeFalse();
        retry.Entry.Value!.Values.Select(v => (v.From.Text, v.Thru?.Text)).Should().Equal(("'10'", null), ("'20'", "'29'"), ("'35'", null));
        var bad = Item(r, "REC.STATUS-CODE.BAD");
        bad.Entry.Value!.Values.Should().ContainSingle().Which.Thru!.Text.Should().Be("'99'");
        bad.Entry.Value.FalseValue!.Text.Should().Be("'00'");
        At(r, "REC.STATUS-CODE.BAD").Should().Be((0, 0));
        At(r, "REC.NEXT-F").Should().Be((2, 1));
        Item(r, "REC.STATUS-CODE").Value.Should().Be("'00'");
    }

    [Fact]
    public void SynchronizedBinaryItemsGetSlackBytes()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  REC."),
            B("05  C1 PIC X."),
            B("05  B4 PIC S9(9) COMP SYNC."),
            B("05  C2 PIC X(3)."),
            B("05  B2 PIC S9(4) BINARY SYNCHRONIZED LEFT."),
            B("05  D8 COMP-2 SYNC."));

        At(r, "REC.B4").Should().Be((4, 4));
        Item(r, "REC.B4").SlackBefore.Should().Be(3);
        At(r, "REC.C2").Should().Be((8, 3));
        At(r, "REC.B2").Should().Be((12, 2));
        Item(r, "REC.B2").SlackBefore.Should().Be(1);
        At(r, "REC.D8").Should().Be((16, 8));
        Item(r, "REC.D8").SlackBefore.Should().Be(2);
        At(r, "REC").Should().Be((0, 24));
        r.Programs.Single().SlackBytesInserted.Should().BeTrue();
    }

    [Fact]
    public void OccursGroupWithSyncGetsOccurrenceSlack()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  REC."),
            B("05  ROW OCCURS 2."),
            B("    10  B4 PIC S9(9) COMP SYNC."),
            B("    10  C1 PIC X."));

        var row = Item(r, "REC.ROW");
        row.OccurrenceSlack.Should().Be(3);
        At(r, "REC.ROW").Should().Be((0, 8));
        At(r, "REC").Should().Be((0, 16));
    }

    [Fact]
    public void ClausesInAnyOrder()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  REC."),
            B("05  AMOUNTS VALUE ZERO OCCURS 4 SIGN TRAILING SEPARATE"),
            B("        USAGE DISPLAY PIC IS S9(3)V9 BLANK WHEN ZERO."),
            B("05  NAME-F JUST RIGHT VALUE ALL '*' PIC X(6)."),
            B("05  FILLER PICTURE X(2) VALUE SPACES."));

        var amounts = Item(r, "REC.AMOUNTS");
        amounts.Length.Should().Be(5);
        amounts.TotalLength.Should().Be(20);
        amounts.Scale.Should().Be(1);
        amounts.Value.Should().Be("ZERO");
        amounts.Entry.BlankWhenZero.Should().BeTrue();
        Item(r, "REC.NAME-F").Entry.Justified.Should().BeTrue();
        Item(r, "REC.NAME-F").Value.Should().Be("ALL '*'");
        At(r, "REC.NAME-F").Should().Be((20, 6));
        At(r, "REC.FILLER").Should().Be((26, 2));
        At(r, "REC").Should().Be((0, 28));
        r.Diagnostics.Where(d => d.Severity != DiagnosticSeverity.Info).Should().BeEmpty();
    }

    [Fact]
    public void ChildLevelCopybookUnderProgramRecord()
    {
        using var estate = new TestEstate();
        estate.Write("src/CHILDREN.cpy", B("05  C-ID PIC 9(4) COMP."), B("05  C-NAME PIC X(20)."));
        var r = WorkingStorage(
            estate,
            A("01  CUSTOMER."),
            B("COPY CHILDREN."),
            B("05  C-FLAG PIC X."));

        At(r, "CUSTOMER.C-ID").Should().Be((0, 2));
        At(r, "CUSTOMER.C-NAME").Should().Be((2, 20));
        At(r, "CUSTOMER.C-FLAG").Should().Be((22, 1));
        At(r, "CUSTOMER").Should().Be((0, 23));
        Item(r, "CUSTOMER.C-NAME").FromCopybook.Should().Be("CHILDREN");
        Item(r, "CUSTOMER").FromCopybook.Should().BeNull();
        Item(r, "CUSTOMER.C-NAME").Range.StartLine.Should().Be(2);
        Path.GetFileName(Item(r, "CUSTOMER.C-NAME").Range.File).Should().Be("CHILDREN.cpy");
    }

    [Fact]
    public void QualifiedNamesResolveThroughAncestors()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(
            estate,
            A("01  IN-REC."),
            B("05  KEY-F PIC X(4)."),
            A("01  OUT-REC."),
            B("05  GRP."),
            B("    10  KEY-F PIC X(4)."));
        var symbols = r.Programs.Single().Symbols;

        symbols.Resolve("KEY-F").Status.Should().Be(LookupStatus.Ambiguous);
        symbols.Resolve("KEY-F", new[] { "OUT-REC" }).Item!.Path.Should().Be("OUT-REC.GRP.KEY-F");
        symbols.Resolve("KEY-F", new[] { "IN-REC" }).Item!.Path.Should().Be("IN-REC.KEY-F");
        symbols.Resolve("NOPE").Status.Should().Be(LookupStatus.NotFound);
    }

    [Fact]
    public void DataDivisionCharactersBecomeParsedAndProcedureBodyStaysLexed()
    {
        using var estate = new TestEstate();
        var r = WorkingStorage(estate, A("01  REC PIC X."));

        r.Coverage.Count(CoverageClass.Parsed).Should().Be(
            "IDENTIFICATIONDIVISION.PROGRAM-ID.MAIN.DATADIVISION.WORKING-STORAGESECTION.01RECPICX.PROCEDUREDIVISION.".Length);
        r.Coverage.Count(CoverageClass.Lexed).Should().Be("GOBACK.".Length);
        r.CountInProcedureBodies(CoverageClass.Lexed).Should().Be("GOBACK.".Length);
    }
}
