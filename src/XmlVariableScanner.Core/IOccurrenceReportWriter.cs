namespace XmlVariableScanner.Core;

public interface IOccurrenceReportWriter
{
    Task<ReportResult> WriteAsync(
        string outputPath,
        IAsyncEnumerable<Occurrence> occurrences,
        ReportContext context,
        CancellationToken cancellationToken);
}
