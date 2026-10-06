namespace WBDropp.Models;

public sealed class SiteDownloadResult
{
    public required string Article { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string? FolderPath { get; init; }
    public int CatalogImageCount { get; init; }
    public int DownloadedImageCount { get; init; }
    public int FailedImageCount { get; init; }
    public bool IsLegacy { get; init; }
    public string? FailureReason { get; init; }
    public IReadOnlyList<string> DownloadedPaths { get; init; } = [];
    public IReadOnlyList<string> Sizes { get; init; } = [];
    public IReadOnlyList<string> DownloadErrors { get; init; } = [];
    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath) && DownloadedImageCount > 0;
}

public sealed record SiteDownloadProgress(
    int CompletedArticles,
    int TotalArticles,
    string Message);
