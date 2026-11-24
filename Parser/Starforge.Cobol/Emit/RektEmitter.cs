using System.Security.Cryptography;
using System.Text.Json;
using Starforge.Cobol.Deps;
using Starforge.Cobol.Flow;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Emit;

// Content is written on demand so a large program's AST never sits in memory as one buffer.
public sealed record EmittedFile(string RelativePath, Action<Stream> WriteTo)
{
    public byte[] ToBytes()
    {
        using var buffer = new MemoryStream();
        WriteTo(buffer);
        return buffer.ToArray();
    }
}

// Writes the files downstream readers load from a REKT report tree, in the same layout and JSON vocabulary:
// <rel>-deps.json and <rel>.report/{flow_ast,cfg,data_structures}/..., plus parse-diagnostics.json when copybooks are missing.
public static class RektEmitter
{
    public static IReadOnlyList<EmittedFile> Render(CompilationResult result, string relativePath, PathDisplay paths)
    {
        var basename = Path.GetFileName(relativePath);
        var report = relativePath + ".report/";
        var program = result.Syntax.AllPrograms().FirstOrDefault();
        var data = result.Programs.Count > 0 ? result.Programs[0] : null;
        var flow = program?.Procedure is { } procedure ? FlowBuilder.Build(procedure, result.Tokens) : null;
        var deps = DependencyExtractor.Extract(result);

        var files = new List<EmittedFile>
        {
            new(relativePath + "-deps.json", Json(w => WriteDeps(w, result, deps, basename, relativePath, paths))),
        };
        if (flow is not null)
        {
            files.Add(new(report + "flow_ast/flow-ast-" + basename + ".json", Json(w => WriteAstNode(w, flow.Root, children: true))));
            files.Add(new(report + "cfg/cfg-" + basename + ".json", Json(w => WriteCfg(w, flow))));
        }

        files.Add(new(report + "data_structures/" + basename + "-data.json", Json(w => WriteData(w, result, data))));
        var missing = deps.MissingCopybooks.Select(c => c.Member).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            files.Add(new(report + "parse-diagnostics.json", Json(w =>
            {
                w.WriteStartArray();
                foreach (var name in missing)
                {
                    w.WriteStartObject();
                    w.WriteString("code", "missing copybook");
                    w.WriteString("severity", "WARNING");
                    w.WriteString("message", name);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            })));
        }

        return files;
    }

    private static Action<Stream> Json(Action<Utf8JsonWriter> write) => stream =>
    {
        using (var writer = new Utf8JsonWriter(stream, JsonReport.WriterOptions))
        {
            write(writer);
        }

        stream.WriteByte((byte)'\n');
    };

    // Utf8JsonWriter never flushes on its own, so long node lists would otherwise buffer the whole file.
    private static void Drain(Utf8JsonWriter w)
    {
        if (w.BytesPending > 1 << 16)
        {
            w.Flush();
        }
    }

    private static void WriteAstNode(Utf8JsonWriter w, FlowNode node, bool children)
    {
        Drain(w);
        w.WriteStartObject();
        w.WriteString("id", "n" + node.Id);
        w.WriteString("type", node.Type);
        w.WriteString("nodeType", "CODE_VERTEX");
        w.WriteString("name", node.Name);
        w.WriteString("label", node.Name.Length == 0 ? node.Type : node.Name);
        w.WriteString("originalText", node.Text);
        w.WriteNumber("startLine", node.StartLine);
        w.WriteNumber("endLine", node.EndLine);
        w.WriteStartObject("span");
        w.WriteNumber("start", node.StartLine);
        w.WriteNumber("end", node.EndLine);
        w.WriteEndObject();
        if (node.Target is { } target)
        {
            w.WriteString("target", target);
        }

        if (node.Thru is { } thru)
        {
            w.WriteString("thru", thru);
        }

        if (node.Type == "PERFORM")
        {
            w.WriteBoolean("conditional", node.Conditional);
        }

        if (node.Type == "CALL")
        {
            w.WriteBoolean("dynamic", node.Dynamic);
        }

        if (node.SqlText is { } sql)
        {
            w.WriteString("text", sql);
        }

        if (node.Terminates)
        {
            w.WriteBoolean("terminates", true);
        }

        w.WriteStartArray("categories");
        foreach (var category in Categories(node))
        {
            w.WriteStringValue(category);
        }

        w.WriteEndArray();
        if (children)
        {
            w.WriteStartArray("children");
            foreach (var child in node.Children)
            {
                WriteAstNode(w, child, children: true);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static IEnumerable<string> Categories(FlowNode node) => node.Type switch
    {
        "PROCEDURE_DIVISION_BODY" or "SECTION" or "PARAGRAPHS" or "PARAGRAPH" or "SENTENCE" => new[] { "CODE_BLOCK" },
        "SECTION_HEADER" or "PARAGRAPH_NAME" or "GENERIC_STATEMENT" => new[] { "GENERIC_CODE" },
        "MOVE" => new[] { "DATA_FLOW" },
        "COMPUTE" or "ADD" or "SUBTRACT" or "MULTIPLY" or "DIVIDE" => new[] { "COMPUTATIONAL", "DATA_FLOW" },
        "DIALECT" => new[] { "DIALECT" },
        "DIALECT_CONTAINER" => new[] { "TRANSACTION" },
        "IF_BRANCH" or "EVALUATE" => new[] { "DECISION" },
        "IF_YES" or "IF_NO" or "WHEN" or "WHEN_OTHER" or "HANDLER" => new[] { "DECISION_BRANCH" },
        "PERFORM" when node.Conditional => new[] { "LOOP" },
        "PERFORM" or "GOTO" or "CALL" or "STOP" or "NEXT_SENTENCE" or "ALTER" => new[] { "CONTROL_FLOW" },
        "EXIT" => new[] { "TERMINAL" },
        "DISPLAY" => new[] { "IO" },
        "SEARCH" => new[] { "SEARCH" },
        _ => new[] { "GENERIC_CODE" },
    };

    private static void WriteCfg(Utf8JsonWriter w, ProcedureFlow flow)
    {
        w.WriteStartObject();
        w.WriteStartArray("nodes");
        foreach (var node in flow.Root.SelfAndDescendants())
        {
            WriteAstNode(w, node, children: false);
        }

        w.WriteEndArray();
        w.WriteStartArray("edges");
        var n = 0;
        foreach (var edge in flow.Edges)
        {
            Drain(w);
            w.WriteStartObject();
            w.WriteString("id", "e" + n++);
            w.WriteString("fromNodeID", "n" + edge.From);
            w.WriteString("toNodeID", "n" + edge.To);
            w.WriteString("edgeType", edge.Kind switch
            {
                FlowEdgeKind.StartsWith => "STARTS_WITH",
                FlowEdgeKind.FollowedBy => "FOLLOWED_BY",
                _ => "JUMPS_TO",
            });
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("unresolved");
        foreach (var u in flow.Unresolved)
        {
            w.WriteStartObject();
            w.WriteString("name", u.Name);
            if (u.Qualifier is { } qualifier)
            {
                w.WriteString("qualifier", qualifier);
            }

            w.WriteNumber("line", u.Line);
            w.WriteString("statement", u.Statement);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteData(Utf8JsonWriter w, CompilationResult result, ProgramData? data)
    {
        var id = 0;
        w.WriteStartObject();
        w.WriteString("id", "d" + id++);
        w.WriteString("name", "[ROOT]");
        w.WriteString("content", "[ROOT]");
        w.WriteNumber("levelNumber", 0);
        w.WriteString("rawText", "[ROOT]");
        w.WriteBoolean("isRedefinition", false);
        w.WriteString("redefines", string.Empty);
        w.WriteString("nodeType", "DATA_VERTEX");
        w.WriteString("dataType", "OBJECT");
        w.WriteStartArray("categories");
        w.WriteStringValue("ROOT");
        w.WriteEndArray();
        w.WriteString("sourceSection", "ROOT");
        w.WriteStartArray("children");
        foreach (var record in data?.Records ?? Array.Empty<DataItem>())
        {
            WriteItem(w, result, record, ref id);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteItem(Utf8JsonWriter w, CompilationResult result, DataItem item, ref int id)
    {
        if (!item.HasStorage)
        {
            return;
        }

        var own = item.Entry.HeaderSpan.IsEmpty ? item.Entry.Span : item.Entry.HeaderSpan;
        var raw = SourceText.Collapse(SourceText.Of(result.Expanded.Content, own.Start, own.End));
        w.WriteStartObject();
        w.WriteString("id", "d" + id++);
        w.WriteString("name", item.DisplayName);
        w.WriteString("content", raw.Replace(" ", string.Empty, StringComparison.Ordinal));
        w.WriteNumber("levelNumber", item.Level);
        w.WriteString("rawText", raw);
        w.WriteBoolean("isRedefinition", item.RedefinesName is not null);
        w.WriteString("redefines", item.RedefinesName ?? string.Empty);
        if (item.Occurs is { } occurs)
        {
            w.WriteNumber("occurs", occurs.Max);
        }

        w.WriteString("nodeType", "DATA_VERTEX");
        w.WriteString("dataType", item.Category switch
        {
            DataCategory.Group => "OBJECT",
            DataCategory.Pointer => "POINTER",
            DataCategory.Numeric or DataCategory.InternalFloatingPoint or DataCategory.Index => "NUMBER",
            _ => "STRING",
        });
        w.WriteNumber("offset", item.Offset);
        w.WriteNumber("length", item.Length);
        w.WriteStartArray("categories");
        w.WriteStringValue(item.Section switch
        {
            DataSectionKind.File => "IO",
            DataSectionKind.Linkage => "PARAMETER",
            DataSectionKind.LocalStorage => "LOCAL",
            _ => "GLOBAL",
        });
        w.WriteEndArray();
        w.WriteString("sourceSection", item.Section switch
        {
            DataSectionKind.File => "FILE_DESCRIPTOR",
            DataSectionKind.Linkage => "LINKAGE",
            DataSectionKind.LocalStorage => "LOCAL_STORAGE",
            _ => "WORKING_STORAGE",
        });
        w.WriteStartArray("children");
        foreach (var child in item.Children)
        {
            WriteItem(w, result, child, ref id);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteDeps(Utf8JsonWriter w, CompilationResult result, ProgramDependencies deps, string basename, string relativePath, PathDisplay paths)
    {
        w.WriteStartObject();
        WriteTarget(w, basename, true);
        w.WriteNumber("dependencyExportSchemaVersion", 1);
        w.WriteString("dependencyProducer", "starforge");
        w.WriteString("dependencyKind", "PROGRAM");
        // Strict-gate evidence (coverage, runtime contracts) is not produced yet, so the export never claims completeness.
        w.WriteBoolean("dependencyAnalysisComplete", false);
        w.WriteString("sourcePath", relativePath);
        w.WriteString("sourceSha256", Sha256(result.Program.Path));
        w.WriteStartArray("dependencies");
        var seen = new HashSet<(string, string, bool)>();
        foreach (var c in deps.Copybooks)
        {
            var kind = c.Kind == DependencyKind.PlatformCopy ? "PLATFORM_COPY" : "COPY";
            if (seen.Add((kind, c.Name.ToUpperInvariant(), true)))
            {
                w.WriteStartObject();
                WriteTarget(w, c.Name, true);
                w.WriteString("dependencyKind", kind);
                w.WriteString("copyStatus", c.Status.ToString());
                if (c.ResolvedPath is { } resolved)
                {
                    w.WriteString("sourcePath", paths.Show(resolved));
                }

                w.WriteEndObject();
            }
        }

        foreach (var p in deps.Programs)
        {
            var kind = p.Kind switch
            {
                DependencyKind.CicsLink => "CICS_LINK",
                DependencyKind.CicsXctl => "CICS_XCTL",
                _ => "CALL",
            };
            if (seen.Add((kind, p.Name.ToUpperInvariant(), p.IsStatic)))
            {
                w.WriteStartObject();
                WriteTarget(w, p.Name, p.IsStatic);
                w.WriteString("dependencyKind", kind);
                if (p.ResolvedFrom is { } from)
                {
                    w.WriteString("resolvedFrom", from);
                }

                w.WriteNumber("line", p.Line);
                w.WriteEndObject();
            }
        }

        w.WriteEndArray();

        // Starforge's own inventory, under keys REKT does not use so no reader mistakes it for REKT evidence.
        w.WriteStartObject("starforgeInventory");
        w.WriteStartObject("presence");
        foreach (var (key, count) in deps.Presence)
        {
            w.WriteNumber(key, count);
        }

        w.WriteEndObject();
        w.WriteStartArray("controlTransfers");
        foreach (var t in deps.Transfers)
        {
            w.WriteStartObject();
            w.WriteString("operation", t.Operation);
            if (t.Target is { } target)
            {
                w.WriteString("target", target);
                w.WriteString("programReferenceType", t.IsStatic ? "STATIC" : "DYNAMIC");
            }

            if (t.ResolvedFrom is { } from)
            {
                w.WriteString("resolvedFrom", from);
            }

            w.WriteNumber("line", t.Line);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("resources");
        foreach (var r in deps.Resources
                     .GroupBy(r => (r.Kind, r.Name, r.IsStatic))
                     .OrderBy(g => g.Key.Kind, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Name, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("kind", r.Key.Kind);
            w.WriteString("name", r.Key.Name);
            w.WriteBoolean("literal", r.Key.IsStatic);
            w.WriteStartArray("operations");
            foreach (var op in r.Select(x => x.Operation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                w.WriteStringValue(op);
            }

            w.WriteEndArray();
            w.WriteStartArray("lines");
            foreach (var line in r.Select(x => x.Line).Distinct().Order())
            {
                w.WriteNumberValue(line);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteTarget(Utf8JsonWriter w, string name, bool isStatic)
    {
        w.WriteStartObject("callTarget");
        w.WriteString("name", name);
        w.WriteString("programReferenceType", isStatic ? "STATIC" : "DYNAMIC");
        w.WriteEndObject();
    }

    private static string Sha256(string path)
        => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : string.Empty;
}
