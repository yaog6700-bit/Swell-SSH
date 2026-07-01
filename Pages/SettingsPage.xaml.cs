using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SwellSSH.Models;
using SwellSSH.Services;

namespace SwellSSH.Pages
{
    public class ColorSchemeItem
    {
        public string Name { get; set; } = "";
        public Microsoft.UI.Xaml.Media.SolidColorBrush ColorBrush { get; set; } = null!;
    }

    public sealed partial class SettingsPage : Page
    {
        private readonly ConnectionStorage _storage = new();
        private readonly BackupService _backup = new();
        private TerminalSettings _settings = new();
        private bool _isLoading;

        // 记录已应用到窗口的值，避免没有变化时重建 Backdrop / 切换主题（会导致白闪）
        private string _appliedBackdropType = "";
        private ElementTheme _appliedTheme = ElementTheme.Default;

        public ColorSchemeItem[] ColorSchemes { get; } =
        {
            new() { Name = "One Dark", ColorBrush = new(Windows.UI.Color.FromArgb(255, 40, 44, 52)) },
            new() { Name = "Dracula", ColorBrush = new(Windows.UI.Color.FromArgb(255, 40, 42, 54)) },
            new() { Name = "Solarized Dark", ColorBrush = new(Windows.UI.Color.FromArgb(255, 0, 43, 54)) },
            new() { Name = "Catppuccin Mocha", ColorBrush = new(Windows.UI.Color.FromArgb(255, 30, 30, 46)) },
            new() { Name = "Tokyo Night", ColorBrush = new(Windows.UI.Color.FromArgb(255, 26, 27, 38)) },
            new() { Name = "Nord", ColorBrush = new(Windows.UI.Color.FromArgb(255, 46, 52, 64)) },
            new() { Name = "Gruvbox Dark", ColorBrush = new(Windows.UI.Color.FromArgb(255, 40, 40, 40)) },
            new() { Name = "Default Light", ColorBrush = new(Windows.UI.Color.FromArgb(255, 250, 250, 250)) }
        };
        public string[] BackdropTypes { get; } = { "Mica", "Acrylic", "None" };
        public string[] FontFamilies { get; } = Microsoft.Graphics.Canvas.Text.CanvasTextFormat.GetSystemFontFamilies();

        public SettingsPage()
        {
            // 必须在 InitializeComponent() 之前设为 true，
            // 否则控件初始化时（Slider 默认值=10、ComboBox 首次赋值）
            // 触发的事件会在加载完成前把错误的默认值写入配置文件。
            _isLoading = true;
            this.InitializeComponent();
            _ = LoadSettingsAsync();

            // 显示当前版本号
            var ver = AppUpdateService.CurrentVersion;
            AppVersionText.Text = ver.Major == 0 && ver.Minor == 0
                ? "版本 开发版 (未打包)"
                : $"版本 {ver.Major}.{ver.Minor}.{ver.Build}";
        }

        private async Task LoadSettingsAsync()
        {
            // _isLoading 已在构造函数里设为 true，这里无需重复设置
            _settings = await _storage.LoadSettingsAsync();
            
            _appliedTheme = _settings.AppTheme == "Light" ? ElementTheme.Light : ElementTheme.Dark;
            _appliedBackdropType = _settings.BackdropType;

            ApplyToUi(_settings);
            
            // 给 UI 事件一点处理时间，防止 SelectedIndex 异步触发触发 SaveAndApplySettings
            await Task.Delay(50);
            _isLoading = false;
        }

        private void ApplyToUi(TerminalSettings s)
        {
            // 使用 SelectedIndex 而非 SelectedItem，避免 WinUI 3 中
            // SelectedItem 赋值后不立即同步（getter 仍返回旧值）
            // 导致 null 误判、错误 fallback 覆盖正确设置的问题。

            FontFamilyCombo.ItemsSource = FontFamilies;
            int fontIdx = System.Array.IndexOf(FontFamilies, s.FontFamily);
            FontFamilyCombo.SelectedIndex = fontIdx >= 0 ? fontIdx
                : System.Array.IndexOf(FontFamilies, "Consolas");

            FontSizeSlider.Value = s.FontSize;



            // BackdropTypes = { "Mica", "Acrylic", "None" }
            int backdropIdx = System.Array.IndexOf(BackdropTypes, s.BackdropType);
            BackdropSegmented.SelectedIndex = backdropIdx >= 0 ? backdropIdx : 0;

            switch (s.CursorStyle)
            {
                case "Underline": CursorUnderline.IsChecked = true; break;
                case "Bar":       CursorBar.IsChecked = true; break;
                default:          CursorBlock.IsChecked = true; break;
            }
        }

        private void FontSizeSlider_ValueChanged(object sender,
            Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            _settings.FontSize = e.NewValue;
            SaveAndApplySettings();
        }

        private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FontFamilyCombo.SelectedItem is string font)
            {
                _settings.FontFamily = font;
                SaveAndApplySettings();
            }
        }


        private void BackdropSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (BackdropSegmented.SelectedItem is CommunityToolkit.WinUI.Controls.SegmentedItem item && item.Content is string backdrop)
            {
                _settings.BackdropType = backdrop;
                SaveAndApplySettings();
            }
        }

        private void CursorStyle_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                _settings.CursorStyle = rb.Tag?.ToString() ?? "Block";
                SaveAndApplySettings();
            }
        }



        private async void SaveAndApplySettings()
        {
            if (_isLoading) return;
            await _storage.SaveSettingsAsync(_settings);
            TerminalSettings.NotifyGlobalSettingsChanged(_settings);

            if (MainWindow.Instance == null) return;

            // App theme (dark/light) is controlled only by the sidebar toggle, not by terminal ColorScheme.
            // No need to sync app theme here — terminal color changes should not affect app appearance.

            // 只有 BackdropType 真正变化时才重建 Backdrop，
            // 否则拖动字体大小等其他设置每次都会重建亚克力，导致白闪
            if (_settings.BackdropType != _appliedBackdropType)
            {
                _appliedBackdropType = _settings.BackdropType;
                MainWindow.Instance.SystemBackdrop = _settings.BackdropType switch
                {
                    "Acrylic" => new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
                    "None"    => null,
                    _         => new Microsoft.UI.Xaml.Media.MicaBackdrop()
                };
            }
        }

        // ── 设置页搜索 ──────────────────────────────────────────────────────

        private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            FilterSections(sender.Text);
        }

        private void FilterSections(string query)
        {
            query = query.Trim().ToLowerInvariant();
            bool isSearching = !string.IsNullOrEmpty(query);

            // 不搜索时恢复全部显示
            if (!isSearching)
            {
                FontSection.Visibility = CursorSection.Visibility =
                    BackdropSection.Visibility = DataSection.Visibility =
                    AboutSection.Visibility = Visibility.Visible;
                NoResultsText.Visibility = Visibility.Collapsed;
                return;
            }

            // 每个分区对应的匹配关键词（中英文混合）
            var sections = new (UIElement Section, string[] Keywords)[]
            {
                (FontSection,    new[] { "终端字体", "字体", "字号", "font", "consolas", "cascadia", "大小", "size" }),
                (CursorSection,  new[] { "光标样式", "光标", "cursor", "闪烁", "blink", "块状", "block", "下划线", "underline", "竖线", "bar" }),
                (BackdropSection,new[] { "窗口背景", "背景", "材质", "mica", "acrylic", "backdrop", "透明", "毛玻璃" }),
                (DataSection,    new[] { "数据", "备份", "导出", "恢复", "backup", "export", "import", "restore", "迁移" }),
                (AboutSection,   new[] { "关于", "版本", "更新", "about", "version", "update", "swellssh" }),
            };

            int visibleCount = 0;
            foreach (var (section, keywords) in sections)
            {
                bool matches = keywords.Any(k => k.Contains(query) || query.Contains(k));
                section.Visibility = matches ? Visibility.Visible : Visibility.Collapsed;
                if (matches) visibleCount++;
            }

            NoResultsText.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── 检查客户端更新 ──────────────────────────────────────────────────

        private async void CheckAppUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            CheckAppUpdateButton.IsEnabled = false;
            CheckAppUpdateButton.Content = "正在检查…";

            try
            {
                var updater = new AppUpdateService();
                var info = await updater.CheckAsync(CancellationToken.None);

                if (info == null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = "当前已是最新版本，或网络无法访问 GitHub。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                    return;
                }

                var confirmDialog = new ContentDialog
                {
                    Title = "发现新版本",
                    Content = $"是否将 Swell SSH 更新至 {info.TagName}？\n\n下载完成后应用将自动重启以完成更新。",
                    PrimaryButtonText = "确认更新",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                // 进度 UI
                var progressText = new TextBlock { Text = "准备下载…", Margin = new Thickness(0, 0, 0, 10) };
                var progressBar  = new ProgressBar { IsIndeterminate = true, Width = 320 };
                var stack        = new StackPanel { Children = { progressText, progressBar } };

                var progressDialog = new ContentDialog
                {
                    Title    = "正在更新 Swell SSH",
                    Content  = stack,
                    XamlRoot = this.XamlRoot
                };

                var progress = new Progress<ProgressDialogUpdate>(update =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        progressText.Text = update.StatusText;
                        if (update.PercentComplete.HasValue)
                        {
                            progressBar.IsIndeterminate = false;
                            progressBar.Value = update.PercentComplete.Value;
                        }
                    });
                });

                var updateTask = updater.DownloadVerifyAndExtractAsync(info, progress, CancellationToken.None);
                _ = progressDialog.ShowAsync();

                try
                {
                    var staging = await updateTask;
                    progressDialog.Hide();
                    await Task.Delay(50);

                    var readyDialog = new ContentDialog
                    {
                        Title             = "更新准备就绪",
                        Content           = $"新版本 {info.TagName} 下载并校验成功！\n\n点击\"立即重启\"后，应用将关闭并完成文件覆盖更新。",
                        PrimaryButtonText = "立即重启",
                        CloseButtonText   = "稍后",
                        DefaultButton     = ContentDialogButton.Primary,
                        XamlRoot          = this.XamlRoot
                    };

                    if (await readyDialog.ShowAsync() == ContentDialogResult.Primary)
                    {
                        updater.LaunchUpdater(staging);
                        Application.Current.Exit();
                    }
                }
                catch (Exception ex)
                {
                    progressDialog.Hide();
                    await Task.Delay(50);

                    var errDialog = new ContentDialog
                    {
                        Title           = "更新失败",
                        Content         = ex.Message,
                        CloseButtonText = "确定",
                        XamlRoot        = this.XamlRoot
                    };
                    await errDialog.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title           = "检查更新失败",
                    Content         = $"错误信息：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot        = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                CheckAppUpdateButton.IsEnabled = true;
                CheckAppUpdateButton.Content   = "检查更新";
            }
        }

        private async void ResetOnboardingButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings.OnboardingCompleted = false;
                await _storage.SaveSettingsAsync(_settings);
                
                if (MainWindow.Instance != null)
                {
                    MainWindow.Instance.ShowOnboarding();
                }
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "Reset Error",
                    Content = ex.ToString(),
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        // ── 数据备份与恢复 ──────────────────────────────────────────────────

        private async void ExportBackupButton_Click(object sender, RoutedEventArgs e)
        {
            ExportBackupButton.IsEnabled = false;
            ExportBackupButton.Content = "正在导出…";
            try
            {
                var result = await _backup.ExportAsync(MainWindow.Instance!);
                if (result.IsCancelled) return;

                var dialog = new ContentDialog
                {
                    Title           = result.IsSuccess ? "备份导出成功" : "备份导出失败",
                    Content         = result.Message,
                    CloseButtonText = "确定",
                    XamlRoot        = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                ExportBackupButton.IsEnabled = true;
                ExportBackupButton.Content   = "导出备份";
            }
        }

        private async void ImportBackupButton_Click(object sender, RoutedEventArgs e)
        {
            // Confirm before overwriting
            var confirmDialog = new ContentDialog
            {
                Title             = "确认恢复备份",
                Content           = "恢复备份将覆盖当前全部连接配置、指令片段和终端设置，此操作不可撤销。\n\n是否继续？",
                PrimaryButtonText = "确认恢复",
                CloseButtonText   = "取消",
                DefaultButton     = ContentDialogButton.Close,
                XamlRoot          = this.XamlRoot
            };
            if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            ImportBackupButton.IsEnabled = false;
            ImportBackupButton.Content   = "正在恢复…";
            try
            {
                var result = await _backup.ImportAsync(MainWindow.Instance!);
                if (result.IsCancelled) return;

                if (result.IsSuccess && result.NeedsReload)
                {
                    // Reload in-memory settings and notify
                    _settings = await _storage.LoadSettingsAsync();
                    _isLoading = true;
                    ApplyToUi(_settings);
                    await Task.Delay(50);
                    _isLoading = false;
                    TerminalSettings.NotifyGlobalSettingsChanged(_settings);

                    // Ask MainPage to reload connection list
                    MainWindow.Instance?.RequestConnectionsReload();
                }

                var dialog = new ContentDialog
                {
                    Title           = result.IsSuccess ? "备份恢复成功" : "备份恢复失败",
                    Content         = result.Message,
                    CloseButtonText = "确定",
                    XamlRoot        = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                ImportBackupButton.IsEnabled = true;
                ImportBackupButton.Content   = "恢复备份";
            }
        }
    }
}
