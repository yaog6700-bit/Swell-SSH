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
        private AISettings _aiSettings = new();
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
            _aiSettings = await _storage.LoadAISettingsAsync();
            
            _appliedTheme = _settings.AppTheme == "Light" ? ElementTheme.Light : ElementTheme.Dark;
            _appliedBackdropType = _settings.BackdropType;

            ApplyToUi(_settings);
            
            // AI Settings to UI
            AiContextLineBox.Value = _aiSettings.ContextLineCount;

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

        // ── AI 助手设置 ──────────────────────────────────────────────────────

        private async void AiContextLineBox_ValueChanged(Microsoft.UI.Xaml.Controls.NumberBox sender, Microsoft.UI.Xaml.Controls.NumberBoxValueChangedEventArgs args)
        {
            if (_isLoading) return;
            _aiSettings.ContextLineCount = (int)args.NewValue;
            await _storage.SaveAISettingsAsync(_aiSettings);
        }

        private StackPanel CreateField(string title, UIElement control, string description = "")
        {
            var sp = new StackPanel { Spacing = 6 };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14 });
            sp.Children.Add(control);
            if (!string.IsNullOrEmpty(description))
            {
                sp.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap });
            }
            return sp;
        }

        private async void ManageAiEnvs_Click(object sender, RoutedEventArgs e)
        {
            if (_aiSettings.Environments.Count == 0) _aiSettings.Environments.Add(new ApiEnvironment { Id = Guid.NewGuid().ToString(), Name = "OpenAI" });
            
            int currentIndex = _aiSettings.Environments.FindIndex(env => env.Id == _aiSettings.CurrentEnvironmentId);
            if (currentIndex < 0) currentIndex = 0;

            var providerPresets = new[] {
                new { Name = "OpenAI", Url = "https://api.openai.com/v1", Model = "gpt-4o-mini" },
                new { Name = "DeepSeek", Url = "https://api.deepseek.com/v1", Model = "deepseek-chat" },
                new { Name = "Qwen", Url = "https://dashscope.aliyuncs.com/compatible-mode/v1", Model = "qwen-plus" },
                new { Name = "Kimi (Moonshot)", Url = "https://api.moonshot.cn/v1", Model = "moonshot-v1-8k" },
                new { Name = "GLM (Zhipu)", Url = "https://open.bigmodel.cn/api/paas/v4", Model = "glm-4-flash" },
                new { Name = "Ollama", Url = "http://localhost:11434/v1", Model = "llama3" },
                new { Name = "Custom", Url = "", Model = "" }
            };
            
            var envSelector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var addBtn = new Button { Content = new FontIcon { Glyph = "\uE710", FontSize = 14 }, Padding = new Thickness(8,6,8,6) };
            ToolTipService.SetToolTip(addBtn, "新增环境");
            var delBtn = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, Padding = new Thickness(8,6,8,6) };
            ToolTipService.SetToolTip(delBtn, "删除当前环境");
            
            var headerGrid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0,0,0,16) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(envSelector, 0); Grid.SetColumn(addBtn, 1); Grid.SetColumn(delBtn, 2);
            headerGrid.Children.Add(envSelector); headerGrid.Children.Add(addBtn); headerGrid.Children.Add(delBtn);

            var nameBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var apiTypeCombo = new ComboBox { ItemsSource = providerPresets.Select(p => p.Name).ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
            var urlBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var keyBox = new PasswordBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var modelBox = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            var reasoningBox = new ComboBox { ItemsSource = new[] { "默认 (不附加)", "OpenAI reasoning_effort" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            var contextTokensBox = new NumberBox { Minimum = 1000, Maximum = 1000000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, HorizontalAlignment = HorizontalAlignment.Stretch };
            var retriesBox = new NumberBox { Minimum = 0, Maximum = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden, HorizontalAlignment = HorizontalAlignment.Stretch };
            var userAgentBox = new TextBox { PlaceholderText = "留空使用浏览器默认 UA", HorizontalAlignment = HorizontalAlignment.Stretch };
            var proxyBox = new TextBox { PlaceholderText = "http://127.0.0.1:7890", HorizontalAlignment = HorizontalAlignment.Stretch };

            Action saveForm = () => {
                if (currentIndex >= 0 && currentIndex < _aiSettings.Environments.Count) {
                    var env = _aiSettings.Environments[currentIndex];
                    env.Name = nameBox.Text;
                    env.ApiBaseUrl = urlBox.Text;
                    if (keyBox.Password != "********" && keyBox.Password != "") env.EncryptedApiKey = ConnectionStorage.EncryptSecret(keyBox.Password);
                    env.CurrentModel = modelBox.Text;
                    env.ReasoningEffort = reasoningBox.SelectedIndex == 1 ? "high" : "";
                    env.ContextTokens = (int)contextTokensBox.Value;
                    env.MaxRetries = (int)retriesBox.Value;
                    env.CustomUserAgent = userAgentBox.Text;
                    env.HttpProxy = proxyBox.Text;
                }
            };

            bool isUpdating = false;

            Action loadForm = () => {
                isUpdating = true;
                if (currentIndex >= 0 && currentIndex < _aiSettings.Environments.Count) {
                    var env = _aiSettings.Environments[currentIndex];
                    nameBox.Text = string.IsNullOrEmpty(env.Name) ? "OpenAI" : env.Name;
                    urlBox.Text = env.ApiBaseUrl ?? "";
                    keyBox.Password = string.IsNullOrEmpty(env.EncryptedApiKey) ? "" : "********";
                    modelBox.Text = env.CurrentModel ?? "";
                    reasoningBox.SelectedIndex = string.IsNullOrEmpty(env.ReasoningEffort) ? 0 : 1;
                    contextTokensBox.Value = env.ContextTokens > 0 ? env.ContextTokens : 128000;
                    retriesBox.Value = env.MaxRetries;
                    userAgentBox.Text = env.CustomUserAgent ?? "";
                    proxyBox.Text = env.HttpProxy ?? "";
                    
                    var matchedIdx = Array.FindIndex(providerPresets, p => p.Url == env.ApiBaseUrl);
                    apiTypeCombo.SelectedIndex = matchedIdx >= 0 ? matchedIdx : 6;
                }
                isUpdating = false;
            };

            Action updateSelector = () => {
                isUpdating = true;
                envSelector.ItemsSource = _aiSettings.Environments.Select(e => e.Name ?? "新环境").ToList();
                envSelector.SelectedIndex = currentIndex;
                delBtn.IsEnabled = _aiSettings.Environments.Count > 1;
                isUpdating = false;
            };

            envSelector.SelectionChanged += (s, ev) => {
                if (isUpdating || envSelector.SelectedIndex < 0) return;
                saveForm();
                currentIndex = envSelector.SelectedIndex;
                loadForm();
            };

            nameBox.TextChanged += (s, ev) => {
                if (isUpdating) return;
                _aiSettings.Environments[currentIndex].Name = nameBox.Text;
                isUpdating = true;
                envSelector.ItemsSource = _aiSettings.Environments.Select(e => e.Name ?? "新环境").ToList();
                envSelector.SelectedIndex = currentIndex;
                isUpdating = false;
            };

            apiTypeCombo.SelectionChanged += (s, ev) => {
                if (isUpdating || apiTypeCombo.SelectedIndex < 0 || apiTypeCombo.SelectedIndex >= providerPresets.Length - 1) return;
                var preset = providerPresets[apiTypeCombo.SelectedIndex];
                urlBox.Text = preset.Url;
                modelBox.Text = preset.Model;
                if (nameBox.Text == "OpenAI" || nameBox.Text == "Custom" || nameBox.Text == "") nameBox.Text = preset.Name;
            };

            addBtn.Click += (s, ev) => {
                saveForm();
                _aiSettings.Environments.Add(new ApiEnvironment { Id = Guid.NewGuid().ToString(), Name = "新环境", ApiBaseUrl = "https://api.openai.com/v1", CurrentModel = "gpt-4o-mini", ContextTokens = 128000, MaxRetries = 3 });
                currentIndex = _aiSettings.Environments.Count - 1;
                updateSelector();
                loadForm();
            };

            delBtn.Click += (s, ev) => {
                if (_aiSettings.Environments.Count <= 1) return;
                _aiSettings.Environments.RemoveAt(currentIndex);
                currentIndex = Math.Max(0, currentIndex - 1);
                updateSelector();
                loadForm();
            };

            // Layout
            var grid = new Grid { ColumnSpacing = 24, RowSpacing = 20, MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Stretch };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i=0; i<6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var fieldName = CreateField("环境名称", nameBox);
            var fieldPreset = CreateField("供应商预设", apiTypeCombo);
            Grid.SetRow(fieldName, 0); Grid.SetColumn(fieldName, 0);
            Grid.SetRow(fieldPreset, 0); Grid.SetColumn(fieldPreset, 1);
            grid.Children.Add(fieldName); grid.Children.Add(fieldPreset);

            var fieldUrl = CreateField("API 地址", urlBox, "兼容 OpenAI 协议的 API 端点\n(OpenAI/DeepSeek/Qwen/Kimi/ollama 等)");
            Grid.SetRow(fieldUrl, 1); Grid.SetColumn(fieldUrl, 0);
            grid.Children.Add(fieldUrl);

            var fieldKey = CreateField("API Key", keyBox);
            Grid.SetRow(fieldKey, 2); Grid.SetColumn(fieldKey, 0);
            grid.Children.Add(fieldKey);

            var modelHeader = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, new ColumnDefinition { Width = GridLength.Auto } } };
            var refreshBtn = new Button { Content = "刷新模型", Padding = new Thickness(8,4,8,4), FontSize = 12, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)), BorderThickness = new Thickness(0) };
            Grid.SetColumn(refreshBtn, 1);
            modelHeader.Children.Add(new TextBlock { Text = "模型列表", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            modelHeader.Children.Add(refreshBtn);
            
            refreshBtn.Click += async (s, ev) =>
            {
                var tempEnv = new ApiEnvironment {
                    ApiBaseUrl = urlBox.Text,
                    EncryptedApiKey = keyBox.Password == "********" ? _aiSettings.Environments[currentIndex].EncryptedApiKey : ConnectionStorage.EncryptSecret(keyBox.Password),
                    HttpProxy = proxyBox.Text,
                    CustomUserAgent = userAgentBox.Text
                };
                refreshBtn.Content = "正在获取...";
                refreshBtn.IsEnabled = false;
                var models = await new AIAssistantService().GetAvailableModelsAsync(tempEnv);
                refreshBtn.Content = "刷新模型";
                refreshBtn.IsEnabled = true;
                
                if (models.Count > 0)
                {
                    var flyout = new MenuFlyout();
                    foreach (var m in models)
                    {
                        var item = new MenuFlyoutItem { Text = m };
                        item.Click += (s2, ev2) => modelBox.Text = m;
                        flyout.Items.Add(item);
                    }
                    flyout.ShowAt(refreshBtn);
                }
                else
                {
                    var flyout = new Flyout { Content = new TextBlock { Text = "未获取到模型列表，请检查网络或密钥" } };
                    flyout.ShowAt(refreshBtn);
                }
            };
            
            var modelSp = new StackPanel { Spacing = 6 };
            modelSp.Children.Add(modelHeader);
            modelSp.Children.Add(modelBox);
            Grid.SetRow(modelSp, 3); Grid.SetColumn(modelSp, 0);
            grid.Children.Add(modelSp);

            var fieldReasoning = CreateField("思考协议", reasoningBox, "无法确认模型协议时保持默认，后端不会附加思考字段。");
            Grid.SetRow(fieldReasoning, 4); Grid.SetColumn(fieldReasoning, 0);
            grid.Children.Add(fieldReasoning);

            var fieldTokens = CreateField("上下文窗口 (tokens)", contextTokensBox, "所选模型的最大上下文长度，用于在 AI tab 显示用量进度。常见值: 128000, 65536 等。");
            var fieldRetries = CreateField("最大重试次数", retriesBox, "遇到限流或超时时的重试次数，默认 3。");
            Grid.SetRow(fieldTokens, 5); Grid.SetColumn(fieldTokens, 0);
            Grid.SetRow(fieldRetries, 5); Grid.SetColumn(fieldRetries, 1);
            grid.Children.Add(fieldTokens); grid.Children.Add(fieldRetries);

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var fieldUa = CreateField("自定义 User-Agent", userAgentBox, "留空使用浏览器默认风格，可填特定值适配代理网关。");
            var fieldProxy = CreateField("HTTP 代理", proxyBox, "留空直连，此环境所有请求将通过该代理访问。");
            Grid.SetRow(fieldUa, 6); Grid.SetColumn(fieldUa, 0);
            Grid.SetRow(fieldProxy, 6); Grid.SetColumn(fieldProxy, 1);
            grid.Children.Add(fieldUa); grid.Children.Add(fieldProxy);

            var rootStack = new StackPanel { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            rootStack.Children.Add(new TextBlock { Text = "选择环境", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0,0,0,8) });
            rootStack.Children.Add(headerGrid);
            rootStack.Children.Add(new Border { Height = 1, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], Margin = new Thickness(0,0,0,16) });
            rootStack.Children.Add(grid);

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "管理 API 环境",
                Content = new ScrollViewer { Content = rootStack, Padding = new Thickness(0,0,16,0) },
                PrimaryButtonText = "保存全部环境",
                SecondaryButtonText = "测试连通性",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                MaxWidth = 800
            };

            dialog.SecondaryButtonClick += async (s, args) =>
            {
                var deferral = args.GetDeferral();
                args.Cancel = true;
                
                var tempEnv = new ApiEnvironment {
                    ApiBaseUrl = urlBox.Text,
                    EncryptedApiKey = keyBox.Password == "********" ? _aiSettings.Environments[currentIndex].EncryptedApiKey : ConnectionStorage.EncryptSecret(keyBox.Password),
                    HttpProxy = proxyBox.Text,
                    CustomUserAgent = userAgentBox.Text
                };
                
                var originalText = dialog.SecondaryButtonText;
                dialog.SecondaryButtonText = "正在测试...";
                dialog.IsSecondaryButtonEnabled = false;

                bool ok = await new AIAssistantService().TestConnectionAsync(tempEnv);
                
                dialog.SecondaryButtonText = ok ? "✅ 测试成功" : "❌ 测试失败";
                await Task.Delay(2000);
                
                dialog.SecondaryButtonText = originalText;
                deferral.Complete();
            };

            updateSelector();
            loadForm();

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                saveForm();
                if (currentIndex >= 0 && currentIndex < _aiSettings.Environments.Count)
                {
                    _aiSettings.CurrentEnvironmentId = _aiSettings.Environments[currentIndex].Id;
                }
                else if (_aiSettings.Environments.Count > 0)
                {
                    _aiSettings.CurrentEnvironmentId = _aiSettings.Environments[0].Id;
                }
                
                await _storage.SaveAISettingsAsync(_aiSettings);
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
                (AiSection,      new[] { "ai", "助手", "assistant", "api", "key", "openai", "gpt", "模型", "环境" }),
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
