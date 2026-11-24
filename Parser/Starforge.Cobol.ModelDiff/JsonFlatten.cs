using System.Text.Json;

namespace Starforge.Cobol.ModelDiff;

// Flattens a JSON document into (path, value) pairs: object members by name, array elements as a multiset.
internal static class JsonFlatten
{
    public static IEnumerable<(string Category, string Key, string Value)> Flatten(JsonElement element, string prefix, string path = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var category = path.Length == 0 ? $"{prefix}.{property.Name}" : null;
                    foreach (var item in Flatten(property.Value, prefix, path.Length == 0 ? property.Name : $"{path}.{property.Name}"))
                    {
                        yield return category is null ? item : item with { Category = category };
                    }
                }

                break;
            case JsonValueKind.Array:
                var any = false;
                foreach (var item in element.EnumerateArray())
                {
                    any = true;
                    yield return (prefix, $"{path}[] {Scalar(item)}", "");
                }

                if (!any)
                {
                    yield return (prefix, path, "[]");
                }

                break;
            default:
                yield return (prefix, path, Scalar(element));
                break;
        }
    }

    public static string Scalar(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : JsonSerializer.Serialize(element);
}
