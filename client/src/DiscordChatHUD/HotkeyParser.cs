namespace DiscordChatHUD;

internal sealed record ParsedHotkey(bool Control, bool Alt, bool Shift, Keys Key)
{
    public bool IsDisabled => Key == Keys.None;
}

internal static class HotkeyParser
{
    public static string Normalize(string? value, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var compact = raw.Replace(" ", string.Empty).ToUpperInvariant();
        if (compact is "NONE" or "OFF" or "DISABLE" or "DISABLED" or "없음" or "끄기")
            return "NONE";

        var parts = compact.Split(['+', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizePart)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var ordered = new List<string>();
        foreach (var modifier in new[] { "CTRL", "ALT", "SHIFT" })
            if (parts.Contains(modifier)) ordered.Add(modifier);
        ordered.AddRange(parts.Where(x => x is not "CTRL" and not "ALT" and not "SHIFT"));
        return TryParse(string.Join('+', ordered), out _) ? string.Join('+', ordered) : fallback;
    }

    public static bool TryParse(string? value, out ParsedHotkey hotkey)
    {
        hotkey = new ParsedHotkey(false, false, false, Keys.None);
        var normalized = (value ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        if (normalized is "" or "NONE" or "OFF" or "DISABLE" or "DISABLED") return true;

        var control = false;
        var alt = false;
        var shift = false;
        var key = Keys.None;
        foreach (var rawPart in normalized.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = NormalizePart(rawPart);
            switch (part)
            {
                case "CTRL": control = true; break;
                case "ALT": alt = true; break;
                case "SHIFT": shift = true; break;
                default:
                    if (key != Keys.None || !TryParseKey(part, out key)) return false;
                    break;
            }
        }
        if (key == Keys.None) return false;
        hotkey = new ParsedHotkey(control, alt, shift, key);
        return true;
    }

    private static string NormalizePart(string value) => value.ToUpperInvariant() switch
    {
        "CONTROL" or "LCTRL" or "RCTRL" => "CTRL",
        "MENU" or "LALT" or "RALT" => "ALT",
        "LSHIFT" or "RSHIFT" => "SHIFT",
        "ESCAPE" => "ESC",
        "PGUP" or "PRIOR" => "PAGEUP",
        "PGDN" or "NEXT" => "PAGEDOWN",
        "RETURN" => "ENTER",
        var x => x
    };

    private static bool TryParseKey(string value, out Keys key)
    {
        key = value switch
        {
            "ESC" => Keys.Escape,
            "PAGEUP" => Keys.PageUp,
            "PAGEDOWN" => Keys.PageDown,
            "ENTER" => Keys.Enter,
            "SPACE" => Keys.Space,
            "TAB" => Keys.Tab,
            "HOME" => Keys.Home,
            "END" => Keys.End,
            "INSERT" => Keys.Insert,
            "DELETE" => Keys.Delete,
            "BACKSPACE" => Keys.Back,
            "UP" => Keys.Up,
            "DOWN" => Keys.Down,
            "LEFT" => Keys.Left,
            "RIGHT" => Keys.Right,
            _ => Keys.None
        };
        if (key != Keys.None) return true;
        if (value.Length == 1 && char.IsLetterOrDigit(value[0]))
        {
            key = (Keys)char.ToUpperInvariant(value[0]);
            return true;
        }
        if (value.StartsWith('F') && int.TryParse(value[1..], out var function) && function is >= 1 and <= 24)
        {
            key = (Keys)((int)Keys.F1 + function - 1);
            return true;
        }
        return false;
    }
}
