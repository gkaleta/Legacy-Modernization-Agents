using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileAccess = System.IO.FileAccess;

namespace McpChatWeb.Services;

public sealed record TeachEndpoint(string? Service, string? Endpoint, string? Model);
public sealed record TeachCounts(int? Programs, int? Excluded, int? Places, int? Groups, int? Rules);
public sealed record TeachCheck(int? Fixed, int? Places, int? Excluded, int? Remaining, int? NewProblems, int? ChangedElsewhere,
    bool? Passed, string? FirstRemaining, string? FirstNew, string? FirstChanged);
public sealed record TeachGroupRecord(string Label, string? Code, string? Meaning, int? Places, int? Programs, string? FirstFile,
    int? FirstLine, int? ProgramsMissingCopybooks, string? Prompt, string Decision, string? Category, string? Explanation,
    string? Answer, string? AnswerError, string? RuleId, string? Rule, string? RuleJson, TeachCheck? Check, string? Effects,
    string? Outcome, bool Saved, string? Note = null);
public sealed record TeachSession(string ManifestPath, string Name, IReadOnlyList<string> Sources, string? RulesFile,
    TeachEndpoint? Endpoint, TeachCounts? Before, TeachCounts? After, IReadOnlyList<TeachGroupRecord> Groups,
    IReadOnlyList<string> SavedRules, bool Truncated, string Mode = "review");

// Reads teach-parser journals (output/teach-parser/<id>/teach.json). Read-only, bounded, regular files only.
public sealed partial class AiLoopReader
{
    private const string TeachFolder = "teach-parser";
    private const int MaxTeachGroups = 500;
    private const int MaxTeachText = 8000;

    private readonly object _teachLock = new();
    private readonly Dictionary<string, (long Length, DateTime Write, AiLoopRunDetail Detail)> _teachCache = [];

    private IReadOnlyList<AiLoopRunDetail> TeachActivities()
    {
        lock (_teachLock)
        {
            var output = Path.Join(RepoRoot, "output");
            var root = Path.Join(output, TeachFolder);
            if (!Directory.Exists(root) || (File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return [];
            var result = new List<AiLoopRunDetail>();
            var seen = new HashSet<string>();
            long bytesRead = 0;
            var newest = Directory.EnumerateDirectories(root)
                .Select(directory => (Directory: directory, Write: File.GetLastWriteTimeUtc(Path.Join(directory, "teach.json"))))
                .OrderByDescending(candidate => candidate.Write)
                .ThenBy(candidate => candidate.Directory, StringComparer.Ordinal)
                .Take(200)
                .Select(candidate => candidate.Directory);
            foreach (var directory in newest)
            {
                var path = Path.Join(directory, "teach.json");
                seen.Add(path);
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || !File.Exists(path)) continue;
                    var info = new FileInfo(path);
                    if (info.Length > 4 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (_teachCache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Write == info.LastWriteTimeUtc)
                    { result.Add(RefreshTeachStatus(cached.Detail)); continue; }
                    bytesRead += info.Length;
                    if (bytesRead > 64 * 1024 * 1024) continue;
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
                    if (ReadTeach(document.RootElement, directory) is not { } detail) continue;
                    _teachCache[path] = (info.Length, info.LastWriteTimeUtc, detail);
                    result.Add(RefreshTeachStatus(detail));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
                {
                    if (_teachCache.TryGetValue(path, out var prior))
                        result.Add(RefreshTeachStatus(prior.Detail) with
                        { Warning = "The teach record is unreadable right now; showing the last good copy." });
                    _logger.LogDebug(ex, "Cannot read teach-parser journal {Path}", path);
                }
            }
            foreach (var key in _teachCache.Keys.Where(k => !seen.Contains(k)).ToList()) _teachCache.Remove(key);
            return result;
        }
    }

    private AiLoopRunDetail RefreshTeachStatus(AiLoopRunDetail detail)
        => detail.Summary.Status == "running" && detail.Summary.LastEventAt is { } last
            && DateTime.UtcNow - last > TimeSpan.FromMinutes(Math.Max(1, LoadOptions(out _).StaleRunMinutes))
            ? detail with
            {
                Summary = detail.Summary with { Status = "interrupted" },
                Warning = "Nothing recorded for a while and no finish. The terminal session may have been closed."
            }
            : detail;

    private AiLoopRunDetail? ReadTeach(JsonElement doc, string directory)
    {
        static string? Text(JsonElement e, string key, int max = MaxTeachText)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() is { } s && s.Length > max ? s[..max] + "…" : v.GetString() : null;
        static int? Number(JsonElement e, string key)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        static bool? Flag(JsonElement e, string key)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        static JsonElement Child(JsonElement e, string key)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        static IReadOnlyList<string> Strings(JsonElement e, string key)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Take(200).Select(x => x.GetString()!).ToList() : [];
        static DateTime? Time(JsonElement e, string key)
            => DateTimeOffset.TryParse(Text(e, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.UtcDateTime : null;
        static TeachCounts? Counts(JsonElement e)
            => e.ValueKind == JsonValueKind.Object
                ? new(Number(e, "programs"), Number(e, "excluded"), Number(e, "places"), Number(e, "groups"), Number(e, "rules")) : null;

        if (doc.ValueKind != JsonValueKind.Object || Text(doc, "activityKind") != "teach-parser" || Number(doc, "schemaVersion") != 1)
            return null;
        var name = Text(doc, "name");
        var start = Time(doc, "startedAt");
        if (name != Path.GetFileName(directory) || start is null
            || !doc.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            return null;

        var finish = Time(doc, "finishedAt");
        var status = Text(doc, "status") is { } s && s is "running" or "completed" or "stopped" or "failed" ? s : "unknown";
        if (finish < start) finish = null;
        if (finish is null && status is "completed" or "stopped") status = "unknown";
        var last = new[] { start, finish, Time(doc, "updatedAt") }.Max();

        var records = new List<TeachGroupRecord>();
        foreach (var g in groups.EnumerateArray().Take(MaxTeachGroups))
        {
            if (g.ValueKind != JsonValueKind.Object || Text(g, "label", 200) is not { } label) continue;
            var check = Child(g, "check");
            var first = Child(g, "firstPlace");
            records.Add(new(label, Text(g, "code", 200), Text(g, "meaning", 500), Number(g, "places"), Number(g, "programs"),
                Text(first, "file", 1000), Number(first, "line"), Number(g, "programsMissingCopybooks"), Text(g, "prompt"),
                Text(g, "decision", 20) ?? "asked", Text(g, "category", 100), Text(g, "explanation", 2000), Text(g, "answer"),
                Text(g, "answerError", 1000), Text(g, "ruleId", 200), Text(g, "rule", 1000), Text(g, "ruleJson", 4000),
                check.ValueKind == JsonValueKind.Object
                    ? new(Number(check, "fixed"), Number(check, "places"), Number(check, "excluded"), Number(check, "remaining"),
                        Number(check, "newProblems"), Number(check, "changedElsewhere"), Flag(check, "passed"),
                        Text(check, "firstRemaining", 1000), Text(check, "firstNew", 1000), Text(check, "firstChanged", 1000))
                    : null,
                Text(g, "effects", 500), Text(g, "outcome", 2000), Flag(g, "saved") == true, Text(g, "note", 100)));
        }

        var endpoint = Child(doc, "endpoint");
        var relative = Path.GetRelativePath(RepoRoot, directory).Replace('\\', '/');
        var locator = "teach-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative)))[..24].ToLowerInvariant();
        var sent = records.Count(r => r.Decision == "sent");
        var failed = records.Count(r => r.AnswerError is not null);
        var saved = Strings(doc, "savedRules");
        var stage = status == "running"
            ? records.LastOrDefault() is { } current ? $"Group {records.Count}: {current.Label}" : "Reading the estate"
            : $"{records.Count} group(s) looked at · {sent} sent · {saved.Count} rule(s) saved";
        var summary = new AiLoopRunSummary(locator, "teach-parser", null, status, start, finish,
            finish is { } end ? Math.Max(0, (long)(end - start.Value).TotalMilliseconds) : null,
            sent, failed, 0, 0, stage, RequestCoverage: "teach-journal", FailureReason: Text(doc, "error", 2000),
            LastEventAt: last, ActivityKind: "teach-parser", ActivityId: "teach:" + name);
        var session = new TeachSession(relative + "/teach.json", name!, Strings(doc, "sources"), Text(doc, "rulesFile", 1000),
            endpoint.ValueKind == JsonValueKind.Object ? new(Text(endpoint, "service", 200), Text(endpoint, "endpoint", 1000), Text(endpoint, "model", 200)) : null,
            Counts(Child(doc, "before")), Counts(Child(doc, "after")), records, saved, groups.GetArrayLength() > records.Count,
            Text(doc, "mode", 20) == "automatic" ? "automatic" : "review");
        return new(summary, relative, [new(1, 1, "Teach the parser", start.Value, summary.DurationMs)], [], [], [], null, [], [],
            Logs: [new(relative + "/teach.json", "teach-parser record", null, true)], Teach: session);
    }
}
