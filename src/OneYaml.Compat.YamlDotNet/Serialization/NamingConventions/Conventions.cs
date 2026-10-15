using System;
using System.Globalization;
using System.Text;

namespace OneYaml.Compat.YamlDotNet.Serialization.NamingConventions;

internal static class CaseConversions
{
    // Pascal case: an underscore (or hyphen) followed by a letter is dropped and that character is capitalised;
    // other underscores stay, as in YamlDotNet.
    public static string ToPascalCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        string s = value;
        var result = new StringBuilder(s.Length);
        bool capitalize = true;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '_' or '-' && i + 1 < s.Length && char.IsLetter(s[i + 1]))
            {
                capitalize = true;
                continue;
            }

            result.Append(capitalize ? char.ToUpperInvariant(c) : c);
            capitalize = false;
        }

        return result.ToString();
    }

    public static string ToCamelCase(string value)
    {
        string pascal = ToPascalCase(value);
        return pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    // Camel case with a separator and a lower-case letter in front of every ASCII capital: HTTPServer becomes h_t_t_p_server.
    public static string ToSeparated(string value, char separator)
    {
        string camel = ToCamelCase(value);
        var result = new StringBuilder(camel.Length + 4);
        foreach (char c in camel)
        {
            if (c is >= 'A' and <= 'Z')
            {
                result.Append(separator).Append((char)(c + 32));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    public static string CapitaliseFirst(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];
}

/// <summary>Keeps member names as they are.</summary>
public sealed class NullNamingConvention : INamingConvention
{
    private NullNamingConvention()
    {
    }

    /// <summary>The only instance.</summary>
    public static NullNamingConvention Instance { get; } = new();

    /// <inheritdoc/>
    public string Apply(string value) => value;

    /// <inheritdoc/>
    public string Reverse(string value) => value;
}

/// <summary>Writes names in camel case: <c>firstName</c>.</summary>
public sealed class CamelCaseNamingConvention : INamingConvention
{
    private CamelCaseNamingConvention()
    {
    }

    /// <summary>The only instance.</summary>
    public static CamelCaseNamingConvention Instance { get; } = new();

    /// <inheritdoc/>
    public string Apply(string value) => CaseConversions.ToCamelCase(value);

    /// <inheritdoc/>
    public string Reverse(string value) => CaseConversions.ToPascalCase(value);
}

/// <summary>Writes names in Pascal case: <c>FirstName</c>.</summary>
public sealed class PascalCaseNamingConvention : INamingConvention
{
    private PascalCaseNamingConvention()
    {
    }

    /// <summary>The only instance.</summary>
    public static PascalCaseNamingConvention Instance { get; } = new();

    /// <inheritdoc/>
    public string Apply(string value) => CaseConversions.ToPascalCase(value);

    /// <inheritdoc/>
    public string Reverse(string value) => CaseConversions.ToPascalCase(value);
}

/// <summary>Writes names in lower case with underscores: <c>first_name</c>.</summary>
public sealed class UnderscoredNamingConvention : INamingConvention
{
    private UnderscoredNamingConvention()
    {
    }

    /// <summary>The only instance.</summary>
    public static UnderscoredNamingConvention Instance { get; } = new();

    /// <inheritdoc/>
    public string Apply(string value) => CaseConversions.ToSeparated(value, '_');

    /// <inheritdoc/>
    public string Reverse(string value) => CaseConversions.ToPascalCase(value);
}

/// <summary>Writes names in lower case with hyphens: <c>first-name</c>.</summary>
public sealed class HyphenatedNamingConvention : INamingConvention
{
    private HyphenatedNamingConvention()
    {
    }

    /// <summary>The only instance.</summary>
    public static HyphenatedNamingConvention Instance { get; } = new();

    /// <inheritdoc/>
    public string Apply(string value) => CaseConversions.ToSeparated(value, '-');

    /// <inheritdoc/>
    public string Reverse(string value) => CaseConversions.ToPascalCase(value);
}

/// <summary>Writes names in lower case: <c>firstname</c>.</summary>
public sealed class LowerCaseNamingConvention : INamingConvention
{
    private LowerCaseNamingConvention()
    {
    }

    /// <summary>The only instance.</summary>
    public static LowerCaseNamingConvention Instance { get; } = new();

    /// <inheritdoc/>
    public string Apply(string value) => CaseConversions.ToCamelCase(value).ToLowerInvariant();

    /// <inheritdoc/>
    public string Reverse(string value) => CaseConversions.CapitaliseFirst(value);
}
