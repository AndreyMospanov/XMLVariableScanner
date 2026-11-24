using System.Text;

namespace XmlVariableScanner.Core;

internal static class TechnicalLogWriter
{
    public static async Task<string> WriteAsync(
        ScanOptions options,
        DateTimeOffset startedAt,
        ScanSummary summary,
        IReadOnlyCollection<ScanError> errors,
        CancellationToken cancellationToken)
    {
        string logPath = Path.ChangeExtension(options.OutputPath, ".log");
        StringBuilder content = new();
        content.AppendLine("XML Variable Scanner");
        content.AppendLine($"Начало: {startedAt:yyyy-MM-dd HH:mm:ss zzz}");
        content.AppendLine($"Завершение: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        content.AppendLine($"Исходная папка: {options.InputDirectory}");
        content.AppendLine($"Режим чтения: {options.ReadingMode}");
        content.AppendLine($"Режим маски: {options.PatternMode}");
        content.AppendLine($"Маска: {options.SearchPattern}");
        content.AppendLine($"Без учёта регистра: {options.IgnoreCase}");
        content.AppendLine($"Найдено файлов: {summary.TotalFiles}");
        content.AppendLine($"Обработано файлов: {summary.ProcessedFiles}");
        content.AppendLine($"Строк отчёта: {summary.Occurrences}");
        content.AppendLine($"Обрезано длинных строк: {summary.TruncatedLines}");
        content.AppendLine($"Ошибок: {summary.ErrorCount}");
        content.AppendLine($"Время выполнения: {summary.Elapsed}");

        if (errors.Count > 0)
        {
            content.AppendLine();
            content.AppendLine("Ошибки:");
            foreach (ScanError error in errors)
            {
                content.AppendLine($"[{error.FilePath}] {error.Message}");
            }
        }

        await File.WriteAllTextAsync(logPath, content.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
        return logPath;
    }
}
