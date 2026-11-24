namespace XmlVariableScanner.Core;

internal static class SafeFileEnumerator
{
    public static IReadOnlyList<string> EnumerateXmlFiles(
        string rootDirectory,
        bool includeSubdirectories,
        Action<ScanError> addError,
        CancellationToken cancellationToken)
    {
        List<string> files = [];
        Stack<string> pendingDirectories = new();
        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pendingDirectories.Pop();

            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                addError(new ScanError(directory, $"Не удалось получить список XML-файлов: {exception.Message}"));
                continue;
            }

            if (!includeSubdirectories)
            {
                continue;
            }

            try
            {
                foreach (string childDirectory in Directory.EnumerateDirectories(directory))
                {
                    FileAttributes attributes = File.GetAttributes(childDirectory);
                    if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        pendingDirectories.Push(childDirectory);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                addError(new ScanError(directory, $"Не удалось прочитать вложенные папки: {exception.Message}"));
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}
