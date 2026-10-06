using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

var buttonErrors = new List<string>();
var buttonTestThread = new Thread(() => CheckPrimaryButtonContrast(buttonErrors));
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
                Article = missing.Article,
                FolderPath = "Папка не создана",
                DownloadStatus = "❌ Не скачано",
                Status = "Пропущено",
                Reason = missing.FailureReason ?? string.Empty
            }
        ],
        Path.Combine(siteRoot, "Отчёты WB Dropp"));
    var reportText = File.ReadAllText(report.TextPath);
    if (!reportText.Contains("📦 WB DROPP", StringComparison.Ordinal) ||
        !reportText.Contains("Артикул не найден", StringComparison.OrdinalIgnoreCase))
        errors.Add("REPORT: manager-friendly text or skip reason is missing.");

    Console.WriteLine($"Site smoke: downloaded {found.DownloadedImageCount}/{found.CatalogImageCount}; report: {report.TextPath}");
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

static void CheckPrimaryButtonContrast(List<string> errors)
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
    }
    catch (Exception ex)
    {
        errors.Add("BUTTON: " + ex.Message);
    }
}
