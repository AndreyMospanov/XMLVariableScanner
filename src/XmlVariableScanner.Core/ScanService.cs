using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace XmlVariableScanner.Core;

public sealed class ScanService(IOccurrenceReportWriter reportWriter)
{
    public async Task<ScanSummary> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(options);
        DateTimeOffset startedAt = DateTimeOffset.Now;
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ConcurrentQueue<ScanError> errors = new();
        IReadOnlyList<string> files = await Task.Run(
            () => SafeFileEnumerator.EnumerateXmlFiles(
                options.InputDirectory,
                options.IncludeSubdirectories,
                errors.Enqueue,
                cancellationToken),
            cancellationToken);

        SearchPatternSet patterns = new(
            options.SearchPattern,
            options.ExclusionPatterns,
            options.PatternMode,
            options.IgnoreCase);

        int workerCount = Math.Clamp(options.MaxDegreeOfParallelism, 1, 32);
        Channel<string> fileChannel = Channel.CreateBounded<string>(new BoundedChannelOptions(workerCount * 2)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = workerCount == 1
        });
        Channel<Occurrence> occurrenceChannel = Channel.CreateBounded<Occurrence>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = workerCount == 1
        });

        int processedFiles = 0;
        long occurrenceCount = 0;
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken linkedToken = linkedCancellation.Token;

        ReportContext reportContext = new(
            options,
            startedAt,
            () => errors.ToArray(),
            () => Volatile.Read(ref processedFiles));

        Task<ReportResult> reportTask = reportWriter.WriteAsync(
            options.OutputPath,
            occurrenceChannel.Reader.ReadAllAsync(linkedToken),
            reportContext,
            linkedToken);
        _ = reportTask.ContinueWith(
            _ => linkedCancellation.Cancel(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Task producer = ProduceFilesAsync(files, fileChannel.Writer, linkedToken);
        Task[] workers = Enumerable.Range(0, workerCount)
            .Select(_ => ProcessFilesAsync(
                options,
                patterns,
                files.Count,
                fileChannel.Reader,
                occurrenceChannel.Writer,
                errors,
                progress,
                () => Interlocked.Increment(ref processedFiles),
                () => Interlocked.Increment(ref occurrenceCount),
                () => Interlocked.Read(ref occurrenceCount),
                linkedToken))
            .ToArray();

        try
        {
            await producer;
            await Task.WhenAll(workers);
            occurrenceChannel.Writer.TryComplete();
            ReportResult reportResult = await reportTask;
            stopwatch.Stop();

            ScanSummary preliminary = new(
                files.Count,
                processedFiles,
                reportResult.WrittenOccurrences,
                errors.Count,
                reportResult.TruncatedLines,
                stopwatch.Elapsed,
                options.OutputPath,
                Path.ChangeExtension(options.OutputPath, ".log"));

            string logPath = await TechnicalLogWriter.WriteAsync(
                options,
                startedAt,
                preliminary,
                errors.ToArray(),
                cancellationToken);

            return preliminary with { LogPath = logPath };
        }
        catch
        {
            linkedCancellation.Cancel();
            fileChannel.Writer.TryComplete();
            occurrenceChannel.Writer.TryComplete();
            try
            {
                await reportTask;
            }
            catch
            {
                // Исходное исключение содержит более полезную причину сбоя.
            }

            throw;
        }
    }

    private static async Task ProduceFilesAsync(
        IReadOnlyList<string> files,
        ChannelWriter<string> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (string file in files)
            {
                await writer.WriteAsync(file, cancellationToken);
            }

            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            throw;
        }
    }

    private static async Task ProcessFilesAsync(
        ScanOptions options,
        SearchPatternSet patterns,
        int totalFiles,
        ChannelReader<string> files,
        ChannelWriter<Occurrence> occurrences,
        ConcurrentQueue<ScanError> errors,
        IProgress<ScanProgress>? progress,
        Func<int> incrementProcessedFiles,
        Func<long> incrementOccurrences,
        Func<long> getOccurrenceCount,
        CancellationToken cancellationToken)
    {
        await foreach (string path in files.ReadAllAsync(cancellationToken))
        {
            try
            {
                await ProcessFileAsync(path, options, patterns, occurrences, incrementOccurrences, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                errors.Enqueue(new ScanError(path, exception.Message));
            }

            int processed = incrementProcessedFiles();
            progress?.Report(new ScanProgress(
                processed,
                totalFiles,
                getOccurrenceCount(),
                Path.GetFileName(path)));
        }
    }

    private static async Task ProcessFileAsync(
        string path,
        ScanOptions options,
        SearchPatternSet patterns,
        ChannelWriter<Occurrence> occurrences,
        Func<long> incrementOccurrences,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = XmlTextReaderFactory.Open(path);
        XmlLineSegmenter segmenter = new();
        StringComparer comparer = options.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        HashSet<string> variablesOnLine = new(comparer);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<TextSegment> segments = segmenter.GetSearchableSegments(line, options.ReadingMode);
            variablesOnLine.Clear();

            foreach (string variable in patterns.FindMatches(line, segments))
            {
                if (!variablesOnLine.Add(variable))
                {
                    continue;
                }

                Occurrence occurrence = new(
                    variable,
                    Path.GetFileName(path),
                    Path.GetRelativePath(options.InputDirectory, path),
                    line);
                await occurrences.WriteAsync(occurrence, cancellationToken);
                incrementOccurrences();
            }
        }
    }

    private static void Validate(ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(options.InputDirectory))
        {
            throw new DirectoryNotFoundException($"Исходная папка не найдена: {options.InputDirectory}");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SearchPattern);
        string extension = Path.GetExtension(options.OutputPath);
        if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Выходной файл должен иметь расширение .xlsx.", nameof(options));
        }

        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (outputDirectory is null || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException($"Папка для отчёта не найдена: {outputDirectory}");
        }
    }
}
