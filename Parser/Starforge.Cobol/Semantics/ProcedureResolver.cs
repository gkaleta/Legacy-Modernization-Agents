using Starforge.Cobol.Lexing;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol.Semantics;

// What the names in one program's PROCEDURE DIVISION refer to. Keys are compared by reference: the same name
// written twice is two distinct references.
public sealed class ProcedureBindings
{
    public static readonly ProcedureBindings Empty = new();

    internal Dictionary<DataRef, DataItem> Data { get; } = new(ReferenceEqualityComparer.Instance);

    internal Dictionary<ProcedureRef, SyntaxNode> Procedures { get; } = new(ReferenceEqualityComparer.Instance);

    // Index names, files, special registers and other names that are valid but have no data item.
    internal Dictionary<DataRef, string> Implicit { get; } = new(ReferenceEqualityComparer.Instance);

    public DataItem? ItemOf(DataRef reference) => Data.GetValueOrDefault(reference);

    // A Paragraph or ProcedureSection, or null when the name did not resolve.
    public SyntaxNode? TargetOf(ProcedureRef reference) => Procedures.GetValueOrDefault(reference);

    // "index", "file", "special-register", "cics", "dli" or "mnemonic" for names that resolve without a data item.
    public string? ImplicitKindOf(DataRef reference) => Implicit.GetValueOrDefault(reference);
}

// Resolves data and procedure names of each program; names that do not resolve, or resolve to more than one
// candidate, become warnings so nothing is silently dropped.
public static class ProcedureResolver
{
    private static readonly HashSet<string> SpecialRegisters = new(StringComparer.OrdinalIgnoreCase)
    {
        "RETURN-CODE", "SORT-RETURN", "SORT-CONTROL", "SORT-CORE-SIZE", "SORT-FILE-SIZE", "SORT-MESSAGE", "SORT-MODE-SIZE",
        "TALLY", "WHEN-COMPILED", "DEBUG-ITEM", "SHIFT-IN", "SHIFT-OUT", "LINAGE-COUNTER", "XML-CODE", "XML-EVENT",
        "XML-INFORMATION", "XML-NAMESPACE", "XML-NAMESPACE-PREFIX", "XML-NNAMESPACE", "XML-NNAMESPACE-PREFIX", "XML-NTEXT",
        "XML-TEXT", "JSON-CODE", "JSON-STATUS", "JNIENVPTR",
    };

    // CICS translator-supplied names (EIB fields are matched by prefix).
    private static readonly HashSet<string> CicsNames = new(StringComparer.OrdinalIgnoreCase) { "DFHEIBLK", "DFHCOMMAREA", "DFHEIBPTR" };

    public static IReadOnlyList<ProcedureBindings> Resolve(CompilationResult result, List<Diagnostic> diagnostics)
    {
        var hasCics = result.Tokens.Any(t => t.Exec == ExecKind.Cics);
        var hasDli = result.Tokens.Any(t => t.Exec == ExecKind.Dli);
        var list = new List<ProcedureBindings>();
        foreach (var program in result.Programs)
        {
            list.Add(program.Program.Procedure is { } proc ? ResolveProgram(program, proc, hasCics, hasDli, diagnostics) : ProcedureBindings.Empty);
        }

        return list;
    }

    private static ProcedureBindings ResolveProgram(ProgramData program, ProcedureDivision proc, bool hasCics, bool hasDli, List<Diagnostic> diagnostics)
    {
        var bindings = new ProcedureBindings();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mnemonics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var p = program; p is not null; p = p.Parent)
        {
            files.UnionWith(p.Files.Select(f => f.Name).Where(n => n.Length > 0));
            foreach (var m in p.Program.Environment?.Configuration?.SpecialNames?.Mnemonics ?? Array.Empty<MnemonicName>())
            {
                mnemonics.UnionWith(new[] { m.Mnemonic, m.OnStatus, m.OffStatus }.OfType<string>());
            }
        }

        foreach (var r in proc.DataReferences)
        {
            var name = r.Name.Name;
            if (r.Kind == RefKind.File || (r.Name.Qualifiers.Count == 0 && files.Contains(name) && r.Kind != RefKind.Data))
            {
                if (files.Contains(name))
                {
                    bindings.Implicit[r] = "file";
                }
                else
                {
                    Warn(diagnostics, "unresolved-file-name", $"File {name} is not declared", r);
                }

                continue;
            }

            var lookup = program.Symbols.Resolve(r.Name);
            if (lookup.Status == LookupStatus.Found)
            {
                bindings.Data[r] = lookup.Item!;
                continue;
            }

            if (lookup.Status == LookupStatus.Ambiguous)
            {
                var paths = string.Join(", ", lookup.Candidates.Select(c => c.Path).Take(4));
                Warn(diagnostics, "ambiguous-data-name", $"{r.Name} is ambiguous ({lookup.Candidates.Count} candidates: {paths})", r);
                continue;
            }

            var kind = ImplicitKind(program, r.Name, files, mnemonics, hasCics, hasDli);
            if (kind is not null)
            {
                bindings.Implicit[r] = kind;
                continue;
            }

            Warn(diagnostics, "unresolved-data-name", $"{r.Name} is not defined", r);
        }

        ResolveProcedures(proc, bindings, diagnostics);
        return bindings;
    }

    private static string? ImplicitKind(ProgramData program, QualifiedName name, HashSet<string> files, HashSet<string> mnemonics, bool hasCics, bool hasDli)
    {
        if (name.Qualifiers.Count > 0)
        {
            return null;
        }

        var n = name.Name;
        for (var p = program; p is not null; p = p.Parent)
        {
            if (p.Symbols.IndexOwner(n) is not null)
            {
                return "index";
            }
        }

        if (SpecialRegisters.Contains(n))
        {
            return "special-register";
        }

        if (files.Contains(n))
        {
            return "file";
        }

        if (mnemonics.Contains(n))
        {
            return "mnemonic";
        }

        if (hasCics && (CicsNames.Contains(n) || n.StartsWith("EIB", StringComparison.OrdinalIgnoreCase)))
        {
            return "cics";
        }

        if (hasDli && n.StartsWith("DIB", StringComparison.OrdinalIgnoreCase))
        {
            return "dli";
        }

        return null;
    }

    private static void ResolveProcedures(ProcedureDivision proc, ProcedureBindings bindings, List<Diagnostic> diagnostics)
    {
        var sections = new Dictionary<string, List<ProcedureSection>>(StringComparer.OrdinalIgnoreCase);
        var paragraphs = new Dictionary<string, List<(ProcedureSection Section, Paragraph Paragraph)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in proc.AllSections())
        {
            if (s.Name is not null)
            {
                Add(sections, s.Name, s);
            }

            foreach (var p in s.Paragraphs)
            {
                Add(paragraphs, p.Name, (s, p));
            }
        }

        foreach (var r in proc.ProcedureReferences)
        {
            var paras = paragraphs.GetValueOrDefault(r.Name) ?? new();
            if (r.Qualifier is not null)
            {
                paras = paras.Where(p => string.Equals(p.Section.Name, r.Qualifier, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (paras.Count > 1)
            {
                // An unqualified paragraph name that is not unique means the one in the referencing section.
                var local = paras.Where(p => p.Section.Name is not null && string.Equals(p.Section.Name, r.FromSection, StringComparison.OrdinalIgnoreCase)).ToList();
                if (local.Count == 1)
                {
                    paras = local;
                }
            }

            var secs = r.Qualifier is null ? sections.GetValueOrDefault(r.Name) ?? new() : new();
            var count = paras.Count + secs.Count;
            if (count == 1)
            {
                bindings.Procedures[r] = paras.Count == 1 ? paras[0].Paragraph : secs[0];
            }
            else if (count == 0)
            {
                var name = r.Qualifier is null ? r.Name : $"{r.Name} OF {r.Qualifier}";
                Warn(diagnostics, "unresolved-procedure-name", $"Procedure {name} is not defined", r.Range, r.Name);
            }
            else
            {
                Warn(diagnostics, "ambiguous-procedure-name", $"Procedure {r.Name} names {count} paragraphs or sections", r.Range, r.Name);
            }
        }
    }

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<T>();
            map[key] = list;
        }

        list.Add(value);
    }

    private static void Warn(List<Diagnostic> diagnostics, string code, string message, DataRef r)
        => Warn(diagnostics, code, message, r.Range, r.Name.Name);

    // The name is the fingerprint shape so one undefined copybook field clusters across programs.
    private static void Warn(List<Diagnostic> diagnostics, string code, string message, Source.SourceRange range, string name)
        => diagnostics.Add(Diagnostic.Create(code, DiagnosticSeverity.Warning, message, range, DiagnosticShape.Normalize(name)));
}
