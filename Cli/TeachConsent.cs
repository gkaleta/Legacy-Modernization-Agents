using System.Text.Json;
using System.Text.Json.Nodes;

namespace CobolToQuarkusMigration.Cli;

// The person's one-time agreement that an automatic teach-parser run may send pseudonymised excerpts from these estate
// folders to this endpoint without asking per group. Kept locally (Config/ is git-ignored). Another folder or another
// endpoint asks again; 'teach-parser forget' withdraws it.
public sealed class TeachConsent(string path)
{
    public const string DefaultPath = "Config/starforge-teach-consent.local.json";

    public string FilePath => path;

    public bool Covers(IReadOnlyList<string> roots, TeachAiEndpoint ai)
    {
        var agreed = Load();
        return roots.Count > 0 && roots.All(r => agreed.Any(e => Same(e, Key(r), ai)));
    }

    public void Give(IReadOnlyList<string> roots, TeachAiEndpoint ai)
    {
        var agreed = Load();
        foreach (var root in roots.Select(Key).Where(k => !agreed.Any(e => Same(e, k, ai))))
        {
            agreed.Add(new JsonObject
            {
                ["folder"] = root,
                ["service"] = ai.Service,
                ["endpoint"] = ai.Endpoint,
                ["agreedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var doc = new JsonObject { ["schemaVersion"] = 1, ["agreed"] = new JsonArray(agreed.Select(e => e.DeepClone()).ToArray()) };
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    public bool Forget()
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private static bool Same(JsonObject e, string folder, TeachAiEndpoint ai)
        => Str(e, "folder") == folder && Str(e, "service") == ai.Service && Str(e, "endpoint") == ai.Endpoint;

    private static string Key(string root) => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? Str(JsonObject e, string name) => e[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // An unreadable file counts as no agreement: the run asks again rather than sending on a guess.
    private List<JsonObject> Load()
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["agreed"] is JsonArray list
                ? list.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList()
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return [];
        }
    }
}
