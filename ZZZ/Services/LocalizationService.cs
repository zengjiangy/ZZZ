using System.Globalization;
using System.Windows;

namespace ZZZ.Services;

public sealed class LanguageOption
{
    public LanguageOption(string code, string displayName) { Code = code; DisplayName = displayName; }
    public string Code { get; }
    public string DisplayName { get; }
}

public sealed class SettingChoice<T> where T : struct, Enum
{
    public SettingChoice(T value, string label) { Value = value; Label = label; }
    public T Value { get; }
    public string Label { get; }
}

public static class LocalizationService
{
    private const string ResourceMarker = "Resources/Languages/Strings.";
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("auto", "跟随系统 / System"),
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁體中文"),
        new("en-US", "English"),
        new("ja-JP", "日本語"),
        new("ko-KR", "한국어"),
        new("pt-BR", "Português"),
        new("es-ES", "Español"),
        new("ru-RU", "Русский"),
        new("fr-FR", "Français"),
        new("de-DE", "Deutsch")
    ];

    public static IReadOnlyList<LanguageOption> TranslationTargets { get; } =
    [
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁體中文"),
        new("en", "English"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("pt", "Português"),
        new("es", "Español"),
        new("ru", "Русский"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("__custom__", "自定义 / Custom")
    ];

    public static string CurrentLanguage { get; private set; } = "en-US";

    public static void Apply(string requestedLanguage)
    {
        var language = Resolve(requestedLanguage);
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        foreach (var existing in dictionaries.Where(x => x.Source?.OriginalString.IndexOf(ResourceMarker, StringComparison.OrdinalIgnoreCase) >= 0).ToArray())
            dictionaries.Remove(existing);

        dictionaries.Add(Load("en-US"));
        if (language != "en-US") dictionaries.Add(Load(language));
        CurrentLanguage = language;

        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static string Text(string key) => Application.Current.TryFindResource(key)?.ToString() ?? key;

    private static ResourceDictionary Load(string code) => new()
    {
        Source = new Uri($"/ZZZ;component/Resources/Languages/Strings.{code}.xaml", UriKind.Relative)
    };

    private static string Resolve(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && requested != "auto" && Languages.Any(x => x.Code == requested)) return requested;
        var name = CultureInfo.CurrentUICulture.Name;
        if (name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || name is "zh-TW" or "zh-HK" or "zh-MO") return "zh-TW";
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        if (name.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja-JP";
        if (name.StartsWith("ko", StringComparison.OrdinalIgnoreCase)) return "ko-KR";
        if (name.StartsWith("pt", StringComparison.OrdinalIgnoreCase)) return "pt-BR";
        if (name.StartsWith("es", StringComparison.OrdinalIgnoreCase)) return "es-ES";
        if (name.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return "ru-RU";
        if (name.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return "fr-FR";
        if (name.StartsWith("de", StringComparison.OrdinalIgnoreCase)) return "de-DE";
        return "en-US";
    }
}
