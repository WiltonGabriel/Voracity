using System.Globalization;

namespace Voracity.Core.Data;

/// <summary>
/// Data-driven localization dictionary supporting Portuguese (pt-BR) and English (en-US),
/// with moddable overrides and automatic OS locale detection.
/// </summary>
public sealed class LocalizationCatalog
{
    public const string PortugueseCode = "pt-BR";
    public const string EnglishCode = "en-US";

    private readonly Dictionary<string, Dictionary<string, string>> _tables;

    public LocalizationCatalog(Dictionary<string, Dictionary<string, string>> tables)
    {
        _tables = tables;
    }

    public static string ResolveLanguageCode(string? languageMode, string? systemLocaleOverride = null)
    {
        if (string.Equals(languageMode, PortugueseCode, StringComparison.OrdinalIgnoreCase))
            return PortugueseCode;
        if (string.Equals(languageMode, EnglishCode, StringComparison.OrdinalIgnoreCase))
            return EnglishCode;

        return DetectSystemLanguage(systemLocaleOverride);
    }

    public static string DetectSystemLanguage(string? systemLocaleOverride = null)
    {
        var candidate = systemLocaleOverride;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Environment.GetEnvironmentVariable("LC_ALL");
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Environment.GetEnvironmentVariable("LANG");
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = CultureInfo.CurrentUICulture.Name;

        if (!string.IsNullOrWhiteSpace(candidate) &&
            candidate.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            return PortugueseCode;
        }

        return EnglishCode;
    }

    public string Get(string languageCode, string key)
    {
        var resolved = ResolveLanguageCode(languageCode);
        if (_tables.TryGetValue(resolved, out var table) && table.TryGetValue(key, out var val))
            return val;

        if (_tables.TryGetValue(EnglishCode, out var fallback) && fallback.TryGetValue(key, out var fbVal))
            return fbVal;

        return key;
    }

    public string Format(string languageCode, string key, params object[] args)
    {
        var template = Get(languageCode, key);
        return string.Format(CultureInfo.InvariantCulture, template, args);
    }
}
