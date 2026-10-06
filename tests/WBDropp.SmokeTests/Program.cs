using System.IO;
using WBDropp.Models;
using WBDropp.Services;

var sampleRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("Tets");
if (!Directory.Exists(sampleRoot))
{
    Console.Error.WriteLine($"Sample directory not found: {sampleRoot}");
    return 2;
}

var classifier = new ImageClassifier();
var errors = new List<string>();
var total = 0;
var closeUps = 0;

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

Console.WriteLine($"Checked {total} images; close-ups: {closeUps}; errors: {errors.Count}");
foreach (var error in errors) Console.Error.WriteLine(error);
return errors.Count == 0 ? 0 : 1;
