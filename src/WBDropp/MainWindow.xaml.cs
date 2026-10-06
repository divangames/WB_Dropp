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
    private readonly SiteCatalogDownloader _siteDownloader = new();
    private readonly ReportService _reportService = new();
    private readonly UpdateService _updateService = new();
    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly List<ProcessingReportEntry> _reportEntries = [];
    private readonly Dictionary<string, ProcessingReportEntry> _reportsByPackPath = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCancellation;
    private bool _isScanning;
    private bool _isProcessing;
    private bool _sourceModeInitialized;
    private bool _lastSourceWasSite;
    private bool _suppressSelectionEvents;
    private bool _isUpdateDownloading;
    private string? _outputPath;
    private string _downloadPath;
    private string? _lastReportPath;
    private string? _activeActionText;
    private DownloadedAppUpdate? _downloadedUpdate;
    private DateTime? _sessionStartedAt;
    private TimeSpan _accumulatedWorkDuration;

    public ObservableCollection<ProductPack> Packs { get; } = [];
    private bool IsSiteMode => SiteSourceMode.IsChecked == true;

    public MainWindow()
    {
        _downloadPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "WB Dropp");

        InitializeComponent();
        DataContext = this;
        Packs.CollectionChanged += Packs_CollectionChanged;
        DownloadPathText.Text = _downloadPath;
        DownloadPathText.ToolTip = _downloadPath;
        _sourceModeInitialized = true;
        _lastSourceWasSite = IsSiteMode;
        ApplySourceMode(clearState: false);
        RefreshPhotoshopStatus();
        RefreshControls();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (SystemParameters.ClientAreaAnimation)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            RootGrid.RenderTransform = new TranslateTransform(0, 8);
            WindowSurface.Opacity = 0;
            WindowSurface.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            ((TranslateTransform)RootGrid.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }

        await CheckForUpdatesAsync();
    }

    private void RefreshPhotoshopStatus()
    {
        var running = Process.GetProcessesByName("Photoshop").Length > 0;
        PhotoshopDot.Fill = BrushFrom(running ? "#22C55E" : "#A3A3A3");
        PhotoshopStatusText.Text = running ? "Photoshop готов" : "Photoshop запустится сам";
    }

    private void SourceMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_sourceModeInitialized || SitePanel is null) return;
        var modeChanged = _lastSourceWasSite != IsSiteMode;
        _lastSourceWasSite = IsSiteMode;
        ApplySourceMode(modeChanged);
    }

    private void ApplySourceMode(bool clearState)
    {
        if (clearState && !_isProcessing)
        {
            Packs.Clear();
            _reportEntries.Clear();
            _reportsByPackPath.Clear();
            _lastReportPath = null;
            ResetRunTiming();
            RunNotice.Visibility = Visibility.Collapsed;
            OpenReportButton.Visibility = Visibility.Collapsed;
        }

        SitePanel.Visibility = IsSiteMode ? Visibility.Visible : Visibility.Collapsed;
        DropZone.Visibility = IsSiteMode ? Visibility.Collapsed : Visibility.Visible;
        AddFolderButton.Visibility = IsSiteMode ? Visibility.Collapsed : Visibility.Visible;
        SourceTitleText.Text = IsSiteMode ? "Товары с сайта" : "Паки товаров";
        DownloadOnlyCheckBox.Visibility = IsSiteMode ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadOnlyPresentation();
        RefreshControls();
    }

    private void DownloadOnly_Checked(object sender, RoutedEventArgs e)
    {
        if (ResultOptionsPanel is null) return;
        UpdateDownloadOnlyPresentation();
        RefreshControls();
    }

    private void UpdateDownloadOnlyPresentation()
    {
        if (ResultOptionsPanel is null) return;
        var downloadOnly = IsSiteMode && DownloadOnlyCheckBox.IsChecked == true;
        ResultOptionsPanel.Visibility = downloadOnly ? Visibility.Collapsed : Visibility.Visible;
        OutputFolderPanel.Visibility = downloadOnly
            ? Visibility.Collapsed
            : FolderMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RoutingPanel.Visibility = downloadOnly ? Visibility.Collapsed : Visibility.Visible;
        DownloadOnlyHint.Visibility = downloadOnly ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Packs_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ProductPack pack in e.OldItems) pack.PropertyChanged -= Pack_PropertyChanged;
        if (e.NewItems is not null)
            foreach (ProductPack pack in e.NewItems) pack.PropertyChanged += Pack_PropertyChanged;
        if (DownloadOnlyCheckBox is not null) UpdateDownloadOnlyPresentation();
        RefreshControls();
    }

    private void Pack_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProductPack.IsSelected) && !_suppressSelectionEvents) RefreshControls();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || _isScanning) return;
        var select = SelectAllCheckBox.IsChecked == true;
        _suppressSelectionEvents = true;
        foreach (var pack in Packs) pack.IsSelected = select;
        _suppressSelectionEvents = false;
        RefreshControls();
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Выберите папку с фотографиями", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await AddFoldersAsync(dialog.FolderNames, "Папка");
    }

    private void ImportArticles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт артикулов",
            Multiselect = true,
            Filter = "Списки артикулов (*.txt;*.doc;*.docx)|*.txt;*.doc;*.docx|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true) ImportArticleFiles(dialog.FileNames);
    }

    private void ChooseDownload_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Папка для скачанных фотографий",
            Multiselect = false,
            InitialDirectory = Directory.Exists(_downloadPath) ? _downloadPath : null
        };
        if (dialog.ShowDialog(this) != true) return;
        _downloadPath = dialog.FolderName;
        DownloadPathText.Text = _downloadPath;
        DownloadPathText.ToolTip = _downloadPath;
        RefreshControls();
    }

    private void ArticlesInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ArticlesPlaceholder is null) return;
        ArticlesPlaceholder.Visibility = string.IsNullOrWhiteSpace(ArticlesInput.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshControls();
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        SetDropZoneActive(false);
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var dropped = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? [];

        if (IsSiteMode)
        {
            ImportArticleFiles(dropped.Where(File.Exists));
            return;
        }

        var folders = dropped
            .Select(path => Directory.Exists(path) ? path : File.Exists(path) ? Path.GetDirectoryName(path) : null)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        await AddFoldersAsync(folders, "Папка");
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
        }
        else if (IsSiteMode)
        {
            var files = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? [];
            e.Effects = files.Any(path => File.Exists(path) && ArticleParser.IsSupportedFile(path))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
            SetDropZoneActive(true);
        }
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => SetDropZoneActive(false);

    private void SetDropZoneActive(bool active)
    {
        if (IsSiteMode) return;
        DropZone.BorderBrush = BrushFrom(active ? "#EA580C" : "#D6D3D1");
        DropZone.Background = BrushFrom(active ? "#FFF7ED" : "#FBFBFA");
    }

    private void ImportArticleFiles(IEnumerable<string> paths)
    {
        try
        {
            var files = paths.Where(ArticleParser.IsSupportedFile).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0)
            {
                ShowNotice("Поддерживаются файлы .txt, .doc и .docx со списком артикулов.", false);
                return;
            }

            var current = ArticleParser.Parse(ArticlesInput.Text);
            var imported = ArticleParser.ParseFiles(files);
            var merged = current.Concat(imported).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            ArticlesInput.Text = string.Join(Environment.NewLine, merged);
            ShowNotice($"Импортировано артикулов: {imported.Count}. Всего уникальных: {merged.Count}.", true, showReport: false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка импорта артикулов", ex);
            ShowNotice($"Не удалось прочитать список артикулов: {ex.Message}", false);
        }
    }

    private async Task AddFoldersAsync(IEnumerable<string> paths, string source)
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
                var scan = await Task.Run(() => ScanFolder(path, _windowCancellation.Token), _windowCancellation.Token);
                pack.Photos.AddRange(scan.Photos);
                pack.NotifyScanComplete();
                RegisterPackReport(pack, source, scanErrors: scan.Errors);
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
                RegisterScanError(pack, source, ex.Message);
                AppLogger.Error($"Ошибка сканирования {path}", ex);
            }
        }

        _isScanning = false;
        RefreshControls();
    }

    private ScanResult ScanFolder(string root, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)));
        return ScanFiles(root, files, cancellationToken);
    }

    private ScanResult ScanFiles(string root, IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var result = new List<PhotoItem>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                result.Add(_classifier.Classify(file, Path.GetRelativePath(root, file)));
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)} — {ex.Message}");
                AppLogger.Error($"Не удалось прочитать изображение {file}", ex);
            }
        }
        return new ScanResult(result, errors);
    }

    private void RegisterPackReport(
        ProductPack pack,
        string source,
        SiteDownloadResult? site = null,
        IReadOnlyList<string>? scanErrors = null)
    {
        if (_reportsByPackPath.TryGetValue(pack.SourcePath, out var previous)) _reportEntries.Remove(previous);

        var unsupported = pack.Photos
            .Where(photo => photo.Format == PhotoFormat.Unsupported)
            .GroupBy(photo => $"{photo.Width}×{photo.Height}")
            .Select(group => $"{group.Key} — {group.Count()} фото")
            .ToList();
        var reasonParts = new List<string>();
        if (unsupported.Count > 0)
            reasonParts.Add("Неподдерживаемые размеры: " + string.Join(", ", unsupported));
        if (site?.DownloadErrors.Count > 0)
            reasonParts.AddRange(site.DownloadErrors);
        if (scanErrors?.Count > 0)
            reasonParts.Add("Не удалось прочитать: " + string.Join("; ", scanErrors));

        var entry = new ProcessingReportEntry
        {
            Source = source,
            Article = site?.Article ?? ExtractArticle(pack.Name),
            ProductName = site?.ProductName ?? string.Empty,
            FolderPath = pack.SourcePath,
            DownloadStatus = site is null
                ? "Не требуется"
                : site.FailedImageCount == 0 ? "✅ Скачано полностью" : "⚠️ Скачано частично",
            CatalogImages = site?.CatalogImageCount ?? 0,
            DownloadedImages = site?.DownloadedImageCount ?? 0,
            DownloadErrors = site?.FailedImageCount ?? 0,
            InputImages = pack.TotalCount + (scanErrors?.Count ?? 0),
            ReadyImages = pack.ReadyCount,
            CloseUpImages = pack.CloseUpCount,
            UnsupportedImages = pack.UnsupportedCount,
            Formats = BuildFormatSummary(pack),
            Status = pack.ReadyCount > 0 ? "Ожидает обработки" : "Пропущено",
            Reason = pack.ReadyCount > 0
                ? string.Join("; ", reasonParts)
                : reasonParts.Count > 0
                    ? "Нет фото 1400×1050 или 1000×750. " + string.Join("; ", reasonParts)
                    : "В папке нет поддерживаемых изображений."
        };
        _reportEntries.Add(entry);
        _reportsByPackPath[pack.SourcePath] = entry;
    }

    private void RegisterSkippedSiteResult(SiteDownloadResult result)
    {
        _reportEntries.Add(new ProcessingReportEntry
        {
            Source = "Сайт OutmaxShop",
            Article = result.Article,
            ProductName = result.ProductName,
            FolderPath = result.FolderPath ?? "Папка не создана",
            DownloadStatus = "❌ Не скачано",
            CatalogImages = result.CatalogImageCount,
            DownloadedImages = result.DownloadedImageCount,
            DownloadErrors = result.FailedImageCount,
            Status = "Пропущено",
            Reason = result.FailureReason ?? "Неизвестная ошибка загрузки.",
            Formats = "Нет изображений"
        });
    }

    private void RegisterScanError(ProductPack pack, string source, string reason)
    {
        var entry = new ProcessingReportEntry
        {
            Source = source,
            Article = ExtractArticle(pack.Name),
            FolderPath = pack.SourcePath,
            Status = "Ошибка",
            Reason = "Не удалось просканировать папку: " + reason
        };
        _reportEntries.Add(entry);
        _reportsByPackPath[pack.SourcePath] = entry;
    }

    private void RemovePack_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || sender is not FrameworkElement { Tag: ProductPack pack }) return;
        Packs.Remove(pack);
        if (_reportsByPackPath.Remove(pack.SourcePath, out var report)) _reportEntries.Remove(report);
        RefreshControls();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        Packs.Clear();
        if (IsSiteMode) ArticlesInput.Clear();
        _reportEntries.Clear();
        _reportsByPackPath.Clear();
        _lastReportPath = null;
        ResetRunTiming();
        RunNotice.Visibility = Visibility.Collapsed;
        OpenReportButton.Visibility = Visibility.Collapsed;
        OverallProgress.Visibility = Visibility.Collapsed;
        OverallProgress.Value = 0;
        UpdateDownloadOnlyPresentation();
        RefreshControls();
    }

    private void OutputMode_Checked(object sender, RoutedEventArgs e)
    {
        if (OutputFolderPanel is null) return;
        UpdateDownloadOnlyPresentation();
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

        var isSiteDownloadStage = IsSiteMode && Packs.Count == 0;
        var articles = isSiteDownloadStage ? ArticleParser.Parse(ArticlesInput.Text) : [];
        if (isSiteDownloadStage && articles.Count == 0)
        {
            ShowNotice("Введите хотя бы один артикул или импортируйте список.", false);
            return;
        }

        if (!isSiteDownloadStage)
        {
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
        }

        _isProcessing = true;
        _runCancellation = new CancellationTokenSource();
        _lastReportPath = null;
        RunNotice.Visibility = Visibility.Collapsed;
        OpenReportButton.Visibility = Visibility.Collapsed;
        OverallProgress.Visibility = Visibility.Visible;
        OverallProgress.Value = 0;
        _activeActionText = isSiteDownloadStage ? "Загружаем каталог…" : "Готовим Photoshop…";
        RefreshControls();

        var completed = 0;
        var failed = 0;
        var cancellationToken = _runCancellation.Token;
        var reportDirectory = GetReportDirectory();
        _sessionStartedAt ??= DateTime.Now;
        var operationTimer = Stopwatch.StartNew();
        var runFinalized = false;
        var downloadOnly = isSiteDownloadStage && DownloadOnlyCheckBox.IsChecked == true;

        try
        {
            if (isSiteDownloadStage)
            {
                Packs.Clear();
                _reportEntries.Clear();
                _reportsByPackPath.Clear();

                var progress = new Progress<SiteDownloadProgress>(value =>
                {
                    _activeActionText = value.TotalArticles == 0
                        ? "Загружаем каталог…"
                        : $"Скачиваем · {value.CompletedArticles}/{value.TotalArticles}";
                    OverallProgress.Value = value.TotalArticles == 0
                        ? 0
                        : value.CompletedArticles * 100.0 / value.TotalArticles;
                    RefreshControls();
                });

                var results = await _siteDownloader.DownloadAsync(articles, _downloadPath, progress, cancellationToken);
                foreach (var result in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!result.HasFolder)
                    {
                        RegisterSkippedSiteResult(result);
                        continue;
                    }

                    var pack = new ProductPack(result.FolderPath!);
                    Packs.Add(pack);
                    var scan = await Task.Run(
                        () => ScanFiles(result.FolderPath!, result.DownloadedPaths, cancellationToken),
                        cancellationToken);
                    pack.Photos.AddRange(scan.Photos);
                    pack.NotifyScanComplete();
                    RegisterPackReport(pack, "Сайт OutmaxShop", result, scan.Errors);
                }

                _accumulatedWorkDuration += operationTimer.Elapsed;
                operationTimer.Restart();
                OverallProgress.Value = 100;

                if (downloadOnly)
                {
                    MarkDownloadOnlyComplete();
                    var reports = SaveReport(reportDirectory, true, _accumulatedWorkDuration);
                    _lastReportPath = reports.TextPath;
                    runFinalized = true;
                    var skipped = _reportEntries.Count(entry => entry.Status is "Пропущено" or "Ошибка");
                    ShowNotice(
                        $"Выгрузка завершена: артикулов {Packs.Count}, фотографий {Packs.Sum(pack => pack.TotalCount)}." +
                        (skipped > 0 ? $" Пропущено: {skipped}." : string.Empty) +
                        "\n📝 Отчёт для менеджера готов.",
                        skipped == 0,
                        showReport: true);
                    return;
                }

                if (Packs.Count == 0)
                {
                    var reports = SaveReport(reportDirectory, false, _accumulatedWorkDuration);
                    _lastReportPath = reports.TextPath;
                    runFinalized = true;
                    ShowNotice("Не найдено ни одного пака для обработки. Причины записаны в отчёте.", false, showReport: true);
                    return;
                }

                ShowNotice(
                    $"Скачано паков: {Packs.Count}. Отметьте нужные галочками и запустите кадрирование.",
                    true);
                return;
            }

            (completed, failed) = await ProcessPacksAsync(0, cancellationToken);
            var workDuration = _accumulatedWorkDuration + operationTimer.Elapsed;
            var finishedReports = SaveReport(reportDirectory, false, workDuration);
            _lastReportPath = finishedReports.TextPath;
            runFinalized = true;

            var issues = _reportEntries.Count(entry => entry.Status is not "Готово" and not "Скачано");
            var message = failed == 0 && issues == 0
                ? $"Готово: обработано {completed} фото.\n📝 Отчёт для менеджера сохранён рядом с результатом."
                : $"Готово: обработано {completed}, позиций с замечаниями: {issues}, ошибок Photoshop: {failed}.\n📝 Причины записаны в отчёте для менеджера.";
            ShowNotice(message, failed == 0 && issues == 0, showReport: true);
        }
        catch (OperationCanceledException)
        {
            foreach (var entry in _reportEntries.Where(entry => entry.Status == "Ожидает обработки"))
            {
                entry.Status = "Остановлено";
                entry.Reason = AppendReason(entry.Reason, "Обработка остановлена пользователем.");
            }
            var workDuration = _accumulatedWorkDuration + operationTimer.Elapsed;
            var reports = SaveReport(reportDirectory, downloadOnly, workDuration);
            _lastReportPath = reports.TextPath;
            runFinalized = true;
            ShowNotice($"Остановлено. Успешно обработано {completed} фото.\n📝 Отчёт сохранён.", false, showReport: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Ошибка выполнения", ex);
            if (_reportEntries.Count == 0)
            {
                _reportEntries.Add(new ProcessingReportEntry
                {
                    Source = IsSiteMode ? "Сайт OutmaxShop" : "Папки",
                    Status = "Ошибка",
                    Reason = ex.Message
                });
            }
            try
            {
                var workDuration = _accumulatedWorkDuration + operationTimer.Elapsed;
                var reports = SaveReport(reportDirectory, downloadOnly, workDuration);
                _lastReportPath = reports.TextPath;
                runFinalized = true;
            }
            catch (Exception reportException)
            {
                AppLogger.Error("Не удалось сохранить отчёт", reportException);
            }
            ShowNotice($"Не удалось завершить обработку: {ex.Message}", false, showReport: _lastReportPath is not null);
        }
        finally
        {
            _isProcessing = false;
            _activeActionText = null;
            _runCancellation?.Dispose();
            _runCancellation = null;
            if (runFinalized) ResetRunTiming();
            RefreshPhotoshopStatus();
            RefreshControls();
        }
    }

    private void MarkDownloadOnlyComplete()
    {
        foreach (var pack in Packs)
        {
            pack.Status = $"Скачано · {pack.TotalCount} фото";
            pack.Progress = 100;
            pack.IsComplete = true;
            pack.HasError = false;
            if (!_reportsByPackPath.TryGetValue(pack.SourcePath, out var report)) continue;
            report.Status = "Скачано";
            report.ProcessedImages = 0;
            report.ProcessingErrors = 0;
            report.Reason = report.DownloadErrors > 0
                ? $"Не скачалось фотографий: {report.DownloadErrors}."
                : string.Empty;
        }
    }

    private ReportFiles SaveReport(string directory, bool downloadOnly, TimeSpan workDuration) =>
        _reportService.Save(
            _reportEntries,
            directory,
            new ReportRunSummary(
                _sessionStartedAt ?? DateTime.Now,
                DateTime.Now,
                workDuration,
                downloadOnly));

    private void ResetRunTiming()
    {
        _sessionStartedAt = null;
        _accumulatedWorkDuration = TimeSpan.Zero;
    }

    private async Task<(int Completed, int Failed)> ProcessPacksAsync(double progressOffset, CancellationToken cancellationToken)
    {
        var selectedPacks = Packs.Where(pack => pack.IsSelected).ToList();
        foreach (var skippedPack in Packs.Where(pack => !pack.IsSelected))
        {
            skippedPack.Status = "Не выбран";
            if (_reportsByPackPath.TryGetValue(skippedPack.SourcePath, out var skippedReport))
            {
                skippedReport.Status = "Не выбрано";
                skippedReport.Reason = AppendReason(skippedReport.Reason, "Папка снята с обработки пользователем.");
            }
        }

        var processable = selectedPacks.SelectMany(pack => pack.Photos).Count(photo => photo.Droplet is not null);
        var progressSpan = 100 - progressOffset;
        var completed = 0;
        var failed = 0;

        foreach (var pack in selectedPacks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = pack.Photos.Where(photo => photo.Droplet is not null).ToList();
            var report = _reportsByPackPath.GetValueOrDefault(pack.SourcePath);
            if (ready.Count == 0)
            {
                pack.HasError = true;
                pack.Status = "Пропущен · нет подходящих фото";
                pack.Progress = 100;
                if (report is not null) report.Status = "Пропущено";
                continue;
            }

            var packCompleted = 0;
            var packFailed = 0;
            pack.IsComplete = false;
            pack.HasError = false;

            foreach (var photo in ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var number = packCompleted + packFailed + 1;
                pack.Status = $"{Path.GetFileName(photo.SourcePath)} · {number}/{ready.Count}";
                _activeActionText = $"Photoshop · {completed + failed + 1}/{processable}";
                RefreshControls();
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
                    if (report is not null)
                        report.Reason = AppendReason(report.Reason, $"{Path.GetFileName(photo.SourcePath)}: {ex.Message}");
                    AppLogger.Error($"Ошибка обработки {targetPath}", ex);
                }

                pack.Progress = (packCompleted + packFailed) * 100.0 / ready.Count;
                OverallProgress.Value = processable == 0
                    ? 100
                    : progressOffset + (completed + failed) * progressSpan / processable;
            }

            pack.IsComplete = packFailed == 0;
            pack.HasError = packFailed > 0 || pack.UnsupportedCount > 0;
            pack.Status = packFailed == 0
                ? pack.UnsupportedCount == 0 ? $"Готово · {packCompleted}" : $"Готово · {packCompleted}, пропуск · {pack.UnsupportedCount}"
                : $"Готово · {packCompleted}, ошибок · {packFailed}";

            if (report is not null)
            {
                report.ProcessedImages = packCompleted;
                report.ProcessingErrors = packFailed;
                report.Status = packFailed > 0
                    ? "Ошибка"
                    : pack.UnsupportedCount > 0 ? "Частично готово" : "Готово";
            }
        }

        OverallProgress.Value = 100;
        return (completed, failed);
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
        if (string.IsNullOrWhiteSpace(_outputPath))
            throw new InvalidOperationException("Выберите папку для сохранения.");

        var outputRoot = Path.GetFullPath(_outputPath);
        foreach (var pack in Packs.Where(pack => pack.IsSelected))
        {
            var sourceRoot = Path.GetFullPath(pack.SourcePath);
            var packDestination = Path.Combine(outputRoot, SanitizeDirectoryName(pack.Name));
            if (IsSameOrChild(outputRoot, sourceRoot) ||
                string.Equals(packDestination.TrimEnd('\\'), sourceRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Папка сохранения пересекается с исходным паком «{pack.Name}». Выберите другую папку.");
        }
    }

    private string GetReportDirectory()
    {
        if (FolderMode.IsChecked == true && !string.IsNullOrWhiteSpace(_outputPath))
            return Path.Combine(_outputPath, "Отчёты WB Dropp");
        if (IsSiteMode) return Path.Combine(_downloadPath, "Отчёты WB Dropp");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WB Dropp", "Отчёты");
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

    private static string BuildFormatSummary(ProductPack pack)
    {
        var parts = new List<string>();
        if (pack.ModernCount > 0) parts.Add($"1400×1050: {pack.ModernCount}");
        if (pack.LegacyCount > 0) parts.Add($"1000×750: {pack.LegacyCount}");
        if (pack.UnsupportedCount > 0) parts.Add($"другие размеры: {pack.UnsupportedCount}");
        return parts.Count == 0 ? "Нет изображений" : string.Join(", ", parts);
    }

    private static string ExtractArticle(string folderName) =>
        ArticleParser.Parse(folderName).FirstOrDefault() ?? folderName;

    private static string AppendReason(string current, string addition) =>
        string.IsNullOrWhiteSpace(current) ? addition : current + "; " + addition;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        CancelButton.Content = "Останавливаем…";
        _runCancellation?.Cancel();
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastReportPath) || !File.Exists(_lastReportPath)) return;
        Process.Start(new ProcessStartInfo { FileName = _lastReportPath, UseShellExecute = true });
    }

    private void ShowNotice(string message, bool success, bool showReport = false)
    {
        RunNoticeText.Text = message;
        RunNoticeText.Foreground = BrushFrom(success ? "#166534" : "#9A3412");
        RunNotice.Background = BrushFrom(success ? "#F0FDF4" : "#FFF7ED");
        OpenReportButton.Visibility = showReport && _lastReportPath is not null ? Visibility.Visible : Visibility.Collapsed;
        RunNotice.Visibility = Visibility.Visible;

        if (!SystemParameters.ClientAreaAnimation) return;
        RunNotice.Opacity = 0;
        RunNotice.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void RefreshControls()
    {
        if (StartButton is null) return;
        var selectedPacks = Packs.Where(pack => pack.IsSelected).ToList();
        var selectedReady = selectedPacks.Sum(pack => pack.ReadyCount);
        var closeUps = selectedPacks.Sum(pack => pack.CloseUpCount);
        var unsupported = selectedPacks.Sum(pack => pack.UnsupportedCount);
        var articles = IsSiteMode ? ArticleParser.Parse(ArticlesInput.Text).Count : 0;
        var siteDownloadStage = IsSiteMode && Packs.Count == 0;
        var downloadOnly = siteDownloadStage && DownloadOnlyCheckBox.IsChecked == true;
        var completedDownloadOnly = IsSiteMode && Packs.Count > 0 && DownloadOnlyCheckBox.IsChecked == true;

        EmptyState.Visibility = Packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = (Packs.Count > 0 || (IsSiteMode && articles > 0)) && !_isProcessing && !_isScanning;
        AddFolderButton.IsEnabled = !_isProcessing && !_isScanning;
        ArticlesInput.IsEnabled = !_isProcessing && Packs.Count == 0;
        DownloadOnlyCheckBox.IsEnabled = !_isProcessing && Packs.Count == 0;
        SitePanel.IsEnabled = !_isProcessing;
        FoldersSourceMode.IsEnabled = !_isProcessing && !_isScanning;
        SiteSourceMode.IsEnabled = !_isProcessing && !_isScanning;
        ReplaceMode.IsEnabled = !_isProcessing;
        FolderMode.IsEnabled = !_isProcessing;
        OutputFolderPanel.IsEnabled = !_isProcessing;
        PackList.IsEnabled = !_isProcessing && !_isScanning && !completedDownloadOnly;
        SelectAllCheckBox.Visibility = Packs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllCheckBox.IsEnabled = !_isProcessing && !_isScanning && !completedDownloadOnly;
        _suppressSelectionEvents = true;
        SelectAllCheckBox.IsChecked = Packs.Count > 0 && selectedPacks.Count == Packs.Count;
        SelectAllCheckBox.Content = $"Все паки · {selectedPacks.Count}/{Packs.Count}";
        _suppressSelectionEvents = false;
        UpdateButton.IsEnabled = _downloadedUpdate is not null && !_isProcessing && !_isScanning && !_isUpdateDownloading;

        var hasSource = completedDownloadOnly ? false : siteDownloadStage ? articles > 0 : selectedReady > 0;
        var outputReady = siteDownloadStage || FolderMode.IsChecked != true || !string.IsNullOrWhiteSpace(_outputPath);
        StartButton.IsEnabled = hasSource && !_isScanning && !_isProcessing &&
                                outputReady;
        CancelButton.Visibility = _isProcessing ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = _isProcessing;
        if (_isProcessing) CancelButton.Content = "Остановить после текущего фото";

        StartButton.Content = _isProcessing
            ? _activeActionText ?? "Выполняется…"
            : _isScanning
                ? "Анализируем фотографии…"
                : siteDownloadStage
                    ? articles > 0
                        ? downloadOnly ? $"Выгрузить · {articles}" : $"Скачать паки · {articles}"
                        : "Введите артикулы"
                    : completedDownloadOnly
                        ? "Выгрузка завершена"
                        : selectedReady > 0 ? $"Обработать {selectedReady} фото" : "Выберите паки";

        SummaryText.Text = IsSiteMode
            ? Packs.Count == 0
                ? articles == 0 ? "Введите или импортируйте артикулы" : $"Артикулов в очереди: {articles}"
                : $"Выбрано: {selectedPacks.Count}/{Packs.Count}   •   к обработке: {selectedReady}   •   крупняк: {closeUps}" +
                  (unsupported > 0 ? $"   •   пропуск: {unsupported}" : string.Empty)
            : Packs.Count == 0
                ? "Добавьте папки с фотографиями"
                : $"Выбрано: {selectedPacks.Count}/{Packs.Count}   •   к обработке: {selectedReady}   •   крупняк: {closeUps}" +
                  (unsupported > 0 ? $"   •   пропуск: {unsupported}" : string.Empty);
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.CheckAsync(_windowCancellation.Token);
            if (update is null) return;

            _isUpdateDownloading = true;
            UpdateButton.Visibility = Visibility.Visible;
            UpdateButton.IsEnabled = false;
            UpdateButton.Content = $"Скачиваем v{update.Version.ToString(3)} · 0%";

            var progress = new Progress<int>(percent =>
            {
                UpdateButton.Content = $"Скачиваем v{update.Version.ToString(3)} · {percent}%";
            });
            _downloadedUpdate = await _updateService.DownloadAsync(update, progress, _windowCancellation.Token);
            UpdateButton.Content = $"Установить v{update.Version.ToString(3)}";
            UpdateButton.ToolTip = "Обновление уже скачано и готово к установке";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            UpdateButton.Visibility = Visibility.Collapsed;
            AppLogger.Error("Не удалось проверить или скачать обновление", ex);
        }
        finally
        {
            _isUpdateDownloading = false;
            RefreshControls();
        }
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedUpdate is null || _isProcessing || _isScanning) return;
        var version = _downloadedUpdate.Release.Version.ToString(3);
        var answer = MessageBox.Show(this,
            $"Обновление {version} уже скачано. Установить его сейчас?\n\nWB Dropp закроется, заменит файлы и запустится снова.",
            "Обновление WB Dropp",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _updateService.InstallAndRestart(_downloadedUpdate);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Не удалось запустить установку обновления", ex);
            ShowNotice($"Не удалось установить обновление: {ex.Message}", false);
        }
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
                "Сейчас идёт загрузка или обработка Photoshop. Закрыть WB Dropp и остановить очередь?",
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

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private sealed record ScanResult(List<PhotoItem> Photos, List<string> Errors);
}
