namespace XmlVariableScanner.Core;

public enum ReadingMode
{
    FastText,
    XmlAware
}

public enum PatternMode
{
    Simple,
    RegularExpression
}

public sealed record ScanOptions
{
    public required string InputDirectory { get; init; }

    public required string OutputPath { get; init; }

    public string SearchPattern { get; init; } = "$$P_*";

    public IReadOnlyList<string> ExclusionPatterns { get; init; } = [];

    public ReadingMode ReadingMode { get; init; } = ReadingMode.FastText;

    public PatternMode PatternMode { get; init; } = PatternMode.Simple;

    public bool IgnoreCase { get; init; } = true;

    public bool IncludeSubdirectories { get; init; } = true;

    public int MaxDegreeOfParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
}

public sealed record Occurrence(
    string Variable,
    string FileName,
    string RelativePath,
    string LineText);

public sealed record ScanError(string FilePath, string Message);

public sealed record ScanProgress(
    int ProcessedFiles,
    int TotalFiles,
    long Occurrences,
    string? CurrentFile);

public sealed record ScanSummary(
    int TotalFiles,
    int ProcessedFiles,
    long Occurrences,
    int ErrorCount,
    long TruncatedLines,
    TimeSpan Elapsed,
    string OutputPath,
    string LogPath);

public sealed record ReportResult(long WrittenOccurrences, long TruncatedLines);

public sealed record ReportContext(
    ScanOptions Options,
    DateTimeOffset StartedAt,
    Func<IReadOnlyCollection<ScanError>> GetErrors,
    Func<int> GetProcessedFiles);
