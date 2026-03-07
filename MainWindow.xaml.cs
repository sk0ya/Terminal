using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ConPtyTerminal;

public partial class MainWindow : Window
{
    private ITerminalSession? _session;
    private AnsiTerminalBuffer _terminalBuffer = new(120, 30);
    private short _currentColumns = 120;
    private short _currentRows = 30;
    private readonly DispatcherTimer _sessionWatchdog = new();
    private int _autoRecoveryAttempts;
    private bool _isRecovering;
    private bool _useCompatibilityMode;

    private const int MaxAutoRecoveryAttempts = 1;
    private static readonly TimeSpan InitialOutputTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleOutputTimeout = TimeSpan.FromSeconds(20);

    public MainWindow()
    {
        InitializeComponent();
        CommandTextBox.Text = BuildDefaultCommandLine();
        _sessionWatchdog.Interval = TimeSpan.FromSeconds(1);
        _sessionWatchdog.Tick += SessionWatchdog_Tick;
        _sessionWatchdog.Start();
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
        _sessionWatchdog.Stop();
        StopTerminal();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        _autoRecoveryAttempts = 0;
        _useCompatibilityMode = false;
        StartTerminal();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _autoRecoveryAttempts = 0;
        StopTerminal();
    }

    private void RecoverButton_Click(object sender, RoutedEventArgs e)
    {
        RecoverSession(isAutomatic: false);
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
            ? BuildDefaultCommandLine()
            : CommandTextBox.Text.Trim();

        (_currentColumns, _currentRows) = CalculateTerminalSize();
        _terminalBuffer = new AnsiTerminalBuffer(_currentColumns, _currentRows);
        RenderTerminal();

        try
        {
            string modeLabel;
            if (_useCompatibilityMode)
            {
                _session = new ProcessPipeSession(commandLine);
                modeLabel = "Compat";
            }
            else
            {
                try
                {
                    _session = new ConPtySession(_currentColumns, _currentRows, commandLine);
                    modeLabel = "ConPTY";
                }
                catch
                {
                    _useCompatibilityMode = true;
                    _session = new ProcessPipeSession(commandLine);
                    modeLabel = "Compat";
                }
            }

            _session.OutputReceived += OnOutputReceived;
            _session.Exited += OnProcessExited;
            _session.Start();
            UpdateUiState(isRunning: true);
            InputTextBox.Focus();
            SetStatus($"Started ({modeLabel}): {commandLine}");
        }
        catch (Exception ex)
        {
            UpdateUiState(isRunning: false);
            SetStatus($"Failed to start terminal: {FormatExceptionMessage(ex)}");
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
        RecoverButton.IsEnabled = isRunning;
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

    private static string BuildDefaultCommandLine()
    {
        string? comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && File.Exists(comSpec))
        {
            return $"\"{comSpec}\" /K";
        }

        return "cmd.exe /K";
    }

    private static string FormatExceptionMessage(Exception ex)
    {
        if (ex is Win32Exception win32)
        {
            return $"{win32.Message} (Win32={win32.NativeErrorCode}, HRESULT=0x{win32.HResult:X8})";
        }

        return $"{ex.Message} (HRESULT=0x{ex.HResult:X8})";
    }

    private void SessionWatchdog_Tick(object? sender, EventArgs e)
    {
        if (_session is null || _isRecovering)
        {
            return;
        }

        if (!_session.IsOutputStalled(InitialOutputTimeout, IdleOutputTimeout))
        {
            return;
        }

        if (_autoRecoveryAttempts >= MaxAutoRecoveryAttempts)
        {
            SetStatus(_useCompatibilityMode
                ? "Output stalled in compatibility mode. Click Recover."
                : "Output stalled. Switching to compatibility mode via Recover.");
            return;
        }

        _autoRecoveryAttempts++;
        RecoverSession(isAutomatic: true);
    }

    private void RecoverSession(bool isAutomatic)
    {
        if (_session is null || _isRecovering)
        {
            return;
        }

        _isRecovering = true;
        try
        {
            _session.TryForceUnlock();
            if (!_useCompatibilityMode)
            {
                _useCompatibilityMode = true;
            }

            SetStatus(isAutomatic
                ? "Output stalled. Unlocking and restarting in compatibility mode..."
                : "Recover requested. Unlocking and restarting in compatibility mode...");
            StartTerminal();
        }
        catch (Exception ex)
        {
            SetStatus($"Recovery failed: {FormatExceptionMessage(ex)}");
        }
        finally
        {
            _isRecovering = false;
        }
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
