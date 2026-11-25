namespace Starforge.Cobol;

public static class EstateFiles
{
    private static readonly string[] ProgramExtensions = { ".cbl", ".cob" };

    public static IEnumerable<string> DiscoverPrograms(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                // Dot folders hold generated output (.preprocessed, .previous snapshots), never source programs.
                if (!Path.GetFileName(sub).StartsWith('.'))
                {
                    stack.Push(sub);
                }
            }

            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (ProgramExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                {
                    yield return f;
                }
            }
        }
    }
}
