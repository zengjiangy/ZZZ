using System.Windows.Input;
using ZZZ.Configuration;

namespace ZZZ.Services;

/// <summary>Single shortcut resolver used by both WPF and WebView2 input paths.</summary>
public static class ShortcutRouter
{
    public static bool TryResolve(AppSettings settings, Key key, ModifierKeys modifiers, out string actionId, out int tabNumber)
    {
        actionId = string.Empty;
        tabNumber = 0;
        foreach (var binding in settings.Shortcuts)
        {
            foreach (var gesture in SplitGestures(binding.Gesture))
            {
                if (gesture.IndexOf("1-9", StringComparison.Ordinal) >= 0) continue;
                if (!TryParseGesture(gesture, out var boundKey, out var boundModifiers)) continue;
                if (key != boundKey || modifiers != boundModifiers) continue;
                actionId = binding.ActionId;
                return true;
            }
        }

        tabNumber = TabNumberFromKey(key);
        if (tabNumber == 0) return false;
        foreach (var binding in settings.Shortcuts)
        {
            if (binding.ActionId != BrowserActionIds.SelectTabNumber) continue;
            foreach (var gesture in SplitGestures(binding.Gesture))
            {
                if (!TryParseTabRange(gesture, out var boundModifiers) || modifiers != boundModifiers) continue;
                actionId = binding.ActionId;
                return true;
            }
        }
        tabNumber = 0;
        return false;
    }

    public static bool TryCapture(Key key, ModifierKeys modifiers, out string gesture)
    {
        gesture = string.Empty;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.System)
            return false;
        var functionKey = key is >= Key.F1 and <= Key.F24;
        if (modifiers == ModifierKeys.None && !functionKey) return false;
        gesture = Format(key, modifiers);
        return true;
    }

    public static string GestureFor(AppSettings settings, string actionId, bool firstOnly = true)
    {
        var gesture = settings.Shortcuts.FirstOrDefault(x => string.Equals(x.ActionId, actionId, StringComparison.OrdinalIgnoreCase))?.Gesture ?? string.Empty;
        var values = SplitGestures(gesture).ToArray();
        if (values.Length == 0) return string.Empty;
        return firstOnly ? values[0] : string.Join(" / ", values);
    }

    public static string? FindDuplicateGesture(IEnumerable<ShortcutSetting> bindings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings)
        foreach (var gesture in SplitGestures(binding.Gesture))
        {
            string canonical;
            if (TryParseGesture(gesture, out var key, out var modifiers)) canonical = Format(key, modifiers);
            else if (TryParseTabRange(gesture, out modifiers))
            {
                canonical = Format(Key.D1, modifiers);
                canonical = canonical.Substring(0, canonical.Length - 1) + "1-9";
            }
            else continue;
            if (!seen.Add(canonical)) return canonical;
        }
        return null;
    }

    public static bool IsValidConfiguration(IEnumerable<ShortcutSetting> bindings) =>
        bindings.SelectMany(x => SplitGestures(x.Gesture)).All(x =>
            TryParseGesture(x, out _, out _) || TryParseTabRange(x, out _));

    private static IEnumerable<string> SplitGestures(string? value) =>
        (value ?? string.Empty).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);

    private static bool TryParseGesture(string gesture, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        var parts = gesture.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (parts.Length == 0 || gesture.IndexOf("1-9", StringComparison.Ordinal) >= 0) return false;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!TryAddModifier(parts[i], ref modifiers)) return false;
        }
        key = ParseKey(parts[parts.Length - 1]);
        if (key == Key.None) return false;
        var functionKey = key is >= Key.F1 and <= Key.F24;
        return modifiers != ModifierKeys.None || functionKey;
    }

    private static bool TryParseTabRange(string gesture, out ModifierKeys modifiers)
    {
        modifiers = ModifierKeys.None;
        if (!gesture.EndsWith("1-9", StringComparison.OrdinalIgnoreCase)) return false;
        var prefix = gesture.Substring(0, gesture.Length - 3).TrimEnd('+');
        if (prefix.Length == 0) return false;
        foreach (var part in prefix.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0))
            if (!TryAddModifier(part, ref modifiers)) return false;
        return modifiers != ModifierKeys.None;
    }

    private static bool TryAddModifier(string value, ref ModifierKeys modifiers)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "ctrl": case "control": modifiers |= ModifierKeys.Control; return true;
            case "shift": modifiers |= ModifierKeys.Shift; return true;
            case "alt": modifiers |= ModifierKeys.Alt; return true;
            case "win": case "windows": modifiers |= ModifierKeys.Windows; return true;
            default: return false;
        }
    }

    private static Key ParseKey(string value)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "esc": return Key.Escape;
            case "space": return Key.Space;
            case "enter": return Key.Enter;
            case "up": return Key.Up;
            case "down": return Key.Down;
            case "left": return Key.Left;
            case "right": return Key.Right;
            case "pageup": return Key.PageUp;
            case "pagedown": return Key.PageDown;
            case "tab": return Key.Tab;
        }
        if (value.Length == 1 && value[0] is >= '1' and <= '9') return Key.D1 + (value[0] - '1');
        if (value.Length == 1 && value[0] == '0') return Key.D0;
        try { return (Key)new KeyConverter().ConvertFromInvariantString(value); }
        catch { return Key.None; }
    }

    private static string Format(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var name = key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + key - Key.D0)).ToString(),
            Key.Escape => "Esc",
            _ => key.ToString()
        };
        parts.Add(name);
        return string.Join("+", parts);
    }

    private static int TabNumberFromKey(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
        _ => 0
    };
}
