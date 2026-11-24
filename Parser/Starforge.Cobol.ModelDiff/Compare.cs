namespace Starforge.Cobol.ModelDiff;

// One keyed comparison: values present on both sides are compared for equality; keys present on one side only are reported.
internal sealed class Compare(DiffReport report, Classifier classifier, string program)
{
    public string Program { get; } = program;

    // With presence off, keys on one side only are skipped: the category's presence is already counted elsewhere.
    public void Keyed(string category, IEnumerable<(string Key, string Value)> rekt, IEnumerable<(string Key, string Value)> starforge, bool presence = true)
    {
        var tally = report[category];
        var left = Occurrences(rekt);
        var right = Occurrences(starforge);
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other))
            {
                if (!presence)
                {
                    continue;
                }

                var c = classifier.Classify(category, DiffKind.OnlyRekt, Program, key, value, null);
                tally.Add(DiffKind.OnlyRekt, c, $"{Program} {key} = {value}");
                report.Record(category, DiffKind.OnlyRekt, c, Program, key, value, null);
            }
            else if (value == other)
            {
                tally.Match();
            }
            else
            {
                var c = classifier.Classify(category, DiffKind.Mismatch, Program, key, value, other);
                tally.Add(DiffKind.Mismatch, c, $"{Program} {key}: rekt={value} starforge={other}");
                report.Record(category, DiffKind.Mismatch, c, Program, key, value, other);
            }
        }

        foreach (var (key, value) in right)
        {
            if (presence && !left.ContainsKey(key))
            {
                var c = classifier.Classify(category, DiffKind.OnlyStarforge, Program, key, null, value);
                tally.Add(DiffKind.OnlyStarforge, c, $"{Program} {key} = {value}");
                report.Record(category, DiffKind.OnlyStarforge, c, Program, key, null, value);
            }
        }
    }

    public void Shared(string category, IEnumerable<(string Key, string Value)> rekt, IEnumerable<(string Key, string Value)> starforge) =>
        Keyed(category, rekt, starforge, presence: false);

    public void Value(string category, string key, string rekt, string starforge) =>
        Keyed(category, [(key, rekt)], [(key, starforge)]);

    // Repeated keys become key#2, key#3 so multisets compare by occurrence.
    private static Dictionary<string, string> Occurrences(IEnumerable<(string Key, string Value)> items)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in items)
        {
            var n = seen[key] = seen.GetValueOrDefault(key) + 1;
            result[n == 1 ? key : $"{key}#{n}"] = value;
        }

        return result;
    }
}
