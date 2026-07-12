using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Shell;
using System.Windows.Threading;

using Terminal.Settings;
using Terminal.Tabs;
using Terminal.Input;

namespace Terminal;

public partial class MainWindow : Window
{
    private readonly List<TerminalTabItem> _tabs = [];
    private TerminalAppSettings _settings;
    private SettingsWindow? _settingsWindow;
    private bool _isClosing;
    private bool _allowClose;
    private bool _highContrastActive;
    private readonly Dictionary<object, object> _normalChromeResources = [];
    private readonly Brush _normalWindowForeground;
    private readonly TerminalKeyBindings _keyBindings;
    private Point? _tabDragStart;
    private TerminalTabItem? _draggedTab;

    public MainWindow()
    {
        InitializeComponent();
        _normalWindowForeground = Foreground;
        _settings = TerminalAppSettings.Load();
        _keyBindings = new(_settings.KeyBindings);
        ApplyWindowSettings(_settings);
        ApplyTabStripPlacement(_settings.TabStripPlacement);
        UpdateMaximizeRestoreButton();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        TerminalProfileCatalog.ProfilesChanged += TerminalProfilesChanged;
        _highContrastActive = SystemParameters.HighContrast;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyHighContrastState(_highContrastActive);
        if (_tabs.Count == 0)
        {
            RestoreSavedTabs();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        TerminalProfileCatalog.ProfilesChanged -= TerminalProfilesChanged;
    }

    private void TerminalProfilesChanged(object? sender, EventArgs e)
        => _ = Dispatcher.BeginInvoke(PopulateProfilePicker);

    private void SystemParameters_StaticPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.HighContrast)) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ApplyHighContrastState(SystemParameters.HighContrast));
            return;
        }

        ApplyHighContrastState(SystemParameters.HighContrast);
    }

    private void ApplyHighContrastState(bool active)
    {
        _highContrastActive = active;
        ApplyBackdrop(_settings);
        ApplyChromeTheme(active);
        foreach (Window window in Application.Current.Windows)
            if (!ReferenceEquals(window, this)) HighContrastAppearance.Apply(window, active);
        foreach (TerminalTabItem tab in _tabs)
        {
            foreach (TerminalTabView pane in tab.Panes) ApplyAccessibilityTheme(pane);
            UpdateTabHeader(tab, tab.View.HeaderTitle);
        }
    }

    private void ApplyChromeTheme(bool active)
    {
        string[] keys = ["ChromeBrush", "ChromeBorderBrush", "TabIdleBrush", "TabActiveBrush",
            "TabHoverBrush", "TabTextBrush", "TabTextMutedBrush", "AccentBrush", "CommandHoverBrush",
            "CommandPressedBrush", "CloseHoverBrush", "ClosePressedBrush", "ProfilePressedBrush", "StateTextBrush"];
        foreach (string key in keys)
        {
            if (active)
            {
                if (!_normalChromeResources.ContainsKey(key)) _normalChromeResources[key] = Resources[key];
                Resources[key] = key is "CommandPressedBrush" or "ClosePressedBrush" or "ProfilePressedBrush"
                    ? SystemColors.HotTrackBrush : key is "AccentBrush" or "TabActiveBrush" or "TabHoverBrush" or "CommandHoverBrush" or "CloseHoverBrush"
                    ? SystemColors.HighlightBrush : key is "StateTextBrush"
                        ? SystemColors.HighlightTextBrush : key.Contains("Text", StringComparison.Ordinal)
                        ? SystemColors.WindowTextBrush : key.Contains("Border", StringComparison.Ordinal)
                            ? SystemColors.WindowTextBrush : SystemColors.WindowBrush;
            }
            else if (_normalChromeResources.Remove(key, out object? value)) Resources[key] = value;
        }
        foreach (GridSplitter splitter in FindVisualChildren<GridSplitter>(ActiveTabHost))
            splitter.Background = active ? SystemColors.WindowTextBrush : (Brush)Resources["ChromeBorderBrush"];
        UpdateTabVisuals();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void ApplyAccessibilityTheme(TerminalTabView pane)
    {
        pane.SetHighContrastMode(_highContrastActive);
        pane.SetColorTheme(TerminalColorThemeCatalog.ResolveEffective(
            _settings, _highContrastActive, SystemColors.WindowTextColor,
            SystemColors.WindowColor, SystemColors.HighlightColor));
    }

    private void RestoreSavedTabs()
    {
        foreach (TerminalSavedTab saved in _settings.SavedTabs.Where(tab => !string.IsNullOrWhiteSpace(tab.CommandLine)))
        {
            string directory = Directory.Exists(saved.WorkingDirectory) ? saved.WorkingDirectory : GetWorkingDirectoryOrDefault();
            AddNewTab(saved.CommandLine, directory);
        }
        if (_tabs.Count == 0) AddNewTabFromSettings();
        else TabStrip.SelectedIndex = Math.Clamp(_settings.ActiveTabIndex, 0, _tabs.Count - 1);
    }

    private void ApplyBackdrop(TerminalAppSettings settings)
    {
        if (_highContrastActive) DwmBackdrop.Clear(this);
        bool active = !_highContrastActive && DwmBackdrop.Apply(this, settings.BackdropType);
        var transparent = new SolidColorBrush(Colors.Transparent);
        var opaque = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x0E));
        Background = active ? transparent : opaque;
        ActiveTabHost.Background = active ? transparent : opaque;

        var chromeBg = active
            ? new SolidColorBrush(Color.FromArgb(0xB0, 0x15, 0x15, 0x15))
            : new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15));

        TopChromeBar.Background = chromeBg;
        LeftTabChrome.Background = chromeBg;
        RightTabChrome.Background = chromeBg;
        BottomTabChrome.Background = chromeBg;

        foreach (TerminalTabItem tab in _tabs)
        {
            foreach (TerminalTabView pane in tab.Panes) pane.SetBackdropActive(active);
        }

        if (_highContrastActive)
        {
            Background = SystemColors.WindowBrush;
            Foreground = SystemColors.WindowTextBrush;
            ActiveTabHost.Background = SystemColors.WindowBrush;
            TopChromeBar.Background = SystemColors.WindowBrush;
            LeftTabChrome.Background = SystemColors.WindowBrush;
            RightTabChrome.Background = SystemColors.WindowBrush;
            BottomTabChrome.Background = SystemColors.WindowBrush;
        }
        else Foreground = _normalWindowForeground;
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

        if (_settings.ConfirmCloseWithRunningProcesses &&
            TerminalCloseConfirmation.NeedsConfirmation(_tabs.SelectMany(tab => tab.Panes).Select(pane => pane.RequiresCloseConfirmation)) &&
            MessageBox.Show(
                this,
                "実行中の処理があります。すべてのタブを終了しますか？",
                "Terminal を終了",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _isClosing = true;
        try
        {
            SaveWindowSettings();
            _settings.Save();
            foreach (TerminalTabItem tab in _tabs.ToArray())
            {
                foreach (TerminalTabView pane in tab.Panes.ToArray()) await pane.CloseAsync();
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

    private void SplitHorizontalButton_Click(object sender, RoutedEventArgs e) { AppMenuPopup.IsOpen = false; SplitActivePane(true); }
    private void SplitVerticalButton_Click(object sender, RoutedEventArgs e) { AppMenuPopup.IsOpen = false; SplitActivePane(false); }
    private void ClosePaneButton_Click(object sender, RoutedEventArgs e) { AppMenuPopup.IsOpen = false; _ = CloseActivePaneAsync(); }

    private void SplitActivePane(bool horizontal)
    {
        TerminalTabItem? tab = GetActiveTab();
        if (tab is null) return;
        TerminalTabView existing = tab.ActivePane;
        var added = new TerminalTabView(existing.CommandLine, existing.WorkingDirectory);
        WirePane(tab, added); tab.Panes.Add(added);
        var grid = new Grid();
        if (horizontal)
        {
            grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) }); grid.RowDefinitions.Add(new RowDefinition());
            Grid.SetRow(existing, 0); Grid.SetRow(added, 2);
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(existing, 0); Grid.SetColumn(added, 2);
        }
        var splitter = new GridSplitter
        {
            Background = _highContrastActive ? SystemColors.WindowTextBrush : (Brush)Resources["ChromeBorderBrush"],
            HorizontalAlignment = horizontal ? HorizontalAlignment.Stretch : HorizontalAlignment.Center,
            VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch,
            ResizeDirection = horizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns,
            ShowsPreview = true
        };
        if (horizontal) Grid.SetRow(splitter, 1); else Grid.SetColumn(splitter, 1);

        if (ReferenceEquals(tab.Content, existing))
        {
            ActiveTabHost.Content = null;
            tab.Content = grid;
        }
        else if (VisualTreeHelper.GetParent(existing) is Grid parent)
        {
            int row = Grid.GetRow(existing), column = Grid.GetColumn(existing);
            parent.Children.Remove(existing); parent.Children.Add(grid); Grid.SetRow(grid, row); Grid.SetColumn(grid, column);
        }
        grid.Children.Add(existing); grid.Children.Add(splitter); grid.Children.Add(added);
        tab.ActivePane = added; ActiveTabHost.Content = tab.Content;
        _ = Dispatcher.BeginInvoke(added.FocusTerminal, DispatcherPriority.Input);
    }

    private async Task CloseActivePaneAsync()
    {
        TerminalTabItem? tab = GetActiveTab();
        if (tab is null) return;
        if (tab.Panes.Count == 1) { await CloseTabAsync(tab); return; }
        TerminalTabView pane = tab.ActivePane;
        if (_settings.ConfirmCloseWithRunningProcesses && pane.RequiresCloseConfirmation && MessageBox.Show(this, "このペインでは処理が実行中です。終了しますか？", "ペインを終了", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (VisualTreeHelper.GetParent(pane) is not Grid parent) return;
        TerminalTabView sibling = parent.Children.OfType<TerminalTabView>().FirstOrDefault(candidate => !ReferenceEquals(candidate, pane))!;
        DependencyObject? grandParent = VisualTreeHelper.GetParent(parent);
        parent.Children.Clear();
        if (ReferenceEquals(tab.Content, parent)) { ActiveTabHost.Content = null; tab.Content = sibling; }
        else if (grandParent is Grid grandGrid)
        {
            int row = Grid.GetRow(parent), column = Grid.GetColumn(parent); grandGrid.Children.Remove(parent); grandGrid.Children.Add(sibling); Grid.SetRow(sibling, row); Grid.SetColumn(sibling, column);
        }
        tab.Panes.Remove(pane); tab.ActivePane = sibling; ActiveTabHost.Content = tab.Content;
        await pane.CloseAsync(); sibling.FocusTerminal();
    }

    private void MovePaneFocus(int delta)
    {
        TerminalTabItem? tab = GetActiveTab();
        if (tab is null || tab.Panes.Count == 0) return;
        int current = Math.Max(0, tab.Panes.IndexOf(tab.ActivePane));
        int next = TerminalTabCollectionState.MoveSelection(current, tab.Panes.Count, delta);
        tab.ActivePane = tab.Panes[next];
        tab.ActivePane.FocusTerminal();
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

    private void AddNewTabInSameDirectory()
    {
        string commandLine = string.IsNullOrWhiteSpace(_settings.CommandLine)
            ? TerminalProfileCatalog.BuildDefaultCommandLine()
            : _settings.CommandLine.Trim();
        string workingDirectory = GetActiveTab()?.View.WorkingDirectory ?? GetWorkingDirectoryOrDefault();
        AddNewTab(commandLine, workingDirectory);
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
        WirePane(tab, view);
        view.HeaderTitleChanged += (_, title) => UpdateTabHeader(tab, title);
        view.TaskbarProgressChanged += (_, e) =>
        {
            // 非アクティブタブの進捗はタスクバーに出さない。
            if (ReferenceEquals(GetActiveTab()?.View, view))
            {
                ApplyTaskbarProgress(e.State, e.Progress);
            }
        };
        view.BellRang += (_, _) =>
        {
            // アクティブタブは可聴ベルのみ。非アクティブタブはヘッダにベルインジケータを出す。
            if (ReferenceEquals(GetActiveTab()?.View, view))
            {
                view.ClearPendingBell();
                return;
            }

            UpdateTabHeader(tab, view.HeaderTitle);
        };
        UpdateTabHeader(tab, view.HeaderTitle);
        TabStrip.SelectedItem = tab.ListBoxItem;
    }

    private void WirePane(TerminalTabItem tab, TerminalTabView view)
    {
        view.GotKeyboardFocus += (_, _) => tab.ActivePane = view;
        view.HeaderTitleChanged += (_, title) => { if (ReferenceEquals(tab.ActivePane, view)) UpdateTabHeader(tab, title); };
        view.TaskbarProgressChanged += (_, e) =>
        {
            if (ReferenceEquals(GetActiveTab()?.View, view)) ApplyTaskbarProgress(e.State, e.Progress);
        };
        view.BellRang += (_, _) =>
        {
            if (ReferenceEquals(GetActiveTab()?.View, view)) view.ClearPendingBell();
            UpdateTabHeader(tab, tab.View.HeaderTitle);
        };
        // The view was constructed with this pane's explicit command and directory.
        // Applying global launch defaults here would make every profile start the
        // currently selected default profile instead.
        view.ApplySettings(_settings, applyLaunchSettings: false);
        ApplyAccessibilityTheme(view);
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
        var iconText = new TextBlock
        {
            Text = "❯",
            Width = 24,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xA7, 0xA7))
        };
        var titleText = new TextBlock
        {
            Text = "Terminal",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 110
        };

        var closeButton = new Button
        {
            Content = "×",
            Width = 18,
            Height = 18,
            Margin = new Thickness(3, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA7, 0xA7, 0xA7)),
            FontSize = 13,
            Cursor = Cursors.Hand
        };

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        headerPanel.Children.Add(iconText);
        headerPanel.Children.Add(titleText);
        headerPanel.Children.Add(closeButton);

        var border = new Border
        {
            Background = (Brush)Resources["TabIdleBrush"],
            BorderBrush = (Brush)Resources["ChromeBorderBrush"],
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(8, 0, 6, 0),
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

        var tab = new TerminalTabItem(view, listBoxItem, border, iconText, titleText, closeButton);
        WindowChrome.SetIsHitTestVisibleInChrome(iconText, true);
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

        if (_settings.ConfirmCloseWithRunningProcesses && tab.Panes.Any(pane => pane.RequiresCloseConfirmation) &&
            MessageBox.Show(
                this,
                "このタブでは処理が実行中です。タブを終了しますか？",
                "タブを終了",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        bool wasSelected = ReferenceEquals(TabStrip.SelectedItem, tab.ListBoxItem);
        TabStrip.Items.Remove(tab.ListBoxItem);
        _tabs.RemoveAt(tabIndex);
        foreach (TerminalTabView pane in tab.Panes.ToArray()) await pane.CloseAsync();

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }

        if (wasSelected)
        {
            int nextIndex = TerminalTabCollectionState.GetSelectionAfterClose(tabIndex, _tabs.Count, wasSelected);
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

        ActiveTabHost.Content = tab.Content;
        // アクティブになったタブの未確認ベルをクリアし、ヘッダのベルインジケータを消す。
        tab.View.ClearPendingBell();
        UpdateTabHeader(tab, tab.View.HeaderTitle);
        Title = $"{tab.View.HeaderTitle} - ConPTY Terminal";
        UpdateTabVisuals();
        // 新しいアクティブタブが保持する進捗をタスクバーへ反映する。
        ApplyTaskbarProgress(tab.View.CurrentTaskbarProgressState, tab.View.CurrentTaskbarProgress);
        _ = Dispatcher.BeginInvoke(tab.View.FocusTerminal, DispatcherPriority.Input);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (_keyBindings.Matches("SplitHorizontal", key, modifiers)) { SplitActivePane(horizontal: true); e.Handled = true; return; }
        if (_keyBindings.Matches("SplitVertical", key, modifiers)) { SplitActivePane(horizontal: false); e.Handled = true; return; }
        if (_keyBindings.Matches("ClosePane", key, modifiers)) { _ = CloseActivePaneAsync(); e.Handled = true; return; }
        if (_keyBindings.Matches("NextPane", key, modifiers)) { MovePaneFocus(1); e.Handled = true; return; }
        if (_keyBindings.Matches("PreviousPane", key, modifiers)) { MovePaneFocus(-1); e.Handled = true; return; }

        if (_keyBindings.Matches("NewTab", key, modifiers))
        {
            AddNewTabFromSettings();
            e.Handled = true;
            return;
        }

        if (_keyBindings.Matches("NewTabHere", key, modifiers))
        {
            AddNewTabInSameDirectory();
            e.Handled = true;
            return;
        }

        if (_keyBindings.Matches("OpenSettings", key, modifiers))
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

        if (_keyBindings.Matches("CloseTab", key, modifiers))
        {
            if (GetActiveTab() is TerminalTabItem activeTab)
            {
                _ = CloseTabAsync(activeTab);
                e.Handled = true;
            }

            return;
        }

        if (_keyBindings.Matches("NextTab", key, modifiers))
        {
            MoveSelection(1);
            e.Handled = true;
            return;
        }

        if (_keyBindings.Matches("PreviousTab", key, modifiers))
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
        TerminalWindowLayout layout = TerminalWindowLayout.Resolve(rawPlacement);
        string placement = layout.Placement;
        _settings.TabStripPlacement = placement;

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
        TopChromeBar.Visibility = layout.IsTop ? Visibility.Visible : Visibility.Collapsed;
        TopChromeRow.Height = layout.IsTop ? new GridLength(40) : new GridLength(0);
        if (WindowChrome.GetWindowChrome(this) is WindowChrome chrome)
        {
            chrome.CaptionHeight = layout.IsTop ? 40 : 0;
        }

        ConfigureChromePanelLayout(layout.IsHorizontal);
        ConfigureVerticalTabChrome(layout.IsHorizontal);
        ConfigurePopupPlacement(ProfilePickerPopup, layout.PopupEdge, layout.HorizontalOffset, layout.ProfilePickerVerticalOffset);
        ConfigurePopupPlacement(AppMenuPopup, layout.PopupEdge, layout.HorizontalOffset, layout.AppMenuVerticalOffset);
        UpdateTabVisuals();
    }

    private void TabStrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(TabStrip);
        ListBoxItem? item = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _draggedTab = item is null ? null : _tabs.FirstOrDefault(tab => ReferenceEquals(tab.ListBoxItem, item));
    }

    private void TabStrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = null; _draggedTab = null;
    }

    private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !_tabDragStart.HasValue || _draggedTab is null) return;
        Point current = e.GetPosition(TabStrip);
        if (Math.Abs(current.X - _tabDragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _tabDragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        ListBoxItem? targetItem = FindVisualAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        TerminalTabItem? target = targetItem is null ? null : _tabs.FirstOrDefault(tab => ReferenceEquals(tab.ListBoxItem, targetItem));
        if (target is null || ReferenceEquals(target, _draggedTab)) return;
        int from = _tabs.IndexOf(_draggedTab), to = _tabs.IndexOf(target);
        ListBoxItem selected = _draggedTab.ListBoxItem;
        if (!TerminalTabCollectionState.MoveItem(_tabs, from, to)) return;
        TabStrip.Items.Remove(selected); TabStrip.Items.Insert(to, selected); TabStrip.SelectedItem = selected;
        _tabDragStart = current;
    }

    private void ConfigureVerticalTabChrome(bool isHorizontal)
    {
        bool isCollapsed = !isHorizontal && _settings.VerticalTabsCollapsed;
        double width = isCollapsed
            ? 52
            : TerminalAppSettings.ClampVerticalTabWidth(_settings.VerticalTabWidth);
        LeftTabChrome.Width = width;
        RightTabChrome.Width = width;
        ToggleVerticalTabsButton.Visibility = isHorizontal ? Visibility.Collapsed : Visibility.Visible;
        ToggleVerticalTabsButton.Content = isCollapsed ? "Expand vertical tabs" : "Collapse vertical tabs";
    }

    private void ToggleVerticalTabsButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.VerticalTabsCollapsed = !_settings.VerticalTabsCollapsed;
        ConfigureVerticalTabChrome(TerminalTabStripPlacementCatalog.IsHorizontal(_settings.TabStripPlacement));
        UpdateTabVisuals();
        _settings.Save();
        AppMenuPopup.IsOpen = false;
    }

    private void LeftTabResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeVerticalTabs(e.HorizontalChange);

    private void RightTabResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        => ResizeVerticalTabs(-e.HorizontalChange);

    private void ResizeVerticalTabs(double delta)
    {
        if (_settings.VerticalTabsCollapsed)
        {
            return;
        }

        _settings.VerticalTabWidth = TerminalAppSettings.ClampVerticalTabWidth(
            _settings.VerticalTabWidth + delta);
        ConfigureVerticalTabChrome(isHorizontal: false);
    }

    private void VerticalTabResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _settings.Save();
    }

    private void ConfigureChromePanelLayout(bool isHorizontal)
    {
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

    private static void ConfigurePopupPlacement(
        Popup popup,
        TerminalPopupEdge edge,
        double horizontalOffset,
        double verticalOffset)
    {
        popup.Placement = edge switch
        {
            TerminalPopupEdge.Top => PlacementMode.Top,
            TerminalPopupEdge.Left => PlacementMode.Left,
            TerminalPopupEdge.Right => PlacementMode.Right,
            _ => PlacementMode.Bottom
        };
        popup.HorizontalOffset = horizontalOffset;
        popup.VerticalOffset = verticalOffset;
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
        int nextIndex = TerminalTabCollectionState.MoveSelection(currentIndex, _tabs.Count, delta);
        TabStrip.SelectedItem = _tabs[nextIndex].ListBoxItem;
    }

    private TerminalTabItem? GetActiveTab()
    {
        return _tabs.FirstOrDefault(tab => ReferenceEquals(tab.ListBoxItem, TabStrip.SelectedItem));
    }

    // アクティブタブの OSC 9;4 進捗をウィンドウのタスクバーへ反映する。
    private void ApplyTaskbarProgress(TaskbarProgressState state, int progress)
    {
        TaskbarItemInfo ??= new TaskbarItemInfo();
        switch (state)
        {
            case TaskbarProgressState.Normal:
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
                TaskbarItemInfo.ProgressValue = progress / 100.0;
                break;
            case TaskbarProgressState.Error:
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Error;
                TaskbarItemInfo.ProgressValue = progress / 100.0;
                break;
            case TaskbarProgressState.Warning:
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Paused;
                TaskbarItemInfo.ProgressValue = progress / 100.0;
                break;
            case TaskbarProgressState.Indeterminate:
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Indeterminate;
                break;
            default:
                TaskbarItemInfo.ProgressState = TaskbarItemProgressState.None;
                break;
        }
    }

    private void UpdateTabHeader(TerminalTabItem tab, string title)
    {
        // 未確認ベルがある非アクティブタブはタイトル先頭にベルインジケータを付ける。
        tab.TitleText.Text = tab.View.HasPendingBell ? $"🔔 {title}" : title;
        if (ReferenceEquals(TabStrip.SelectedItem, tab.ListBoxItem))
        {
            Title = $"{title} - ConPTY Terminal";
        }
    }

    private void UpdateTabVisuals()
    {
        bool isHorizontal = TerminalTabStripPlacementCatalog.IsHorizontal(_settings.TabStripPlacement);
        bool showIconOnly = !isHorizontal && _settings.VerticalTabsCollapsed;

        foreach (TerminalTabItem tab in _tabs)
        {
            bool isSelected = ReferenceEquals(TabStrip.SelectedItem, tab.ListBoxItem);
            tab.HeaderBorder.Background = _highContrastActive
                ? (isSelected ? SystemColors.HighlightBrush : SystemColors.WindowBrush)
                : new SolidColorBrush(isSelected
                ? Color.FromRgb(0x0E, 0x0E, 0x0E)
                : Color.FromRgb(0x1B, 0x1B, 0x1B));
            tab.HeaderBorder.BorderThickness = isHorizontal
                ? new Thickness(0, 0, 1, 0)
                : new Thickness(0, 0, 0, 1);
            tab.HeaderBorder.BorderBrush = _highContrastActive
                ? SystemColors.WindowTextBrush : (Brush)Resources["ChromeBorderBrush"];
            tab.TitleText.Foreground = _highContrastActive
                ? (isSelected ? SystemColors.HighlightTextBrush : SystemColors.WindowTextBrush)
                : new SolidColorBrush(isSelected
                ? Color.FromRgb(0xED, 0xED, 0xED)
                : Color.FromRgb(0xA7, 0xA7, 0xA7));
            tab.IconText.Foreground = tab.TitleText.Foreground;
            tab.IconText.Visibility = isHorizontal ? Visibility.Collapsed : Visibility.Visible;
            tab.TitleText.Visibility = showIconOnly ? Visibility.Collapsed : Visibility.Visible;
            tab.CloseButton.Visibility = showIconOnly ? Visibility.Collapsed : Visibility.Visible;
            tab.HeaderBorder.Padding = showIconOnly
                ? new Thickness(8, 0, 8, 0)
                : new Thickness(8, 0, 6, 0);
            tab.ListBoxItem.ToolTip = showIconOnly ? tab.View.HeaderTitle : null;
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
        HighContrastAppearance.Apply(_settingsWindow, _highContrastActive);
        _settingsWindow.Activate();
    }

    private void ApplyUpdatedSettings(TerminalAppSettings settings)
    {
        _settings = settings;
        _keyBindings.Update(_settings.KeyBindings);
        ApplyTabStripPlacement(_settings.TabStripPlacement);
        ApplyBackdrop(_settings);
        SaveWindowSettings();
        _settings.Save();

        foreach (TerminalTabItem tab in _tabs)
        {
            foreach (TerminalTabView pane in tab.Panes)
            {
                // Existing panes keep their own launch command and directory when
                // appearance/runtime settings are changed.
                pane.ApplySettings(_settings, applyLaunchSettings: false);
                ApplyAccessibilityTheme(pane);
            }
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
            WindowHeight = _settings.WindowHeight,
            EnableSessionLogging = _settings.EnableSessionLogging,
            EnableShellIntegrationInjection = _settings.EnableShellIntegrationInjection,
            ShowStatusBar = _settings.ShowStatusBar,
            SessionLogDirectory = _settings.SessionLogDirectory,
            CjkAmbiguousWidthIsWide = tabSettings.CjkAmbiguousWidthIsWide,
            BackdropType = _settings.BackdropType,
            EnableFontLigatures = tabSettings.EnableFontLigatures,
            VerticalTabWidth = _settings.VerticalTabWidth,
            VerticalTabsCollapsed = _settings.VerticalTabsCollapsed,
            ScrollbackLimit = tabSettings.ScrollbackLimit,
            KeyBindings = TerminalKeyBindingCatalog.Normalize(_settings.KeyBindings),
            ColorScheme = _settings.ColorScheme,
            CustomForeground = _settings.CustomForeground,
            CustomBackground = _settings.CustomBackground,
            CustomCursorColor = _settings.CustomCursorColor,
            CustomSelectionColor = _settings.CustomSelectionColor,
            CustomAnsiPalette = _settings.CustomAnsiPalette?.ToArray(),
            SavedTabs = _settings.SavedTabs.ToList(),
            ActiveTabIndex = _settings.ActiveTabIndex,
            ConfirmCloseWithRunningProcesses = _settings.ConfirmCloseWithRunningProcesses
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
        _settings.SavedTabs = _tabs.Select(tab => new TerminalSavedTab(tab.View.CommandLine, tab.View.WorkingDirectory)).ToList();
        _settings.ActiveTabIndex = Math.Max(0, TabStrip.SelectedIndex);
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

    private sealed class TerminalTabItem
    {
        internal TerminalTabItem(TerminalTabView view, ListBoxItem listBoxItem, Border headerBorder, TextBlock iconText, TextBlock titleText, Button closeButton)
        { ActivePane = view; Content = view; Panes.Add(view); ListBoxItem = listBoxItem; HeaderBorder = headerBorder; IconText = iconText; TitleText = titleText; CloseButton = closeButton; }
        internal TerminalTabView View => ActivePane;
        internal TerminalTabView ActivePane { get; set; }
        internal UIElement Content { get; set; }
        internal List<TerminalTabView> Panes { get; } = [];
        internal ListBoxItem ListBoxItem { get; }
        internal Border HeaderBorder { get; }
        internal TextBlock IconText { get; }
        internal TextBlock TitleText { get; }
        internal Button CloseButton { get; }
    }
}
