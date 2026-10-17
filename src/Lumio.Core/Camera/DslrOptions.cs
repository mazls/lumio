using System.Globalization;

namespace Lumio.Core.Camera;

/// <summary>A selectable exposure value: <see cref="Value"/> is stored, <see cref="Label"/> is shown.</summary>
public sealed record DslrOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Vendor-neutral DSLR exposure values used by the admin menu and translated by every camera driver.
/// </summary>
public static class DslrOptions
{
    public const string Auto = "Auto";

    public static IReadOnlyList<DslrOption> Iso { get; } =
    [
        new(Auto, "Auto"),
        new("100", "ISO 100"),
        new("200", "ISO 200"),
        new("400", "ISO 400 (Blitz-Empfehlung)"),
        new("800", "ISO 800"),
        new("1600", "ISO 1600"),
        new("3200", "ISO 3200"),
        new("6400", "ISO 6400"),
    ];

    public static IReadOnlyList<DslrOption> ShutterSpeeds { get; } =
    [
        new(Auto, "Auto"),
        new("1/30", "1/30 s"),
        new("1/60", "1/60 s"),
        new("1/100", "1/100 s"),
        new("1/125", "1/125 s (Blitz-Standard)"),
        new("1/160", "1/160 s"),
        new("1/200", "1/200 s"),
        new("1/250", "1/250 s"),
    ];

    public static IReadOnlyList<DslrOption> Apertures { get; } =
    [
        new(Auto, "Auto"),
        new("2.8", "f/2.8"),
        new("3.5", "f/3.5"),
        new("4", "f/4"),
        new("5.6", "f/5.6 (Empfohlen)"),
        new("8", "f/8"),
        new("11", "f/11"),
    ];

    public static IReadOnlyList<DslrOption> WhiteBalances { get; } =
    [
        new(Auto, "Auto"),
        new("Daylight", "Tageslicht (5400K)"),
        new("Flash", "Blitz (5600K)"),
        new("Tungsten", "Kunstlicht (3200K)"),
        new("Fluorescent", "Leuchtstoff (4000K)"),
        new("Cloudy", "Bewölkt (6500K)"),
        new("Shade", "Schatten (7500K)"),
    ];

    public static IReadOnlyList<DslrOption> ExposureCompensations { get; } =
    [
        new("-2", "-2 EV (viel dunkler)"),
        new("-1", "-1 EV (dunkler)"),
        new("0", "0 EV (Standard)"),
        new("+1", "+1 EV (heller)"),
        new("+2", "+2 EV (viel heller)"),
    ];

    /// <summary>Synonyms vendors use for the neutral white balance ids (lower case).</summary>
    public static IReadOnlyDictionary<string, string[]> WhiteBalanceSynonyms { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        [Auto] = ["auto", "automatic", "awb", "auto1", "auto white balance"],
        ["Daylight"] = ["daylight", "direct sunlight", "sunny", "fine weather", "fine"],
        ["Flash"] = ["flash", "speedlight"],
        ["Tungsten"] = ["tungsten", "incandescent", "artificial light", "light bulb"],
        ["Fluorescent"] = ["fluorescent", "white fluorescent", "fluorescent light"],
        ["Cloudy"] = ["cloudy", "overcast"],
        ["Shade"] = ["shade", "shadow"],
    };

    public static DslrOption Find(IReadOnlyList<DslrOption> options, string? value) =>
        options.FirstOrDefault(o => string.Equals(o.Value, value, StringComparison.OrdinalIgnoreCase))
        ?? options[0];

    public static string NormalizeIso(string? raw)
    {
        var s = StripLabel(raw).Replace("ISO", "", StringComparison.OrdinalIgnoreCase).Trim();
        return IsAuto(s) ? Auto : s;
    }

    public static string NormalizeShutterSpeed(string? raw)
    {
        var s = StripLabel(raw).TrimEnd('s', 'S').Trim();
        return IsAuto(s) ? Auto : s;
    }

    public static string NormalizeAperture(string? raw)
    {
        var s = StripLabel(raw);
        if (s.StartsWith("f/", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        s = s.Replace(',', '.').Trim();
        if (IsAuto(s))
            return Auto;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
            ? f.ToString("0.#", CultureInfo.InvariantCulture)
            : s;
    }

    public static string NormalizeWhiteBalance(string? raw)
    {
        var s = StripLabel(raw);
        if (IsAuto(s))
            return Auto;

        // Old settings stored the German label, e.g. "Tageslicht (5400K)".
        var byLabel = WhiteBalances.FirstOrDefault(o => o.Label.StartsWith(s, StringComparison.OrdinalIgnoreCase));
        if (byLabel is not null)
            return byLabel.Value;

        foreach (var (id, synonyms) in WhiteBalanceSynonyms)
        {
            if (synonyms.Any(syn => syn.Equals(s, StringComparison.OrdinalIgnoreCase)) || id.Equals(s, StringComparison.OrdinalIgnoreCase))
                return id;
        }

        return s;
    }

    public static string NormalizeExposureCompensation(string? raw)
    {
        var s = StripLabel(raw).Replace("EV", "", StringComparison.OrdinalIgnoreCase).Replace(',', '.').Trim();
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ev))
            return "0";
        if (ev == 0)
            return "0";
        var formatted = ev.ToString("0.#", CultureInfo.InvariantCulture);
        return ev > 0 ? "+" + formatted : formatted;
    }

    /// <summary>Parses camera specific number strings like "f/5.6", "5,6", "+1.0", "1/125" into a double.</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.StartsWith("f/", StringComparison.OrdinalIgnoreCase) || s.StartsWith("f", StringComparison.OrdinalIgnoreCase))
            s = s.TrimStart('f', 'F', '/');
        s = s.Replace("ISO", "", StringComparison.OrdinalIgnoreCase)
             .Replace("EV", "", StringComparison.OrdinalIgnoreCase)
             .Replace(',', '.')
             .TrimEnd('s', 'S', '"')
             .Trim();

        var slash = s.IndexOf('/');
        if (slash > 0
            && double.TryParse(s[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(s[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den != 0)
        {
            value = num / den;
            return true;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Picks the camera's own value that best matches a neutral value, e.g. "5.6" → "f/5.6" or "1/125" → "1/125".
    /// Returns <c>null</c> if the camera does not offer a matching value.
    /// </summary>
    public static string? MatchCameraValue(IEnumerable<string> cameraValues, string neutralValue, DslrSetting setting)
    {
        var choices = cameraValues.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        if (choices.Count == 0 || string.IsNullOrWhiteSpace(neutralValue))
            return null;

        var exact = choices.FirstOrDefault(c => c.Equals(neutralValue, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        if (setting == DslrSetting.WhiteBalance)
        {
            if (!WhiteBalanceSynonyms.TryGetValue(neutralValue, out var synonyms))
                return null;
            return choices.FirstOrDefault(c => synonyms.Any(s => c.Equals(s, StringComparison.OrdinalIgnoreCase)))
                   ?? choices.FirstOrDefault(c => synonyms.Any(s => c.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }

        if (IsAuto(neutralValue))
            return choices.FirstOrDefault(c => c.Contains("auto", StringComparison.OrdinalIgnoreCase));

        if (!TryParseNumber(neutralValue, out var target))
            return null;

        // Numeric match with a small tolerance (cameras report 1/125 as "1/125", "0.008" or "1/125s").
        foreach (var c in choices)
        {
            if (TryParseNumber(c, out var v) && Math.Abs(v - target) <= Math.Max(0.0001, Math.Abs(target) * 0.02))
                return c;
        }

        return null;
    }

    private static string StripLabel(string? raw)
    {
        var s = (raw ?? string.Empty).Trim();
        var paren = s.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0)
            s = s[..paren];
        return s.Trim();
    }

    private static bool IsAuto(string s) =>
        s.Length == 0 || s.Equals(Auto, StringComparison.OrdinalIgnoreCase) || s.Equals("Automatisch", StringComparison.OrdinalIgnoreCase);
}

public enum DslrSetting
{
    Iso,
    ShutterSpeed,
    Aperture,
    WhiteBalance,
    ExposureCompensation,
}
