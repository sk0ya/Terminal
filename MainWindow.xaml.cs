using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ConPtyTerminal;

public partial class MainWindow : Window
{
    private readonly List<TerminalTabItem> _tabs = [];
    private TerminalAppSettings _settings;
    private bool _isClosing;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _settings = TerminalAppSettings.Load();
        ApplyWindowSettings(_settings);
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_tabs.Count == 0)
        {
            AddNewTabFromSettings();
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        try
        {
            SaveWindowSettings();
            foreach (TerminalTabItem tab in _tabs.ToArray())
            {
                await tab.View.CloseAsync();
            }
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleProfilePicker();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    private void AddNewTabFromSettings()
    {
        AddNewTab(
            string.IsNullOrWhiteSpace(_settings.CommandLine)
                ? TerminalProfileCatalog.BuildDefaultCommandLine()
                : _settings.CommandLine.Trim(),
            GetWorkingDirectoryOrDefault());
    }

    private void AddNewTab(TerminalProfileDefinition profile)
    {
        AddNewTab(profile.CommandLine, GetWorkingDirectoryOrDefault());
    }

    private void AddNewTab(string commandLine, string workingDirectory)
    {
        var view = new TerminalTabView(commandLine, workingDirectory);
        var tab = CreateTabItem(view);
        _tabs.Add(tab);
        TabStrip.Items.Add(tab.ListBoxItem);
        view.HeaderTitleChanged += (_, title) => UpdateTabHeader(tab, title);
        UpdateTabHeader(tab, view.HeaderTitle);
        TabStrip.SelectedItem = tab.ListBoxItem;
    }

    private string GetWorkingDirectoryOrDefault()
    {
        return string.IsNullOrWhiteSpace(_settings.WorkingDirectory)
            ? Environment.CurrentDirectory
            : _settings.WorkingDirectory.Trim();
    }

    private void ToggleProfilePicker()
    {
        if (ProfilePickerPopup.IsOpen)
        {
            ProfilePickerPopup.IsOpen = false;
            return;
        }

        PopulateProfilePicker();
        ProfilePickerPopup.IsOpen = true;

        if (ProfilePickerPanel.Children.OfType<Button>().FirstOrDefault() is Button firstButton)
        {
            _ = Dispatcher.BeginInvoke(firstButton.Focus, DispatcherPriority.Input);
        }
    }

    private void PopulateProfilePicker()
    {
        ProfilePickerPanel.Children.Clear();

        foreach (TerminalProfileDefinition profile in TerminalProfileCatalog.CreateProfiles())
        {
            ProfilePickerPanel.Children.Add(CreateProfilePickerButton(profile));
        }
    }

    private Button CreateProfilePickerButton(TerminalProfileDefinition profile)
    {
        var nameText = new TextBlock
        {
            Text = profile.DisplayName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED))
        };

        var descriptionText = new TextBlock
        {
            Text = profile.Description,
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xA7, 0xA7)),
            TextWrapping = TextWrapping.Wrap
        };

        var contentPanel = new StackPanel();
        contentPanel.Children.Add(nameText);
        contentPanel.Children.Add(descriptionText);

        var button = new Button
        {
            Tag = profile,
            Content = contentPanel,
            Style = (Style)FindResource("ProfilePickerButtonStyle")
        };
        button.Click += ProfilePickerButton_Click;
        return button;
    }

    private void ProfilePickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TerminalProfileDefinition profile })
        {
            return;
        }

        ProfilePickerPopup.IsOpen = false;
        AddNewTab(profile);
    }

    private TerminalTabItem CreateTabItem(TerminalTabView view)
    {
        var titleText = new TextBlock
        {
            Text = "Terminal",
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 150
        };

        var closeButton = new Button
        {
            Content = "×",
            Width = 20,
            Height = 20,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xA7, 0xA7)),
            FontSize = 12,
            Cursor = Cursors.Hand
        };

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(closeButton);

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(14, 8, 12, 8),
            Child = headerPanel
        };

        var listBoxItem = new ListBoxItem
        {
            Content = border,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        var tab = new TerminalTabItem(view, listBoxItem, border, titleText, closeButton);
        closeButton.Click += async (_, _) => await CloseTabAsync(tab);
        return tab;
    }

    private async Task CloseTabAsync(TerminalTabItem tab)
    {
        int tabIndex = _tabs.IndexOf(tab);
        if (tabIndex < 0)
        {
            return;
        }

        bool wasSelected = ReferenceEquals(TabStrip.SelectedItem, tab.ListBoxItem);
        TabStrip.Items.Remove(tab.ListBoxItem);
        _tabs.RemoveAt(tabIndex);
        await tab.View.CloseAsync();

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        if (wasSelected)
        {
            int nextIndex = Math.Clamp(tabIndex, 0, _tabs.Count - 1);
            TabStrip.SelectedItem = _tabs[nextIndex].ListBoxItem;
        }

        UpdateTabVisuals();
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TabStrip.SelectedItem is not ListBoxItem selectedItem)
        {
            ActiveTabHost.Content = null;
            return;
        }

        TerminalTabItem? tab = _tabs.FirstOrDefault(candidate => ReferenceEquals(candidate.ListBoxItem, selectedItem));
        if (tab is null)
        {
            return;
        }

        ActiveTabHost.Content = tab.View;
        Title = $"{tab.TitleText.Text} - ConPTY Terminal";
        UpdateTabVisuals();
        _ = Dispatcher.BeginInvoke(tab.View.FocusTerminal, DispatcherPriority.Input);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.T)
        {
            AddNewTabFromSettings();
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && key == Key.OemComma)
        {
            OpenSettings();
            e.Handled = true;
            return;
        }

        if (ProfilePickerPopup.IsOpen && key == Key.Escape)
        {
            ProfilePickerPopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.W)
        {
            if (GetActiveTab() is TerminalTabItem activeTab)
            {
                _ = CloseTabAsync(activeTab);
                e.Handled = true;
            }

            return;
        }

        if (modifiers == ModifierKeys.Control && key == Key.Tab)
        {
            MoveSelection(1);
            e.Handled = true;
            return;
        }

        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Tab)
        {
            MoveSelection(-1);
            e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && key >= Key.D1 && key <= Key.D9)
        {
            int targetIndex = key - Key.D1;
            if (targetIndex < _tabs.Count)
            {
                TabStrip.SelectedItem = _tabs[targetIndex].ListBoxItem;
                e.Handled = true;
            }
        }
    }

    private void MoveSelection(int delta)
    {
        if (_tabs.Count == 0)
        {
            return;
        }

        int currentIndex = Math.Max(0, _tabs.FindIndex(tab => ReferenceEquals(tab.ListBoxItem, TabStrip.SelectedItem)));
        int nextIndex = (currentIndex + delta + _tabs.Count) % _tabs.Count;
        TabStrip.SelectedItem = _tabs[nextIndex].ListBoxItem;
    }

    private TerminalTabItem? GetActiveTab()
    {
        return _tabs.FirstOrDefault(tab => ReferenceEquals(tab.ListBoxItem, TabStrip.SelectedItem));
    }

    private void UpdateTabHeader(TerminalTabItem tab, string title)
    {
        tab.TitleText.Text = title;
        if (ReferenceEquals(TabStrip.SelectedItem, tab.ListBoxItem))
        {
            Title = $"{title} - ConPTY Terminal";
        }
    }

    private void UpdateTabVisuals()
    {
        foreach (TerminalTabItem tab in _tabs)
        {
            bool isSelected = ReferenceEquals(TabStrip.SelectedItem, tab.ListBoxItem);
            tab.HeaderBorder.Background = new SolidColorBrush(isSelected
                ? Color.FromRgb(0x0E, 0x0E, 0x0E)
                : Color.FromRgb(0x1B, 0x1B, 0x1B));
            tab.TitleText.Foreground = new SolidColorBrush(isSelected
                ? Color.FromRgb(0xED, 0xED, 0xED)
                : Color.FromRgb(0xA7, 0xA7, 0xA7));
        }
    }

    private static string GetDefaultCommandLine()
        => TerminalProfileCatalog.BuildDefaultCommandLine();

    private void OpenSettings()
    {
        TerminalAppSettings seed = GetSettingsSeed();
        var window = new SettingsWindow(seed)
        {
            Owner = this
        };

        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings = window.SavedSettings;
        SaveWindowSettings();
        _settings.Save();

        foreach (TerminalTabItem tab in _tabs)
        {
            tab.View.ApplySettings(_settings);
            UpdateTabHeader(tab, tab.View.HeaderTitle);
        }
    }

    private TerminalAppSettings GetSettingsSeed()
    {
        TerminalTabItem? activeTab = GetActiveTab();
        if (activeTab is null)
        {
            return _settings;
        }

        TerminalAppSettings tabSettings = activeTab.View.CreateSettingsSnapshot();
        return new TerminalAppSettings
        {
            SelectedProfileId = tabSettings.SelectedProfileId,
            CommandLine = tabSettings.CommandLine,
            WorkingDirectory = tabSettings.WorkingDirectory,
            FontSize = tabSettings.FontSize,
            WindowWidth = _settings.WindowWidth,
            WindowHeight = _settings.WindowHeight
        };
    }

    private void ApplyWindowSettings(TerminalAppSettings settings)
    {
        if (settings.WindowWidth >= MinWidth)
        {
            Width = settings.WindowWidth;
        }

        if (settings.WindowHeight >= MinHeight)
        {
            Height = settings.WindowHeight;
        }
    }

    private void SaveWindowSettings()
    {
        _settings.WindowWidth = ActualWidth;
        _settings.WindowHeight = ActualHeight;
    }

    private sealed record TerminalTabItem(
        TerminalTabView View,
        ListBoxItem ListBoxItem,
        Border HeaderBorder,
        TextBlock TitleText,
        Button CloseButton);
}
