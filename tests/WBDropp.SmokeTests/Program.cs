using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WBDropp.Models;
using WBDropp.Services;

var sampleRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("Tets");
var closeUpRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : null;
if (!Directory.Exists(sampleRoot))
{
    Console.Error.WriteLine($"Sample directory not found: {sampleRoot}");
    return 2;
}

var classifier = new ImageClassifier();
var errors = new List<string>();
var total = 0;
var closeUps = 0;

var selectionPack = new ProductPack(Path.Combine(Path.GetTempPath(), "selection-pack"));
if (!selectionPack.IsSelected) errors.Add("SELECTION: new packs must be selected by default.");
selectionPack.IsSelected = false;
if (selectionPack.IsSelected) errors.Add("SELECTION: pack could not be deselected.");
if (!UpdateService.TryParseVersion("v1.2.3", out var parsedVersion) || parsedVersion != new Version(1, 2, 3))
    errors.Add("UPDATE: GitHub tag version parsing failed.");

var buttonErrors = new List<string>();
var captureUi = args.Contains("--ui-snapshot", StringComparer.OrdinalIgnoreCase);
var buttonTestThread = new Thread(() => CheckPrimaryButtonContrast(buttonErrors, captureUi));
buttonTestThread.SetApartmentState(ApartmentState.STA);
buttonTestThread.Start();
buttonTestThread.Join();
errors.AddRange(buttonErrors);
Console.WriteLine("Primary button contrast checked in enabled and disabled states.");

foreach (var path in Directory.EnumerateFiles(sampleRoot, "*.jpg", SearchOption.AllDirectories))
{
    var photo = classifier.Classify(path, Path.GetRelativePath(sampleRoot, path));
    total++;
    if (photo.IsCloseUp) closeUps++;

    var legacy = path.Contains("_legacy", StringComparison.OrdinalIgnoreCase);
    var suffix = Path.GetFileNameWithoutExtension(path).Split('_').Last();
    var expectedCloseUp = legacy ? suffix is "7" or "8" : suffix is "5" or "7";
    var expectedFormat = legacy ? PhotoFormat.Legacy : PhotoFormat.Modern;

    if (photo.Format != expectedFormat)
        errors.Add($"FORMAT {photo.RelativePath}: {photo.Format}, expected {expectedFormat}");
    if (photo.IsCloseUp != expectedCloseUp)
        errors.Add($"CLOSEUP {photo.RelativePath}: {photo.IsCloseUp}, expected {expectedCloseUp} (foreground={photo.ForegroundRatio:F3}; edge={photo.EdgeTouchScore:F3})");
}

Console.WriteLine($"Checked {total} baseline images; close-ups: {closeUps}.");

if (closeUpRoot is not null && Directory.Exists(closeUpRoot))
{
    var closeUpExamples = 0;
    foreach (var path in Directory.EnumerateFiles(closeUpRoot, "*.*", SearchOption.AllDirectories)
                 .Where(path => new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff" }
                     .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)))
    {
        var photo = classifier.Classify(path, Path.GetRelativePath(closeUpRoot, path));
        closeUpExamples++;
        if (!photo.IsCloseUp)
            errors.Add($"CLOSEUP EXAMPLE {photo.RelativePath}: classified as regular (foreground={photo.ForegroundRatio:F3}; edge={photo.EdgeTouchScore:F3})");
    }

    Console.WriteLine($"Checked {closeUpExamples} additional close-up examples.");
}

Console.WriteLine($"Routing errors: {errors.Count}");
foreach (var error in errors) Console.Error.WriteLine(error);

if (args.Contains("--site-smoke", StringComparer.OrdinalIgnoreCase))
{
    var siteRoot = Path.Combine(
        Path.GetFullPath("artifacts"),
        "site-smoke",
        DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    var downloader = new SiteCatalogDownloader();
    var results = await downloader.DownloadAsync(
        ["47528", "999999999"],
        siteRoot,
        progress: null,
        CancellationToken.None);

    var found = results.Single(result => result.Article == "47528");
    var missing = results.Single(result => result.Article == "999999999");
    if (!found.HasFolder || found.DownloadedImageCount == 0)
        errors.Add("SITE 47528: images were not downloaded.");
    if (missing.HasFolder || string.IsNullOrWhiteSpace(missing.FailureReason))
        errors.Add("SITE missing article: skip reason was not returned.");

    var report = new ReportService().Save(
        [
            new ProcessingReportEntry
            {
                Source = "Сайт OutmaxShop",
                Article = found.Article,
                ProductName = found.ProductName,
                FolderPath = found.FolderPath ?? string.Empty,
                DownloadStatus = "✅ Скачано полностью",
                CatalogImages = found.CatalogImageCount,
                DownloadedImages = found.DownloadedImageCount,
                InputImages = found.DownloadedImageCount,
                Status = "Скачано"
            },
            new ProcessingReportEntry
            {
                Source = "Сайт OutmaxShop",
                Article = missing.Article,
                FolderPath = "Папка не создана",
                DownloadStatus = "❌ Не скачано",
                Status = "Пропущено",
                Reason = missing.FailureReason ?? string.Empty
            }
        ],
        Path.Combine(siteRoot, "Отчёты WB Dropp"),
        new ReportRunSummary(
            new DateTime(2026, 10, 6, 10, 0, 0),
            new DateTime(2026, 10, 6, 10, 2, 5),
            TimeSpan.FromSeconds(125),
            DownloadOnly: true));
    var reportText = File.ReadAllText(report.TextPath);
    if (!reportText.Contains("📦 WB DROPP", StringComparison.Ordinal) ||
        !reportText.Contains("Артикул не найден", StringComparison.OrdinalIgnoreCase) ||
        !reportText.Contains("2 мин 5 сек", StringComparison.Ordinal) ||
        !reportText.Contains("Сделано артикулов / папок: 1", StringComparison.Ordinal) ||
        !reportText.Contains("Скачано фотографий: 8", StringComparison.Ordinal))
        errors.Add("REPORT: manager-friendly text or skip reason is missing.");

    Console.WriteLine($"Site smoke: downloaded {found.DownloadedImageCount}/{found.CatalogImageCount}; report: {report.TextPath}");
}

if (args.Contains("--update-smoke", StringComparer.OrdinalIgnoreCase))
{
    var updateService = new UpdateService();
    var available = await updateService.CheckAsync(new Version(0, 1, 0), CancellationToken.None);
    if (available is null || available.Version <= new Version(0, 1, 0))
        errors.Add("UPDATE: latest GitHub release was not detected for an older client.");
    var knownRelease = new AppUpdate(
        new Version(0, 1, 1),
        "v0.1.1",
        "WBDropp-v0.1.1-win-x64.zip",
        new Uri("https://github.com/divangames/WB_Dropp/releases/download/v0.1.1/WBDropp-v0.1.1-win-x64.zip"),
        "7690356be7082b79fd82ed83e061c3f347b01295c6711ab783f7a4efa7b1c355");
    var downloaded = await updateService.DownloadAsync(knownRelease, progress: null, CancellationToken.None);
    if (!File.Exists(Path.Combine(downloaded.PayloadDirectory, "WB Dropp.exe")))
        errors.Add("UPDATE: downloaded release was not safely extracted.");
    Console.WriteLine($"Update smoke: verified and extracted {knownRelease.AssetName}.");
}

if (errors.Count > 0)
{
    Console.Error.WriteLine("Final errors:");
    foreach (var error in errors) Console.Error.WriteLine(error);
}
return errors.Count == 0 ? 0 : 1;

static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
{
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
    {
        var child = VisualTreeHelper.GetChild(parent, index);
        if (child is T match) return match;
        var nested = FindVisualChild<T>(child);
        if (nested is not null) return nested;
    }
    return null;
}

static void CheckPrimaryButtonContrast(List<string> errors, bool captureUi)
{
    try
    {
        var application = new WBDropp.App();
        application.InitializeComponent();
        var primaryButton = new Button
        {
            Style = (Style)application.FindResource("PrimaryButtonStyle"),
            Content = "Скачать и обработать · 109",
            Width = 262
        };
        primaryButton.Measure(new Size(262, 46));
        primaryButton.Arrange(new Rect(0, 0, 262, 46));
        primaryButton.ApplyTemplate();
        var primaryText = FindVisualChild<TextBlock>(primaryButton);
        if (primaryText?.Foreground is not SolidColorBrush enabledBrush || enabledBrush.Color != Colors.White)
            errors.Add("BUTTON enabled: label is not rendered white.");
        primaryButton.Content = "Photoshop · 1/109";
        primaryButton.UpdateLayout();
        if (primaryText?.Text != "Photoshop · 1/109" ||
            primaryText.Foreground is not SolidColorBrush dynamicBrush || dynamicBrush.Color != Colors.White)
            errors.Add("BUTTON dynamic: updated label is not rendered white.");
        primaryButton.IsEnabled = false;
        primaryButton.UpdateLayout();
        if (primaryText?.Foreground is not SolidColorBrush disabledBrush || disabledBrush.Color != Colors.White)
            errors.Add("BUTTON disabled: label is not rendered white.");

        var mainWindow = new WBDropp.MainWindow();
        var siteMode = (RadioButton)mainWindow.FindName("SiteSourceMode");
        var downloadOnly = (CheckBox)mainWindow.FindName("DownloadOnlyCheckBox");
        var resultOptions = (FrameworkElement)mainWindow.FindName("ResultOptionsPanel");
        var downloadHint = (FrameworkElement)mainWindow.FindName("DownloadOnlyHint");
        var selectAll = (CheckBox)mainWindow.FindName("SelectAllCheckBox");
        siteMode.IsChecked = true;
        downloadOnly.IsChecked = true;
        if (resultOptions.Visibility != Visibility.Collapsed || downloadHint.Visibility != Visibility.Visible)
            errors.Add("UI: download-only mode does not simplify the result panel.");
        if (selectAll.Visibility != Visibility.Collapsed)
            errors.Add("UI: select-all control must stay hidden while there are no packs.");

        mainWindow.Packs.Add(new ProductPack(@"C:\Товары\47528_legacy"));
        mainWindow.Packs.Add(new ProductPack(@"C:\Товары\47513") { IsSelected = false });
        if (selectAll.Visibility != Visibility.Visible)
            errors.Add("UI: select-all control is not visible for loaded packs.");

        if (captureUi)
        {
            const int width = 1180;
            const int height = 820;
            var surface = (FrameworkElement)mainWindow.FindName("WindowSurface");
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var snapshotPath = Path.GetFullPath(Path.Combine("artifacts", "ui", "0.2.0-download-only.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            using var output = File.Create(snapshotPath);
            encoder.Save(output);
            Console.WriteLine($"UI snapshot: {snapshotPath}");
        }
        mainWindow.Close();
    }
    catch (Exception ex)
    {
        errors.Add("BUTTON: " + ex.Message);
    }
}
