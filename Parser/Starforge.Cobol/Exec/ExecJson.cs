using System.Text.Json;
using Starforge.Cobol.Emit;
using Starforge.Cobol.Lexing;

namespace Starforge.Cobol.Exec;

public static class ExecJson
{
    public static string KindName(ExecKind kind) => kind switch
    {
        ExecKind.Cics => "CICS",
        ExecKind.Sql => "SQL",
        ExecKind.SqlIms => "SQLIMS",
        ExecKind.Dli => "DLI",
        _ => "OTHER",
    };

    // Writes "execSummary" and the "exec" array as properties of the enclosing program object.
    public static void Write(Utf8JsonWriter w, ExecPassResult exec, PathDisplay paths)
    {
        w.WriteStartObject("execSummary");
        w.WriteNumber("total", exec.Statements.Count);
        w.WriteNumber("parsed", exec.Parsed);
        w.WriteNumber("unsupported", exec.Unsupported);
        foreach (var g in exec.Statements.GroupBy(s => KindName(s.Kind)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            w.WriteStartObject(g.Key);
            w.WriteNumber("total", g.Count());
            w.WriteNumber("parsed", g.Count(s => s.Status == ExecStatus.Parsed));
            w.WriteNumber("unsupported", g.Count(s => s.Status == ExecStatus.Unsupported));
            w.WriteEndObject();
        }

        w.WriteEndObject();

        w.WriteStartArray("exec");
        foreach (var s in exec.Statements)
        {
            WriteStatement(w, s, paths);
        }

        w.WriteEndArray();
    }

    public static void WriteStatement(Utf8JsonWriter w, ExecStatement s, PathDisplay paths)
    {
        w.WriteStartObject();
        w.WriteString("kind", KindName(s.Kind));
        w.WriteString("status", s.Status.ToString());
        w.WriteString("operation", s.Operation);
        w.WriteString("range", paths.Show(s.Range));
        if (s.UnsupportedReason is not null)
        {
            w.WriteString("unsupportedReason", s.UnsupportedReason);
        }

        switch (s)
        {
            case CicsCommand c:
                WriteOptions(w, "options", c.Options);
                break;
            case SqlStatement q:
                WriteSql(w, q);
                break;
            case DliCommand d:
                WriteDli(w, d);
                break;
        }

        if (s.Resources.Count > 0)
        {
            w.WriteStartArray("resources");
            foreach (var r in s.Resources)
            {
                w.WriteStartObject();
                w.WriteString("kind", r.Kind.ToString());
                w.WriteString("option", r.Option);
                WriteTarget(w, r.Target);
                if (r.Qualifier is { } q)
                {
                    w.WriteStartObject("qualifier");
                    WriteTarget(w, q);
                    if (r.QualifierImplied)
                    {
                        w.WriteBoolean("implied", true);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (s.Diagnostics.Count > 0)
        {
            w.WriteStartArray("diagnostics");
            foreach (var d in s.Diagnostics)
            {
                w.WriteStartObject();
                w.WriteString("code", d.Code);
                w.WriteString("severity", d.Severity.ToString());
                w.WriteString("fingerprint", d.Fingerprint);
                w.WriteString("message", d.Message);
                if (d.Range is { } range)
                {
                    w.WriteString("range", paths.Show(range));
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteSql(Utf8JsonWriter w, SqlStatement q)
    {
        if (q.StatementKind == SqlStatementKind.Unknown && q.Leading.Length > 0)
        {
            w.WriteString("leading", q.Leading);
        }

        if (q.Tables.Count > 0)
        {
            w.WriteStartArray("tables");
            foreach (var t in q.Tables)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                if (t.Schema is not null)
                {
                    w.WriteString("schema", t.Schema);
                }

                w.WriteString("table", t.Table);
                if (t.Alias is not null)
                {
                    w.WriteString("alias", t.Alias);
                }

                w.WriteString("operation", t.Operation.ToString().ToUpperInvariant());
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        WriteStrings(w, "ctes", q.CommonTableExpressions);
        if (q.Cursor is not null)
        {
            w.WriteString("cursor", q.Cursor);
        }

        if (q.WhereCurrentOf)
        {
            w.WriteBoolean("whereCurrentOf", true);
        }

        WriteStrings(w, "cursorAttributes", q.CursorAttributes);
        if (q.StatementName is not null)
        {
            w.WriteString("statementName", q.StatementName);
        }

        if (q.Target is not null)
        {
            w.WriteString("target", q.Target);
        }

        if (q.HostVariables.Count > 0)
        {
            w.WriteStartArray("hostVariables");
            foreach (var h in q.HostVariables)
            {
                w.WriteStartObject();
                w.WriteString("name", h.Name);
                if (h.Qualifier is not null)
                {
                    w.WriteString("qualifier", h.Qualifier);
                }

                if (h.Indicator is not null)
                {
                    w.WriteString("indicator", h.Indicator);
                }

                w.WriteString("role", h.Role.ToString());
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }
    }

    private static void WriteDli(Utf8JsonWriter w, DliCommand d)
    {
        if (d.Pcb is { } pcb)
        {
            w.WriteStartObject("pcb");
            WriteArgument(w, pcb);
            w.WriteEndObject();
        }

        if (d.Psb is { } psb)
        {
            w.WriteStartObject("psb");
            WriteTarget(w, psb);
            w.WriteEndObject();
        }

        WriteOptions(w, "options", d.Options);
        if (d.Segments.Count > 0)
        {
            w.WriteStartArray("segments");
            foreach (var seg in d.Segments)
            {
                w.WriteStartObject();
                WriteTarget(w, seg.Name);
                WriteOptions(w, "options", seg.Options);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }
    }

    private static void WriteOptions(Utf8JsonWriter w, string property, IReadOnlyList<ExecOption> options)
    {
        if (options.Count == 0)
        {
            return;
        }

        w.WriteStartArray(property);
        foreach (var o in options)
        {
            w.WriteStartObject();
            w.WriteString("name", o.Name);
            if (o.Argument is { } a)
            {
                WriteArgument(w, a);
            }

            if (!o.Known)
            {
                w.WriteBoolean("known", false);
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteArgument(Utf8JsonWriter w, ExecArgument a)
    {
        w.WriteString("argumentKind", a.Kind.ToString());
        w.WriteString("argument", a.Text);
        if (a.Value is not null && a.Kind == ExecArgumentKind.Literal)
        {
            w.WriteString("value", a.Value);
        }

        if (a.DataName is not null)
        {
            w.WriteString("dataName", a.DataName);
        }
    }

    private static void WriteTarget(Utf8JsonWriter w, ExecTarget t)
    {
        w.WriteString("name", t.Name);
        w.WriteBoolean("static", t.IsStatic);
        if (t.DataName is not null)
        {
            w.WriteString("dataName", t.DataName);
        }
    }

    private static void WriteStrings(Utf8JsonWriter w, string property, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        w.WriteStartArray(property);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }
}
