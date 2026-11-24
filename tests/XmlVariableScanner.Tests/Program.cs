using System.IO.Compression;
using System.Text;
using XmlVariableScanner.Core;
using XmlVariableScanner.Reports;

namespace XmlVariableScanner.Tests;

internal static class Program
{
    private static int _passed;

    public static async Task<int> Main()
    {
        (string Name, Func<Task> Test)[] tests =
        [
            ("Простая маска и исключения", TestSimplePatternsAsync),
            ("Комментарии между строками", TestMultilineCommentsAsync),
            ("XML-aware сегментация", TestXmlAwareSegmentsAsync),
            ("Regex с группой variable", TestProfessionalRegexAsync),
            ("Интеграционное сканирование", TestScanServiceAsync),
            ("Создание XLSX", TestOpenXmlReportAsync)
        ];

        foreach ((string name, Func<Task> test) in tests)
        {
            try
            {
                await test();
                _passed++;
                Console.WriteLine($"PASS: {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: {name}\n{exception}");
                return 1;
            }
        }

        Console.WriteLine($"Успешно: {_passed}/{tests.Length}");
        return 0;
    }

    private static Task TestSimplePatternsAsync()
    {
        const string line = "<x>$$P_AMOUNT $$P_SRC_SCHEMA $$P_Name</x>";
        SearchPatternSet patterns = new("$$P_*", ["$$P_*_schema"], PatternMode.Simple, ignoreCase: true);
        XmlLineSegmenter segmenter = new();
        string[] matches = patterns.FindMatches(
            line,
            segmenter.GetSearchableSegments(line, ReadingMode.FastText)).ToArray();

        AssertSequence(["$$P_AMOUNT", "$$P_Name"], matches);
        return Task.CompletedTask;
    }

    private static Task TestMultilineCommentsAsync()
    {
        SearchPatternSet patterns = new("$$P_*", [], PatternMode.Simple, ignoreCase: true);
        XmlLineSegmenter segmenter = new();
        const string first = "<x>$$P_KEEP <!-- $$P_DROP";
        const string second = "$$P_DROP2 --> $$P_KEEP2</x>";

        string[] matches = patterns.FindMatches(
                first,
                segmenter.GetSearchableSegments(first, ReadingMode.FastText))
            .Concat(patterns.FindMatches(
                second,
                segmenter.GetSearchableSegments(second, ReadingMode.FastText)))
            .ToArray();

        AssertSequence(["$$P_KEEP", "$$P_KEEP2"], matches);
        return Task.CompletedTask;
    }

    private static Task TestXmlAwareSegmentsAsync()
    {
        const string line = "<?pi $$P_PI?><root name=\"$$P_ATTR\"><child>$$P_TEXT</child><!-- $$P_COMMENT --><![CDATA[$$P_CDATA]]></root>";
        SearchPatternSet patterns = new("$$P_*", [], PatternMode.Simple, ignoreCase: true);
        XmlLineSegmenter segmenter = new();
        string[] matches = patterns.FindMatches(
            line,
            segmenter.GetSearchableSegments(line, ReadingMode.XmlAware)).ToArray();

        AssertSequence(["$$P_ATTR", "$$P_TEXT", "$$P_CDATA"], matches);
        return Task.CompletedTask;
    }

    private static Task TestProfessionalRegexAsync()
    {
        const string line = "value=$$P_FIRST and $$X_IGNORE";
        SearchPatternSet patterns = new(
            @"(?<variable>\$\$P_[A-Z_]+)",
            [],
            PatternMode.RegularExpression,
            ignoreCase: false);
        XmlLineSegmenter segmenter = new();
        string[] matches = patterns.FindMatches(
            line,
            segmenter.GetSearchableSegments(line, ReadingMode.FastText)).ToArray();

        AssertSequence(["$$P_FIRST"], matches);
        return Task.CompletedTask;
    }

    private static async Task TestScanServiceAsync()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            await File.WriteAllTextAsync(
                Path.Combine(root, "one.xml"),
                "<root>$$P_ONE $$P_ONE<!-- $$P_HIDDEN --></root>",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                Path.Combine(nested, "two.xml"),
                "<root value=\"$$P_TWO\" />",
                Encoding.UTF8);

            CollectingReportWriter reportWriter = new();
            ScanService service = new(reportWriter);
            string output = Path.Combine(root, "result.xlsx");
            ScanSummary summary = await service.ScanAsync(new ScanOptions
            {
                InputDirectory = root,
                OutputPath = output,
                SearchPattern = "$$P_*",
                ReadingMode = ReadingMode.XmlAware,
                PatternMode = PatternMode.Simple,
                MaxDegreeOfParallelism = 2
            });

            AssertEqual(2, summary.ProcessedFiles, "Количество файлов");
            AssertEqual(2L, summary.Occurrences, "Количество строк отчёта");
            AssertSequence(
                ["$$P_ONE", "$$P_TWO"],
                reportWriter.Occurrences.Select(item => item.Variable).Order(StringComparer.Ordinal).ToArray());
            Assert(File.Exists(summary.LogPath), "Технический журнал не создан.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestOpenXmlReportAsync()
    {
        string root = CreateTemporaryDirectory();
        try
        {
            string output = Path.Combine(root, "report.xlsx");
            ScanOptions options = new()
            {
                InputDirectory = root,
                OutputPath = output,
                SearchPattern = "$$P_*"
            };
            Occurrence[] rows =
            [
                new("$$P_TEST", "sample.xml", "nested\\sample.xml", "<x>$$P_TEST</x>")
            ];
            OpenXmlReportWriter writer = new();
            ReportResult result = await writer.WriteAsync(
                output,
                ToAsyncEnumerable(rows),
                new ReportContext(options, DateTimeOffset.Now, () => [], () => 1),
                CancellationToken.None);

            AssertEqual(1L, result.WrittenOccurrences, "Число записей XLSX");
            Assert(File.Exists(output), "XLSX-файл не создан.");

            using ZipArchive archive = ZipFile.OpenRead(output);
            Assert(archive.GetEntry("xl/workbook.xml") is not null, "В XLSX нет workbook.xml.");
            Assert(archive.Entries.Count(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)) >= 3,
                "В XLSX должны присутствовать результаты, ошибки и информация о запуске.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async IAsyncEnumerable<Occurrence> ToAsyncEnumerable(IEnumerable<Occurrence> items)
    {
        foreach (Occurrence item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"XmlVariableScanner.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void AssertSequence(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Ожидалось: [{string.Join(", ", expected)}]; получено: [{string.Join(", ", actual)}].");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string name)
        where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidOperationException($"{name}: ожидалось {expected}, получено {actual}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class CollectingReportWriter : IOccurrenceReportWriter
    {
        public List<Occurrence> Occurrences { get; } = [];

        public async Task<ReportResult> WriteAsync(
            string outputPath,
            IAsyncEnumerable<Occurrence> occurrences,
            ReportContext context,
            CancellationToken cancellationToken)
        {
            await foreach (Occurrence occurrence in occurrences.WithCancellation(cancellationToken))
            {
                Occurrences.Add(occurrence);
            }

            return new ReportResult(Occurrences.Count, 0);
        }
    }
}
