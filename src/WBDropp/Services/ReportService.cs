using System.Text;
using WBDropp.Models;

namespace WBDropp.Services;

public sealed class ReportService
{
    public ReportFiles Save(
        IReadOnlyCollection<ProcessingReportEntry> entries,
        string directory,
        ReportRunSummary? runSummary = null)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var textPath = Path.Combine(directory, $"WB_Dropp_отчёт_для_менеджера_{stamp}.txt");

        WriteText(entries, textPath, runSummary);
        return new ReportFiles(textPath);
    }

    private static void WriteText(
        IEnumerable<ProcessingReportEntry> entries,
        string path,
        ReportRunSummary? runSummary)
    {
        var items = entries.ToList();
        var ready = items.Count(item => item.Status is "Готово" or "Скачано");
        var partial = items.Count(item => item.Status == "Частично готово");
        var skipped = items.Count(item => item.Status is "Пропущено" or "Ошибка" or "Остановлено" or "Не выбрано");
        var completedArticles = ready + partial;
        var inputPhotos = items.Sum(item => item.InputImages);
        var downloadedPhotos = items.Sum(item => item.DownloadedImages);
        var processedPhotos = items.Sum(item => item.ProcessedImages);
        var startedAt = runSummary?.StartedAt ?? DateTime.Now;
        var finishedAt = runSummary?.FinishedAt ?? DateTime.Now;
        var duration = runSummary?.WorkDuration ?? TimeSpan.Zero;
        var builder = new StringBuilder()
            .AppendLine("📦 WB DROPP — ОТЧЁТ ДЛЯ МЕНЕДЖЕРА")
            .AppendLine($"🕒 Начало: {startedAt:dd.MM.yyyy HH:mm:ss}")
            .AppendLine($"🏁 Завершение: {finishedAt:dd.MM.yyyy HH:mm:ss}")
            .AppendLine($"⏱️ Время работы: {FormatDuration(duration)}")
            .AppendLine($"⚙️ Режим: {(runSummary?.DownloadOnly == true ? "только выгрузка, без кадрирования" : "выгрузка / кадрирование")}")
            .AppendLine()
            .AppendLine("📊 СВОДКА")
            .AppendLine($"   Всего артикулов / папок: {items.Count}")
            .AppendLine($"   ✅ Сделано артикулов / папок: {completedArticles}")
            .AppendLine($"   ✅ Полностью готово: {ready}")
            .AppendLine($"   ⚠️ Частично готово: {partial}")
            .AppendLine($"   ❌ Пропущено / не выбрано / ошибок: {skipped}")
            .AppendLine($"   🖼️ Всего входных фотографий: {inputPhotos}")
            .AppendLine($"   ⬇️ Скачано фотографий: {downloadedPhotos}")
            .AppendLine($"   🖼️ Обработано фотографий: {processedPhotos}")
            .AppendLine()
            .AppendLine(new string('═', 68));

        foreach (var item in items)
        {
            var icon = item.Status switch
            {
                "Готово" => "✅",
                "Скачано" => "✅",
                "Частично готово" => "⚠️",
                "Ожидает обработки" => "⏳",
                "Остановлено" => "⏹️",
                "Не выбрано" => "◻️",
                _ => "❌"
            };
            var title = string.Join(" · ", new[] { item.Article, item.ProductName }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileName(item.FolderPath);

            builder.AppendLine()
                .AppendLine($"{icon} {title}")
                .AppendLine($"   Итог: {item.Status}")
                .AppendLine($"   📍 Источник: {item.Source}")
                .AppendLine($"   📁 Папка: {item.FolderPath}");

            if (item.Source.Contains("Сайт", StringComparison.OrdinalIgnoreCase))
                builder.AppendLine($"   ⬇️ Загрузка: {item.DownloadStatus}; скачано {item.DownloadedImages}/{item.CatalogImages}; ошибок {item.DownloadErrors}");

            if (runSummary?.DownloadOnly == true)
            {
                builder.AppendLine($"   🖼️ Выгружено без кадрирования: {item.DownloadedImages} фото");
            }
            else
            {
                builder.AppendLine($"   ✂️ Кадрирование: {item.ProcessedImages}/{item.ReadyImages}; крупняк: {item.CloseUpImages}; пропущено фото: {item.UnsupportedImages}; ошибок Photoshop: {item.ProcessingErrors}")
                    .AppendLine($"   🖼️ Форматы: {item.Formats}");
            }
            if (!string.IsNullOrWhiteSpace(item.Reason))
                builder.AppendLine($"   💬 Причина / детали: {item.Reason}");
            builder.AppendLine(new string('─', 68));
        }

        builder.AppendLine()
            .AppendLine("ℹ️ Поддерживаемые исходные размеры: 1400×1050 и 1000×750.")
            .AppendLine("Отчёт создан автоматически программой WB Dropp.");

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        var parts = new List<string>();
        if (duration.Hours > 0) parts.Add($"{duration.Hours} ч");
        if (duration.Minutes > 0) parts.Add($"{duration.Minutes} мин");
        parts.Add($"{duration.Seconds} сек");
        return string.Join(" ", parts);
    }
}
