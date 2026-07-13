using System.Globalization;
using System.Windows;

namespace ZZZ.Services;

public sealed record LanguageOption(string Code, string DisplayName);
public sealed record SettingChoice<T>(T Value, string Label) where T : struct, Enum;

public static class LocalizationService
{
    private const string ResourceMarker = "Resources/Languages/Strings.";
    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new("auto", "跟随系统 / System"),
        new("zh-CN", "简体中文"),
        new("zh-TW", "繁體中文"),
        new("en-US", "English"),
        new("ja-JP", "日本語")
    ];

    public static string CurrentLanguage { get; private set; } = "en-US";

    public static void Apply(string requestedLanguage)
    {
        var language = Resolve(requestedLanguage);
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        foreach (var existing in dictionaries.Where(x => x.Source?.OriginalString.Contains(ResourceMarker, StringComparison.OrdinalIgnoreCase) == true).ToArray())
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
        return "en-US";
    }
}
