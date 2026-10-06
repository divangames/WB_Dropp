using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WBDropp.Models;

public sealed class ProductPack : INotifyPropertyChanged
{
    private string _status = "Сканирование…";
    private double _progress;
    private bool _isComplete;
    private bool _hasError;

    public ProductPack(string sourcePath) => SourcePath = sourcePath;

    public string SourcePath { get; }
    public string Name => Path.GetFileName(SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public List<PhotoItem> Photos { get; } = [];
    public int TotalCount => Photos.Count;
    public int ReadyCount => Photos.Count(photo => photo.Format != PhotoFormat.Unsupported);
    public int ModernCount => Photos.Count(photo => photo.Format == PhotoFormat.Modern);
    public int LegacyCount => Photos.Count(photo => photo.Format == PhotoFormat.Legacy);
    public int CloseUpCount => Photos.Count(photo => photo.IsCloseUp && photo.Format != PhotoFormat.Unsupported);
    public int UnsupportedCount => Photos.Count(photo => photo.Format == PhotoFormat.Unsupported);

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{ReadyCount} фото" };
            if (ModernCount > 0) parts.Add($"1400×1050 · {ModernCount}");
            if (LegacyCount > 0) parts.Add($"1000×750 · {LegacyCount}");
            if (CloseUpCount > 0) parts.Add($"крупняк · {CloseUpCount}");
            if (UnsupportedCount > 0) parts.Add($"пропуск · {UnsupportedCount}");
            return string.Join("   •   ", parts);
        }
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public double Progress
    {
        get => _progress;
        set => SetField(ref _progress, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        set => SetField(ref _isComplete, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => SetField(ref _hasError, value);
    }

    public void NotifyScanComplete()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(ReadyCount));
        OnPropertyChanged(nameof(ModernCount));
        OnPropertyChanged(nameof(LegacyCount));
        OnPropertyChanged(nameof(CloseUpCount));
        OnPropertyChanged(nameof(UnsupportedCount));
        OnPropertyChanged(nameof(Summary));
        Status = ReadyCount == 0 ? "Нет подходящих фото" : "Готов к обработке";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }
}
