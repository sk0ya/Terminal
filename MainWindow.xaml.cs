using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Terminal;

public partial class MainWindow : Window
{
    private readonly List<TerminalTabItem> _tabs = [];
    private TerminalAppSettings _settings;
    private SettingsWindow? _settingsWindow;
    private bool _isClosing;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _settings = TerminalAppSettings.Load();
        ApplyWindowSettings(_settings);
        ApplyTabStripPlacement(_settings.TabStripPlacement);
        UpdateMaximizeRestoreButton();
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
        AppMenuPopup.IsOpen = false;
        ToggleProfilePicker();
    }

    private void AppMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ProfilePickerPopup.IsOpen = false;
        ToggleAppMenu();
    }

    private void AppMenuSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        OpenSettings();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
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

    private void ToggleAppMenu()
    {
        if (AppMenuPopup.IsOpen)
        {
            AppMenuPopup.IsOpen = false;
            return;
        }

        AppMenuPopup.IsOpen = true;
        _ = Dispatcher.BeginInvoke(AppMenuSettingsButton.Focus, DispatcherPriority.Input);
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
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 150
        };

        var closeButton = new Button
        {
            Content = "×",
            Width = 20,
            Height = 20,
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
            Child = headerPanel
        };

        var listBoxItem = new ListBoxItem
        {
            Content = border,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };

        var tab = new TerminalTabItem(view, listBoxItem, border, titleText, closeButton);
        WindowChrome.SetIsHitTestVisibleInChrome(titleText, true);
        WindowChrome.SetIsHitTestVisibleInChrome(closeButton, true);
        WindowChrome.SetIsHitTestVisibleInChrome(headerPanel, true);
        WindowChrome.SetIsHitTestVisibleInChrome(border, true);
        WindowChrome.SetIsHitTestVisibleInChrome(listBoxItem, true);
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

        if (AppMenuPopup.IsOpen && key == Key.Escape)
        {
            AppMenuPopup.IsOpen = false;
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

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeRestoreButton();
    }

    private void ApplyTabStripPlacement(string? rawPlacement)
    {
        string placement = TerminalTabStripPlacementCatalog.Normalize(rawPlacement);
        _settings.TabStripPlacement = placement;
        bool isTop = placement == TerminalTabStripPlacementCatalog.Top;

        TopTabHost.Content = null;
        LeftTabHost.Content = null;
        RightTabHost.Content = null;
        BottomTabHost.Content = null;
        LeftTabChrome.Visibility = Visibility.Collapsed;
        RightTabChrome.Visibility = Visibility.Collapsed;
        BottomTabChrome.Visibility = Visibility.Collapsed;

        switch (placement)
        {
            case TerminalTabStripPlacementCatalog.Bottom:
                BottomTabHost.Content = ChromePanelLayoutGrid;
                BottomTabChrome.Visibility = Visibility.Visible;
                break;
            case TerminalTabStripPlacementCatalog.Left:
                LeftTabHost.Content = ChromePanelLayoutGrid;
                LeftTabChrome.Visibility = Visibility.Visible;
                break;
            case TerminalTabStripPlacementCatalog.Right:
                RightTabHost.Content = ChromePanelLayoutGrid;
                RightTabChrome.Visibility = Visibility.Visible;
                break;
            default:
                TopTabHost.Content = ChromePanelLayoutGrid;
                break;
        }

        WindowTitleText.Visibility = Visibility.Collapsed;
        TopChromeBar.Visibility = isTop ? Visibility.Visible : Visibility.Collapsed;
        TopChromeRow.Height = isTop ? new GridLength(40) : new GridLength(0);
        if (WindowChrome.GetWindowChrome(this) is WindowChrome chrome)
        {
            chrome.CaptionHeight = isTop ? 40 : 0;
        }

        ConfigureChromePanelLayout(placement);
        ConfigureProfilePickerPlacement(placement);
        ConfigureAppMenuPlacement(placement);
        UpdateTabVisuals();
    }

    private void ConfigureChromePanelLayout(string placement)
    {
        bool isHorizontal = TerminalTabStripPlacementCatalog.IsHorizontal(placement);
        bool isVertical = !isHorizontal;

        Grid.SetRow(AppMenuButton, 0);
        Grid.SetColumn(AppMenuButton, 0);
        Grid.SetRow(TabStripLayoutGrid, isHorizontal ? 0 : 1);
        Grid.SetColumn(TabStripLayoutGrid, isHorizontal ? 1 : 0);
        Grid.SetColumnSpan(TabStripLayoutGrid, isHorizontal ? 1 : 3);
        Grid.SetRow(WindowCommandBar, 0);
        Grid.SetColumn(WindowCommandBar, isHorizontal ? 2 : 0);
        Grid.SetColumnSpan(WindowCommandBar, isHorizontal ? 1 : 3);
        Grid.SetRow(VerticalDragRegion, 2);
        Grid.SetColumn(VerticalDragRegion, 0);
        Grid.SetColumnSpan(VerticalDragRegion, 3);

        ChromeRow0.Height = isHorizontal ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        ChromeRow1.Height = isHorizontal ? new GridLength(0) : GridLength.Auto;
        ChromeRow2.Height = isHorizontal ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ChromeRow3.Height = isHorizontal ? new GridLength(0) : GridLength.Auto;
        ChromeColumn0.Width = GridLength.Auto;
        ChromeColumn1.Width = new GridLength(1, GridUnitType.Star);
        ChromeColumn2.Width = isHorizontal ? GridLength.Auto : new GridLength(0);

        WindowCommandBar.Orientation = Orientation.Horizontal;
        WindowCommandBar.HorizontalAlignment = HorizontalAlignment.Right;
        WindowCommandBar.VerticalAlignment = VerticalAlignment.Center;
        AppMenuButton.HorizontalAlignment = HorizontalAlignment.Left;
        AppMenuButton.VerticalAlignment = VerticalAlignment.Center;

        TabStripLayoutGrid.VerticalAlignment = isHorizontal ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        VerticalDragRegion.Visibility = isVertical ? Visibility.Visible : Visibility.Collapsed;

        ConfigureTabStripLayout(isHorizontal);
    }

    private void ConfigureTabStripLayout(bool isHorizontal)
    {
        Grid.SetRow(TabStrip, 0);
        Grid.SetColumn(TabStrip, 0);
        Grid.SetRow(NewTabButton, isHorizontal ? 0 : 1);
        Grid.SetColumn(NewTabButton, isHorizontal ? 1 : 0);

        TabStripPrimaryRow.Height = isHorizontal ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        TabStripSecondaryRow.Height = isHorizontal ? new GridLength(0) : GridLength.Auto;
        TabStripPrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        TabStripSecondaryColumn.Width = isHorizontal ? GridLength.Auto : new GridLength(0);

        TabStrip.ItemsPanel = (ItemsPanelTemplate)FindResource(
            isHorizontal ? "HorizontalTabItemsPanelTemplate" : "VerticalTabItemsPanelTemplate");
        ScrollViewer.SetHorizontalScrollBarVisibility(
            TabStrip,
            isHorizontal ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(
            TabStrip,
            isHorizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        NewTabButton.Width = isHorizontal ? 34 : double.NaN;
        NewTabButton.HorizontalAlignment = isHorizontal ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        NewTabButton.VerticalAlignment = isHorizontal ? VerticalAlignment.Stretch : VerticalAlignment.Top;
    }

    private void ConfigureProfilePickerPlacement(string placement)
    {
        switch (TerminalTabStripPlacementCatalog.Normalize(placement))
        {
            case TerminalTabStripPlacementCatalog.Bottom:
                ProfilePickerPopup.Placement = PlacementMode.Top;
                ProfilePickerPopup.HorizontalOffset = -8;
                ProfilePickerPopup.VerticalOffset = -4;
                break;
            case TerminalTabStripPlacementCatalog.Left:
                ProfilePickerPopup.Placement = PlacementMode.Right;
                ProfilePickerPopup.HorizontalOffset = 4;
                ProfilePickerPopup.VerticalOffset = -8;
                break;
            case TerminalTabStripPlacementCatalog.Right:
                ProfilePickerPopup.Placement = PlacementMode.Left;
                ProfilePickerPopup.HorizontalOffset = -4;
                ProfilePickerPopup.VerticalOffset = -8;
                break;
            default:
                ProfilePickerPopup.Placement = PlacementMode.Bottom;
                ProfilePickerPopup.HorizontalOffset = -8;
                ProfilePickerPopup.VerticalOffset = 4;
                break;
        }
    }

    private void ConfigureAppMenuPlacement(string placement)
    {
        switch (TerminalTabStripPlacementCatalog.Normalize(placement))
        {
            case TerminalTabStripPlacementCatalog.Bottom:
                AppMenuPopup.Placement = PlacementMode.Top;
                AppMenuPopup.HorizontalOffset = -8;
                AppMenuPopup.VerticalOffset = -4;
                break;
            case TerminalTabStripPlacementCatalog.Left:
                AppMenuPopup.Placement = PlacementMode.Right;
                AppMenuPopup.HorizontalOffset = 4;
                AppMenuPopup.VerticalOffset = -6;
                break;
            case TerminalTabStripPlacementCatalog.Right:
                AppMenuPopup.Placement = PlacementMode.Left;
                AppMenuPopup.HorizontalOffset = -4;
                AppMenuPopup.VerticalOffset = -6;
                break;
            default:
                AppMenuPopup.Placement = PlacementMode.Bottom;
                AppMenuPopup.HorizontalOffset = -8;
                AppMenuPopup.VerticalOffset = 4;
                break;
        }
    }

    private void ChromePanelLayoutGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindVisualAncestor<Button>(source) is not null
            || FindVisualAncestor<ListBoxItem>(source) is not null
            || FindVisualAncestor<ScrollBar>(source) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch
        {
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
        bool isHorizontal = TerminalTabStripPlacementCatalog.IsHorizontal(_settings.TabStripPlacement);

        foreach (TerminalTabItem tab in _tabs)
        {
            bool isSelected = ReferenceEquals(TabStrip.SelectedItem, tab.ListBoxItem);
            tab.HeaderBorder.Background = new SolidColorBrush(isSelected
                ? Color.FromRgb(0x0E, 0x0E, 0x0E)
                : Color.FromRgb(0x1B, 0x1B, 0x1B));
            tab.HeaderBorder.BorderThickness = isHorizontal
                ? new Thickness(0, 0, 1, 0)
                : new Thickness(0, 0, 0, 1);
            tab.TitleText.Foreground = new SolidColorBrush(isSelected
                ? Color.FromRgb(0xED, 0xED, 0xED)
                : Color.FromRgb(0xA7, 0xA7, 0xA7));
        }
    }

    private static string GetDefaultCommandLine()
        => TerminalProfileCatalog.BuildDefaultCommandLine();

    private void OpenSettings()
    {
        AppMenuPopup.IsOpen = false;

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(GetSettingsSeed())
        {
            Owner = this,
            ShowInTaskbar = false
        };
        _settingsWindow.SettingsChanged += ApplyUpdatedSettings;
        _settingsWindow.Closed += SettingsWindow_Closed;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void ApplyUpdatedSettings(TerminalAppSettings settings)
    {
        _settings = settings;
        ApplyTabStripPlacement(_settings.TabStripPlacement);
        SaveWindowSettings();
        _settings.Save();

        foreach (TerminalTabItem tab in _tabs)
        {
            tab.View.ApplySettings(_settings);
            UpdateTabHeader(tab, tab.View.HeaderTitle);
        }
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        if (_settingsWindow is null)
        {
            return;
        }

        _settingsWindow.SettingsChanged -= ApplyUpdatedSettings;
        _settingsWindow.Closed -= SettingsWindow_Closed;
        _settingsWindow = null;
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
            FontFamilyName = tabSettings.FontFamilyName,
            FontSize = tabSettings.FontSize,
            TabStripPlacement = _settings.TabStripPlacement,
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

    private void UpdateMaximizeRestoreButton()
    {
        if (MaximizeRestoreButton is null)
        {
            return;
        }

        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized
            ? "\uE923"
            : "\uE922";
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private sealed record TerminalTabItem(
        TerminalTabView View,
        ListBoxItem ListBoxItem,
        Border HeaderBorder,
        TextBlock TitleText,
        Button CloseButton);
}
