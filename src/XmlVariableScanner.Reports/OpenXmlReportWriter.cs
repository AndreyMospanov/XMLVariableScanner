using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using XmlVariableScanner.Core;

namespace XmlVariableScanner.Reports;

public sealed class OpenXmlReportWriter : IOccurrenceReportWriter
{
    private const uint ExcelMaximumRows = 1_048_576;
    private const int ExcelMaximumCellCharacters = 32_767;
    private const string TruncationMarker = " … [обрезано до лимита Excel]";

    public async Task<ReportResult> WriteAsync(
        string outputPath,
        IAsyncEnumerable<Occurrence> occurrences,
        ReportContext context,
        CancellationToken cancellationToken)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        string directory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("Не удалось определить папку отчёта.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        long writtenOccurrences = 0;
        long truncatedLines = 0;

        try
        {
            using (SpreadsheetDocument document = SpreadsheetDocument.Create(
                temporaryPath,
                SpreadsheetDocumentType.Workbook))
            {
                WorkbookPart workbookPart = document.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();
                AddStyles(workbookPart);
                Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
                uint nextSheetId = 1;
                int occurrenceSheetNumber = 1;

                OccurrenceSheetWriter occurrenceSheet = CreateOccurrenceSheet(
                    workbookPart,
                    sheets,
                    nextSheetId++,
                    occurrenceSheetNumber);

                await foreach (Occurrence occurrence in occurrences.WithCancellation(cancellationToken))
                {
                    if (occurrenceSheet.RowIndex >= ExcelMaximumRows)
                    {
                        occurrenceSheet.Dispose();
                        occurrenceSheetNumber++;
                        occurrenceSheet = CreateOccurrenceSheet(
                            workbookPart,
                            sheets,
                            nextSheetId++,
                            occurrenceSheetNumber);
                    }

                    string line = PrepareCellText(occurrence.LineText, out bool wasTruncated);
                    if (wasTruncated)
                    {
                        truncatedLines++;
                    }

                    occurrenceSheet.WriteRow(
                        occurrence.Variable,
                        occurrence.FileName,
                        occurrence.RelativePath,
                        line);
                    writtenOccurrences++;
                }

                occurrenceSheet.Dispose();
                WriteErrorsSheet(workbookPart, sheets, nextSheetId++, context.GetErrors());
                WriteInformationSheet(
                    workbookPart,
                    sheets,
                    nextSheetId,
                    context,
                    writtenOccurrences,
                    truncatedLines);
                workbookPart.Workbook.Save();
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new ReportResult(writtenOccurrences, truncatedLines);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static OccurrenceSheetWriter CreateOccurrenceSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        uint sheetId,
        int number)
    {
        string name = number == 1 ? "Результаты" : $"Результаты {number}";
        WorksheetPart part = workbookPart.AddNewPart<WorksheetPart>();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheetId,
            Name = name
        });

        return new OccurrenceSheetWriter(part);
    }

    private static void WriteErrorsSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        uint sheetId,
        IReadOnlyCollection<ScanError> errors)
    {
        WorksheetPart part = workbookPart.AddNewPart<WorksheetPart>();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheetId,
            Name = "Ошибки"
        });

        using OpenXmlWriter writer = OpenXmlWriter.Create(part);
        WriteWorksheetStart(writer, [48D, 100D]);
        uint rowIndex = 1;
        WriteRow(writer, rowIndex++, ["Файл", "Ошибка"], header: true);

        foreach (ScanError error in errors.Take((int)ExcelMaximumRows - 1))
        {
            WriteRow(writer, rowIndex++, [error.FilePath, PrepareCellText(error.Message, out _)]);
        }

        WriteWorksheetEnd(writer, rowIndex - 1, 2);
    }

    private static void WriteInformationSheet(
        WorkbookPart workbookPart,
        Sheets sheets,
        uint sheetId,
        ReportContext context,
        long writtenOccurrences,
        long truncatedLines)
    {
        WorksheetPart part = workbookPart.AddNewPart<WorksheetPart>();
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(part),
            SheetId = sheetId,
            Name = "О запуске"
        });

        using OpenXmlWriter writer = OpenXmlWriter.Create(part);
        WriteWorksheetStart(writer, [32D, 90D]);
        uint rowIndex = 1;
        WriteRow(writer, rowIndex++, ["Параметр", "Значение"], header: true);
        WriteRow(writer, rowIndex++, ["Начало", context.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz")]);
        WriteRow(writer, rowIndex++, ["Исходная папка", context.Options.InputDirectory]);
        WriteRow(writer, rowIndex++, ["Режим чтения", context.Options.ReadingMode.ToString()]);
        WriteRow(writer, rowIndex++, ["Режим маски", context.Options.PatternMode.ToString()]);
        WriteRow(writer, rowIndex++, ["Маска", context.Options.SearchPattern]);
        WriteRow(writer, rowIndex++, ["Обработано файлов", context.GetProcessedFiles().ToString()]);
        WriteRow(writer, rowIndex++, ["Строк отчёта", writtenOccurrences.ToString()]);
        WriteRow(writer, rowIndex++, ["Обрезано длинных строк", truncatedLines.ToString()]);
        WriteWorksheetEnd(writer, rowIndex - 1, 2);
    }

    private static void WriteWorksheetStart(OpenXmlWriter writer, IReadOnlyList<double> columnWidths)
    {
        writer.WriteStartElement(new Worksheet());

        SheetView view = new() { WorkbookViewId = 0U };
        view.Append(new Pane
        {
            VerticalSplit = 1D,
            TopLeftCell = "A2",
            ActivePane = PaneValues.BottomLeft,
            State = PaneStateValues.Frozen
        });
        writer.WriteElement(new SheetViews(view));

        Columns columns = new();
        for (int index = 0; index < columnWidths.Count; index++)
        {
            columns.Append(new Column
            {
                Min = (uint)(index + 1),
                Max = (uint)(index + 1),
                Width = columnWidths[index],
                CustomWidth = true
            });
        }

        writer.WriteElement(columns);
        writer.WriteStartElement(new SheetData());
    }

    private static void WriteWorksheetEnd(OpenXmlWriter writer, uint finalRow, int columns)
    {
        writer.WriteEndElement();
        writer.WriteElement(new AutoFilter
        {
            Reference = $"A1:{GetColumnName(columns)}{Math.Max(finalRow, 1)}"
        });
        writer.WriteEndElement();
    }

    private static void WriteRow(OpenXmlWriter writer, uint rowIndex, IReadOnlyList<string> values, bool header = false)
    {
        Row row = new() { RowIndex = rowIndex };
        foreach (string value in values)
        {
            row.Append(CreateTextCell(value, header ? 1U : 0U));
        }

        writer.WriteElement(row);
    }

    private static Cell CreateTextCell(string value, uint styleIndex)
    {
        Text text = new(SanitizeXmlText(value)) { Space = SpaceProcessingModeValues.Preserve };
        return new Cell(new InlineString(text))
        {
            DataType = CellValues.InlineString,
            StyleIndex = styleIndex
        };
    }

    private static string PrepareCellText(string value, out bool truncated)
    {
        string sanitized = SanitizeXmlText(value);
        if (sanitized.Length <= ExcelMaximumCellCharacters)
        {
            truncated = false;
            return sanitized;
        }

        int length = ExcelMaximumCellCharacters - TruncationMarker.Length;
        if (length > 0 && char.IsHighSurrogate(sanitized[length - 1]))
        {
            length--;
        }

        truncated = true;
        return string.Concat(sanitized.AsSpan(0, length), TruncationMarker);
    }

    private static string SanitizeXmlText(string value)
    {
        StringBuilder? sanitized = null;

        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            bool valid = XmlConvert.IsXmlChar(character);

            if (char.IsHighSurrogate(character)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                if (sanitized is not null)
                {
                    sanitized.Append(character);
                    sanitized.Append(value[++index]);
                }
                else
                {
                    index++;
                }

                continue;
            }

            if (valid)
            {
                sanitized?.Append(character);
                continue;
            }

            sanitized ??= new StringBuilder(value.Length).Append(value, 0, index);
            sanitized.Append(' ');
        }

        return sanitized?.ToString() ?? value;
    }

    private static string GetColumnName(int number)
    {
        StringBuilder result = new();
        while (number > 0)
        {
            number--;
            result.Insert(0, (char)('A' + (number % 26)));
            number /= 26;
        }

        return result.ToString();
    }

    private static void AddStyles(WorkbookPart workbookPart)
    {
        WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();

        Fonts fonts = new(
            new Font(),
            new Font(new Bold(), new Color { Rgb = "FFFFFFFF" }))
        { Count = 2U };

        Fills fills = new(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(
                new ForegroundColor { Rgb = "FF1F4E78" },
                new BackgroundColor { Indexed = 64U })
            { PatternType = PatternValues.Solid }))
        { Count = 3U };

        Borders borders = new(new Border()) { Count = 1U };
        CellStyleFormats cellStyleFormats = new(new CellFormat()) { Count = 1U };
        CellFormats cellFormats = new(
            new CellFormat(),
            new CellFormat
            {
                FontId = 1U,
                FillId = 2U,
                BorderId = 0U,
                ApplyFont = true,
                ApplyFill = true
            })
        { Count = 2U };
        CellStyles cellStyles = new(new CellStyle
        {
            Name = "Обычный",
            FormatId = 0U,
            BuiltinId = 0U
        })
        { Count = 1U };

        stylesPart.Stylesheet = new Stylesheet(
            fonts,
            fills,
            borders,
            cellStyleFormats,
            cellFormats,
            cellStyles);
        stylesPart.Stylesheet.Save();
    }

    private sealed class OccurrenceSheetWriter : IDisposable
    {
        private readonly OpenXmlWriter _writer;
        private bool _disposed;

        public OccurrenceSheetWriter(WorksheetPart part)
        {
            _writer = OpenXmlWriter.Create(part);
            WriteWorksheetStart(_writer, [28D, 34D, 64D, 120D]);
            RowIndex = 1;
            OpenXmlReportWriter.WriteRow(
                _writer,
                RowIndex,
                ["Переменная", "Имя файла", "Относительный путь", "Строка"],
                header: true);
        }

        public uint RowIndex { get; private set; }

        public void WriteRow(string variable, string fileName, string relativePath, string line)
        {
            RowIndex++;
            OpenXmlReportWriter.WriteRow(
                _writer,
                RowIndex,
                [variable, fileName, relativePath, line]);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            WriteWorksheetEnd(_writer, RowIndex, 4);
            _writer.Dispose();
            _disposed = true;
        }
    }
}
