using System.Text.Json;

namespace OneYaml.Tests.Document;

/// <summary>
/// Applies a patch to the expected model with plain tree operations, independently of the document,
/// so the result of a patch can be compared with a fresh parse of the output.
/// </summary>
internal static class PatchOracle
{
    /// <summary>Whether copies or moves are involved: they may keep an alias or write the value out.</summary>
    public static bool NeedsExpansion(JsonElement patch, bool merge)
        => !merge && patch.ValueKind == JsonValueKind.Array && patch.EnumerateArray().Any(o => o.GetProperty("op").GetString() is "copy" or "move");

    /// <summary>Applies a patch that is known to succeed to the model of one document.</summary>
    public static void Apply(List<Model> models, int document, JsonElement patch, bool merge)
    {
        Model root = models[document];
        if (merge)
        {
            Model.Merge(root, patch, root);
            return;
        }

        foreach (JsonElement op in patch.EnumerateArray())
        {
            string name = op.GetProperty("op").GetString()!;
            string path = op.GetProperty("path").GetString()!;
            switch (name)
            {
                case "add":
                case "replace":
                case "remove":
                    Model.ApplyTo(root, name, path, op.TryGetProperty("value", out JsonElement v) ? v : default);
                    break;
                case "test":
                    break;
                case "copy":
                    Model.ApplyModel(root, "add", path, Model.CopyValue(root, op.GetProperty("from").GetString()!)!);
                    break;
                default:
                    Move(root, op.GetProperty("from").GetString()!, path);
                    break;
            }
        }
    }

    private static string ParentOf(string path) => path.Length == 0 ? string.Empty : path[..path.LastIndexOf('/')];

    /// <summary>
    /// Moves a value in the model: remove then add, except that between two mappings the add comes
    /// first (and the destination is not above the source), so a mapping does not collapse to {} on the way.
    /// </summary>
    public static void Move(Model root, string from, string path)
    {
        if (from == path)
        {
            return;
        }

        Model value = Model.CopyValue(root, from)!;
        bool destinationAbove = from.StartsWith(path + "/", StringComparison.Ordinal) || path.Length == 0;
        bool mappings = from.Length > 0 && path.Length > 0 && Model.Find(root, ParentOf(from))!.Kind == "map" && Model.Find(root, ParentOf(path))!.Kind == "map";
        if (mappings && !destinationAbove)
        {
            Model.ApplyModel(root, "add", path, value);
            Model.ApplyTo(root, "remove", from, default);
        }
        else
        {
            Model.ApplyTo(root, "remove", from, default);
            Model.ApplyModel(root, "add", path, value);
        }
    }
}
