namespace WBDropp.Models;

public enum PhotoFormat
{
    Modern,
    Legacy,
    Unsupported
}

public enum DropletKind
{
    Modern,
    ModernCloseUp,
    Legacy,
    LegacyCloseUp
}

public sealed record PhotoItem(
    string SourcePath,
    string RelativePath,
    int Width,
    int Height,
    PhotoFormat Format,
    bool IsCloseUp,
    double ForegroundRatio,
    double EdgeTouchScore)
{
    public DropletKind? Droplet => Format switch
    {
        PhotoFormat.Modern when IsCloseUp => DropletKind.ModernCloseUp,
        PhotoFormat.Modern => DropletKind.Modern,
        PhotoFormat.Legacy when IsCloseUp => DropletKind.LegacyCloseUp,
        PhotoFormat.Legacy => DropletKind.Legacy,
        _ => null
    };
}
