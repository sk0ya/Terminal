using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ConPtyTerminal;

public partial class MainWindow : Window
{
    private ConPtySession? _session;
    private AnsiTerminalBuffer _terminalBuffer = new(120, 30);
    private short _currentColumns = 120;
    private short _currentRows = 30;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        TerminalOutput.SizeChanged += OnTerminalOutputSizeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartTerminal();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        StopTerminal();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartTerminal();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopTerminal();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e)
    {
        SendInput();
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendInput();
        }
    }

    private void OnTerminalOutputSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var (columns, rows) = CalculateTerminalSize();
        if (columns == _currentColumns && rows == _currentRows)
        {
            return;
        }

        _currentColumns = columns;
        _currentRows = rows;
        _terminalBuffer.Resize(columns, rows);
        RenderTerminal();

        if (_session is null)
        {
            return;
        }

        try
        {
            _session.Resize(columns, rows);
        }
        catch (Exception ex)
        {
            SetStatus($"Resize failed: {ex.Message}");
        }
    }

    private void StartTerminal()
    {
        StopTerminal();

        string commandLine = string.IsNullOrWhiteSpace(CommandTextBox.Text)
            ? "cmd.exe /K chcp 65001 > nul"
            : CommandTextBox.Text.Trim();

        (_currentColumns, _currentRows) = CalculateTerminalSize();
        _terminalBuffer = new AnsiTerminalBuffer(_currentColumns, _currentRows);
        RenderTerminal();

        try
        {
            _session = new ConPtySession(_currentColumns, _currentRows, commandLine);
            _session.OutputReceived += OnOutputReceived;
            _session.Exited += OnProcessExited;
            UpdateUiState(isRunning: true);
            InputTextBox.Focus();
            SetStatus($"Started: {commandLine}");
        }
        catch (Exception ex)
        {
            UpdateUiState(isRunning: false);
            SetStatus($"Failed to start terminal: {ex.Message}");
        }
    }

    private void StopTerminal()
    {
        if (_session is null)
        {
            UpdateUiState(isRunning: false);
            return;
        }

        _session.OutputReceived -= OnOutputReceived;
        _session.Exited -= OnProcessExited;
        _session.Dispose();
        _session = null;

        UpdateUiState(isRunning: false);
        SetStatus("Stopped.");
    }

    private void OnOutputReceived(object? sender, string text)
    {
        Dispatcher.Invoke(() =>
        {
            _terminalBuffer.Process(text);
            RenderTerminal();
        });
    }

    private void OnProcessExited(object? sender, int exitCode)
    {
        Dispatcher.Invoke(() =>
        {
            SetStatus($"Process exited with code {exitCode}.");
            StopTerminal();
        });
    }

    private void SendInput()
    {
        if (_session is null)
        {
            return;
        }

        string input = InputTextBox.Text;
        InputTextBox.Clear();

        try
        {
            _session.Write(input + Environment.NewLine);
        }
        catch (Exception ex)
        {
            SetStatus($"Send failed: {ex.Message}");
        }
    }

    private void UpdateUiState(bool isRunning)
    {
        StartButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;
        SendButton.IsEnabled = isRunning;
        InputTextBox.IsEnabled = isRunning;
        CommandTextBox.IsEnabled = !isRunning;
    }

    private void RenderTerminal()
    {
        TerminalOutput.Text = _terminalBuffer.GetDisplayText();
        TerminalOutput.CaretIndex = TerminalOutput.Text.Length;
        TerminalOutput.ScrollToEnd();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";
    }

    private (short Columns, short Rows) CalculateTerminalSize()
    {
        if (TerminalOutput.ActualWidth <= 0 || TerminalOutput.ActualHeight <= 0)
        {
            return (120, 30);
        }

        var typeface = new Typeface(
            TerminalOutput.FontFamily,
            TerminalOutput.FontStyle,
            TerminalOutput.FontWeight,
            TerminalOutput.FontStretch);

        var sample = new FormattedText(
            "W",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            TerminalOutput.FontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double charWidth = Math.Max(sample.WidthIncludingTrailingWhitespace, 1.0);
        double charHeight = Math.Max(sample.Height, 1.0);

        int columns = Math.Clamp((int)(TerminalOutput.ActualWidth / charWidth), 20, 500);
        int rows = Math.Clamp((int)(TerminalOutput.ActualHeight / charHeight), 10, 300);

        return ((short)columns, (short)rows);
    }
}
