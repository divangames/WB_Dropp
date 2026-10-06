using System.Windows.Media;
using System.Windows.Media.Imaging;
using WBDropp.Models;

namespace WBDropp.Services;

public sealed class ImageClassifier
{
    private const int SampleWidth = 200;
    private const int SampleHeight = 150;
    private const int EdgeBand = 5;

    public PhotoItem Classify(string path, string relativePath)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;

        var format = (width, height) switch
        {
            (1400, 1050) => PhotoFormat.Modern,
            (1000, 750) => PhotoFormat.Legacy,
            _ => PhotoFormat.Unsupported
        };

        if (format == PhotoFormat.Unsupported)
            return new PhotoItem(path, relativePath, width, height, format, false, 0, 0);

        var (isCloseUp, foregroundRatio, edgeTouchScore) = AnalyzeComposition(frame);
        return new PhotoItem(path, relativePath, width, height, format, isCloseUp, foregroundRatio, edgeTouchScore);
    }

    private static (bool IsCloseUp, double ForegroundRatio, double EdgeTouchScore) AnalyzeComposition(BitmapSource source)
    {
        var scaleX = (double)SampleWidth / source.PixelWidth;
        var scaleY = (double)SampleHeight / source.PixelHeight;
        var sampled = new TransformedBitmap(source, new ScaleTransform(scaleX, scaleY));
        var converted = new FormatConvertedBitmap(sampled, PixelFormats.Bgra32, null, 0);
        var stride = SampleWidth * 4;
        var pixels = new byte[stride * SampleHeight];
        converted.CopyPixels(pixels, stride, 0);

        var borderPixels = new List<(byte R, byte G, byte B, int Brightness)>();

        for (var y = 0; y < SampleHeight; y++)
            for (var x = 0; x < SampleWidth; x++)
            {
                if (x >= 6 && x < SampleWidth - 6 && y >= 6 && y < SampleHeight - 6) continue;
                var offset = y * stride + x * 4;
                var b = pixels[offset];
                var g = pixels[offset + 1];
                var r = pixels[offset + 2];
                borderPixels.Add((r, g, b, r + g + b));
            }

        // The product can cover most of the border in a close-up. Using the
        // median of every border pixel would then mistake a dark shoe for the
        // background. Studio backgrounds are the lightest border region, so
        // estimate their colour from the brightest 20% of border pixels.
        borderPixels.Sort((left, right) => left.Brightness.CompareTo(right.Brightness));
        var brightestStart = (int)(borderPixels.Count * 0.80);
        var borderR = borderPixels.Skip(brightestStart).Select(pixel => pixel.R).Order().ToList();
        var borderG = borderPixels.Skip(brightestStart).Select(pixel => pixel.G).Order().ToList();
        var borderB = borderPixels.Skip(brightestStart).Select(pixel => pixel.B).Order().ToList();

        var bgR = borderR[borderR.Count / 2];
        var bgG = borderG[borderG.Count / 2];
        var bgB = borderB[borderB.Count / 2];
        var backgroundLuminance = (bgR + bgG + bgB) / 3.0;
        var darkThreshold = Math.Min(190, backgroundLuminance - 25);

        var foreground = new bool[SampleWidth * SampleHeight];
        var foregroundCount = 0;

        for (var y = 0; y < SampleHeight; y++)
            for (var x = 0; x < SampleWidth; x++)
            {
                var offset = y * stride + x * 4;
                var b = pixels[offset];
                var g = pixels[offset + 1];
                var r = pixels[offset + 2];
                var dr = r - bgR;
                var dg = g - bgG;
                var db = b - bgB;
                var distanceSquared = dr * dr + dg * dg + db * db;
                var luminance = (r + g + b) / 3.0;
                var isForeground = distanceSquared > 35 * 35 || luminance < darkThreshold;
                foreground[y * SampleWidth + x] = isForeground;
                if (isForeground) foregroundCount++;
            }

        var top = EdgeRate(foreground, 0, EdgeBand, 0, SampleWidth);
        var bottom = EdgeRate(foreground, SampleHeight - EdgeBand, SampleHeight, 0, SampleWidth);
        var left = EdgeRate(foreground, 0, SampleHeight, 0, EdgeBand);
        var right = EdgeRate(foreground, 0, SampleHeight, SampleWidth - EdgeBand, SampleWidth);
        var edgeRates = new[] { top, bottom, left, right };
        var edgeTouchScore = edgeRates.Average();
        var touchedEdges = edgeRates.Count(rate => rate >= 0.08);
        var foregroundRatio = foregroundCount / (double)(SampleWidth * SampleHeight);

        // Product close-ups reliably occupy a third of the frame and touch at least
        // two borders. This separates the supplied detail shots from regular cards.
        var isCloseUp = foregroundRatio >= 0.32 && edgeTouchScore >= 0.12 && touchedEdges >= 2;
        return (isCloseUp, foregroundRatio, edgeTouchScore);
    }

    private static double EdgeRate(bool[] mask, int fromY, int toY, int fromX, int toX)
    {
        var hits = 0;
        var total = 0;
        for (var y = fromY; y < toY; y++)
            for (var x = fromX; x < toX; x++)
            {
                total++;
                if (mask[y * SampleWidth + x]) hits++;
            }
        return total == 0 ? 0 : hits / (double)total;
    }
}
