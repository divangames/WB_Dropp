namespace WBDropp.Models;

public sealed record AppUpdate(
    Version Version,
    string Tag,
    string AssetName,
    Uri DownloadUri,
    string? Sha256);

public sealed record DownloadedAppUpdate(
    AppUpdate Release,
    string PayloadDirectory);
