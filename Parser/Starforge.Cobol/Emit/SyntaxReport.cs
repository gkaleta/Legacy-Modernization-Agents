using System.Text.Json;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Emit;

// The "programs" member of the per-program JSON: division summary, special names, files, data item tree and error nodes.
public static class SyntaxReport
{
    public static void WritePrograms(Utf8JsonWriter w, CompilationResult r, PathDisplay paths)
    {
        w.WriteStartArray("programs");
        for (var i = 0; i < r.Programs.Count; i++)
        {
            WriteProgram(w, r.Programs[i], i < r.Bindings.Count ? r.Bindings[i] : ProcedureBindings.Empty, paths);
        }

        w.WriteEndArray();

        var unitErrors = r.Syntax.Errors;
        if (unitErrors.Count > 0)
        {
            w.WriteStartArray("unitErrors");
            foreach (var e in unitErrors)
            {
                WriteError(w, e, paths);
            }

            w.WriteEndArray();
        }
    }

    private static void WriteProgram(Utf8JsonWriter w, ProgramData data, ProcedureBindings bindings, PathDisplay paths)
    {
        var p = data.Program;
        w.WriteStartObject();
        w.WriteString("name", p.Name ?? string.Empty);
        if (data.Parent?.Name is { } parent)
        {
            w.WriteString("parent", parent);
        }

        if (p.Identification is { ProgramNameIsLiteral: true })
        {
            w.WriteBoolean("nameIsLiteral", true);
        }

        if (p.Identification is { Attributes: not ProgramAttributes.None } id)
        {
            w.WriteStartArray("attributes");
            foreach (var a in new[] { ProgramAttributes.Common, ProgramAttributes.Initial, ProgramAttributes.Recursive }.Where(a => id.Attributes.HasFlag(a)))
            {
                w.WriteStringValue(a.ToString().ToUpperInvariant());
            }

            w.WriteEndArray();
        }

        w.WriteString("range", paths.Show(p.Range));
        if (p.EndProgramName is not null)
        {
            w.WriteString("endProgram", p.EndProgramName);
        }

        if (p.Nested.Count > 0)
        {
            w.WriteStartArray("nested");
            foreach (var n in p.Nested)
            {
                w.WriteStringValue(n.Name ?? string.Empty);
            }

            w.WriteEndArray();
        }

        WriteDivisions(w, p, data, bindings, paths);
        WriteSpecialNames(w, p.Environment?.Configuration?.SpecialNames);
        WriteFiles(w, data, paths);
        w.WriteBoolean("slackBytesInserted", data.SlackBytesInserted);
        w.WriteStartArray("dataItems");
        foreach (var record in data.Records)
        {
            WriteItem(w, record, paths);
        }

        w.WriteEndArray();

        w.WriteStartArray("errors");
        foreach (var e in p.AllErrors().OrderBy(e => e.Span.FirstToken))
        {
            WriteError(w, e, paths);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteDivisions(Utf8JsonWriter w, ProgramUnit p, ProgramData data, ProcedureBindings bindings, PathDisplay paths)
    {
        w.WriteStartObject("divisions");
        if (p.Identification is { } id)
        {
            w.WriteStartObject("identification");
            w.WriteString("range", paths.Show(id.Range));
            w.WriteStartArray("paragraphs");
            foreach (var para in id.Paragraphs)
            {
                w.WriteStringValue(para.Name);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        if (p.Environment is { } env)
        {
            w.WriteStartObject("environment");
            w.WriteString("range", paths.Show(env.Range));
            if (env.Configuration is { } config)
            {
                w.WriteStartObject("configuration");
                if (config.SourceComputer is not null)
                {
                    w.WriteString("sourceComputer", config.SourceComputer);
                }

                if (config.DebuggingMode)
                {
                    w.WriteBoolean("debuggingMode", true);
                }

                if (config.ObjectComputer is not null)
                {
                    w.WriteString("objectComputer", config.ObjectComputer);
                }

                w.WriteEndObject();
            }

            if (env.InputOutput is { } io)
            {
                w.WriteStartObject("inputOutput");
                w.WriteNumber("selects", io.Files.Count);
                if (io.IoControl.Count > 0)
                {
                    w.WriteStartArray("ioControl");
                    foreach (var entry in io.IoControl)
                    {
                        w.WriteStringValue(entry.Kind + (entry.Operands.Count > 0 ? " " + string.Join(" ", entry.Operands) : string.Empty));
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        if (p.Data is { } dataDivision)
        {
            w.WriteStartObject("data");
            w.WriteString("range", paths.Show(dataDivision.Range));
            w.WriteStartArray("sections");
            foreach (var section in dataDivision.Sections)
            {
                w.WriteStartObject();
                w.WriteString("kind", section.Kind.ToString());
                w.WriteString("range", paths.Show(section.Range));
                if (section.Files.Count > 0)
                {
                    w.WriteNumber("files", section.Files.Count);
                }

                var records = data.Records.Where(i => i.Section == section.Kind).ToList();
                w.WriteNumber("records", records.Count);
                w.WriteNumber("items", records.Sum(i => i.SelfAndDescendants().Count()));
                if (section.Execs.Count > 0)
                {
                    w.WriteNumber("execBlocks", section.Execs.Count);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        if (p.Procedure is { } proc)
        {
            w.WriteStartObject("procedure");
            w.WriteString("range", paths.Show(proc.Range));
            if (proc.Using.Count > 0)
            {
                w.WriteStartArray("using");
                foreach (var u in proc.Using)
                {
                    w.WriteStartObject();
                    w.WriteString("name", u.Name.ToString());
                    w.WriteString("by", u.Mode.ToString().ToUpperInvariant());
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            if (proc.Returning is not null)
            {
                w.WriteString("returning", proc.Returning.ToString());
            }

            w.WriteNumber("bodyTokens", proc.Body.TokenCount);
            WriteSections(w, "declaratives", proc.Declaratives);
            WriteSections(w, "sections", proc.Sections);
            w.WriteStartObject("statements");
            foreach (var g in proc.AllStatements().GroupBy(StatementName).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                w.WriteNumber(g.Key, g.Count());
            }

            w.WriteEndObject();
            WriteReferences(w, proc, bindings);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    // How each name in the body resolved; unresolved names also appear as warnings.
    private static void WriteReferences(Utf8JsonWriter w, ProcedureDivision proc, ProcedureBindings bindings)
    {
        w.WriteStartObject("references");
        w.WriteNumber("data", proc.DataReferences.Count);
        w.WriteNumber("dataItems", proc.DataReferences.Count(d => bindings.ItemOf(d) is not null));
        foreach (var g in proc.DataReferences.Select(bindings.ImplicitKindOf).OfType<string>().GroupBy(k => k).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            w.WriteNumber(g.Key, g.Count());
        }

        w.WriteNumber("procedures", proc.ProcedureReferences.Count);
        w.WriteNumber("proceduresResolved", proc.ProcedureReferences.Count(p => bindings.TargetOf(p) is not null));
        w.WriteEndObject();
    }

    private static void WriteSections(Utf8JsonWriter w, string name, IReadOnlyList<ProcedureSection> sections)
    {
        if (sections.Count == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (var s in sections)
        {
            w.WriteStartObject();
            if (s.Name is not null)
            {
                w.WriteString("name", s.Name);
            }

            w.WriteString("lines", $"{s.StartLine}-{s.EndLine}");
            if (s.Use is not null)
            {
                w.WriteString("use", s.Use.Text);
            }

            if (s.Leading.Count > 0)
            {
                w.WriteNumber("leadingSentences", s.Leading.Count);
            }

            w.WriteStartArray("paragraphs");
            foreach (var p in s.Paragraphs)
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("lines", $"{p.StartLine}-{p.EndLine}");
                w.WriteNumber("sentences", p.Sentences.Count);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    // MoveStatement -> MOVE; FileStatement and SortStatement report their verb.
    private static string StatementName(Statement s) => s switch
    {
        FileStatement f => f.Verb,
        SortStatement f => f.Verb,
        ArithmeticStatement a => a.Verb.ToString().ToUpperInvariant(),
        ExecStatementSyntax e => "EXEC " + e.Kind.ToString().ToUpperInvariant(),
        ErrorStatement => "ERROR",
        _ => s.GetType().Name[..^"Statement".Length].ToUpperInvariant(),
    };

    private static void WriteSpecialNames(Utf8JsonWriter w, SpecialNamesParagraph? sn)
    {
        if (sn is null)
        {
            return;
        }

        w.WriteStartObject("specialNames");
        w.WriteBoolean("decimalPointIsComma", sn.DecimalPointIsComma);
        if (sn.Currencies.Count > 0)
        {
            w.WriteStartArray("currency");
            foreach (var c in sn.Currencies)
            {
                w.WriteStartObject();
                w.WriteString("sign", c.Literal);
                if (c.PictureSymbol is not null)
                {
                    w.WriteString("pictureSymbol", c.PictureSymbol);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (sn.Classes.Count > 0)
        {
            w.WriteStartArray("classes");
            foreach (var c in sn.Classes)
            {
                w.WriteStartObject();
                w.WriteString("name", c.Name);
                w.WriteStartArray("values");
                foreach (var v in c.Values)
                {
                    w.WriteStringValue(v.Thru is null ? v.From : v.From + " THRU " + v.Thru);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (sn.SymbolicCharacters.Count > 0)
        {
            w.WriteStartArray("symbolicCharacters");
            foreach (var s in sn.SymbolicCharacters)
            {
                w.WriteStartObject();
                w.WriteString("name", s.Name);
                w.WriteNumber("ordinal", s.Ordinal);
                if (s.Alphabet is not null)
                {
                    w.WriteString("alphabet", s.Alphabet);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (sn.Mnemonics.Count > 0)
        {
            w.WriteStartArray("mnemonics");
            foreach (var m in sn.Mnemonics)
            {
                w.WriteStartObject();
                w.WriteString("environmentName", m.EnvironmentName);
                if (m.Mnemonic is not null)
                {
                    w.WriteString("mnemonic", m.Mnemonic);
                }

                if (m.OnStatus is not null)
                {
                    w.WriteString("onStatus", m.OnStatus);
                }

                if (m.OffStatus is not null)
                {
                    w.WriteString("offStatus", m.OffStatus);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (sn.Alphabets.Count > 0)
        {
            w.WriteStartArray("alphabets");
            foreach (var a in sn.Alphabets)
            {
                w.WriteStartObject();
                w.WriteString("name", a.Name);
                w.WriteString("specification", a.Specification);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteFiles(Utf8JsonWriter w, ProgramData data, PathDisplay paths)
    {
        w.WriteStartArray("files");
        foreach (var f in data.Files)
        {
            w.WriteStartObject();
            w.WriteString("name", f.Name);
            if (f.Select is { } s)
            {
                w.WriteStartObject("select");
                w.WriteString("range", paths.Show(s.Range));
                if (s.Optional)
                {
                    w.WriteBoolean("optional", true);
                }

                w.WriteStartArray("assign");
                foreach (var a in s.Assign)
                {
                    w.WriteStringValue(a);
                }

                w.WriteEndArray();
                WriteOptional(w, "organization", s.Organization);
                WriteOptional(w, "accessMode", s.AccessMode);
                WriteOptional(w, "recordKey", s.RecordKey?.ToString());
                if (s.AlternateKeys.Count > 0)
                {
                    w.WriteStartArray("alternateKeys");
                    foreach (var k in s.AlternateKeys)
                    {
                        w.WriteStartObject();
                        w.WriteString("key", k.Key.ToString());
                        w.WriteBoolean("duplicates", k.WithDuplicates);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }

                WriteOptional(w, "relativeKey", s.RelativeKey?.ToString());
                WriteOptional(w, "fileStatus", s.FileStatus?.ToString());
                WriteOptional(w, "vsamStatus", s.VsamStatus?.ToString());
                w.WriteEndObject();
            }

            if (f.Description is { } fd)
            {
                w.WriteStartObject("fd");
                w.WriteString("type", fd.IsSort ? "SD" : "FD");
                w.WriteString("range", paths.Show(fd.Range));
                if (fd.External)
                {
                    w.WriteBoolean("external", true);
                }

                if (fd.Global)
                {
                    w.WriteBoolean("global", true);
                }

                WriteOptional(w, "recordingMode", fd.RecordingMode);
                if (fd.Block is { } b)
                {
                    w.WriteStartObject("blockContains");
                    if (b.Min is int bmin)
                    {
                        w.WriteNumber("min", bmin);
                    }

                    w.WriteNumber("max", b.Max);
                    w.WriteString("unit", b.Records ? "RECORDS" : "CHARACTERS");
                    w.WriteEndObject();
                }

                if (fd.Record is { } rc)
                {
                    w.WriteStartObject("record");
                    if (rc.Varying)
                    {
                        w.WriteBoolean("varying", true);
                    }

                    if (rc.Min is int rmin)
                    {
                        w.WriteNumber("min", rmin);
                    }

                    if (rc.Max is int rmax)
                    {
                        w.WriteNumber("max", rmax);
                    }

                    WriteOptional(w, "dependingOn", rc.DependingOn?.ToString());
                    w.WriteEndObject();
                }

                WriteStrings(w, "labelRecords", fd.LabelRecords);
                WriteStrings(w, "dataRecords", fd.DataRecords);
                if (fd.ValueOf.Count > 0)
                {
                    w.WriteStartObject("valueOf");
                    foreach (var kv in fd.ValueOf)
                    {
                        w.WriteString(kv.Key, kv.Value);
                    }

                    w.WriteEndObject();
                }

                WriteOptional(w, "codeSet", fd.CodeSet);
                w.WriteEndObject();
            }

            w.WriteStartArray("records");
            foreach (var rec in f.Records)
            {
                w.WriteStringValue(rec.DisplayName);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteItem(Utf8JsonWriter w, DataItem item, PathDisplay paths)
    {
        var e = item.Entry;
        w.WriteStartObject();
        w.WriteNumber("level", item.Level);
        w.WriteString("name", item.DisplayName);
        w.WriteString("path", item.Path);
        if (item.Parent is null)
        {
            w.WriteString("section", item.Section.ToString());
            WriteOptional(w, "file", item.FileName);
        }

        w.WriteString("category", item.Category.ToString());
        if (item.HasStorage)
        {
            w.WriteString("usage", item.Usage.ToString());
        }

        WriteOptional(w, "pic", item.Picture);
        if (e.Picture is { } pic && pic.Info.Category is PictureCategory.Numeric or PictureCategory.NumericEdited)
        {
            w.WriteNumber("digits", item.Digits);
            if (item.Scale != 0)
            {
                w.WriteNumber("scale", item.Scale);
            }

            if (item.Signed)
            {
                w.WriteBoolean("signed", true);
            }
        }

        if (item.SignSeparate || item.SignLeading)
        {
            w.WriteString("sign", (item.SignLeading ? "LEADING" : "TRAILING") + (item.SignSeparate ? " SEPARATE" : string.Empty));
        }

        WriteOptional(w, "value", item.Value);
        if (item.RedefinesName is not null)
        {
            w.WriteString("redefines", item.Redefines?.Path ?? item.RedefinesName);
        }

        if (e.Renames is { } ren)
        {
            w.WriteString("renames", ren.Thru is null ? ren.From.ToString() : ren.From + " THRU " + ren.Thru);
        }

        if (item.Occurs is { } o)
        {
            w.WriteStartObject("occurs");
            w.WriteNumber("min", o.Min);
            w.WriteNumber("max", o.Max);
            WriteOptional(w, "dependingOn", o.DependingOn?.ToString());
            if (o.Keys.Count > 0)
            {
                w.WriteStartArray("keys");
                foreach (var k in o.Keys)
                {
                    w.WriteStringValue((k.Ascending ? "ASCENDING " : "DESCENDING ") + k.Name);
                }

                w.WriteEndArray();
            }

            WriteStrings(w, "indexedBy", o.IndexedBy);
            w.WriteEndObject();
        }

        w.WriteNumber("offset", item.Offset);
        w.WriteNumber("length", item.Length);
        if (item.Occurs is not null)
        {
            w.WriteNumber("totalLength", item.TotalLength);
        }

        if (item.Synchronized)
        {
            w.WriteBoolean("sync", true);
        }

        if (item.SlackBefore > 0)
        {
            w.WriteNumber("slackBefore", item.SlackBefore);
        }

        if (item.OccurrenceSlack > 0)
        {
            w.WriteNumber("occurrenceSlack", item.OccurrenceSlack);
        }

        if (e.Justified)
        {
            w.WriteBoolean("justified", true);
        }

        if (e.BlankWhenZero)
        {
            w.WriteBoolean("blankWhenZero", true);
        }

        if (item.Parent is null && item.Global)
        {
            w.WriteBoolean("global", true);
        }

        if (item.Parent is null && item.External)
        {
            w.WriteBoolean("external", true);
        }

        w.WriteString("sourceRange", paths.Show(item.Range));
        WriteOptional(w, "fromCopybook", item.FromCopybook);
        if (item.Children.Count > 0)
        {
            w.WriteStartArray("children");
            foreach (var c in item.Children)
            {
                WriteItem(w, c, paths);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteError(Utf8JsonWriter w, ErrorNode e, PathDisplay paths)
    {
        w.WriteStartObject();
        w.WriteString("code", e.Diagnostic.Code);
        w.WriteString("fingerprint", e.Diagnostic.Fingerprint);
        w.WriteString("message", e.Diagnostic.Message);
        w.WriteString("range", paths.Show(e.Range));
        w.WriteNumber("tokens", e.Span.TokenCount);
        w.WriteEndObject();
    }

    private static void WriteOptional(Utf8JsonWriter w, string name, string? value)
    {
        if (value is not null)
        {
            w.WriteString(name, value);
        }
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }
}
