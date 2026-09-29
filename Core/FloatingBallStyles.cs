using System.IO;

namespace Lychee.Core;

/// <summary>
/// The visual styles available for the floating ball.
/// Add a resource and one definition here to make a new style available in Settings.
/// </summary>
public sealed record FloatingBallStyleDefinition(string Id, string DisplayName, string ResourcePath);

public static class FloatingBallStyles
{
    public const string DefaultId = "default";
    public const string BasketballId = "basketball";
    public const string CustomId = "custom";
    public const string PixelLycheeId = "pixel";

    public static IReadOnlyList<FloatingBallStyleDefinition> All { get; } =
    [
        new(DefaultId, "Default", "Assets/icon.png"),
        new(BasketballId, "Basketball", "Assets/basketball.png"),
        new("planet", "Planet", "Assets/planet.png"),
        new("moon", "Moon", "Assets/moon.png"),
        new(PixelLycheeId, "Pixel Lychee", "Assets/pixel-lychee.gif")
    ];

    public static IReadOnlyList<FloatingBallStyleDefinition> GetAvailable(string? customImagePath)
    {
        if (string.IsNullOrWhiteSpace(customImagePath) || !File.Exists(customImagePath)) return All;
        return All.Concat([new FloatingBallStyleDefinition(CustomId, "Custom", customImagePath!)]).ToList();
    }

    public static FloatingBallStyleDefinition Get(string? id, string? customImagePath = null)
    {
        return GetAvailable(customImagePath).FirstOrDefault(style =>
                   string.Equals(style.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? All[0];
    }
}
