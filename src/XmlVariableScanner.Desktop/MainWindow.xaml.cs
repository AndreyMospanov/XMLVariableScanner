using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using XmlVariableScanner.Core;
using XmlVariableScanner.Reports;

namespace XmlVariableScanner.Desktop;

public partial class MainWindow : Window
{
    private readonly ScanService _scanService = new(new OpenXmlReportWriter());
    private CancellationTokenSource? _scanCancellation;
    private string? _lastOutputPath;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void BrowseInputDirectory_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderDialog dialog = new()
        {
            Title = "Выберите папку с XML-файлами",
            Multiselect = false
        };

        if (Directory.Exists(InputDirectoryTextBox.Text))
        {
            dialog.InitialDirectory = InputDirectoryTextBox.Text;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        InputDirectoryTextBox.Text = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(OutputPathTextBox.Text))
        {
            OutputPathTextBox.Text = Path.Combine(
                dialog.FolderName,
                $"XML_Variables_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }
    }

    private void BrowseOutputPath_Click(object sender, RoutedEventArgs e)
    {
        SaveFileDialog dialog = new()
        {
            Title = "Куда сохранить отчёт",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = string.IsNullOrWhiteSpace(OutputPathTextBox.Text)
                ? $"XML_Variables_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
                : Path.GetFileName(OutputPathTextBox.Text)
        };

        string? existingDirectory = Path.GetDirectoryName(OutputPathTextBox.Text);
        if (Directory.Exists(existingDirectory))
        {
            dialog.InitialDirectory = existingDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            OutputPathTextBox.Text = dialog.FileName;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScanOptions options = BuildOptions();
            _ = new SearchPatternSet(
                options.SearchPattern,
                options.ExclusionPatterns,
                options.PatternMode,
                options.IgnoreCase);

            SetRunningState(isRunning: true);
            _scanCancellation = new CancellationTokenSource();
            Progress<ScanProgress> progress = new(UpdateProgress);

            ScanSummary summary = await _scanService.ScanAsync(options, progress, _scanCancellation.Token);
            _lastOutputPath = summary.OutputPath;
            ProgressBar.Value = 100;
            StatusTextBlock.Text = "Обработка завершена";
            DetailsTextBlock.Text =
                $"Файлов: {summary.ProcessedFiles:N0}; строк отчёта: {summary.Occurrences:N0}; " +
                $"ошибок: {summary.ErrorCount:N0}; время: {summary.Elapsed:hh\\:mm\\:ss}.";
            OpenOutputFolderButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Обработка отменена";
            DetailsTextBlock.Text = "Незавершённый временный отчёт удалён.";
        }
        catch (RegexParseException exception)
        {
            ShowError("Ошибка в регулярном выражении", exception.Message);
        }
        catch (Exception exception)
        {
            ShowError("Не удалось выполнить обработку", exception.Message);
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            SetRunningState(isRunning: false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusTextBlock.Text = "Отмена…";
        _scanCancellation?.Cancel();
    }

    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputPath is null)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_lastOutputPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void PatternModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        bool regex = GetSelectedTag(PatternModeComboBox) == nameof(PatternMode.RegularExpression);
        SearchHelpRun.ToolTip = regex
            ? "Регулярное выражение .NET. Для возврата только части совпадения используйте именованную группу (?<variable>...)."
            : "Простая маска: * заменяет любое количество символов переменной, ? — один символ. Знак % считается обычным символом.";
        ExclusionsHelpRun.ToolTip = regex
            ? "Каждая строка — отдельное регулярное выражение, проверяемое против найденной переменной."
            : "Переменная не попадёт в отчёт, если соответствует хотя бы одной маске. Здесь также работают * и ?.";
    }

    private ScanOptions BuildOptions()
    {
        string inputDirectory = InputDirectoryTextBox.Text.Trim();
        string outputPath = OutputPathTextBox.Text.Trim();

        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException("Выберите существующую папку с XML-файлами.");
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Укажите путь к выходному XLSX-файлу.");
        }

        string? outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException("Папка для выходного отчёта не существует.");
        }

        PatternMode patternMode = Enum.Parse<PatternMode>(GetSelectedTag(PatternModeComboBox));
        ReadingMode readingMode = Enum.Parse<ReadingMode>(GetSelectedTag(ReadingModeComboBox));
        int parallelism = int.Parse(((ComboBoxItem)ParallelismComboBox.SelectedItem).Content.ToString()!);
        string[] exclusions = ExclusionsTextBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ScanOptions
        {
            InputDirectory = Path.GetFullPath(inputDirectory),
            OutputPath = Path.GetFullPath(outputPath),
            SearchPattern = SearchPatternTextBox.Text.Trim(),
            ExclusionPatterns = exclusions,
            ReadingMode = readingMode,
            PatternMode = patternMode,
            IgnoreCase = IgnoreCaseCheckBox.IsChecked == true,
            IncludeSubdirectories = IncludeSubdirectoriesCheckBox.IsChecked == true,
            MaxDegreeOfParallelism = parallelism
        };
    }

    private void UpdateProgress(ScanProgress progress)
    {
        ProgressBar.Value = progress.TotalFiles == 0
            ? 0
            : (double)progress.ProcessedFiles / progress.TotalFiles * 100;
        StatusTextBlock.Text = $"Обработано {progress.ProcessedFiles:N0} из {progress.TotalFiles:N0} файлов";
        DetailsTextBlock.Text = $"Строк отчёта: {progress.Occurrences:N0}. Последний файл: {progress.CurrentFile}";
    }

    private void SetRunningState(bool isRunning)
    {
        ConfigurationPanel.IsEnabled = !isRunning;
        StartButton.IsEnabled = !isRunning;
        CancelButton.IsEnabled = isRunning;
        if (isRunning)
        {
            ProgressBar.Value = 0;
            StatusTextBlock.Text = "Подготовка списка XML-файлов…";
            DetailsTextBlock.Text = string.Empty;
            OpenOutputFolderButton.IsEnabled = false;
        }
    }

    private static string GetSelectedTag(ComboBox comboBox) =>
        ((ComboBoxItem)comboBox.SelectedItem).Tag?.ToString()
        ?? throw new InvalidOperationException("Не выбран режим обработки.");

    private void ShowError(string title, string message)
    {
        StatusTextBlock.Text = title;
        DetailsTextBlock.Text = message;
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanCancellation?.Cancel();
        base.OnClosed(e);
    }
}
