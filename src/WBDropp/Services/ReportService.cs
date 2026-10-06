using System.Text;
using WBDropp.Models;

namespace WBDropp.Services;

public sealed class ReportService
{
    public ReportFiles Save(IReadOnlyCollection<ProcessingReportEntry> entries, string directory)
    {
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var textPath = Path.Combine(directory, $"WB_Dropp_отчёт_для_менеджера_{stamp}.txt");

        WriteText(entries, textPath);
        return new ReportFiles(textPath);
    }

    private static void WriteText(IEnumerable<ProcessingReportEntry> entries, string path)
    {
        var items = entries.ToList();
        var ready = items.Count(item => item.Status == "Готово");
        var partial = items.Count(item => item.Status == "Частично готово");
        var skipped = items.Count(item => item.Status is "Пропущено" or "Ошибка" or "Остановлено");
        var processedPhotos = items.Sum(item => item.ProcessedImages);
        var builder = new StringBuilder()
            .AppendLine("📦 WB DROPP — ОТЧЁТ ДЛЯ МЕНЕДЖЕРА")
            .AppendLine($"🕒 Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}")
            .AppendLine()
            .AppendLine("📊 СВОДКА")
            .AppendLine($"   Всего артикулов / папок: {items.Count}")
            .AppendLine($"   ✅ Полностью готово: {ready}")
            .AppendLine($"   ⚠️ Частично готово: {partial}")
            .AppendLine($"   ❌ Пропущено / ошибок: {skipped}")
            .AppendLine($"   🖼️ Обработано фотографий: {processedPhotos}")
            .AppendLine()
            .AppendLine(new string('═', 68));

        foreach (var item in items)
        {
            var icon = item.Status switch
            {
                "Готово" => "✅",
                "Частично готово" => "⚠️",
                "Ожидает обработки" => "⏳",
                "Остановлено" => "⏹️",
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

            builder.AppendLine($"   ✂️ Кадрирование: {item.ProcessedImages}/{item.ReadyImages}; крупняк: {item.CloseUpImages}; пропущено фото: {item.UnsupportedImages}; ошибок Photoshop: {item.ProcessingErrors}")
                .AppendLine($"   🖼️ Форматы: {item.Formats}");
            if (!string.IsNullOrWhiteSpace(item.Reason))
                builder.AppendLine($"   💬 Причина / детали: {item.Reason}");
            builder.AppendLine(new string('─', 68));
        }

        builder.AppendLine()
            .AppendLine("ℹ️ Поддерживаемые исходные размеры: 1400×1050 и 1000×750.")
            .AppendLine("Отчёт создан автоматически программой WB Dropp.");

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }
}
