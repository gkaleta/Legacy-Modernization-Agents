using CobolToQuarkusMigration.Helpers;

namespace Starforge.Cobol.ModelDiff;

// Compares the RektContext the application builds from each tree.
internal static class ContextDiff
{
    public static void Run(Compare cmp, RektContext rekt, RektContext sf)
    {
        cmp.Value("context.scalars", "Program", rekt.Program, sf.Program);
        cmp.Value("context.scalars", "LineCount", rekt.LineCount.ToString(), sf.LineCount.ToString());
        cmp.Value("context.scalars", "IsCopybook", rekt.IsCopybook.ToString(), sf.IsCopybook.ToString());

        cmp.Keyed("context.sections", rekt.Sections.Select(s => (s.Name, "")), sf.Sections.Select(s => (s.Name, "")));
        Ordered(cmp, "context.section-order", rekt.Sections.Select(s => s.Name), sf.Sections.Select(s => s.Name));
        cmp.Shared("context.section-lines", rekt.Sections.Select(s => (s.Name, $"{s.StartLine}-{s.EndLine}")),
            sf.Sections.Select(s => (s.Name, $"{s.StartLine}-{s.EndLine}")));

        cmp.Keyed("context.paragraphs", Paragraphs(rekt).Select(p => (p.Key, "")), Paragraphs(sf).Select(p => (p.Key, "")));
        Ordered(cmp, "context.paragraph-order", Paragraphs(rekt).Select(p => p.Key), Paragraphs(sf).Select(p => p.Key));
        cmp.Shared("context.paragraph-lines", Paragraphs(rekt), Paragraphs(sf));

        cmp.Keyed("context.perform-edges", rekt.PerformGraph.Select(e => ($"{e.From} -> {e.To}", "")),
            sf.PerformGraph.Select(e => ($"{e.From} -> {e.To}", "")));
        cmp.Shared("context.perform-conditional", rekt.PerformGraph.Select(e => ($"{e.From} -> {e.To}", e.Conditional.ToString())),
            sf.PerformGraph.Select(e => ($"{e.From} -> {e.To}", e.Conditional.ToString())));

        cmp.Keyed("context.call-targets", rekt.CallTargets.Select(c => (c.TargetProgram, $"dynamic={c.IsDynamic}")),
            sf.CallTargets.Select(c => (c.TargetProgram, $"dynamic={c.IsDynamic}")));
        cmp.Shared("context.call-lines", rekt.CallTargets.Select(c => (c.TargetProgram, c.LineNumber.ToString())),
            sf.CallTargets.Select(c => (c.TargetProgram, c.LineNumber.ToString())));

        cmp.Keyed("context.sql", rekt.SqlStatements.Select(Sql), sf.SqlStatements.Select(Sql));

        cmp.Keyed("context.copybooks", rekt.CopybookUsage.Select(c => (c, "")), sf.CopybookUsage.Select(c => (c, "")));
        Ordered(cmp, "context.copybook-order", rekt.CopybookUsage, sf.CopybookUsage);

        var left = Data(rekt.DataStructure).ToList();
        var right = Data(sf.DataStructure).ToList();
        cmp.Keyed("context.data-items", left.Select(d => (d.Path, "")), right.Select(d => (d.Path, "")));
        cmp.Shared("context.data-name", left.Select(d => (d.Path, d.Item.Name)), right.Select(d => (d.Path, d.Item.Name)));
        Ordered(cmp, "context.data-order", left.Select(d => d.Path), right.Select(d => d.Path));
        foreach (var (field, get) in Fields)
        {
            cmp.Shared($"context.data-{field}", left.Select(d => (d.Path, get(d.Item))), right.Select(d => (d.Path, get(d.Item))));
        }
    }

    private static readonly (string Field, Func<RektDataItem, string> Get)[] Fields =
    [
        ("level", d => d.Level.ToString()),
        ("pic", d => d.PicClause ?? "null"),
        ("usage", d => d.Usage ?? "null"),
        ("value", d => d.Value ?? "null"),
        ("redefines", d => d.Redefines ?? "null"),
        ("occurs", d => d.Occurs?.ToString() ?? "null"),
        ("section", d => d.SourceSection ?? "null"),
    ];

    private static (string Key, string Value) Sql(RektSqlStatement s) =>
        ($"{s.Operation} [{string.Join(",", s.Tables)}]", $"line={s.LineNumber} excerpt={s.Excerpt}");

    private static IEnumerable<(string Key, string Value)> Paragraphs(RektContext ctx) =>
        ctx.Sections.SelectMany(s => s.Paragraphs.Select(p => ($"{s.Name}/{p.Name}", $"{p.StartLine}-{p.EndLine}")));

    // Only the relative order of keys both sides share is compared; presence is reported separately.
    private static void Ordered(Compare cmp, string category, IEnumerable<string> rekt, IEnumerable<string> sf)
    {
        var left = rekt.ToList();
        var right = sf.ToList();
        var shared = left.Intersect(right, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        cmp.Value(category, "order", string.Join(" | ", left.Where(shared.Contains)), string.Join(" | ", right.Where(shared.Contains)));
    }

    // REKT renames FILLER REDEFINES to FIL-Rnnn; aligning on FILLER keeps the children comparable, and context.data-name still counts the rename.
    private static string PathName(string name) =>
        name.StartsWith("FIL-R", StringComparison.Ordinal) && name.Length > 5 && name[5..].All(char.IsAsciiDigit) ? "FILLER" : name;

    public static IEnumerable<(string Path, RektDataItem Item)> Data(IEnumerable<RektDataItem> items, string prefix = "")
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var name = PathName(item.Name);
            var n = seen[name] = seen.GetValueOrDefault(name) + 1;
            var path = prefix + (n == 1 ? name : $"{name}#{n}");
            yield return (path, item);
            foreach (var child in Data(item.Children, path + "/"))
            {
                yield return child;
            }
        }
    }
}
