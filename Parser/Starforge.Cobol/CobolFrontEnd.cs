using System.Text.RegularExpressions;
using Starforge.Cobol.Copy;
using Starforge.Cobol.Coverage;
using Starforge.Cobol.Lexing;
using Starforge.Cobol.Semantics;
using Starforge.Cobol.Source;
using Starforge.Cobol.Syntax;

namespace Starforge.Cobol;

// Null switches are detected from the source (WITH DEBUGGING MODE, DECIMAL-POINT IS COMMA).
public sealed record FrontEndOptions(
    IReadOnlyList<string> EstateRoots,
    bool? DebuggingMode = null,
    bool? DecimalPointIsComma = null,
    int MaxCopyDepth = 50,
    SourceFormat? Format = null,
    Dialect.DialectRules? Rules = null);

public sealed class CompilationResult
{
    internal CompilationResult(
        SourceUnit program,
        IReadOnlyList<SourceUnit> units,
        IReadOnlyList<CopyInclusion> inclusions,
        ExpandedSource expanded,
        TokenStream tokens,
        IReadOnlyList<Diagnostic> diagnostics,
        TokenAccounting accounting,
        CoverageReport coverage,
        bool debuggingMode,
        bool decimalPointIsComma,
        CompilationUnitSyntax syntax,
        IReadOnlyList<ProgramData> programs,
        IReadOnlyDictionary<int, Exec.ExecStatement> parsedExecs)
    {
        ParsedExecs = parsedExecs;
        Syntax = syntax;
        Programs = programs;
        Program = program;
        Units = units;
        Inclusions = inclusions;
        Expanded = expanded;
        Tokens = tokens;
        Diagnostics = diagnostics;
        Accounting = accounting;
        Coverage = coverage;
        DebuggingMode = debuggingMode;
        DecimalPointIsComma = decimalPointIsComma;
    }

    public SourceUnit Program { get; }

    public IReadOnlyList<SourceUnit> Units { get; }

    public IReadOnlyList<CopyInclusion> Inclusions { get; }

    public ExpandedSource Expanded { get; }

    public TokenStream Tokens { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    // Later stages promote Lexed characters to Parsed here and call RebuildCoverage().
    public TokenAccounting Accounting { get; }

    public CoverageReport Coverage { get; private set; }

    public bool DebuggingMode { get; }

    public CompilationUnitSyntax Syntax { get; }

    // EXEC blocks the procedure parser parsed, by token index; ExecPass reuses them instead of parsing twice.
    public IReadOnlyDictionary<int, Exec.ExecStatement> ParsedExecs { get; }

    // Data layout per program, in Syntax.AllPrograms() order.
    public IReadOnlyList<ProgramData> Programs { get; }

    // Procedure name bindings per program, aligned with Programs.
    public IReadOnlyList<ProcedureBindings> Bindings { get; internal set; } = Array.Empty<ProcedureBindings>();

    public bool DecimalPointIsComma { get; }

    public int PhysicalLines => Program.Source.File.Lines.Count;

    // Physical lines of the program plus every copybook instance, i.e. what a listing of the expanded source would show.
    public int ExpandedPhysicalLines => Units.Sum(u => u.Source.File.Lines.Count);

    public int ExpandedCodeLines => Units.Sum(u => u.Source.Lines.Count(l => l.Kind is SourceLineKind.Code or SourceLineKind.Continuation));

    public CoverageReport RebuildCoverage() => Coverage = Accounting.BuildReport();

    // The taught rules this program was read with, and where they applied.
    public Dialect.DialectRules Rules { get; internal set; } = Dialect.DialectRules.Empty;

    public IReadOnlyList<Dialect.TaughtUse> TaughtUses { get; internal set; } = Array.Empty<Dialect.TaughtUse>();

    // Text Starforge read past without understanding it; compile-time checks such as invalid-picture are not listed.
    private static readonly HashSet<string> UnreadableCodes = new(StringComparer.Ordinal)
    {
        "unterminated-literal", "unexpected-character", "continuation-missing-quote", "exec-without-end-exec",
        "exec-malformed-block", "exec-unknown-kind", "malformed-copy-statement", "malformed-replace-statement",
        "copy-prefix-unsupported", "parser-gap",
    };

    // Source text Starforge could not read: parser error nodes and errors over unread text, one per code and place.
    // Semantic findings (unresolved names, layout notes, missing copybooks) are reported elsewhere.
    public IReadOnlyList<Diagnostic> UnreadableSource()
        => Syntax.Errors.Concat(Syntax.AllPrograms().SelectMany(p => p.AllErrors())).Select(e => e.Diagnostic)
            .Concat(Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && UnreadableCodes.Contains(d.Code)))
            .Where(d => d.Range is not null)
            .DistinctBy(d => (d.Code, d.Range!.Value.File, d.Range.Value.StartLine))
            .OrderBy(d => d.Range!.Value.File, StringComparer.Ordinal).ThenBy(d => d.Range!.Value.StartLine).ThenBy(d => d.Code, StringComparer.Ordinal)
            .ToList();

    // Characters of the given class inside PROCEDURE DIVISION bodies.
    public long CountInProcedureBodies(CoverageClass cls)
        => Syntax.AllPrograms()
            .Select(p => p.Procedure?.Body)
            .Where(b => b is { IsEmpty: false })
            .Sum(b => Accounting.CountExpanded(Expanded.Text, b!.Value.Start, b.Value.End, cls));

    // Later passes (EXEC, statement parsers) report here so one list holds every diagnostic of the program.
    public void AddDiagnostics(IEnumerable<Diagnostic> diagnostics) => ((List<Diagnostic>)Diagnostics).AddRange(diagnostics);
}

// Thread-safe: one instance can process many programs in parallel and shares the copybook cache between them.
public sealed class CobolFrontEnd
{
    private static readonly Regex DecimalPointComma = new(@"\bDECIMAL-POINT\s+(IS\s+)?COMMA\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly FrontEndOptions _options;
    private readonly CopybookResolver _resolver;
    private readonly SourceCache _cache = new();

    public CobolFrontEnd(FrontEndOptions options)
    {
        _options = options;
        _resolver = new CopybookResolver(options.EstateRoots);
    }

    public CopybookResolver Resolver => _resolver;

    public CompilationResult Process(string programPath) => Process(SourceFile.Load(Path.GetFullPath(programPath)));

    public CompilationResult Process(SourceFile file)
    {
        var debug = _options.DebuggingMode ?? SourceReader.DeclaresDebuggingMode(file);
        var logical = SourceReader.Read(file, new SourceReaderOptions(debug, IsProgram: true, _options.Format));
        var accounting = new TokenAccounting();
        var program = new SourceUnit(0, SourceUnitKind.Program, logical, -1, null, null, null, 0, false);
        accounting.Register(program);

        var diagnostics = new List<Diagnostic>(logical.Diagnostics);
        var inclusions = new List<CopyInclusion>();
        var expander = new CopyExpander(_resolver, _cache, accounting, diagnostics, inclusions, new CopyExpansionOptions(_options.MaxCopyDepth, debug, _options.Rules));
        var copied = expander.ExpandProgram(program);
        var afterReplace = ReplaceStage.Apply(copied, new ExpandedSource(copied, accounting.Units), accounting, diagnostics);
        var expanded = new ExpandedSource(afterReplace, accounting.Units);

        var comma = _options.DecimalPointIsComma ?? DecimalPointComma.IsMatch(expanded.Content);
        var lex = Lexer.Lex(expanded, new LexerOptions(comma));
        diagnostics.AddRange(lex.Diagnostics);
        MarkTokens(lex.Tokens, accounting);

        var parse = CobolParser.Parse(lex.Tokens, new ParserOptions(comma, inclusions.Any(i => i.Idms), _options.Rules), accounting);
        diagnostics.AddRange(parse.Diagnostics);
        var programs = DataLayout.Compute(parse.Syntax, accounting.Units);
        diagnostics.AddRange(programs.SelectMany(p => p.Diagnostics));

        var result = new CompilationResult(program, accounting.Units, inclusions, expanded, lex.Tokens, diagnostics, accounting, accounting.BuildReport(), debug, comma, parse.Syntax, programs, parse.ParsedExecs);
        result.Bindings = ProcedureResolver.Resolve(result, diagnostics);
        result.Rules = _options.Rules ?? Dialect.DialectRules.Empty;
        result.TaughtUses = expander.TaughtUses.Concat(parse.TaughtUses).ToList();
        return result;
    }

    private static void MarkTokens(TokenStream tokens, TokenAccounting accounting)
    {
        var text = tokens.Source.Text;
        foreach (var token in tokens)
        {
            var (cls, reason) = token.Kind switch
            {
                TokenKind.ExecBlock => (CoverageClass.Unsupported, "exec-not-yet-parsed"),
                TokenKind.Unknown => (CoverageClass.Unsupported, "lex-error"),
                TokenKind.CompilerDirective => (CoverageClass.Directive, "trace-statement"),
                TokenKind.CommentEntry => (CoverageClass.Comment, "comment-entry"),
                _ when (token.Flags & TokenFlags.Unterminated) != 0 => (CoverageClass.Unsupported, "lex-error"),
                _ => (CoverageClass.Lexed, string.Empty),
            };
            accounting.MarkExpanded(text, token.Start, token.End, cls, reason);
        }
    }
}
