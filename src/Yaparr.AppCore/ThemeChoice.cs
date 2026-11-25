namespace Yaparr.AppCore;

/// <summary>The choices for <see cref="AppSettings.Theme"/>: the colours follow the system, or are always light, or always dark.</summary>
public static class ThemeChoice
{
    public const string System = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    public static readonly string[] All = { System, Light, Dark };

    /// <summary>The choice as it is kept, or System for anything that is not one (an empty or damaged settings file).</summary>
    public static string Normalize(string? choice) =>
        All.FirstOrDefault(c => string.Equals(c, choice?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? System;
}
