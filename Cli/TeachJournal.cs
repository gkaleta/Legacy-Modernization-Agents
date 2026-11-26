using System.Text.Json;
using System.Text.Json.Nodes;
using Starforge.Cobol.Dialect;

namespace CobolToQuarkusMigration.Cli;

// Local record of one teach-parser run (output/teach-parser/<id>/teach.json) for the portal's AI Loop tab. It holds
// what the terminal showed: the pseudonymised text sent, the AI answer, the check and the person's choices. It stays
// under output/ like every other scan result. A journal that cannot be written never stops the run.
public sealed class TeachJournal
{
    public const string ActivityKind = "teach-parser";
    public const string FileName = "teach.json";
    public const string Folder = "output/teach-parser";
    private const int MaxText = 8000;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly JsonObject _doc;
    private readonly TextWriter? _warnings;
    private JsonObject? _group;
    private bool _broken;

    private TeachJournal(string path, JsonObject doc, TextWriter? warnings)
    {
        FilePath = path;
        _doc = doc;
        _warnings = warnings;
    }

    public string FilePath { get; }

    public static TeachJournal? Start(string root, IReadOnlyList<string> sources, string rulesPath, TeachScan before,
        bool automatic = false, TextWriter? warnings = null)
    {
        var now = DateTime.UtcNow;
        var name = $"teach-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        try
        {
            var folder = Path.Join(root, name);
            Directory.CreateDirectory(folder);
            var doc = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["activityKind"] = ActivityKind,
                ["name"] = name,
                ["status"] = "running",
                ["mode"] = automatic ? "automatic" : "review",
                ["startedAt"] = Stamp(now),
                ["updatedAt"] = Stamp(now),
                ["sources"] = new JsonArray(sources.Select(s => (JsonNode)Path.GetFileName(s.TrimEnd('/', '\\'))).ToArray()),
                ["rulesFile"] = rulesPath,
                ["before"] = Counts(before),
                ["groups"] = new JsonArray(),
                ["savedRules"] = new JsonArray(),
            };
            var journal = new TeachJournal(Path.Join(folder, FileName), doc, warnings);
            journal.Write();
            return journal._broken ? null : journal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings?.WriteLine($"   (The portal will not show this run: {ex.Message})");
            return null;
        }
    }

    public void Endpoint(TeachAiEndpoint ai)
    {
        _doc["endpoint"] = new JsonObject { ["service"] = ai.Service, ["endpoint"] = ai.Endpoint, ["model"] = ai.Model };
        Write();
    }

    public void Group(TeachGroup group, string label, string meaning, int programsMissingCopybooks, string prompt)
    {
        var first = group.Sites[0];
        _group = new JsonObject
        {
            ["label"] = label,
            ["code"] = group.Code,
            ["meaning"] = meaning,
            ["places"] = group.Places,
            ["programs"] = group.Programs.Count,
            ["firstPlace"] = new JsonObject { ["file"] = first.File, ["line"] = first.Line },
            ["programsMissingCopybooks"] = programsMissingCopybooks,
            ["prompt"] = Bounded(prompt),
            ["decision"] = "asked",
            ["saved"] = false,
        };
        _doc["groups"]!.AsArray().Add(_group);
        Write();
    }

    public void Decision(string decision) => Set("decision", decision);

    public void Answer(TeachAnswer answer, string raw)
    {
        if (_group is null)
        {
            return;
        }

        _group["category"] = answer.Category;
        _group["explanation"] = answer.Explanation;
        _group["answer"] = Bounded(raw);
        Write();
    }

    public void AnswerFailed(string message) => Set("answerError", message);

    public void Rule(DialectRule rule, string description)
    {
        if (_group is null)
        {
            return;
        }

        _group["ruleId"] = rule.Id;
        _group["rule"] = description;
        _group["ruleJson"] = DialectRules.Create([rule]).ToJson().Trim();
        Write();
    }

    public void Verified(TeachVerification check, int places)
    {
        if (_group is null)
        {
            return;
        }

        static string? First(TeachSite? s) => s is null ? null : $"{s.File} line {s.Line} ({TeachParser.Meaning(s.Code)})";
        _group["check"] = new JsonObject
        {
            ["fixed"] = check.Fixed,
            ["places"] = places,
            ["excluded"] = check.Excluded,
            ["remaining"] = check.Remaining.Count,
            ["newProblems"] = check.NewSites.Count,
            ["changedElsewhere"] = check.UsesElsewhere.Count,
            ["passed"] = check.Passed,
            ["firstRemaining"] = First(check.Remaining.FirstOrDefault()),
            ["firstNew"] = First(check.NewSites.FirstOrDefault()),
            ["firstChanged"] = check.UsesElsewhere.FirstOrDefault() is { } u ? $"{u.File} line {u.Line}" : null,
        };
        Write();
    }

    public void Effects(string effects) => Set("effects", effects);

    public void Note(string id) => Set("note", id);

    public void Outcome(string text, bool saved = false)
    {
        if (_group is null)
        {
            return;
        }

        _group["outcome"] = text.Trim();
        _group["saved"] = saved;
        if (saved && _group["ruleId"]?.GetValue<string>() is { } id)
        {
            _doc["savedRules"]!.AsArray().Add(id);
        }

        Write();
    }

    public void Finish(string status, TeachScan? after, string? error = null)
    {
        _doc["status"] = status;
        _doc["finishedAt"] = Stamp(DateTime.UtcNow);
        if (after is not null)
        {
            _doc["after"] = Counts(after);
        }

        if (error is not null)
        {
            _doc["error"] = error;
        }

        Write();
    }

    private void Set(string key, string value)
    {
        if (_group is null)
        {
            return;
        }

        _group[key] = value;
        Write();
    }

    private static JsonObject Counts(TeachScan scan) => new()
    {
        ["programs"] = scan.Programs.Count,
        ["excluded"] = scan.Excluded.Count,
        ["places"] = scan.Groups.Sum(g => g.Places),
        ["groups"] = scan.Groups.Count,
        ["rules"] = scan.Rules.Rules.Count,
    };

    private static string Bounded(string text) => text.Length > MaxText ? text[..MaxText] + "…" : text;

    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);

    // Atomic replace, so the portal never reads half a record.
    private void Write()
    {
        if (_broken)
        {
            return;
        }

        _doc["updatedAt"] = Stamp(DateTime.UtcNow);
        var pending = FilePath + $".{Environment.ProcessId}.pending";
        try
        {
            File.WriteAllText(pending, _doc.ToJsonString(Indented) + "\n");
            File.Move(pending, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _broken = true;
            _warnings?.WriteLine($"   (The portal will not show the rest of this run: {ex.Message})");
        }
    }
}
