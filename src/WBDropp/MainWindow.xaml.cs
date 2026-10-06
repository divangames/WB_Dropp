using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using WBDropp.Models;
using WBDropp.Services;

namespace WBDropp;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".tif", ".tiff"
    };

    private readonly ImageClassifier _classifier = new();
    private readonly DropletRunner _dropletRunner = new();
    private readonly CancellationTokenSource _windowCancellation = new();
    private CancellationTokenSource? _runCancellation;
    private bool _isScanning;
    private bool _isProcessing;
    private string? _outputPath;

    public ObservableCollection<ProductPack> Packs { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        RefreshPhotoshopStatus();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootGrid.RenderTransform = new TranslateTransform(0, 8);
        WindowSurface.Opacity = 0;
        WindowSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        ((TranslateTransform)RootGrid.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void RefreshPhotoshopStatus()
    {
        var running = Process.GetProcessesByName("Photoshop").Length > 0;
        PhotoshopDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#22C55E" : "#A3A3A3"));
        PhotoshopStatusText.Text = running ? "Photoshop готов" : "Photoshop запустится сам";
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку с фотографиями", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddFoldersAsync(dialog.FolderNames);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        SetDropZoneActive(false);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var dropped = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? [];
        var folders = dropped
            .Select(path => Directory.Exists(path) ? path : File.Exists(path) ? Path.GetDirectoryName(path) : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        await AddFoldersAsync(folders);
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropZoneActive(e.Effects == DragDropEffects.Copy);
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => SetDropZoneActive(false);

    private void SetDropZoneActive(bool active)
    {
        DropZone.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#EA580C" : "#D6D3D1"));
        DropZone.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#FFF7ED" : "#FBFBFA"));
    }

    private async Task AddFoldersAsync(IEnumerable<string> paths)
    {
        if (_isProcessing) return;
        var normalized = paths
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => Packs.All(pack => !string.Equals(pack.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (normalized.Count == 0) return;

        _isScanning = true;
        RunNotice.Visibility = Visibility.Collapsed;
        RefreshControls();

        foreach (var path in normalized)
        {
            var pack = new ProductPack(path);
            Packs.Add(pack);
            RefreshControls();

            try
            {
                var photos = await Task.Run(() => ScanFolder(path, _windowCancellation.Token), _windowCancellation.Token);
                pack.Photos.AddRange(photos);
                pack.NotifyScanComplete();
                AppLogger.Info($"Пак просканирован: {path}; фото={pack.TotalCount}; крупняк={pack.CloseUpCount}; пропуск={pack.UnsupportedCount}");
            }
            catch (OperationCanceledException)
            {
                pack.Status = "Сканирование отменено";
            }
            catch (Exception ex)
            {
                pack.HasError = true;
                pack.Status = "Ошибка сканирования";
                AppLogger.Error($"Ошибка сканирования {path}", ex);
            }
        }

        _isScanning = false;
        RefreshControls();
    }

    private List<PhotoItem> ScanFolder(string root, CancellationToken cancellationToken)
    {
        var result = new List<PhotoItem>();
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)));

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result.Add(_classifier.Classify(file, Path.GetRelativePath(root, file)));
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Не удалось прочитать изображение {file}", ex);
            }
        }
        return result;
    }

    private void RemovePack_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || sender is not FrameworkElement { Tag: ProductPack pack }) return;
        Packs.Remove(pack);
        RefreshControls();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        Packs.Clear();
        RunNotice.Visibility = Visibility.Collapsed;
        RefreshControls();
    }

    private void OutputMode_Checked(object sender, RoutedEventArgs e)
    {
        if (OutputFolderPanel is null) return;
        OutputFolderPanel.Visibility = FolderMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshControls();
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка для готовых фотографий", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        _outputPath = dialog.FolderName;
        OutputPathText.Text = _outputPath;
        OutputPathText.ToolTip = _outputPath;
        RefreshControls();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || _isScanning) return;

        try
        {
            _dropletRunner.ValidateDroplets();
            ValidateOutputSelection();
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, false);
            return;
        }

        _isProcessing = true;
        _runCancellation = new CancellationTokenSource();
        RunNotice.Visibility = Visibility.Collapsed;
        OverallProgress.Visibility = Visibility.Visible;
        OverallProgress.Value = 0;
        RefreshControls();

        var processable = Packs.SelectMany(pack => pack.Photos).Count(photo => photo.Droplet is not null);
        var completed = 0;
        var failed = 0;
        var cancellationToken = _runCancellation.Token;

        try
        {
            foreach (var pack in Packs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ready = pack.Photos.Where(photo => photo.Droplet is not null).ToList();
                var packCompleted = 0;
                var packFailed = 0;
                pack.IsComplete = false;
                pack.HasError = false;

                foreach (var photo in ready)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pack.Status = $"{Path.GetFileName(photo.SourcePath)} · {packCompleted + packFailed + 1}/{ready.Count}";
                    var targetPath = PrepareTargetPath(pack, photo);

                    try
                    {
                        AppLogger.Info($"Старт: {targetPath}; droplet={photo.Droplet}; foreground={photo.ForegroundRatio:F3}; edge={photo.EdgeTouchScore:F3}");
                        await _dropletRunner.ProcessAsync(targetPath, photo.Droplet!.Value, cancellationToken);
                        packCompleted++;
                        completed++;
                        AppLogger.Info($"Готово: {targetPath}");
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        packFailed++;
                        failed++;
                        AppLogger.Error($"Ошибка обработки {targetPath}", ex);
                    }

                    pack.Progress = ready.Count == 0 ? 100 : (packCompleted + packFailed) * 100.0 / ready.Count;
                    OverallProgress.Value = processable == 0 ? 100 : (completed + failed) * 100.0 / processable;
                }

                pack.IsComplete = packFailed == 0;
                pack.HasError = packFailed > 0;
                pack.Status = packFailed == 0 ? $"Готово · {packCompleted}" : $"Готово · {packCompleted}, ошибок · {packFailed}";
            }

            var message = failed == 0
                ? $"Готово. Обработано {completed} фото."
                : $"Обработано {completed} фото, ошибок: {failed}. Подробности: {AppLogger.CurrentLogPath}";
            ShowNotice(message, failed == 0);
        }
        catch (OperationCanceledException)
        {
            ShowNotice($"Остановлено. Успешно обработано {completed} фото.", false);
        }
        finally
        {
            _isProcessing = false;
            _runCancellation.Dispose();
            _runCancellation = null;
            RefreshPhotoshopStatus();
            RefreshControls();
        }
    }

    private string PrepareTargetPath(ProductPack pack, PhotoItem photo)
    {
        if (ReplaceMode.IsChecked == true) return photo.SourcePath;
        var packDestination = Path.Combine(_outputPath!, SanitizeDirectoryName(pack.Name));
        var destination = Path.Combine(packDestination, photo.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(photo.SourcePath, destination, true);
        return destination;
    }

    private void ValidateOutputSelection()
    {
        if (FolderMode.IsChecked != true) return;
        if (string.IsNullOrWhiteSpace(_outputPath)) throw new InvalidOperationException("Выберите папку для сохранения.");

        var outputRoot = Path.GetFullPath(_outputPath);
        foreach (var pack in Packs)
        {
            var sourceRoot = Path.GetFullPath(pack.SourcePath);
            var packDestination = Path.Combine(outputRoot, SanitizeDirectoryName(pack.Name));
            if (IsSameOrChild(outputRoot, sourceRoot) || string.Equals(packDestination.TrimEnd('\\'), sourceRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Папка сохранения пересекается с исходным паком «{pack.Name}». Выберите другую папку.");
        }
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        candidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDirectoryName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Останавливаем…";
        _runCancellation?.Cancel();
    }

    private void ShowNotice(string message, bool success)
    {
        RunNoticeText.Text = message;
        RunNoticeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#166534" : "#9A3412"));
        RunNotice.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#F0FDF4" : "#FFF7ED"));
        RunNotice.Visibility = Visibility.Visible;

        if (!SystemParameters.ClientAreaAnimation) return;
        RunNotice.Opacity = 0;
        RunNotice.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void RefreshControls()
    {
        var ready = Packs.Sum(pack => pack.ReadyCount);
        var closeUps = Packs.Sum(pack => pack.CloseUpCount);
        var unsupported = Packs.Sum(pack => pack.UnsupportedCount);

        EmptyState.Visibility = Packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = Packs.Count > 0 && !_isProcessing && !_isScanning;
        AddFolderButton.IsEnabled = !_isProcessing;
        ReplaceMode.IsEnabled = !_isProcessing;
        FolderMode.IsEnabled = !_isProcessing;
        OutputFolderPanel.IsEnabled = !_isProcessing;
        StartButton.IsEnabled = ready > 0 && !_isScanning && !_isProcessing && (FolderMode.IsChecked != true || !string.IsNullOrWhiteSpace(_outputPath));
        CancelButton.Visibility = _isProcessing ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _isProcessing;
        CancelButton.Content = "Остановить после текущего фото";

        if (_isProcessing)
        {
            StartButton.Content = "Photoshop обрабатывает…";
        }
        else if (_isScanning)
        {
            StartButton.Content = "Анализируем фотографии…";
        }
        else
        {
            StartButton.Content = ready > 0 ? $"Обработать {ready} фото" : "Добавьте папки";
        }

        SummaryText.Text = Packs.Count == 0
            ? "Добавьте папки с фотографиями"
            : $"Паков: {Packs.Count}   •   к обработке: {ready}   •   крупняк: {closeUps}" + (unsupported > 0 ? $"   •   пропуск: {unsupported}" : string.Empty);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isProcessing)
        {
            var answer = MessageBox.Show(this,
                "Сейчас Photoshop обрабатывает фотографию. Закрыть WB Dropp и остановить очередь после текущего файла?",
                "WB Dropp", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _runCancellation?.Cancel();
        }
        _windowCancellation.Cancel();
    }
}
