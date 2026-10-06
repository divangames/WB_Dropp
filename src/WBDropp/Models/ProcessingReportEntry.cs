namespace WBDropp.Models;

public sealed class ProcessingReportEntry
{
    public string Source { get; set; } = "Папка";
    public string Article { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string DownloadStatus { get; set; } = "Не требуется";
    public int CatalogImages { get; set; }
    public int DownloadedImages { get; set; }
    public int DownloadErrors { get; set; }
    public int InputImages { get; set; }
    public int ReadyImages { get; set; }
    public int CloseUpImages { get; set; }
    public int UnsupportedImages { get; set; }
    public int ProcessedImages { get; set; }
    public int ProcessingErrors { get; set; }
    public string Formats { get; set; } = string.Empty;
    public string Status { get; set; } = "Ожидает";
    public string Reason { get; set; } = string.Empty;
}

public sealed record ReportFiles(string TextPath);
