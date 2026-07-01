using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using SwellSSH.Models;
using SwellSSH.Services;
using SwellSSH.Terminal;

namespace SwellSSH.Pages
{
    /// <summary>Simple view-model row for the connection list.</summary>
    public abstract class SidebarItemViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ConnectionGroupViewModel : SidebarItemViewModel
    {
        public string Name { get; }
        public ObservableCollection<ConnectionItemViewModel> Children { get; } = new();

        private bool _isExpanded = true;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ChevronGlyph));
                }
            }
        }
        public string ChevronGlyph => IsExpanded ? "\uE70D" : "\uE76C"; // Down / Right arrow

        public ConnectionGroupViewModel(string name) => Name = name;
    }

    public class ConnectionItemViewModel : SidebarItemViewModel
    {
        public ConnectionProfile Profile { get; }
        public string Group => Profile.Group;
        public string Name => Profile.Name;

        public bool IsFavorite
        {
            get => Profile.IsFavorite;
            set
            {
                if (Profile.IsFavorite != value)
                {
                    Profile.IsFavorite = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FavoriteMenuText));
                    OnPropertyChanged(nameof(FavoriteMenuIcon));
                    OnPropertyChanged(nameof(FavoriteVisibility));
                }
            }
        }

        public string FavoriteMenuText => IsFavorite ? "取消收藏" : "加入收藏";
        public string FavoriteMenuIcon => IsFavorite ? "\uE735" : "\uE734"; // Solid star / Outline star
        public Microsoft.UI.Xaml.Visibility FavoriteVisibility => IsFavorite ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        private bool _isIpVisible;
        public bool IsIpVisible
        {
            get => _isIpVisible;
            set
            {
                _isIpVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayHostPort));
                OnPropertyChanged(nameof(EyeGlyph));
            }
        }

        public string DisplayHostPort =>
            IsIpVisible
                ? $"{Profile.Username}@{Profile.Host}:{Profile.Port}"
                : $"{Profile.Username}@***.***.***.***:{Profile.Port}";

        public string EyeGlyph => IsIpVisible ? "\uE7B3" : "\uED1A";

        private string _statsText = "";
        private bool _monitoringVisible;

        public string StatsText
        {
            get => _statsText;
            set { _statsText = value; OnPropertyChanged(); }
        }

        public Visibility IsMonitoringVisible =>
            _monitoringVisible ? Visibility.Visible : Visibility.Collapsed;

        public void ApplyStats(ServerStats s)
        {
            if (s.HasError)
            {
                StatsText = $"🔴 {s.ErrorMessage?.Split('\n')[0] ?? "监控失败"}".Substring(0, Math.Min(28, (s.ErrorMessage?.Length ?? 0) + 3));
                _monitoringVisible = true;
            }
            else if (s.IsAvailable)
            {
                StatsText = $"CPU {s.CpuPercent,4:F0}%  RAM {s.RamPercent,4:F0}%  Disk {s.DiskPercent,3:F0}%";
                _monitoringVisible = true;
            }
            else
            {
                StatsText = s.RamPercent >= 0
                    ? $"RAM {s.RamPercent,4:F0}%  Disk {s.DiskPercent,3:F0}%  …"
                    : "正在获取监控数据…";
                _monitoringVisible = true;
            }
            OnPropertyChanged(nameof(StatsText));
            OnPropertyChanged(nameof(IsMonitoringVisible));
        }

        public ConnectionItemViewModel(ConnectionProfile profile) => Profile = profile;
    }

    public sealed partial class MainPage : Page
    {
        private readonly ConnectionStorage _storage = new();
        private readonly KnownHostsService _knownHosts = new();
        public ObservableCollection<SidebarItemViewModel> FlatSidebarItems { get; } = new();
        public ObservableCollection<ThemeViewModel> Themes { get; } = new();
        public ObservableCollection<SnippetViewModel> Snippets { get; } = new();
        private ThemeViewModel? _selectedTheme;
        public ThemeViewModel? SelectedTheme
        {
            get => _selectedTheme;
            set
            {
                if (_selectedTheme != value)
                {
                    _selectedTheme = value;
                    if (value != null)
                        _ = ApplyThemeAsync(value.Name);
                }
            }
        }
        private readonly List<ConnectionGroupViewModel> _groups = new();
        // Map profileId → ViewModel for fast stats lookup
        private readonly Dictionary<string, ConnectionItemViewModel> _vmById = new();

        public MainPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            _ = LoadConnectionsAsync();

            if (MainWindow.Instance != null)
                MainWindow.Instance.ThemeChanged += OnThemeChanged;
            TerminalSettings.GlobalSettingsChanged += OnGlobalSettingsChanged;

            // Subscribe to monitoring stats updates
            ServerMonitorService.Instance.StatsUpdated += OnStatsUpdated;

            LoadThemes();
            _ = LoadSidebarAppearanceAsync();
            _ = LoadSnippetsAsync();
            
            // Set initial Sidebar Tab
            SidebarTabButton_Click(TabSnippetsButton, new RoutedEventArgs());

            // Hook tab selection changed to update sidebar toggle button state
            TerminalTabView.SelectionChanged += TerminalTabView_SelectionChanged;
            AddHandler(PointerReleasedEvent, new PointerEventHandler(MainPage_PointerReleased), true);

            this.Unloaded += (_, _) =>
            {
                if (MainWindow.Instance != null)
                    MainWindow.Instance.ThemeChanged -= OnThemeChanged;
                TerminalSettings.GlobalSettingsChanged -= OnGlobalSettingsChanged;
                ServerMonitorService.Instance.StatsUpdated -= OnStatsUpdated;
            };

            AiPane.CloseRequested += () => AiPaneContainer.Visibility = Visibility.Collapsed;
            AiPane.SettingsRequested += () => { AiPaneContainer.Visibility = Visibility.Collapsed; OpenSettingsTab(); };

            SetupKeyboardShortcuts();
        }

        private void OnGlobalSettingsChanged(TerminalSettings settings)
        {
            _cachedSettings = settings;
            foreach (TabViewItem tab in TerminalTabView.TabItems)
            {
                if (tab.Content is Grid grid && grid.Children.FirstOrDefault(c => c is TerminalView) is TerminalView terminalView)
                {
                    terminalView.ApplySettings(settings);
                    grid.Background = GetTerminalBackgroundBrush(settings);
                }
            }
            SyncThemeMenuCheckedState(settings.ColorScheme);
        }

        private static SolidColorBrush GetTerminalBackgroundBrush(TerminalSettings settings)
        {
            var theme = TerminalThemeService.Instance.Find(settings.ColorScheme);
            var color = theme == null
                ? Windows.UI.Color.FromArgb(255, 12, 12, 12)
                : TerminalThemeService.ParseColor(theme.Background, Windows.UI.Color.FromArgb(255, 12, 12, 12));
            return new SolidColorBrush(color);
        }

        private void SetupKeyboardShortcuts()
        {
            // Hide the default tooltip for keyboard accelerators so it doesn't show up everywhere
            this.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;

            // Ctrl+T: New tab (invokes the add tab button logic)
            var ctrlT = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
            { 
                Modifiers = Windows.System.VirtualKeyModifiers.Control, 
                Key = Windows.System.VirtualKey.T 
            };
            ctrlT.Invoked += (s, e) => 
            { 
                e.Handled = true; 
                TerminalTabView_AddTabButtonClick(TerminalTabView, null!);
            };
            this.KeyboardAccelerators.Add(ctrlT);

            // Ctrl+W: Close current tab
            var ctrlW = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
            { 
                Modifiers = Windows.System.VirtualKeyModifiers.Control, 
                Key = Windows.System.VirtualKey.W 
            };
            ctrlW.Invoked += (s, e) => 
            { 
                e.Handled = true; 
                if (TerminalTabView.SelectedItem is TabViewItem tab) 
                    CloseTab(tab); 
            };
            this.KeyboardAccelerators.Add(ctrlW);

            // Ctrl+Tab: Next tab
            var ctrlTab = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
            { 
                Modifiers = Windows.System.VirtualKeyModifiers.Control, 
                Key = Windows.System.VirtualKey.Tab 
            };
            ctrlTab.Invoked += (s, e) => 
            {
                e.Handled = true;
                if (TerminalTabView.TabItems.Count > 1) 
                    TerminalTabView.SelectedIndex = (TerminalTabView.SelectedIndex + 1) % TerminalTabView.TabItems.Count;
            };
            this.KeyboardAccelerators.Add(ctrlTab);

            // Ctrl+Shift+Tab: Previous tab
            var ctrlShiftTab = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
            { 
                Modifiers = Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift, 
                Key = Windows.System.VirtualKey.Tab 
            };
            ctrlShiftTab.Invoked += (s, e) => 
            {
                e.Handled = true;
                if (TerminalTabView.TabItems.Count > 1) 
                    TerminalTabView.SelectedIndex = (TerminalTabView.SelectedIndex - 1 + TerminalTabView.TabItems.Count) % TerminalTabView.TabItems.Count;
            };
            this.KeyboardAccelerators.Add(ctrlShiftTab);

            // Ctrl+1~9: Jump to specific tab
            for (int i = 1; i <= 9; i++)
            {
                var key = (Windows.System.VirtualKey)(Windows.System.VirtualKey.Number0 + i);
                var numAcc = new Microsoft.UI.Xaml.Input.KeyboardAccelerator 
                { 
                    Modifiers = Windows.System.VirtualKeyModifiers.Control, 
                    Key = key 
                };
                int targetIndex = i - 1;
                numAcc.Invoked += (s, e) => 
                {
                    e.Handled = true;
                    if (targetIndex < TerminalTabView.TabItems.Count)
                        TerminalTabView.SelectedIndex = targetIndex;
                };
                this.KeyboardAccelerators.Add(numAcc);
            }
        }

        private void OnThemeChanged(ElementTheme newTheme)
        {
            // Do NOT override terminal color scheme when switching dark/light mode.
            // The terminal theme is controlled exclusively by the right sidebar theme picker.
            // Only sync the sidebar theme list check state without changing the terminal colors.
        }

        private async Task LoadConnectionsAsync()
        {
            var profiles = await _storage.LoadConnectionsAsync();
            _groups.Clear();
            _vmById.Clear();

            // 1. Group profiles
            var grouped = profiles.GroupBy(p => string.IsNullOrEmpty(p.Group) ? "默认分组" : p.Group);
            foreach (var g in grouped)
            {
                var groupVm = new ConnectionGroupViewModel(g.Key);
                // Sort favorites to the top, then by name
                var sortedGroup = g.OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name);
                foreach (var p in sortedGroup)
                {
                    var itemVm = new ConnectionItemViewModel(p);
                    groupVm.Children.Add(itemVm);
                    _vmById[p.Id] = itemVm;
                }
                _groups.Add(groupVm);
            }

            // 2. Build flat list
            RefreshFlatSidebarList();

            // Sync settings theme menu
            var settings = await _storage.LoadSettingsAsync();
            SyncThemeMenuCheckedState(settings.ColorScheme);

            // Start/stop background monitoring per profile
            ServerMonitorService.Instance.Sync(profiles);
        }

        /// <summary>
        /// Public entry point used by backup restore to reload the full connection list from disk.
        /// </summary>
        public Task ReloadConnectionsAsync() => LoadConnectionsAsync();

        private void RefreshFlatSidebarList()
        {
            FlatSidebarItems.Clear();
            foreach (var g in _groups)
            {
                FlatSidebarItems.Add(g);
                if (g.IsExpanded)
                {
                    foreach (var child in g.Children)
                        FlatSidebarItems.Add(child);
                }
            }
            UpdateEmptyState();
        }

        private void GroupHeader_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionGroupViewModel g)
            {
                e.Handled = true;
                g.IsExpanded = !g.IsExpanded;

                // Smart update flat list (insert/remove instead of full rebuild for animation-friendly UI)
                int groupIndex = FlatSidebarItems.IndexOf(g);
                if (groupIndex < 0) return;

                if (g.IsExpanded)
                {
                    int insertAt = groupIndex + 1;
                    foreach (var child in g.Children)
                        FlatSidebarItems.Insert(insertAt++, child);
                }
                else
                {
                    for (int i = 0; i < g.Children.Count; i++)
                    {
                        if (groupIndex + 1 < FlatSidebarItems.Count && FlatSidebarItems[groupIndex + 1] is ConnectionItemViewModel)
                            FlatSidebarItems.RemoveAt(groupIndex + 1);
                    }
                }
            }
        }

        /// <summary>Called from ServerMonitorService on thread-pool; marshal to UI thread.</summary>
        private void OnStatsUpdated(ServerStats stats)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_vmById.TryGetValue(stats.ConnectionId, out var vm))
                    vm.ApplyStats(stats);
            });
        }

        /// <summary>使侧边栏的下拉框与当前配色方案对齐</summary>
        private void SyncThemeMenuCheckedState(string colorScheme)
        {
            var matchedTheme = Themes.FirstOrDefault(t => t.Name == colorScheme);
            if (matchedTheme != null)
            {
                SelectedTheme = matchedTheme;
            }
        }

        private void UpdateEmptyState()
        {
            var isEmpty = TerminalTabView.TabItems.Count == 0;
            if (EmptyStatePanel != null)
                EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            
            if (TerminalTabView != null)
                TerminalTabView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;

            UpdateTitleBarInteractiveRegions();
        }

        // ── Connection list actions ───────────────────────────────────────────

        // ── Public API for MainWindow pane delegation ────────────────────────

        public SidebarTemplateSelector GetSidebarTemplateSelector()
        {
            return Resources["SidebarTemplateSelector"] as SidebarTemplateSelector
                ?? new SidebarTemplateSelector();
        }

        public void OpenSettingsPane()
        {
            if (SettingsPaneFrame.Content == null)
            {
                SettingsPaneFrame.Navigate(typeof(SettingsPage));
            }
            SettingsPaneHost.Visibility = Visibility.Visible;
            TerminalSplitView.Visibility = Visibility.Collapsed;
        }

        public void OpenSettingsTab()
        {
            foreach (TabViewItem existingTab in TerminalTabView.TabItems)
            {
                if (existingTab.Tag is string tag && tag == "settings")
                {
                    TerminalTabView.SelectedItem = existingTab;
                    UpdateEmptyState();
                    return;
                }
            }

            var frame = new Frame();
            frame.Navigate(typeof(SettingsPage));

            var tab = new TabViewItem
            {
                Header = "应用设置",
                IconSource = new FontIconSource { Glyph = "\uE713" },
                Tag = "settings",
                Content = frame
            };

            TerminalTabView.TabItems.Add(tab);
            TerminalTabView.SelectedItem = tab;
            UpdateEmptyState();
            RestoreTerminalFocus();
        }

        public void ConnectToProfile(ConnectionProfile profile)
        {
            OpenTerminalTab(profile);
        }

        public void DoQuickConnectFromPane(TextBox textBox)
        {
            _ = DoQuickConnectAsync(textBox);
        }

        public async Task AddConnectionFromPane()
        {
            var profile = new ConnectionProfile();
            bool saved = await ShowConnectionDialogAsync(profile, isNew: true);
            if (!saved) return;

            var profiles = await _storage.LoadConnectionsAsync();
            profiles.Add(profile);
            await _storage.SaveConnectionsAsync(profiles);
            await LoadConnectionsAsync();
        }

        public async Task EditProfileFromPane(ConnectionItemViewModel vm)
        {
            await EditProfileAsync(vm);
        }

        public async Task DeleteProfileFromPane(ConnectionItemViewModel vm)
        {
            await DeleteProfileAsync(vm);
        }

        private void EmptyQuickConnectBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                _ = DoQuickConnectAsync(EmptyQuickConnectBox);
            }
        }

        private void EmptyQuickConnectButton_Click(object sender, RoutedEventArgs e)
        {
            _ = DoQuickConnectAsync(EmptyQuickConnectBox);
        }

        private async void EmptyAddConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            await AddConnectionFromPane();
            MainWindow.Instance?.OpenConnectionsPane();
        }

        private void EmptyOpenConnectionsButton_Click(object sender, RoutedEventArgs e)
        {
            MainWindow.Instance?.OpenConnectionsPane();
        }

        private async void EmptyNewSftpButton_Click(object sender, RoutedEventArgs e)
        {
            await OpenSftpTabAsync();
        }

        public async Task OpenSftpTabAsync()
        {
            var sftpPage = new SftpPage();
            var tab = new TabViewItem
            {
                Header = "SFTP",
                IconSource = new FontIconSource { Glyph = "\uE8B7" },
                Content = sftpPage,
                Tag = "sftp"
            };

            var flyout = new MenuFlyout();
            var closeItem = new MenuFlyoutItem { Text = "关闭标签" };
            closeItem.Click += (_, _) => CloseTab(tab);
            flyout.Items.Add(closeItem);
            tab.ContextFlyout = flyout;

            TerminalTabView.TabItems.Add(tab);
            TerminalTabView.SelectedItem = tab;
            UpdateEmptyState();

            // Wait for the SftpPage to be loaded into the visual tree so that
            // its XamlRoot is available for ContentDialog.ShowAsync().
            var loadedTcs = new TaskCompletionSource();
            sftpPage.Loaded += (_, _) => loadedTcs.TrySetResult();
            if (sftpPage.IsLoaded) loadedTcs.TrySetResult();
            await loadedTcs.Task;

            await sftpPage.OpenRemoteSessionFromPickerAsync();
        }

        private async void ToggleFavoriteMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
            {
                vm.IsFavorite = !vm.IsFavorite;
                var profiles = await _storage.LoadConnectionsAsync();
                var p = profiles.FirstOrDefault(x => x.Id == vm.Profile.Id);
                if (p != null)
                {
                    p.IsFavorite = vm.IsFavorite;
                    await _storage.SaveConnectionsAsync(profiles);
                    await LoadConnectionsAsync(); // Re-sort and re-render
                }
            }
        }

        public void CloseSettingsPane_Click(object sender, RoutedEventArgs e)
        {
            SettingsPaneHost.Visibility = Visibility.Collapsed;
            TerminalSplitView.Visibility = Visibility.Visible;
        }

        // ── Context Menu handlers ─────────────────────────────────────────────

        private void ConnectMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
                OpenTerminalTab(vm.Profile);
        }

        private void ToggleIpVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
                vm.IsIpVisible = !vm.IsIpVisible;
        }

        private void ShareMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText($"ssh {vm.Profile.Username}@{vm.Profile.Host} -p {vm.Profile.Port}");
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
        }

        // ── Quick Connect ────────────────────────────────────────────────────────────────────

        private async Task DoQuickConnectAsync(TextBox textBox)
        {
            var input = textBox.Text.Trim();
            if (string.IsNullOrEmpty(input)) return;

            if (!TryParseQuickConnect(input, out var profile))
            {
                textBox.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
                _ = Task.Delay(1500).ContinueWith(_ =>
                    DispatcherQueue.TryEnqueue(() => textBox.ClearValue(TextBox.BorderBrushProperty)));
                return;
            }

            // ── Password prompt ───────────────────────────────────────────────
            // Quick-connect always uses Password auth; show a dialog to collect it.
            var pwdBox = new PasswordBox
            {
                PlaceholderText = "输入密码",
                Margin = new Thickness(0, 8, 0, 0)
            };
            var promptContent = new StackPanel { Spacing = 4 };
            promptContent.Children.Add(new TextBlock
            {
                Text = $"{profile.Username}@{profile.Host}:{profile.Port}",
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                Opacity = 0.8
            });
            promptContent.Children.Add(pwdBox);

            var pwdDialog = new ContentDialog
            {
                XamlRoot         = this.XamlRoot,
                RequestedTheme   = this.ActualTheme,
                Title            = "🔑 输入密码",
                Content          = promptContent,
                PrimaryButtonText  = "连接",
                CloseButtonText    = "取消",
                DefaultButton    = ContentDialogButton.Primary
            };

            // Auto-focus the password box when dialog opens
            pwdDialog.Opened += (_, _) => pwdBox.Focus(FocusState.Programmatic);

            // Allow pressing Enter inside the PasswordBox to confirm
            pwdBox.KeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    pwdDialog.Hide();
                    // Simulate primary button — we'll check Text below
                }
            };

            var result = await pwdDialog.ShowAsync();

            // Accept either the Primary button or Enter-key dismiss (password non-empty)
            string pwd = pwdBox.Password;
            if (result != ContentDialogResult.Primary && string.IsNullOrEmpty(pwd)) return;

            if (!string.IsNullOrEmpty(pwd))
                profile.EncryptedPassword = ConnectionStorage.EncryptSecret(pwd);

            textBox.Text = "";
            OpenTerminalTab(profile);
        }

        /// <summary>解析 [user@]host[:port] 格式到临时 ConnectionProfile。</summary>
        private static bool TryParseQuickConnect(string input, out ConnectionProfile profile)
        {
            profile = new ConnectionProfile { Name = input };

            string user = "root";
            string host = input;
            int port = 22;

            // user@...
            if (host.Contains('@'))
            {
                int at = host.LastIndexOf('@');
                user = host[..at];
                host = host[(at + 1)..];
            }

            // [...]:port  (IPv6)
            if (host.StartsWith('['))
            {
                int close = host.IndexOf(']');
                if (close > 0)
                {
                    string ipv6 = host[1..close];
                    string rest = host[(close + 1)..];
                    if (rest.StartsWith(':') && int.TryParse(rest[1..], out int p6))
                        port = p6;
                    host = ipv6;
                }
            }
            else if (host.Count(c => c == ':') == 1)
            {
                var parts = host.Split(':');
                if (int.TryParse(parts[1], out int p)) { port = p; host = parts[0]; }
            }

            if (!IsValidHost(host)) return false;

            profile.Username = string.IsNullOrEmpty(user) ? "root" : user;
            profile.Host     = host;
            profile.Port     = port;
            profile.Name     = $"{user}@{host}:{port}";
            return true;
        }

        private async void EditMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
                await EditProfileAsync(vm);
        }

        private async void DeleteMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ConnectionItemViewModel vm)
                await DeleteProfileAsync(vm);
        }

        // ── Helper methods for Edit/Delete ──────────────────────────────────

        private async Task EditProfileAsync(ConnectionItemViewModel vm)
        {
            bool saved = await ShowConnectionDialogAsync(vm.Profile, isNew: false);
            if (!saved) return;

            var profiles = await _storage.LoadConnectionsAsync();
            var idx = profiles.FindIndex(p => p.Id == vm.Profile.Id);
            if (idx >= 0) profiles[idx] = vm.Profile;
            await _storage.SaveConnectionsAsync(profiles);
            await LoadConnectionsAsync();
        }

        private async Task DeleteProfileAsync(ConnectionItemViewModel vm)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                RequestedTheme = this.ActualTheme,
                Title = "删除连接",
                Content = $"确定要删除「{vm.Name}」吗？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var profiles = await _storage.LoadConnectionsAsync();
            profiles.RemoveAll(p => p.Id == vm.Profile.Id);
            await _storage.SaveConnectionsAsync(profiles);
            await LoadConnectionsAsync();
        }

        private void SidebarThemesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ThemeViewModel theme)
            {
                SelectedTheme = theme;
            }
        }

        private async Task ApplyThemeAsync(string colorScheme)
        {
            if (_cachedSettings == null)
                _cachedSettings = await _storage.LoadSettingsAsync();

            _cachedSettings.ColorScheme = colorScheme;
            await _storage.SaveSettingsAsync(_cachedSettings);
            TerminalSettings.NotifyGlobalSettingsChanged(_cachedSettings);
        }

        private void LoadThemes()
        {
            Themes.Clear();
            foreach (var theme in TerminalThemeService.Instance.Themes)
            {
                var ansi = theme.AnsiColors;
                Themes.Add(new ThemeViewModel
                {
                    Name = theme.Name,
                    BgBrush = new SolidColorBrush(TerminalThemeService.ParseColor(theme.Background, Microsoft.UI.Colors.Black)),
                    FgBrush = new SolidColorBrush(TerminalThemeService.ParseColor(theme.Foreground, Microsoft.UI.Colors.White)),
                    Accent1Brush = new SolidColorBrush(TerminalThemeService.ParseColor(ansi[1], Microsoft.UI.Colors.Red)),
                    Accent2Brush = new SolidColorBrush(TerminalThemeService.ParseColor(ansi[2], Microsoft.UI.Colors.Green)),
                    Accent3Brush = new SolidColorBrush(TerminalThemeService.ParseColor(ansi[4], Microsoft.UI.Colors.Blue))
                });
            }
            if (Themes.Count > 0) return;

            // 浅色主题 (Light Themes)
            Themes.Add(new ThemeViewModel { Name = "Atom One Light", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xfa, 0xfa, 0xfa)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x38, 0x3a, 0x42)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xe4, 0x56, 0x49)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x50, 0xa1, 0x4f)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x40, 0x78, 0xf2)) });
            Themes.Add(new ThemeViewModel { Name = "Ayu Light", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xfa, 0xfa, 0xfa)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x5c, 0x67, 0x73)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x33, 0x33)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x86, 0xb3, 0x00)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x36, 0xa3, 0xd9)) });
            Themes.Add(new ThemeViewModel { Name = "Default Light", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 250, 250, 250)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 50, 50, 50)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 205, 40, 40)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 155, 0)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 155, 165)) });
            Themes.Add(new ThemeViewModel { Name = "Gruvbox Light", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xfb, 0xf1, 0xc7)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x3c, 0x38, 0x36)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xcc, 0x24, 0x1d)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x98, 0x97, 0x1a)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x45, 0x85, 0x88)) });
            Themes.Add(new ThemeViewModel { Name = "Material Light", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0xff, 0xff)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x26, 0x32, 0x38)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xe5, 0x39, 0x35)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x43, 0xa0, 0x47)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1e, 0x88, 0xe5)) });
            Themes.Add(new ThemeViewModel { Name = "Rose Pine Dawn", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xfa, 0xf4, 0xed)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x57, 0x52, 0x79)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xb4, 0x63, 0x7a)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x28, 0x69, 0x83)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x56, 0x94, 0x9f)) });
            Themes.Add(new ThemeViewModel { Name = "Solarized Light", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xfd, 0xf6, 0xe3)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x65, 0x7b, 0x83)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xdc, 0x32, 0x2f)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x85, 0x99, 0x00)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x26, 0x8b, 0xd2)) });
            Themes.Add(new ThemeViewModel { Name = "Termark Light", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0xff, 0xff)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1f, 0x23, 0x28)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xcf, 0x22, 0x2e)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x11, 0x63, 0x29)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x09, 0x69, 0xda)) });
            Themes.Add(new ThemeViewModel { Name = "Tokyo Day", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0xff, 0xff)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x37, 0x60, 0xbf)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf5, 0x2a, 0x65)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x58, 0x75, 0x39)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x18, 0x80, 0x92)) });

            // 深色主题 (Dark Themes)
            Themes.Add(new ThemeViewModel { Name = "Atom One Dark", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x28, 0x2c, 0x34)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xab, 0xb2, 0xbf)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xe0, 0x6c, 0x75)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x98, 0xc3, 0x79)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x61, 0xaf, 0xef)) });
            Themes.Add(new ThemeViewModel { Name = "Ayu Dark", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0f, 0x14, 0x19)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xe6, 0xe1, 0xcf)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x33, 0x33)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x86, 0xb3, 0x00)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x36, 0xa3, 0xd9)) });
            Themes.Add(new ThemeViewModel { Name = "Catppuccin Latte", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xef, 0xf1, 0xf5)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x4c, 0x4f, 0x69)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xd2, 0x0f, 0x39)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x40, 0xa0, 0x2b)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1e, 0x66, 0xf5)) });
            Themes.Add(new ThemeViewModel { Name = "Catppuccin Mocha", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1e, 0x1e, 0x2e)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xcd, 0xd6, 0xf4)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf3, 0x8b, 0xa8)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xa6, 0xe3, 0xa1)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x89, 0xb4, 0xfa)) });
            Themes.Add(new ThemeViewModel { Name = "Cobalt2", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x13, 0x27, 0x38)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0xff, 0xff)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x5d, 0x38)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x3a, 0xd9, 0x00)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x00, 0x88, 0xff)) });
            Themes.Add(new ThemeViewModel { Name = "Cyberpunk", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0d, 0x02, 0x21)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x00, 0x6e)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x00, 0x6e)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x83, 0x38, 0xec)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x3a, 0x86, 0xff)) });
            Themes.Add(new ThemeViewModel { Name = "Dracula", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x28, 0x2a, 0x36)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf8, 0xf8, 0xf2)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x55, 0x55)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x50, 0xfa, 0x7b)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x61, 0xbf, 0xff)) });
            Themes.Add(new ThemeViewModel { Name = "Flexoki Dark", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x10, 0x0f, 0x0f)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xce, 0xcd, 0xc3)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xaf, 0x30, 0x29)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x66, 0x80, 0x0b)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x20, 0x5e, 0xa6)) });
            Themes.Add(new ThemeViewModel { Name = "Gruvbox Dark", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x28, 0x28, 0x28)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xeb, 0xdb, 0xb2)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xcc, 0x24, 0x1d)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x98, 0x97, 0x1a)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x45, 0x85, 0x88)) });
            Themes.Add(new ThemeViewModel { Name = "Green Screen", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0d, 0x11, 0x17)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x21, 0xb5, 0x68)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x21, 0xb5, 0x68)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x00, 0xaa, 0xff)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x69, 0xb4)) });
            Themes.Add(new ThemeViewModel { Name = "Hacker Green", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x0d, 0x02, 0x08)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x00, 0xff, 0x41)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x00, 0x00)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x00, 0xff, 0x41)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x00, 0x5f, 0x00)) });
            Themes.Add(new ThemeViewModel { Name = "Kanagawa Wave", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1f, 0x1f, 0x28)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xdc, 0xd7, 0xba)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xc3, 0x40, 0x43)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x76, 0x94, 0x6a)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7e, 0x9c, 0xd8)) });
            Themes.Add(new ThemeViewModel { Name = "Material Dark", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x26, 0x32, 0x38)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xee, 0xff, 0xff)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf0, 0x71, 0x78)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xc3, 0xe8, 0x8d)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x82, 0xb1, 0xff)) });
            Themes.Add(new ThemeViewModel { Name = "Monokai", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x27, 0x28, 0x22)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf8, 0xf8, 0xf2)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf9, 0x26, 0x72)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xa6, 0xe2, 0x2e)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x66, 0xd9, 0xef)) });
            Themes.Add(new ThemeViewModel { Name = "Night Owl", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x01, 0x16, 0x27)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xd6, 0xde, 0xeb)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xef, 0x53, 0x50)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x22, 0xda, 0x6e)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x82, 0xaa, 0xff)) });
            Themes.Add(new ThemeViewModel { Name = "Nord", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x2e, 0x34, 0x40)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xd8, 0xde, 0xe9)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xbf, 0x61, 0x6a)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xa3, 0xbe, 0x8c)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x81, 0xa1, 0xc1)) });
            Themes.Add(new ThemeViewModel { Name = "One Dark", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 12, 12, 12)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 204, 204)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 224, 108, 117)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 152, 195, 121)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 97, 175, 239)) });
            Themes.Add(new ThemeViewModel { Name = "Rose Pine", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x19, 0x17, 0x24)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xe0, 0xde, 0xf4)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xeb, 0x6f, 0x92)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x31, 0x74, 0x8f)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9c, 0xcf, 0xd8)) });
            Themes.Add(new ThemeViewModel { Name = "Solarized Dark", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x00, 0x2b, 0x36)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x83, 0x94, 0x96)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xdc, 0x32, 0x2f)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x85, 0x99, 0x00)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x26, 0x8b, 0xd2)) });
            Themes.Add(new ThemeViewModel { Name = "Termark Dark", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x21, 0x21, 0x21)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xe6, 0xed, 0xf3)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xff, 0x7b, 0x72)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7e, 0xe7, 0x87)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x79, 0xc0, 0xff)) });
            Themes.Add(new ThemeViewModel { Name = "Tokyo Night", BgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1a, 0x1b, 0x26)), FgBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xc0, 0xca, 0xf5)), Accent1Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xf7, 0x76, 0x8e)), Accent2Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x9e, 0xce, 0x6a)), Accent3Brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x7a, 0xa2, 0xf7)) });
        
}

        private async Task LoadSidebarAppearanceAsync()
        {
            _cachedSettings = await _storage.LoadSettingsAsync();
            SidebarFontFamilyCombo.ItemsSource = Microsoft.Graphics.Canvas.Text.CanvasTextFormat.GetSystemFontFamilies();
            SidebarFontFamilyCombo.SelectedItem = _cachedSettings.FontFamily;
            SidebarFontSizeBox.Value = _cachedSettings.FontSize;
            SyncThemeMenuCheckedState(_cachedSettings.ColorScheme);
        }

        private async void SidebarFontFamily_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_cachedSettings == null || SidebarFontFamilyCombo.SelectedItem is not string font) return;
            _cachedSettings.FontFamily = font;
            await SaveSidebarAppearanceAsync();
        }

        private async void SidebarFontSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_cachedSettings == null || double.IsNaN(args.NewValue)) return;
            _cachedSettings.FontSize = Math.Clamp(args.NewValue, 10, 32);
            await SaveSidebarAppearanceAsync();
        }

        private async Task SaveSidebarAppearanceAsync()
        {
            if (_cachedSettings == null) return;
            await _storage.SaveSettingsAsync(_cachedSettings);
            TerminalSettings.NotifyGlobalSettingsChanged(_cachedSettings);
        }

        private async void NewTerminalTheme_Click(object sender, RoutedEventArgs e)
        {
            var source = TerminalThemeService.Instance.Find(_cachedSettings?.ColorScheme)
                ?? TerminalThemeService.Instance.Themes.First();
            var colors = new Dictionary<string, Windows.UI.Color>
            {
                ["Background"] = TerminalThemeService.ParseColor(source.Background, Microsoft.UI.Colors.Black),
                ["Foreground"] = TerminalThemeService.ParseColor(source.Foreground, Microsoft.UI.Colors.White),
                ["SelectionBackground"] = TerminalThemeService.ParseColor(source.SelectionBackground, Microsoft.UI.Colors.DarkBlue),
                ["CursorColor"] = TerminalThemeService.ParseColor(source.CursorColor, Microsoft.UI.Colors.White)
            };
            for (var i = 0; i < 16; i++)
                colors[$"Ansi{i}"] = TerminalThemeService.ParseColor(source.AnsiColors[i], Microsoft.UI.Colors.Gray);

            var nameBox = new TextBox { Header = "主题名称", PlaceholderText = "例如 My Theme" };
            var editor = new StackPanel { Spacing = 8 };
            editor.Children.Add(nameBox);
            editor.Children.Add(new TextBlock
            {
                Text = "基础颜色",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
            AddThemeColorEditor(editor, "背景", "Background", colors);
            AddThemeColorEditor(editor, "文字", "Foreground", colors);
            AddThemeColorEditor(editor, "选区", "SelectionBackground", colors);
            AddThemeColorEditor(editor, "光标", "CursorColor", colors);
            editor.Children.Add(new TextBlock
            {
                Text = "ANSI 颜色（普通 0–7 / 高亮 8–15）",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 8, 0, 0)
            });
            var ansiNames = new[] { "黑", "红", "绿", "黄", "蓝", "品红", "青", "白" };
            for (var i = 0; i < 16; i++)
                AddThemeColorEditor(editor, $"{i}: {(i >= 8 ? "高亮" : "")} {ansiNames[i % 8]}", $"Ansi{i}", colors);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = "新建终端主题",
                Content = new ScrollViewer { Content = editor, MaxHeight = 560, Width = 420 },
                PrimaryButtonText = "保存并应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(nameBox.Text)) return;
                args.Cancel = true;
                nameBox.Header = "主题名称（不能为空）";
                nameBox.Focus(FocusState.Programmatic);
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var theme = new TerminalTheme
            {
                Name = nameBox.Text.Trim(),
                Background = ToHex(colors["Background"]),
                Foreground = ToHex(colors["Foreground"]),
                SelectionBackground = ToHex(colors["SelectionBackground"]),
                CursorColor = ToHex(colors["CursorColor"]),
                AnsiColors = Enumerable.Range(0, 16).Select(i => ToHex(colors[$"Ansi{i}"])).ToList()
            };
            await TerminalThemeService.Instance.SaveUserThemeAsync(theme);
            LoadThemes();
            if (_cachedSettings == null) _cachedSettings = await _storage.LoadSettingsAsync();
            _cachedSettings.ColorScheme = theme.Name;
            await SaveSidebarAppearanceAsync();
            SyncThemeMenuCheckedState(theme.Name);
        }

        private static void AddThemeColorEditor(StackPanel panel, string label, string key,
            Dictionary<string, Windows.UI.Color> colors)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var swatch = new Button
            {
                Width = 92,
                Height = 30,
                Padding = new Thickness(6, 2, 6, 2),
                Content = ToHex(colors[key]),
                Background = new SolidColorBrush(colors[key])
            };
            Grid.SetColumn(swatch, 1);
            var picker = new ColorPicker
            {
                Color = colors[key],
                IsAlphaEnabled = false,
                IsColorChannelTextInputVisible = true,
                IsHexInputVisible = true
            };
            picker.ColorChanged += (_, args) =>
            {
                colors[key] = args.NewColor;
                swatch.Background = new SolidColorBrush(args.NewColor);
                swatch.Content = ToHex(args.NewColor);
            };
            swatch.Flyout = new Flyout { Content = picker };
            row.Children.Add(swatch);
            panel.Children.Add(row);
        }

        private static string ToHex(Windows.UI.Color color, bool includeAlpha = false) => includeAlpha
            ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private async Task LoadSnippetsAsync()
        {
            var loaded = await _storage.LoadSnippetsAsync();
            Snippets.Clear();
            if (loaded.Count == 0)
            {
                // Provide some defaults if empty
                loaded.Add(new SnippetViewModel { Name = "系统更新", Command = "sudo apt update && sudo apt upgrade -y" });
                loaded.Add(new SnippetViewModel { Name = "网络连接状态", Command = "netstat -ntlp" });
                await _storage.SaveSnippetsAsync(loaded);
            }
            foreach (var s in loaded) Snippets.Add(s);
        }

        private async void NewSnippetButton_Click(object sender, RoutedEventArgs e)
        {
            var snippet = new SnippetViewModel();
            if (await ShowSnippetDialogAsync(snippet, "新建代码片段"))
            {
                Snippets.Add(snippet);
                await _storage.SaveSnippetsAsync(Snippets.ToList());
            }
        }

        private async void EditSnippet_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SnippetViewModel snippet)
            {
                if (await ShowSnippetDialogAsync(snippet, "编辑代码片段"))
                {
                    // Refresh UI
                    int index = Snippets.IndexOf(snippet);
                    if (index >= 0)
                    {
                        Snippets[index] = new SnippetViewModel { Id = snippet.Id, Name = snippet.Name, Command = snippet.Command };
                    }
                    await _storage.SaveSnippetsAsync(Snippets.ToList());
                }
            }
        }

        private async void DeleteSnippet_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SnippetViewModel snippet)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = this.ActualTheme,
                    Title = "删除代码片段",
                    Content = $"确定要删除「{snippet.Name}」吗？",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    var item = Snippets.FirstOrDefault(s => s.Id == snippet.Id);
                    if (item != null) Snippets.Remove(item);
                    await _storage.SaveSnippetsAsync(Snippets.ToList());
                }
            }
        }

        private void RunSnippet_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is SnippetViewModel snippet)
            {
                if (TerminalTabView.SelectedItem is TabViewItem tab && tab.Tag is TerminalSession session)
                {
                    session.SendText(snippet.Command + "\r");
                }
                else
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = this.ActualTheme,
                        Title = "无法执行",
                        Content = "请先打开或选中一个 SSH 终端会话。",
                        CloseButtonText = "确定"
                    };
                    _ = dialog.ShowAsync();
                }
            }
        }

        private async Task<bool> ShowSnippetDialogAsync(SnippetViewModel snippet, string title)
        {
            var nameBox = new TextBox { Header = "名称", Text = snippet.Name, Margin = new Thickness(0,0,0,12) };
            var commandBox = new TextBox 
            { 
                Header = "命令内容", 
                Text = snippet.Command, 
                AcceptsReturn = true, 
                TextWrapping = TextWrapping.Wrap, 
                Height = 150,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
            };

            var panel = new StackPanel();
            panel.Children.Add(nameBox);
            panel.Children.Add(commandBox);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                RequestedTheme = this.ActualTheme,
                Title = title,
                Content = panel,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text)) nameBox.Text = "未命名";
                snippet.Name = nameBox.Text;
                snippet.Command = commandBox.Text;
                return true;
            }
            return false;
        }

        private void SidebarTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton btn && btn.Tag is string tag)
            {
                // Reset all
                TabSnippetsButton.IsChecked = false;
                TabHistoryButton.IsChecked = false;
                TabThemesButton.IsChecked = false;
                
                TabSnippetsButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                TabHistoryButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                TabThemesButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

                SidebarSnippetsView.Visibility = Visibility.Collapsed;
                SidebarHistoryView.Visibility = Visibility.Collapsed;
                SidebarThemesView.Visibility = Visibility.Collapsed;

                // Set Active
                btn.IsChecked = true;
                btn.Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
                
                switch (tag)
                {
                    case "0": SidebarSnippetsView.Visibility = Visibility.Visible; break;
                    case "1": SidebarHistoryView.Visibility = Visibility.Visible; break;
                    case "2": SidebarThemesView.Visibility = Visibility.Visible; break;
                }
            }
        }

        // ── Connection list actions ───────────────────────────────────────────

        // ConnectionListView_DoubleTapped is now handled in MainWindow pane

        // ── Terminal Sidebar ────────────────────────────────────────

        private TerminalSettings? _cachedSettings;

        public void ToggleSidebar()
        {
            TerminalSplitView.IsPaneOpen = !TerminalSplitView.IsPaneOpen;
        }

        private void TerminalSplitView_PaneClosed(SplitView sender, object args)
        {
            // In Overlay mode the light-dismiss layer consumes the click that closes
            // the pane, so restore terminal input after the close animation completes.
            RestoreTerminalFocus();
        }

        private void TerminalTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSidebarToggleButtonState();
            
            if (TerminalTabView.SelectedItem is TabViewItem tab && tab.Tag is TerminalSession session)
            {
                AiPane.ActiveSession = session;
            }
            else
            {
                AiPane.ActiveSession = null;
            }

            RestoreTerminalFocus();
        }

        private void AiButton_Click(object sender, RoutedEventArgs e)
        {
            AiPaneContainer.Visibility = AiPaneContainer.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (AiPaneContainer.Visibility == Visibility.Visible)
            {
                _ = AiPane.ReloadSettingsAsync();
            }
        }

        public void RestoreTerminalFocus()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (TerminalTabView.SelectedItem is not TabViewItem tab || tab.Tag is not TerminalSession)
                    return;
                if (tab.Content is Grid grid && grid.Children.OfType<TerminalView>().FirstOrDefault() is { } terminal)
                    terminal.FocusTerminal();
            });
        }

        private void MainPage_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0)
                    return;
                var focused = FocusManager.GetFocusedElement(XamlRoot);
                if (focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox or NumberBox)
                    return;
                RestoreTerminalFocus();
            });
        }

        /// <summary>
        /// Enables the sidebar toggle button only when the selected tab is an SSH terminal session.
        /// </summary>
        private void UpdateSidebarToggleButtonState()
        {
            bool isSshTab = false;
            if (TerminalTabView.SelectedItem is TabViewItem selectedTab)
            {
                // SSH tabs have TerminalSession as Tag; SFTP/settings tabs have string tags like "sftp"/"settings"
                isSshTab = selectedTab.Tag is TerminalSession;
            }

            if (MainWindow.Instance != null)
            {
                MainWindow.Instance.SetSidebarToggleEnabled(isSshTab);
            }
        }



        // ── Connection Picker Flyout (created entirely in code to avoid XAML resource issues) ──

        private ContentDialog? _connectionPickerDialog;
        private bool _isConnectionPickerDialogOpen;
        private TextBox? _flyoutSearchBox;
        private ListView? _flyoutList;

        private void TerminalTabView_AddTabButtonClick(TabView sender, object args)
        {
            ShowConnectionPicker();
        }

        private async void ShowConnectionPicker()
        {
            if (_connectionPickerDialog == null)
            {
                var root = new Grid { Width = 320 };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

                // ── Header ──
                var header = new Grid { Padding = new Thickness(16, 14, 16, 10) };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var headerIcon = new FontIcon { Glyph = "\uE703", FontSize = 14 };
                headerIcon.SetValue(Grid.ColumnProperty, 0);
                headerIcon.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
                headerIcon.VerticalAlignment = VerticalAlignment.Center;
                headerIcon.Margin = new Thickness(0, 0, 10, 0);

                var headerText = new TextBlock { Text = "选择连接" };
                headerText.SetValue(Grid.ColumnProperty, 1);
                headerText.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
                headerText.VerticalAlignment = VerticalAlignment.Center;

                header.Children.Add(headerIcon);
                header.Children.Add(headerText);
                Grid.SetRow(header, 0);

                // ── Search box ──
                _flyoutSearchBox = new TextBox { PlaceholderText = "搜索连接..." };
                _flyoutSearchBox.TextChanged += FlyoutSearch_TextChanged;
                var searchContainer = new Grid { Padding = new Thickness(12, 0, 12, 8) };
                searchContainer.Children.Add(_flyoutSearchBox);
                Grid.SetRow(searchContainer, 1);

                // ── Connection list ──
                _flyoutList = new ListView
                {
                    IsItemClickEnabled = true,
                    SelectionMode = ListViewSelectionMode.Single,
                    MaxHeight = 320
                };

                var itemStyle = new Style(typeof(ListViewItem));
                itemStyle.Setters.Add(new Setter(ListViewItem.PaddingProperty, new Thickness(10, 8, 10, 8)));
                itemStyle.Setters.Add(new Setter(ListViewItem.MarginProperty, new Thickness(0, 1, 0, 1)));
                itemStyle.Setters.Add(new Setter(ListViewItem.CornerRadiusProperty, new CornerRadius(4)));
                itemStyle.Setters.Add(new Setter(ListViewItem.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
                itemStyle.Setters.Add(new Setter(ListViewItem.MinHeightProperty, 0));
                _flyoutList.ItemContainerStyle = itemStyle;

                _flyoutList.ItemClick += (s, e) =>
                {
                    if (e.ClickedItem is ConnectionItemViewModel vm)
                    {
                        _connectionPickerDialog?.Hide();
                        OpenTerminalTab(vm.Profile);
                    }
                };

                var listContainer = new Grid { Padding = new Thickness(8, 0, 8, 8) };
                listContainer.Children.Add(_flyoutList);
                Grid.SetRow(listContainer, 2);

                root.Children.Add(header);
                root.Children.Add(searchContainer);
                root.Children.Add(listContainer);

                _connectionPickerDialog = new ContentDialog
                {
                    Title = "选择连接",
                    Content = root,
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
            }

            // Refresh items and show
            _flyoutSearchBox!.Text = "";
            _flyoutList!.ItemsSource = FlatSidebarItems.OfType<ConnectionItemViewModel>().ToList();

            // Build item template if not set yet
            if (_flyoutList.ItemTemplate == null)
                _flyoutList.ItemTemplate = BuildConnectionItemTemplate();

            if (_isConnectionPickerDialogOpen)
                return;

            _connectionPickerDialog.XamlRoot = XamlRoot;
            _connectionPickerDialog.RequestedTheme = ActualTheme;
            _isConnectionPickerDialogOpen = true;

            // Auto-focus search box
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                DispatcherQueue.TryEnqueue(() => _flyoutSearchBox.Focus(FocusState.Programmatic));
            });

            try
            {
                await _connectionPickerDialog.ShowAsync();
            }
            finally
            {
                _isConnectionPickerDialogOpen = false;
            }
        }

        private void FlyoutSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            var query = _flyoutSearchBox!.Text.Trim().ToLower();
            var allItems = FlatSidebarItems.OfType<ConnectionItemViewModel>();
            _flyoutList!.ItemsSource = string.IsNullOrEmpty(query)
                ? allItems.ToList()
                : allItems.Where(item =>
                    item.Name.ToLower().Contains(query) ||
                    item.Profile.Host.ToLower().Contains(query) ||
                    item.Profile.Username.ToLower().Contains(query) ||
                    item.Group.ToLower().Contains(query)).ToList();
        }

        private DataTemplate BuildConnectionItemTemplate()
        {
            // Note: x:DataType cannot be set in code, but the bindings still work
            // since ConnectionItemViewModel exposes the required properties
            string xaml = @"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
              xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
    <Grid ColumnSpacing='12' Padding='2,2,2,2'>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width='32'/>
            <ColumnDefinition Width='*'/>
            <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>

        <Border Grid.Column='0' Width='32' Height='32' CornerRadius='4'
                Opacity='0.15' HorizontalAlignment='Center' VerticalAlignment='Center'
                Background='{ThemeResource AccentFillColorDefaultBrush}'>
            <FontIcon Glyph='&#xE8C8;' FontSize='14' Opacity='1'
                      Foreground='{ThemeResource AccentTextFillColorPrimaryBrush}'
                      HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>

        <StackPanel Grid.Column='1' Spacing='2' VerticalAlignment='Center'>
            <TextBlock Text='{Binding Name}' TextTrimming='CharacterEllipsis' MaxLines='1'/>
            <TextBlock Text='{Binding DisplayHostPort}' FontSize='11' FontFamily='Consolas'
                       Foreground='{ThemeResource TextFillColorSecondaryBrush}'
                       TextTrimming='CharacterEllipsis' MaxLines='1'/>
        </StackPanel>

        <Border Grid.Column='2' CornerRadius='3' Padding='6,2' VerticalAlignment='Center'
                Background='{ThemeResource SubtleFillColorSecondaryBrush}'>
            <TextBlock Text='{Binding Group}' FontSize='10'
                       Foreground='{ThemeResource TextFillColorSecondaryBrush}'/>
        </Border>
    </Grid>
</DataTemplate>";
            return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
        }



        private void TerminalTabView_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            CloseTab(args.Tab);
        }

        private void TerminalTabView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var tabContainer = FindVisualChildByName(TerminalTabView, "TabContainerGrid");
            if (tabContainer != null)
            {
                // Align Sidebar and AI Pane with exact height of the TabStrip (40px)
                if (SidebarPaneGrid != null)
                {
                    SidebarPaneGrid.Margin = new Thickness(0, 40, 0, 0);
                }
                if (AiPaneContainer != null)
                {
                    AiPaneContainer.Margin = new Thickness(0, 40, 0, 0);
                }
            }

            UpdateTitleBarInteractiveRegions();
        }

        private void UpdateTitleBarInteractiveRegions()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var mainWindow = MainWindow.Instance;
                if (mainWindow == null || TerminalTabView == null || TerminalTabView.Visibility != Visibility.Visible)
                {
                    mainWindow?.SetTitleBarPassthroughRects(Array.Empty<Windows.Foundation.Rect>());
                    return;
                }

                var root = mainWindow.TitleBarCoordinateRoot;
                var rects = new List<Windows.Foundation.Rect>();

                foreach (var tabItem in FindVisualChildren<TabViewItem>(TerminalTabView))
                    AddTopRegionRect(tabItem, root, rects);

                foreach (var button in FindVisualChildren<Button>(TerminalTabView))
                    AddTopRegionRect(button, root, rects);

                mainWindow.SetTitleBarPassthroughRects(rects);
            });
        }

        private static void AddTopRegionRect(FrameworkElement element, UIElement root, List<Windows.Foundation.Rect> rects)
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0 || element.Visibility != Visibility.Visible)
                return;

            try
            {
                var point = element.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
                var rect = new Windows.Foundation.Rect(point.X, point.Y, element.ActualWidth, element.ActualHeight);

                if (rect.Y < 48 && rect.Y + rect.Height > 0)
                    rects.Add(rect);
            }
            catch
            {
                // Layout may still be settling; the next size/update pass will refresh this.
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    yield return typedChild;

                foreach (var nestedChild in FindVisualChildren<T>(child))
                    yield return nestedChild;
            }
        }

        private FrameworkElement? FindVisualChildByName(DependencyObject parent, string name)
        {
            for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement fe && fe.Name == name)
                    return fe;

                var result = FindVisualChildByName(child, name);
                if (result != null)
                    return result;
            }
            return null;
        }

        private void CloseTab(TabViewItem tab)
        {
            if (tab.Tag is TerminalSession session)
                session.Dispose();

            if (tab.Content is Grid tabContent)
            {
                var tv = tabContent.Children.OfType<TerminalView>().FirstOrDefault();
                if (tv != null)
                {
                    tv.Dispose();
                }
            }

            if (tab.Content is SftpPage sftpPage)
            {
                sftpPage.Dispose();
            }

            TerminalTabView.TabItems.Remove(tab);
            UpdateEmptyState();
        }

        private async void OpenTerminalTab(ConnectionProfile profile)
        {
            // ── Phase 4 terminal view: Win2D Canvas rendering + Keyboard Input ──
            var terminalView = new TerminalView();
            var settings = await _storage.LoadSettingsAsync();
            terminalView.ApplySettings(settings);

            // Status bar at bottom
            var statusBar = new InfoBar
            {
                IsOpen = true,
                Severity = InfoBarSeverity.Informational,
                Title = "正在连接...",
                Message = $"{profile.Username}@{profile.Host}:{profile.Port}"
            };

            var tabContent = new Grid
            {
                Padding = new Thickness(4, 0, 4, 0),
                Background = GetTerminalBackgroundBrush(settings)
            };
            tabContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            tabContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(terminalView, 0);
            Grid.SetRow(statusBar, 1);
            tabContent.Children.Add(terminalView);
            tabContent.Children.Add(statusBar);

            var tab = new TabViewItem
            {
                Header = profile.Name,
                IconSource = new FontIconSource
                {
                    Glyph = "\uE895", // Sync
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange)
                },
                Content = tabContent
            };

            // ── Tab Context Menu ────────────────────────────────────────────────
            var flyout = new MenuFlyout();

            var closeItem = new MenuFlyoutItem { Text = "关闭标签" };
            closeItem.Click += (_, _) => CloseTab(tab);

            var closeOthersItem = new MenuFlyoutItem { Text = "关闭其他标签" };
            closeOthersItem.Click += (_, _) =>
            {
                var tabsToRemove = TerminalTabView.TabItems.Cast<TabViewItem>().Where(t => t != tab).ToList();
                foreach (var t in tabsToRemove) CloseTab(t);
            };

            var closeRightItem = new MenuFlyoutItem { Text = "关闭右侧标签" };
            closeRightItem.Click += (_, _) =>
            {
                int index = TerminalTabView.TabItems.IndexOf(tab);
                var tabsToRemove = TerminalTabView.TabItems.Cast<TabViewItem>().Skip(index + 1).ToList();
                foreach (var t in tabsToRemove) CloseTab(t);
            };

            flyout.Items.Add(closeItem);
            flyout.Items.Add(closeOthersItem);
            flyout.Items.Add(closeRightItem);
            tab.ContextFlyout = flyout;

            // Create and wire up the session
            var session = new TerminalSession(profile);
            tab.Tag = session;  // so TabCloseRequested can dispose it

            // ── Host Key Verification ───────────────────────────────────────────
            session.Transport.HostKeyVerifier = async (host, port, algorithm, fingerprint) =>
            {
                var trusted = _knownHosts.Check(host, port, algorithm, fingerprint);
                if (trusted == true)  return true;   // already known + unchanged
                if (trusted == false) return await ShowChangedHostKeyDialogAsync(host, port, algorithm, fingerprint);
                return await ShowNewHostKeyDialogAsync(host, port, algorithm, fingerprint);
            };

            // Handle state changes
            session.StateChanged += (_, state) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    switch (state)
                    {
                        case TerminalSession.SessionState.Connected:
                            tab.Header = profile.Name;
                            tab.IconSource = new FontIconSource
                            {
                                Glyph = "\uE8C8", // Terminal
                                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.LimeGreen)
                            };
                            statusBar.Severity = InfoBarSeverity.Success;
                            statusBar.Title = "已连接";
                            statusBar.Message = $"{profile.Username}@{profile.Host}:{profile.Port}";

                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(10000);
                                DispatcherQueue.TryEnqueue(() => statusBar.IsOpen = false);
                            });
                            break;

                        case TerminalSession.SessionState.Error:
                            tab.Header = profile.Name;
                            tab.IconSource = new FontIconSource
                            {
                                Glyph = "\uEA39", // Error
                                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red)
                            };
                            statusBar.Severity = InfoBarSeverity.Error;
                            statusBar.Title = "连接失败";
                            statusBar.Message = session.LastError ?? "未知错误";
                            break;
                    }
                });
            };

            // Handle title updates from OSC sequences
            session.TitleChanged += title =>
            {
                DispatcherQueue.TryEnqueue(() => tab.Header = title);
            };

            // Attach session to the Win2D View
            terminalView.AttachSession(session);

            TerminalTabView.TabItems.Add(tab);
            TerminalTabView.SelectedItem = tab;
            UpdateEmptyState();
            RestoreTerminalFocus();
        }

        // ── Host Key dialogs ─────────────────────────────────────────────────────────────────

        private async Task<bool> ShowNewHostKeyDialogAsync(
            string host, int port, string algorithm, string fingerprint)
        {
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = this.ActualTheme,
                        Title = "🔑 未知主机",
                        Content = new StackPanel { Spacing = 8, Children =
                        {
                            new TextBlock { Text = $"首次连接到 {host}:{port}，请确认主机指纹是否正确。", TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = $"算法：{algorithm}", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 },
                            new TextBlock { Text = fingerprint, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
                        }},
                        PrimaryButtonText = "信任并连接",
                        CloseButtonText   = "拒绝",
                        DefaultButton     = ContentDialogButton.Primary
                    };
                    var result = await dialog.ShowAsync();
                    bool ok = result == ContentDialogResult.Primary;
                    if (ok) _knownHosts.Trust(host, port, algorithm, fingerprint);
                    tcs.SetResult(ok);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return await tcs.Task;
        }

        private async Task<bool> ShowChangedHostKeyDialogAsync(
            string host, int port, string algorithm, string fingerprint)
        {
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var dialog = new ContentDialog
                    {
                        XamlRoot = this.XamlRoot,
                        RequestedTheme = this.ActualTheme,
                        Title = "⚠️ 主机指纹已变更！",
                        Content = new StackPanel { Spacing = 8, Children =
                        {
                            new TextBlock
                            {
                                Text = $"{host}:{port} 的主机密钒与之前保存的不一致！\n" +
                                       "这可能意味着副本攻击（MITM）或服务器密钒已更新。\n" +
                                       "确认新指纹后才可信任。",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed)
                            },
                            new TextBlock { Text = $"新算法：{algorithm}", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 },
                            new TextBlock { Text = fingerprint, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 }
                        }},
                        PrimaryButtonText = "更新并信任",
                        CloseButtonText   = "拒绝",
                        DefaultButton     = ContentDialogButton.Close
                    };
                    var result = await dialog.ShowAsync();
                    bool ok = result == ContentDialogResult.Primary;
                    if (ok) _knownHosts.Trust(host, port, algorithm, fingerprint);
                    tcs.SetResult(ok);
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return await tcs.Task;
        }

        // ── Connection edit dialog ────────────────────────────────────────────

        private async Task<bool> ShowConnectionDialogAsync(ConnectionProfile profile, bool isNew)
        {
            var nameBox = new TextBox { Header = "连接名称", Text = profile.Name, PlaceholderText = "My Server" };

            var existingGroups = _groups.Select(g => g.Name).Distinct().Where(g => !string.IsNullOrEmpty(g)).ToList();
            if (!existingGroups.Contains("默认分组")) existingGroups.Insert(0, "默认分组");
            var groupCombo = new ComboBox
            {
                Header = "分组",
                IsEditable = true,
                ItemsSource = existingGroups,
                Text = string.IsNullOrEmpty(profile.Group) ? "默认分组" : profile.Group,
                PlaceholderText = "选择或输入新分组",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var hostBox = new TextBox { Header = "主机地址", Text = profile.Host, PlaceholderText = "192.168.1.1" };
            // 主机输入时清除错误状态
            hostBox.TextChanged += (s, e) => SetFieldError(hostBox, false);

            var portBox = new NumberBox { Header = "端口", Value = profile.Port, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var userBox = new TextBox { Header = "用户名", Text = profile.Username, PlaceholderText = "root" };
            userBox.TextChanged += (s, e) => SetFieldError(userBox, false);

            var authCombo = new ComboBox { Header = "认证方式", ItemsSource = new[] { "Password", "PrivateKey", "Agent" }, SelectedItem = profile.AuthType, Width = 170, HorizontalAlignment = HorizontalAlignment.Left };

            string existingPwd = "";
            if (!string.IsNullOrEmpty(profile.EncryptedPassword))
                try { existingPwd = ConnectionStorage.DecryptSecret(profile.EncryptedPassword); } catch { }

            var pwdBox = new PasswordBox { Header = "密码", Password = existingPwd, PlaceholderText = "输入密码", Visibility = profile.AuthType == "Password" ? Visibility.Visible : Visibility.Collapsed };
            string currentPwd = existingPwd;
            pwdBox.PasswordChanged += (s, e) => currentPwd = pwdBox.Password;

            var keyPathBox = new TextBox { Header = "私钥路径", Text = profile.PrivateKeyPath, PlaceholderText = @"C:\Users\...\id_rsa", Visibility = profile.AuthType == "PrivateKey" ? Visibility.Visible : Visibility.Collapsed };

            // ── SSH Agent info panel ───────────────────────────────────────────
            var agentInfoPanel = new StackPanel
            {
                Spacing = 6,
                Visibility = profile.AuthType == "Agent" ? Visibility.Visible : Visibility.Collapsed
            };
            agentInfoPanel.Children.Add(new TextBlock
            {
                Text = "使用系统 OpenSSH Agent 认证，无需填写密码或私钥路径。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            });
            var agentStatusText = new TextBlock
            {
                Text = "正在检测 OpenSSH Agent…",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
            };
            agentInfoPanel.Children.Add(agentStatusText);
            // 异步检测 Agent 可用性，不阻塞 UI
            _ = Task.Run(() => SshAgentService.IsOpenSshAgentAvailable())
                .ContinueWith(t => DispatcherQueue.TryEnqueue(() =>
                {
                    agentStatusText.Text = t.Result
                        ? "✓ OpenSSH Agent 运行中，连接时将自动使用已加载的密钥"
                        : "⚠ 未检测到 OpenSSH Agent\n请在系统「服务」中启动「OpenSSH Authentication Agent」";
                    agentStatusText.Foreground = new SolidColorBrush(t.Result
                        ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.OrangeRed);
                }));

            authCombo.SelectionChanged += (s, e) =>
            {
                string sel = authCombo.SelectedItem?.ToString() ?? "Password";
                pwdBox.Visibility      = sel == "Password"   ? Visibility.Visible : Visibility.Collapsed;
                keyPathBox.Visibility  = sel == "PrivateKey" ? Visibility.Visible : Visibility.Collapsed;
                agentInfoPanel.Visibility = sel == "Agent"  ? Visibility.Visible : Visibility.Collapsed;
            };

            // ── Keepalive ──────────────────────────────────────────────────────
            var keepAliveBox = new NumberBox
            {
                Header = "Keepalive 间隔（秒，0=关闭）",
                Value = profile.KeepAliveIntervalSeconds,
                Minimum = 0,
                Maximum = 300,
                MaxWidth = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
            };

            // ── 监控开关 ──────────────────────────────────────────────────────
            var monitorSwitch = new ToggleSwitch
            {
                Header = "在连接列表开启性能监控",
                IsOn = profile.EnableMonitoring
            };
            var monitorInterval = new NumberBox
            {
                Header = "监控间隔（秒）",
                Value = profile.MonitorIntervalSeconds,
                Minimum = 3,
                Maximum = 60,
                MaxWidth = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Visibility = profile.EnableMonitoring ? Visibility.Visible : Visibility.Collapsed
            };
            monitorSwitch.Toggled += (_, _) =>
                monitorInterval.Visibility = monitorSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;

            // ── Port Forwarding ──────────────────────────────────────────────────────
            var pfExpander = new Expander { Header = "端口转发 (Port Forwarding)", HorizontalAlignment = HorizontalAlignment.Stretch };
            var pfStack = new StackPanel { Spacing = 8 };
            var pfList = new StackPanel { Spacing = 4 };
            var pfRules = profile.PortForwards != null ? new System.Collections.Generic.List<PortForwardRule>(profile.PortForwards) : new System.Collections.Generic.List<PortForwardRule>();

            void RenderPfRules()
            {
                pfList.Children.Clear();
                foreach (var rule in pfRules)
                {
                    var row = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    
                    string desc = rule.Type switch {
                        PortForwardType.Local => $"Local: {rule.BindPort} -> {rule.TargetHost}:{rule.TargetPort}",
                        PortForwardType.Remote => $"Remote: {rule.BindPort} <- {rule.TargetHost}:{rule.TargetPort}",
                        PortForwardType.Dynamic => $"Dynamic: {rule.BindPort} (SOCKS5)",
                        _ => "Unknown"
                    };
                    
                    var txt = new TextBlock { Text = desc, VerticalAlignment = VerticalAlignment.Center, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12 };
                    Grid.SetColumn(txt, 0);
                    
                    var delBtn = new Button { Content = "删除", Padding = new Thickness(8,2,8,2) };
                    delBtn.Click += (_, _) => { pfRules.Remove(rule); RenderPfRules(); };
                    Grid.SetColumn(delBtn, 1);
                    
                    row.Children.Add(txt);
                    row.Children.Add(delBtn);
                    pfList.Children.Add(row);
                }
            }
            RenderPfRules();

            var addPfBtn = new Button { Content = "+ 添加规则", HorizontalAlignment = HorizontalAlignment.Right };
            
            var typeCombo = new ComboBox { Header = "类型", ItemsSource = new[] { "Local", "Remote", "Dynamic" }, SelectedIndex = 0 };
            var bindPortBox = new NumberBox { Header = "本地/远程绑定端口", Value = 8080, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            var targetHostBox = new TextBox { Header = "目标主机 (Target Host)", Text = "127.0.0.1", PlaceholderText = "127.0.0.1" };
            var targetPortBox = new NumberBox { Header = "目标端口 (Target Port)", Value = 80, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };

            typeCombo.SelectionChanged += (s, ev) =>
            {
                bool isDynamic = typeCombo.SelectedIndex == 2;
                targetHostBox.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
                targetPortBox.Visibility = isDynamic ? Visibility.Collapsed : Visibility.Visible;
            };

            Grid CreateTwoColumnRow(FrameworkElement left, FrameworkElement right, double leftWeight = 1, double rightWeight = 1)
                => CreateTwoColumnRowWithWidths(
                    left,
                    right,
                    new GridLength(leftWeight, GridUnitType.Star),
                    new GridLength(rightWeight, GridUnitType.Star));

            Grid CreateTwoColumnRowWithWidths(FrameworkElement left, FrameworkElement right, GridLength leftWidth, GridLength rightWidth)
            {
                var row = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = leftWidth });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = rightWidth });

                left.HorizontalAlignment = HorizontalAlignment.Stretch;
                right.HorizontalAlignment = HorizontalAlignment.Stretch;

                Grid.SetColumn(left, 0);
                Grid.SetColumn(right, 1);
                row.Children.Add(left);
                row.Children.Add(right);
                return row;
            }

            Grid CreateCompactTwoColumnRow(FrameworkElement left, FrameworkElement right, double leftWidth, double rightWidth)
            {
                var row = CreateTwoColumnRowWithWidths(left, right, new GridLength(leftWidth), new GridLength(rightWidth));
                row.HorizontalAlignment = HorizontalAlignment.Left;
                return row;
            }

            var subContent = new StackPanel { Spacing = 8, Width = 380 };
            subContent.Children.Add(CreateTwoColumnRow(typeCombo, bindPortBox, 0.9, 1.1));
            subContent.Children.Add(CreateTwoColumnRow(targetHostBox, targetPortBox, 1.4, 1));

            var confirmAddBtn = new Button { Content = "确定添加", HorizontalAlignment = HorizontalAlignment.Right, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            subContent.Children.Add(confirmAddBtn);

            var flyout = new Flyout { Content = subContent };
            addPfBtn.Flyout = flyout;

            confirmAddBtn.Click += (s, e) =>
            {
                pfRules.Add(new PortForwardRule
                {
                    Type = (PortForwardType)typeCombo.SelectedIndex,
                    BindPort = (int)Math.Clamp(bindPortBox.Value, 1, 65535),
                    TargetHost = targetHostBox.Text.Trim(),
                    TargetPort = (int)Math.Clamp(targetPortBox.Value, 1, 65535)
                });
                RenderPfRules();
                flyout.Hide();
            };

            pfStack.Children.Add(pfList);
            pfStack.Children.Add(addPfBtn);
            pfExpander.Content = pfStack;

            // ── 内联错误提示 ───────────────────────────────────────────────────
            var errorLabel = new TextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            var content = new StackPanel { Spacing = 12, Width = 460 };
            content.Children.Add(CreateCompactTwoColumnRow(nameBox, groupCombo, 270, 170));
            content.Children.Add(CreateCompactTwoColumnRow(hostBox, portBox, 330, 80));
            content.Children.Add(CreateCompactTwoColumnRow(userBox, pwdBox, 210, 220));
            content.Children.Add(authCombo);
            content.Children.Add(keyPathBox);
            content.Children.Add(agentInfoPanel);
            content.Children.Add(monitorSwitch);
            content.Children.Add(keepAliveBox);
            content.Children.Add(monitorInterval);
            content.Children.Add(pfExpander);
            content.Children.Add(errorLabel);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                RequestedTheme = this.ActualTheme,
                Title = isNew ? "新建连接" : "编辑连接",
                Content = new ScrollViewer
                {
                    Content = content,
                    MaxHeight = 540,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                },
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            // 保存前校验
            dialog.PrimaryButtonClick += (s, args) =>
            {
                string host = hostBox.Text.Trim();
                string user = userBox.Text.Trim();

                if (string.IsNullOrEmpty(host))
                {
                    args.Cancel = true;
                    SetFieldError(hostBox, true);
                    ShowDialogError(errorLabel, "主机地址不能为空");
                    return;
                }
                if (!IsValidHost(host))
                {
                    args.Cancel = true;
                    SetFieldError(hostBox, true);
                    ShowDialogError(errorLabel, "主机地址格式不正确，请输入合法的 IPv4、IPv6 或域名\n示例：192.168.1.1 / [::1] / my-server.example.com");
                    return;
                }
                if (string.IsNullOrEmpty(user))
                {
                    args.Cancel = true;
                    SetFieldError(userBox, true);
                    ShowDialogError(errorLabel, "用户名不能为空");
                    return;
                }
                errorLabel.Visibility = Visibility.Collapsed;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            profile.Name     = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : "New Connection";
            profile.Group    = groupCombo.Text.Trim().Length > 0 ? groupCombo.Text.Trim() : "默认分组";
            profile.Host     = hostBox.Text.Trim();
            profile.Port     = (int)portBox.Value;
            profile.Username = userBox.Text.Trim();
            profile.AuthType = authCombo.SelectedItem?.ToString() ?? "Password";
            profile.KeepAliveIntervalSeconds = double.IsNaN(keepAliveBox.Value) ? 0 : (int)keepAliveBox.Value;
            profile.EnableMonitoring = monitorSwitch.IsOn;
            profile.MonitorIntervalSeconds = (int)monitorInterval.Value;
            profile.PortForwards = pfRules;
            profile.LastConnected = DateTime.Now;

            if (profile.AuthType == "Password" && !string.IsNullOrEmpty(currentPwd))
                profile.EncryptedPassword = ConnectionStorage.EncryptSecret(currentPwd);

            if (profile.AuthType == "PrivateKey")
                profile.PrivateKeyPath = keyPathBox.Text.Trim();

            return true;
        }

        // ── 字段错误高亮 ──────────────────────────────────────────────────────

        private static void SetFieldError(Control ctrl, bool hasError)
        {
            if (hasError)
                ctrl.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            else
                ctrl.ClearValue(Control.BorderBrushProperty);
        }

        private static void ShowDialogError(TextBlock label, string message)
        {
            label.Text = message;
            label.Visibility = Visibility.Visible;
        }

        // ── 主机地址校验 ──────────────────────────────────────────────────────

        private static bool IsValidHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            host = host.Trim();

            // host:port 格式（非 IPv6）
            if (host.Contains(':') && !host.Contains(']'))
            {
                if (host.Count(c => c == ':') == 1)
                {
                    var parts = host.Split(':');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int p) && p >= 0 && p <= 65535)
                        host = parts[0];
                }
            }

            // 去掉 IPv6 括号 [::1] → ::1
            if (host.StartsWith('[') && host.EndsWith(']'))
                host = host[1..^1];

            return IsValidIpv4(host) || IsValidIpv6(host) || IsValidDomain(host);
        }

        private static bool IsValidIpv4(string addr)
        {
            if (addr.Count(c => c == '.') != 3) return false;
            var parts = addr.Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part)) return false;
                foreach (char c in part)
                    if (c < '0' || c > '9') return false;
                if (part.Length > 1 && part[0] == '0') return false;
                if (!int.TryParse(part, out int num) || num < 0 || num > 255) return false;
            }
            return true;
        }

        private static bool IsValidIpv6(string addr)
        {
            if (IsValidIpv4(addr)) return false;
            return IPAddress.TryParse(addr, out var ip)
                && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        private static bool IsValidDomain(string addr)
        {
            if (addr.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (addr.Length > 253) return false;
            if (addr.StartsWith('.') || addr.EndsWith('.') || addr.Contains("..")) return false;
            var labels = addr.Split('.');
            if (labels.Length < 2) return false;
            var labelPattern = new Regex(@"^[a-zA-Z0-9]([a-zA-Z0-9\-]*[a-zA-Z0-9])?$");
            foreach (var label in labels)
            {
                if (string.IsNullOrEmpty(label) || label.Length > 63) return false;
                if (label.Length == 1) { if (!char.IsLetterOrDigit(label[0])) return false; }
                else if (!labelPattern.IsMatch(label)) return false;
                if (label.StartsWith('-') || label.EndsWith('-')) return false;
            }
            var tld = labels[^1];
            return tld.Length >= 2 && tld.Any(char.IsLetter);
        }
    }
}
