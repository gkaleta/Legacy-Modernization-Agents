namespace Starforge.Cobol.Copy;

public sealed record CopybookResolution(string Name, string? Library, string? Path, string? Stage, IReadOnlyList<string> OtherCandidates)
{
    public bool Found => Path is not null;
}

// Immutable after construction, so one resolver can serve parallel program parses.
public sealed class CopybookResolver
{
    public const string PreprocessedFolder = ".preprocessed";

    private static readonly Tier[] ExtensionTiers =
    {
        new(ExactName: true, new[] { ".cpy", ".CPY", ".copy" }),
        new(ExactName: false, new[] { ".cbl", ".CBL" }),
    };

    private readonly List<Estate> _estates;
    private readonly Dictionary<string, DirectoryIndex> _extraDirs = new(StringComparer.Ordinal);

    public CopybookResolver(IEnumerable<string> estateRoots)
    {
        _estates = estateRoots
            .Select(r => System.IO.Path.GetFullPath(r))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .Select(BuildEstate)
            .ToList();
    }

    public IReadOnlyList<string> EstateRoots => _estates.Select(e => e.Root).ToList();

    public static string NormalizeName(string name)
    {
        var n = name.Trim();
        if (n.Length >= 2 && n[0] is '\'' or '"' && n[^1] == n[0])
        {
            n = n[1..^1].Trim();
        }

        return n;
    }

    // Search order: including file's folder, program folder, every non-dot folder of the estate (nearest first),
    // then <estate>/.preprocessed for generated members (BMS maps, DFHAID, SQLCA) that only exist there.
    // The whole order runs first for copybook extensions and only then for .cbl, so a program never shadows its copybook.
    public CopybookResolution Resolve(string name, string? library, string includingFile, string programFile)
    {
        var member = NormalizeName(name);
        var lib = library is null ? null : NormalizeName(library);
        var includingDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(includingFile)) ?? string.Empty;
        var programFull = System.IO.Path.GetFullPath(programFile);
        var programDir = System.IO.Path.GetDirectoryName(programFull) ?? string.Empty;
        var estate = FindEstate(programDir);

        foreach (var tier in ExtensionTiers)
        {
            var found = ResolveTier(member, lib, tier, includingDir, programDir, programFull, estate);
            if (found is not null)
            {
                return found;
            }
        }

        return new CopybookResolution(member, lib, null, null, Array.Empty<string>());
    }

    private CopybookResolution? ResolveTier(string member, string? lib, Tier tier, string includingDir, string programDir, string programFull, Estate? estate)
    {
        string? P(DirectoryIndex? dir)
        {
            var hit = Probe(dir, member, tier);
            return hit is not null && string.Equals(hit, programFull, StringComparison.Ordinal) ? null : hit;
        }

        var hit = P(GetIndex(estate, includingDir));
        if (hit is not null)
        {
            return new CopybookResolution(member, lib, hit, "including-folder", OtherCandidates(estate, member, hit, tier));
        }

        if (!string.Equals(programDir, includingDir, StringComparison.Ordinal))
        {
            hit = P(GetIndex(estate, programDir));
            if (hit is not null)
            {
                return new CopybookResolution(member, lib, hit, "program-folder", OtherCandidates(estate, member, hit, tier));
            }
        }

        if (estate is null)
        {
            return null;
        }

        var matches = new List<string>();
        foreach (var dir in estate.Directories.OrderBy(d => TreeDistance(programDir, d.Path)).ThenBy(d => d.Path, StringComparer.Ordinal))
        {
            var m = P(dir);
            if (m is not null)
            {
                matches.Add(m);
            }
        }

        if (matches.Count > 0)
        {
            return new CopybookResolution(member, lib, matches[0], "estate-folder", matches.Skip(1).ToList());
        }

        hit = P(estate.Preprocessed);
        return hit is null ? null : new CopybookResolution(member, lib, hit, "preprocessed-fallback", Array.Empty<string>());
    }

    private IReadOnlyList<string> OtherCandidates(Estate? estate, string member, string chosen, Tier tier)
    {
        if (estate is null)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var dir in estate.Directories)
        {
            var m = Probe(dir, member, tier);
            if (m is not null && !string.Equals(m, chosen, StringComparison.Ordinal))
            {
                list.Add(m);
            }
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static string? Probe(DirectoryIndex? dir, string member, Tier tier)
    {
        if (dir is null || member.Length == 0)
        {
            return null;
        }

        if (tier.ExactName && System.IO.Path.HasExtension(member))
        {
            var exact = dir.Find(member, member);
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (var ext in tier.Extensions)
        {
            var found = dir.Find(member + ext, member + ext);
            if (found is not null)
            {
                return found;
            }
        }

        return tier.ExactName ? dir.Find(member, member) : null;
    }

    private sealed record Tier(bool ExactName, string[] Extensions);

    private DirectoryIndex? GetIndex(Estate? estate, string dir)
    {
        if (estate is not null)
        {
            foreach (var d in estate.Directories)
            {
                if (string.Equals(d.Path, dir, StringComparison.Ordinal))
                {
                    return d;
                }
            }
        }

        lock (_extraDirs)
        {
            if (!_extraDirs.TryGetValue(dir, out var index))
            {
                index = Directory.Exists(dir) ? DirectoryIndex.Build(dir) : null;
                if (index is not null)
                {
                    _extraDirs[dir] = index;
                }
            }

            return index;
        }
    }

    private Estate? FindEstate(string programDir)
    {
        Estate? best = null;
        foreach (var e in _estates)
        {
            var prefix = e.Root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? e.Root : e.Root + System.IO.Path.DirectorySeparatorChar;
            if (string.Equals(programDir, e.Root, StringComparison.Ordinal) || programDir.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (best is null || e.Root.Length > best.Root.Length)
                {
                    best = e;
                }
            }
        }

        return best;
    }

    private static int TreeDistance(string from, string to)
    {
        var a = from.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var b = to.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < a.Length && common < b.Length && string.Equals(a[common], b[common], StringComparison.Ordinal))
        {
            common++;
        }

        return (a.Length - common) + (b.Length - common);
    }

    private static Estate BuildEstate(string root)
    {
        var dirs = new List<DirectoryIndex>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            dirs.Add(DirectoryIndex.Build(dir));
            foreach (var sub in Directory.GetDirectories(dir).OrderBy(s => s, StringComparer.Ordinal).Reverse())
            {
                if (!System.IO.Path.GetFileName(sub).StartsWith('.'))
                {
                    stack.Push(sub);
                }
            }
        }

        dirs.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
        var pre = System.IO.Path.Combine(root, PreprocessedFolder);
        return new Estate(root, dirs, Directory.Exists(pre) ? DirectoryIndex.Build(pre) : null);
    }

    private sealed record Estate(string Root, List<DirectoryIndex> Directories, DirectoryIndex? Preprocessed);

    private sealed class DirectoryIndex
    {
        private readonly Dictionary<string, List<string>> _byUpperName;

        private DirectoryIndex(string path, Dictionary<string, List<string>> byUpperName)
        {
            Path = path;
            _byUpperName = byUpperName;
        }

        public string Path { get; }

        public static DirectoryIndex Build(string dir)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = System.IO.Path.GetFileName(file);
                var key = name.ToUpperInvariant();
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    map[key] = list;
                }

                list.Add(file);
            }

            return new DirectoryIndex(dir, map);
        }

        // Case-insensitive so lookups behave the same on Linux as on macOS/Windows; exact case wins ties.
        public string? Find(string fileName, string preferredCase)
        {
            if (!_byUpperName.TryGetValue(fileName.ToUpperInvariant(), out var list))
            {
                return null;
            }

            foreach (var f in list)
            {
                if (string.Equals(System.IO.Path.GetFileName(f), preferredCase, StringComparison.Ordinal))
                {
                    return f;
                }
            }

            return list[0];
        }
    }
}
