using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using WBDropp.Models;

namespace WBDropp.Services;

public sealed class SiteCatalogDownloader
{
    public const string CatalogUrl = "https://outmaxshop.com/yml/all_new.yml";
    private static readonly HttpClient Http = CreateHttpClient();

    public async Task<IReadOnlyList<SiteDownloadResult>> DownloadAsync(
        IReadOnlyList<string> articles,
        string destinationRoot,
        IProgress<SiteDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        progress?.Report(new SiteDownloadProgress(0, articles.Count, "Загружаем каталог OutmaxShop…"));

        var catalog = await LoadCatalogAsync(cancellationToken);
        var requested = new HashSet<string>(articles, StringComparer.OrdinalIgnoreCase);
        var offers = catalog.Descendants()
            .Where(element => element.Name.LocalName == "offer")
            .Where(element => requested.Contains(element.Attribute("id")?.Value ?? string.Empty))
            .ToDictionary(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase);

        var results = new List<SiteDownloadResult>();
        for (var index = 0; index < articles.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var article = articles[index];
            progress?.Report(new SiteDownloadProgress(index, articles.Count, $"Артикул {article}: ищем и скачиваем фото…"));

            if (!offers.TryGetValue(article, out var offer))
            {
                results.Add(new SiteDownloadResult
                {
                    Article = article,
                    FailureReason = "Артикул не найден в каталоге OutmaxShop."
                });
                progress?.Report(new SiteDownloadProgress(index + 1, articles.Count, $"Артикул {article}: не найден"));
                continue;
            }

            SiteDownloadResult result;
            try
            {
                result = await DownloadProductAsync(article, offer, destinationRoot, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new SiteDownloadResult
                {
                    Article = article,
                    ProductName = ChildValue(offer, "name") ?? string.Empty,
                    FailureReason = $"Ошибка обработки карточки: {ex.Message}"
                };
            }
            results.Add(result);
            progress?.Report(new SiteDownloadProgress(index + 1, articles.Count,
                result.HasFolder
                    ? $"Артикул {article}: скачано {result.DownloadedImageCount}/{result.CatalogImageCount}"
                    : $"Артикул {article}: пропущен — {result.FailureReason}"));
        }

        return results;
    }

    private static async Task<XDocument> LoadCatalogAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUrl);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
    }

    private static async Task<SiteDownloadResult> DownloadProductAsync(
        string article,
        XElement offer,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        var productName = ChildValue(offer, "name") ?? "Без названия";
        var pictureUrls = offer.Descendants()
            .Where(element => element.Name.LocalName == "picture")
            .Select(element => element.Value.Trim())
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out _))
            .ToList();
        var sizes = offer.Descendants()
            .Where(element => element.Name.LocalName == "param" && string.Equals(element.Attribute("name")?.Value, "Размер", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0)
            .ToList();

        if (pictureUrls.Count == 0)
        {
            return new SiteDownloadResult
            {
                Article = article,
                ProductName = productName,
                Sizes = sizes,
                FailureReason = "В карточке товара нет фотографий."
            };
        }

        using var semaphore = new SemaphoreSlim(4);
        var tasks = pictureUrls.Select((url, index) => DownloadImageAsync(url, index, semaphore, cancellationToken)).ToArray();
        var downloads = await Task.WhenAll(tasks);
        var successful = downloads.Where(item => item.Bytes is not null).OrderBy(item => item.Index).ToList();
        var errors = downloads.Where(item => item.Bytes is null).Select(item => $"Фото {item.Index + 1}: {item.Error}").ToList();

        if (successful.Count == 0)
        {
            return new SiteDownloadResult
            {
                Article = article,
                ProductName = productName,
                CatalogImageCount = pictureUrls.Count,
                FailedImageCount = pictureUrls.Count,
                Sizes = sizes,
                DownloadErrors = errors,
                FailureReason = "Не удалось скачать ни одной фотографии. " + string.Join("; ", errors.Take(3))
            };
        }

        var (firstWidth, firstHeight) = ReadDimensions(successful[0].Bytes!);
        var isLegacy = firstWidth < 1400 && firstHeight < 1050;
        var folderPath = Path.Combine(destinationRoot, article + (isLegacy ? "_legacy" : string.Empty));
        Directory.CreateDirectory(folderPath);

        var safeName = SanitizeFileName(productName);
        var writtenPaths = new List<string>();
        foreach (var item in successful)
        {
            var imagePath = Path.Combine(folderPath, $"{article}_{safeName}_{item.Index + 1}.jpg");
            await File.WriteAllBytesAsync(imagePath, item.Bytes!, cancellationToken);
            writtenPaths.Add(imagePath);
        }

        var infoPath = Path.Combine(folderPath, $"{article}_info.txt");
        var info = new StringBuilder()
            .AppendLine($"Артикул: {article}")
            .AppendLine($"Название: {productName}")
            .AppendLine()
            .AppendLine("Размеры:");
        foreach (var size in sizes) info.AppendLine($"\"{size}\"");
        await File.WriteAllTextAsync(infoPath, info.ToString(), new UTF8Encoding(true), cancellationToken);

        return new SiteDownloadResult
        {
            Article = article,
            ProductName = productName,
            FolderPath = folderPath,
            CatalogImageCount = pictureUrls.Count,
            DownloadedImageCount = writtenPaths.Count,
            FailedImageCount = errors.Count,
            IsLegacy = isLegacy,
            DownloadedPaths = writtenPaths,
            Sizes = sizes,
            DownloadErrors = errors,
            FailureReason = errors.Count > 0 ? $"Часть фотографий не скачалась: {string.Join("; ", errors.Take(3))}" : null
        };
    }

    private static async Task<ImageDownload> DownloadImageAsync(
        string url,
        int index,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0) throw new InvalidDataException("сервер вернул пустой файл");
            return new ImageDownload(index, bytes, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ImageDownload(index, null, ex.Message);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static (int Width, int Height) ReadDimensions(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
    }

    private static string? ChildValue(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value.Trim();

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value.Trim().TrimEnd('.');
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
        return client;
    }

    private sealed record ImageDownload(int Index, byte[]? Bytes, string? Error);
}
