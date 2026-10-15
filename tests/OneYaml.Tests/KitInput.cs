using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OneYaml.Tests;

/// <summary>
/// Builds the input of a kit case: the stored file, or the bytes a generated-input recipe describes. A recipe is a
/// short list of parts for an input too large to store; this helper follows the kit's case format exactly and
/// fails on anything it does not know, so a new kind of part cannot be skipped unnoticed.
/// </summary>
internal static class KitInput
{
    private const int MaxParts = 64;
    private const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>Recipes that build at least this many bytes run in the slow tier.</summary>
    internal const long LargeBytes = 1024 * 1024;

    /// <summary>The input of a case: its recipe built, or the input file it names.</summary>
    internal static byte[] Read(string caseDirectory, JsonElement caseRoot)
    {
        bool hasRecipe = caseRoot.TryGetProperty("input_recipe", out JsonElement recipe);
        bool hasFile = caseRoot.TryGetProperty("input", out JsonElement named);
        if (hasRecipe && hasFile)
        {
            throw new InvalidOperationException("a case has an input file or a recipe, not both");
        }

        if (hasRecipe)
        {
            return Build(recipe);
        }

        return File.ReadAllBytes(Path.Combine(caseDirectory, hasFile ? named.GetString()! : "input.yaml"));
    }

    /// <summary>The size in bytes of the input a case's recipe builds, or 0 for a case with an input file.</summary>
    internal static long RecipeSize(JsonElement caseRoot) =>
        caseRoot.TryGetProperty("input_recipe", out JsonElement recipe) ? SizeOf(recipe) : 0;

    /// <summary>Builds the bytes a recipe describes.</summary>
    internal static byte[] Build(JsonElement recipe)
    {
        long size = SizeOf(recipe);
        var output = new MemoryStream((int)size);
        foreach (JsonElement part in recipe.EnumerateArray())
        {
            if (part.TryGetProperty("text", out JsonElement text))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text.GetString()!);
                long repeat = Repeat(part);
                if (bytes.Length == 0 || repeat == 0)
                {
                    continue;
                }

                // Write in pieces of about a megabyte instead of one copy per repeat.
                int per = (int)Math.Min(repeat, Math.Max(1, (1 << 20) / bytes.Length));
                byte[] piece = new byte[per * bytes.Length];
                for (int i = 0; i < per; i++)
                {
                    bytes.CopyTo(piece, i * bytes.Length);
                }

                for (long done = 0; done + per <= repeat; done += per)
                {
                    output.Write(piece);
                }

                output.Write(piece, 0, (int)(repeat % per) * bytes.Length);
                continue;
            }

            NumbersSpec n = Numbers(part.GetProperty("numbers"));
            var line = new StringBuilder();
            for (long i = 0; i < n.Count; i++)
            {
                line.Clear().Append(n.Prefix).Append((n.Start + i).ToString(CultureInfo.InvariantCulture).PadLeft(n.Width, '0')).Append(n.Suffix);
                output.Write(Encoding.UTF8.GetBytes(line.ToString()));
            }
        }

        return output.ToArray();
    }

    /// <summary>The text of an expected-events recipe (the events file the recipe stands for).</summary>
    internal static string BuildText(JsonElement recipe) => Encoding.UTF8.GetString(Build(recipe));

    private static long SizeOf(JsonElement recipe)
    {
        if (recipe.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("a recipe is a list of parts");
        }

        if (recipe.GetArrayLength() is 0 or > MaxParts)
        {
            throw new InvalidOperationException("a recipe has 1 to " + MaxParts + " parts");
        }

        long total = 0;
        foreach (JsonElement part in recipe.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("a recipe part is an object");
            }

            if (part.TryGetProperty("text", out JsonElement text))
            {
                if (text.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException("a text part holds a string");
                }

                foreach (JsonProperty p in part.EnumerateObject())
                {
                    if (p.Name is not ("text" or "repeat"))
                    {
                        throw new InvalidOperationException("unknown key in a text part: " + p.Name);
                    }
                }

                total += Encoding.UTF8.GetByteCount(text.GetString()!) * Repeat(part);
            }
            else if (part.TryGetProperty("numbers", out JsonElement numbers))
            {
                if (part.EnumerateObject().Count() != 1)
                {
                    throw new InvalidOperationException("a numbers part has no other keys");
                }

                NumbersSpec n = Numbers(numbers);
                long digits = Math.Max(n.Width, (n.Start + Math.Max(n.Count - 1, 0)).ToString(CultureInfo.InvariantCulture).Length);
                total += (Encoding.UTF8.GetByteCount(n.Prefix) + Encoding.UTF8.GetByteCount(n.Suffix) + digits) * n.Count;
            }
            else
            {
                throw new InvalidOperationException("unknown recipe part kind: " + part.GetRawText());
            }

            if (total > MaxBytes)
            {
                throw new InvalidOperationException("a recipe builds at most 64 MiB");
            }
        }

        return total;
    }

    private static long Repeat(JsonElement part) => part.TryGetProperty("repeat", out JsonElement r) ? NonNegative(r) : 1;

    private static long NonNegative(JsonElement number)
    {
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out long value) || value < 0)
        {
            throw new InvalidOperationException("a count is an integer of 0 or more: " + number.GetRawText());
        }

        return value;
    }

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidOperationException("a prefix and a suffix are strings");

    private readonly record struct NumbersSpec(long Count, long Start, string Prefix, string Suffix, int Width);

    private static NumbersSpec Numbers(JsonElement spec)
    {
        long count = 0, start = 0, width = 0;
        string prefix = string.Empty, suffix = string.Empty;
        bool hasCount = false;
        foreach (JsonProperty p in spec.EnumerateObject())
        {
            switch (p.Name)
            {
                case "count": count = NonNegative(p.Value); hasCount = true; break;
                case "start": start = NonNegative(p.Value); break;
                case "width": width = NonNegative(p.Value); break;
                case "prefix": prefix = Text(p.Value); break;
                case "suffix": suffix = Text(p.Value); break;
                default: throw new InvalidOperationException("unknown key in a numbers part: " + p.Name);
            }
        }

        if (!hasCount || width > int.MaxValue)
        {
            throw new InvalidOperationException("a numbers part needs a count");
        }

        return new NumbersSpec(count, start, prefix, suffix, (int)width);
    }
}
