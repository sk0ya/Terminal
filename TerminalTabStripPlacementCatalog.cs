namespace ConPtyTerminal;

public static class TerminalTabStripPlacementCatalog
{
    public const string Top = "top";
    public const string Bottom = "bottom";
    public const string Left = "left";
    public const string Right = "right";

    private static readonly IReadOnlyList<TerminalTabStripPlacementOption> Options =
    [
        new(Top, "Top"),
        new(Bottom, "Bottom"),
        new(Left, "Left"),
        new(Right, "Right")
    ];

    public static IReadOnlyList<TerminalTabStripPlacementOption> CreateOptions() => Options;

    public static string Normalize(string? placement)
    {
        return placement?.Trim().ToLowerInvariant() switch
        {
            Bottom => Bottom,
            Left => Left,
            Right => Right,
            _ => Top
        };
    }

    public static TerminalTabStripPlacementOption ResolveSelectedOption(string? placement)
    {
        string normalizedPlacement = Normalize(placement);
        return Options.First(option => string.Equals(option.Id, normalizedPlacement, StringComparison.Ordinal));
    }

    public static bool IsHorizontal(string? placement)
    {
        string normalizedPlacement = Normalize(placement);
        return normalizedPlacement is Top or Bottom;
    }

    public static bool IsVertical(string? placement) => !IsHorizontal(placement);
}

public sealed record TerminalTabStripPlacementOption(string Id, string DisplayName);
