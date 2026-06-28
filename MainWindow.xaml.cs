using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using WinUIEx;
using SwellSSH.Pages;
using SwellSSH.Services;
using SwellSSH.Models;
using Windows.Graphics;

namespace SwellSSH
{
    public sealed partial class MainWindow : Window
    {
        public static MainWindow? Instance { get; private set; }

        private readonly WindowManager _windowManager;
        private bool _isHiddenToTray;
        private ElementTheme _currentTheme = ElementTheme.Default;

        private readonly ConnectionStorage _storage = new();
        private readonly List<Windows.Foundation.Rect> _titleBarInteractiveRects = new();

        // BUG-04: 缓存 MinimizeOnClose 设置，避免 关闭事件里异步读取磁盘
        private bool _minimizeOnClose = true; // 默认安全化到托盘

        public MainWindow()
        {
            Instance = this;
            this.InitializeComponent();

            // WinUIEx WindowManager — handles tray, size, persistence
            _windowManager = WindowManager.Get(this);
            _windowManager.Width    = 1400;
            _windowManager.Height   = 820;
            _windowManager.MinWidth  = 1100;
            _windowManager.MinHeight = 600;
            this.CenterOnScreen();

            // Custom title bar
            ExtendsContentIntoTitleBar = true;
            ConfigureTitleBarHitTesting();

            this.Title = "SwellSSH";
            this.AppWindow.Title = "SwellSSH";
            SetIconSafe();

            // Apply saved theme
            _ = ApplySavedSettingsAsync();

            // Tray setup
            ConfigureTray();

            // Navigate to MainPage immediately
            ContentFrame.Navigate(typeof(MainPage));

            // Sidebar toggle is disabled until an SSH tab is selected
            GlobalToggleSidebarButton.IsEnabled = false;
            GlobalToggleSidebarButton.Opacity = 0.4;

            // Pane open/close → sync connection list data
            MainNav.PaneOpening += (_, _) =>
            {
                SyncPaneConnectionList();
                UpdateFloatingThemeToggleVisibility(isOpen: true);
            };
            MainNav.PaneClosing += (_, _) => UpdateFloatingThemeToggleVisibility(isOpen: false);

            // Navigation
            MainNav.SelectionChanged += MainNav_SelectionChanged;
            Activated += (_, args) =>
            {
                if (args.WindowActivationState != WindowActivationState.Deactivated &&
                    ContentFrame.Content is MainPage mainPage)
                    mainPage.RestoreTerminalFocus();
            };

            // Background update check
            _ = CheckForUpdatesAsync();
        }

        private void ConfigureTitleBarHitTesting()
        {
            ContentWrapper.SizeChanged += (_, _) => UpdateTitleBarDragRegions();
            DispatcherQueue.TryEnqueue(UpdateTitleBarDragRegions);
        }

        public void SetTitleBarPassthroughRects(IEnumerable<Windows.Foundation.Rect> rects)
        {
            _titleBarInteractiveRects.Clear();
            _titleBarInteractiveRects.AddRange(rects);
            UpdateTitleBarDragRegions();
        }

        private void UpdateTitleBarDragRegions()
        {
            if (ContentWrapper.XamlRoot == null || ContentWrapper.ActualWidth <= 0)
                return;

            var scale = ContentWrapper.XamlRoot.RasterizationScale;
            const double titleBarHeight = 48;
            const double leftReserved = 88;
            const double rightReserved = 140;

            var dragSegments = new List<Windows.Foundation.Rect>
            {
                new(leftReserved, 0, Math.Max(0, ContentWrapper.ActualWidth - leftReserved - rightReserved), titleBarHeight)
            };

            foreach (var interactiveRect in _titleBarInteractiveRects)
                SubtractInteractiveRect(dragSegments, interactiveRect);

            AppWindow.TitleBar.SetDragRectangles(
                dragSegments
                    .Where(rect => rect.Width > 0 && rect.Height > 0)
                    .Select(rect => ToPhysicalRect(rect, scale))
                    .ToArray());
        }

        private static void SubtractInteractiveRect(
            List<Windows.Foundation.Rect> dragSegments,
            Windows.Foundation.Rect interactiveRect)
        {
            if (interactiveRect.Width <= 0 || interactiveRect.Height <= 0)
                return;

            for (int i = dragSegments.Count - 1; i >= 0; i--)
            {
                var segment = dragSegments[i];
                var overlapLeft = Math.Max(segment.Left, interactiveRect.Left);
                var overlapRight = Math.Min(segment.Right, interactiveRect.Right);

                if (overlapRight <= overlapLeft)
                    continue;

                dragSegments.RemoveAt(i);

                if (overlapLeft > segment.Left)
                    dragSegments.Add(new Windows.Foundation.Rect(
                        segment.Left,
                        segment.Top,
                        overlapLeft - segment.Left,
                        segment.Height));

                if (overlapRight < segment.Right)
                    dragSegments.Add(new Windows.Foundation.Rect(
                        overlapRight,
                        segment.Top,
                        segment.Right - overlapRight,
                        segment.Height));
            }
        }

        private static RectInt32 ToPhysicalRect(Windows.Foundation.Rect rect, double scale)
        {
            return new RectInt32(
                (int)Math.Round(rect.X * scale),
                (int)Math.Round(rect.Y * scale),
                (int)Math.Round(rect.Width * scale),
                (int)Math.Round(rect.Height * scale));
        }

        public FrameworkElement TitleBarCoordinateRoot => ContentWrapper;

        private async Task CheckForUpdatesAsync()
        {
            try
            {
                var updater = new SwellSSH.Services.AppUpdateService();
                var info = await updater.CheckAsync(System.Threading.CancellationToken.None);
                if (info != null && UpdateBadge != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateBadge.Visibility = Visibility.Visible;
                    });
                }
            }
            catch
            {
                // Ignore background check errors
            }
        }

        // ── Settings persistence ─────────────────────────────────────────────

        public async Task ApplySavedSettingsAsync()
        {
            var settings = await _storage.LoadSettingsAsync();
            
            if (!settings.OnboardingCompleted && AppOnboardingHost.Visibility != Visibility.Visible)
            {
                ShowOnboarding();
            }

            var theme = settings.AppTheme == "Light"
                ? ElementTheme.Light
                : settings.AppTheme == "System" 
                    ? ElementTheme.Default 
                    : ElementTheme.Dark;

            SetTheme(theme);

            this.SystemBackdrop = settings.BackdropType switch
            {
                "Acrylic" => new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop(),
                "None"    => null,
                _         => new Microsoft.UI.Xaml.Media.MicaBackdrop()
            };

            // BUG-04: 缓存 MinimizeOnClose，让 ConfigureTray 可以同步读取
            _minimizeOnClose = settings.MinimizeOnClose;

            // 设置页保存后通知主窗口同步
            TerminalSettings.GlobalSettingsChanged += OnGlobalSettingsChanged;
        }

        // BUG-04: 设置页保存时同步更新缓存值
        private void OnGlobalSettingsChanged(TerminalSettings settings)
        {
            _minimizeOnClose = settings.MinimizeOnClose;
        }

        // ── Navigation ───────────────────────────────────────────────────────────────────────────────

        private void MainNav_SelectionChanged(NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is not NavigationViewItem item) return;

            string? tag = item.Tag?.ToString();

            if (tag == "settings")
            {
                OpenSettingsTabFromNav();
                return;
            }
        }

        public void HideOnboarding()
        {
            AppOnboardingHost.Visibility = Visibility.Collapsed;
            AppOnboardingHost.Content = null;
        }

        public void ShowOnboarding()
        {
            try 
            {
                AppOnboardingHost.Content = new Pages.OnboardingControl();
                AppOnboardingHost.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "ShowOnboarding Error",
                    Content = ex.ToString(),
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                _ = dialog.ShowAsync();
            }
        }

        // ── Theme System (ported from AnywhereWinUI) ─────────────────────────

        public event Action<ElementTheme>? ThemeChanged;

        public void SetTheme(ElementTheme theme)
        {
            _currentTheme = theme;
            if (this.Content is FrameworkElement root)
                root.RequestedTheme = theme;
            UpdateTitleBarButtonColors();
            UpdateThemeToggleIcons();
            ThemeChanged?.Invoke(theme);
        }

        private void UpdateTitleBarButtonColors()
        {
            var titleBar = AppWindow.TitleBar;
            var isDark = GetActualTheme() == ElementTheme.Dark;
            var foreground = isDark ? Colors.White : Colors.Black;
            var hoverBackground = isDark
                ? Windows.UI.Color.FromArgb(40, 255, 255, 255)
                : Windows.UI.Color.FromArgb(24, 0, 0, 0);
            var pressedBackground = isDark
                ? Windows.UI.Color.FromArgb(64, 255, 255, 255)
                : Windows.UI.Color.FromArgb(40, 0, 0, 0);

            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonInactiveForegroundColor = isDark
                ? Windows.UI.Color.FromArgb(160, 255, 255, 255)
                : Windows.UI.Color.FromArgb(160, 0, 0, 0);
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonHoverBackgroundColor = hoverBackground;
            titleBar.ButtonPressedBackgroundColor = pressedBackground;
        }

        private ElementTheme GetActualTheme()
        {
            if (_currentTheme != ElementTheme.Default) return _currentTheme;
            return Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? ElementTheme.Dark : ElementTheme.Light;
        }



        private async Task SaveThemeAsync(ElementTheme theme)
        {
            var settings = await _storage.LoadSettingsAsync();
            settings.AppTheme = theme == ElementTheme.Light ? "Light" : "Dark";
            settings.ColorScheme = theme == ElementTheme.Light ? "Default Light" : "One Dark";
            await _storage.SaveSettingsAsync(settings);
            TerminalSettings.NotifyGlobalSettingsChanged(settings);
        }



        // ── Pane connection list sync ────────────────────────────────────────

        private void SyncPaneConnectionList()
        {
            if (ContentFrame.Content is MainPage mainPage)
            {
                PaneConnectionList.ItemsSource = mainPage.FlatSidebarItems;
                PaneConnectionList.ItemTemplateSelector = mainPage.GetSidebarTemplateSelector();
            }
        }

        // ── Pane event handlers (delegated to MainPage) ──────────────────────

        private void PaneQuickConnectBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                if (ContentFrame.Content is MainPage mainPage)
                    mainPage.DoQuickConnectFromPane(PaneQuickConnectBox);
            }
        }

        private void PaneQuickConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is MainPage mainPage)
                mainPage.DoQuickConnectFromPane(PaneQuickConnectBox);
        }

        private void PaneConnectionList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (PaneConnectionList.SelectedItem is ConnectionItemViewModel vm)
            {
                MainNav.IsPaneOpen = false;
                if (ContentFrame.Content is MainPage mainPage)
                    mainPage.ConnectToProfile(vm.Profile);
            }
        }

        private async void PaneAddConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            await AddConnectionFromNavAsync();
        }

        private async void PaneEditConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (PaneConnectionList.SelectedItem is ConnectionItemViewModel vm)
            {
                if (ContentFrame.Content is MainPage mainPage)
                    await mainPage.EditProfileFromPane(vm);
            }
        }

        private async void PaneDeleteConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (PaneConnectionList.SelectedItem is ConnectionItemViewModel vm)
            {
                if (ContentFrame.Content is MainPage mainPage)
                    await mainPage.DeleteProfileFromPane(vm);
            }
        }

        private bool _isThemeTransitioning = false;

        private void GlobalToggleSidebarButton_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is Pages.MainPage mainPage)
            {
                mainPage.ToggleSidebar();
            }
        }

        /// <summary>
        /// Called by MainPage when the active tab changes.
        /// The sidebar toggle button is enabled only when an SSH terminal tab is selected.
        /// </summary>
        public void SetSidebarToggleEnabled(bool enabled)
        {
            GlobalToggleSidebarButton.IsEnabled = enabled;
            GlobalToggleSidebarButton.Opacity = enabled ? 1.0 : 0.4;
        }

        public void OpenConnectionsPane()
        {
            SyncPaneConnectionList();
            MainNav.IsPaneOpen = true;
        }

        public async Task AddConnectionFromNavAsync()
        {
            if (ContentFrame.Content is MainPage mainPage)
            {
                await mainPage.AddConnectionFromPane();
                SyncPaneConnectionList();
                MainNav.IsPaneOpen = true;
            }
        }

        private async void FloatingThemeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleThemeAsync(FloatingThemeToggleButton);
        }

        private void SettingsNavItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            OpenSettingsTabFromNav();
        }

        private void OpenSettingsTabFromNav()
        {
            if (ContentFrame.Content is MainPage mainPage)
            {
                mainPage.OpenSettingsTab();
            }

            MainNav.SelectedItem = null;
            MainNav.IsPaneOpen = false;
        }

        private async void SftpNavItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            MainNav.SelectedItem = null;
            MainNav.IsPaneOpen = false;

            if (ContentFrame.Content is MainPage mainPage)
            {
                await mainPage.OpenSftpTabAsync();
            }
        }

        public async Task ToggleThemeAsync(UIElement? sourceElement = null)
        {
            if (_isThemeTransitioning) return;
            _isThemeTransitioning = true;

            if (sourceElement == FloatingThemeToggleButton)
                _ = AnimateThemeIconAsync(FloatingThemeToggleIcon);

            var actualTheme = GetActualTheme();
            var newTheme = actualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;

            if (this.Content is FrameworkElement rootElement && ThemeTransitionOverlay != null)
            {
                var renderTargetBitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                try
                {
                    await renderTargetBitmap.RenderAsync(rootElement);
                    ThemeTransitionImage.Source = renderTargetBitmap;
                    
                    if (actualTheme == ElementTheme.Dark)
                        ThemeTransitionBackground.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                    else
                        ThemeTransitionBackground.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 243, 243));
                        
                    ThemeTransitionOverlay.Visibility = Visibility.Visible;
                }
                catch
                {
                    SetTheme(newTheme);
                    _ = SaveThemeAsync(newTheme);
                    _isThemeTransitioning = false;
                    return;
                }

                Windows.Foundation.Point buttonCenter = new Windows.Foundation.Point(rootElement.ActualWidth - 40, 40);
                try
                {
                    if (sourceElement != null)
                    {
                        var buttonTransform = sourceElement.TransformToVisual(rootElement);
                        buttonCenter = buttonTransform.TransformPoint(new Windows.Foundation.Point(sourceElement.ActualSize.X / 2, sourceElement.ActualSize.Y / 2));
                    }
                }
                catch { }

                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ContentWrapper);
                var compositor = visual.Compositor;
                
                var ellipseGeometry = compositor.CreateEllipseGeometry();
                ellipseGeometry.Center = new System.Numerics.Vector2((float)buttonCenter.X, (float)buttonCenter.Y);
                ellipseGeometry.Radius = new System.Numerics.Vector2(0, 0);

                visual.Clip = compositor.CreateGeometricClip(ellipseGeometry);

                if (newTheme == ElementTheme.Dark)
                    ContentWrapper.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
                else
                    ContentWrapper.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 243, 243));

                SetTheme(newTheme);
                _ = SaveThemeAsync(newTheme);

                await Task.Delay(30);

                float width = (float)rootElement.ActualWidth;
                float height = (float)rootElement.ActualHeight;

                float maxDistanceX = Math.Max((float)buttonCenter.X, width - (float)buttonCenter.X);
                float maxDistanceY = Math.Max((float)buttonCenter.Y, height - (float)buttonCenter.Y);
                float maxRadius = (float)Math.Sqrt(maxDistanceX * maxDistanceX + maxDistanceY * maxDistanceY);

                var cubicBezierEasing = compositor.CreateCubicBezierEasingFunction(
                    new System.Numerics.Vector2(0.25f, 0.85f), 
                    new System.Numerics.Vector2(0.15f, 1.0f)
                );

                var radiusAnimationX = compositor.CreateScalarKeyFrameAnimation();
                radiusAnimationX.InsertKeyFrame(1f, maxRadius, cubicBezierEasing);
                radiusAnimationX.Duration = TimeSpan.FromMilliseconds(1300);

                var radiusAnimationY = compositor.CreateScalarKeyFrameAnimation();
                radiusAnimationY.InsertKeyFrame(1f, maxRadius, cubicBezierEasing);
                radiusAnimationY.Duration = TimeSpan.FromMilliseconds(1300);

                var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
                ellipseGeometry.StartAnimation("Radius.X", radiusAnimationX);
                ellipseGeometry.StartAnimation("Radius.Y", radiusAnimationY);
                
                batch.Completed += (s, ev) =>
                {
                    visual.Clip = null;
                    ContentWrapper.Background = null;
                    ThemeTransitionOverlay.Visibility = Visibility.Collapsed;
                    ThemeTransitionImage.Source = null;
                    _isThemeTransitioning = false;
                };
                batch.End();
            }
            else
            {
                SetTheme(newTheme);
                _ = SaveThemeAsync(newTheme);
                _isThemeTransitioning = false;
            }
        }

        // ── Nav icon hover animations (ported from AnywhereWinUI) ────────────

        private void UpdateThemeToggleIcons()
        {
            var actualTheme = GetActualTheme();
            var glyph = actualTheme == ElementTheme.Dark ? "\uE706" : "\uE708";
            var tooltip = actualTheme == ElementTheme.Dark ? "切换至浅色模式" : "切换至深色模式";

            if (FloatingThemeToggleIcon != null)
                FloatingThemeToggleIcon.Glyph = glyph;
            if (FloatingThemeToggleButton != null)
                ToolTipService.SetToolTip(FloatingThemeToggleButton, tooltip);
        }

        private void UpdateFloatingThemeToggleVisibility(bool isOpen, bool animate = true)
        {
            if (FloatingThemeToggleButton == null) return;

            if (animate)
            {
                FadeVisual(FloatingThemeToggleButton, isOpen ? 1f : 0f, 220);
                return;
            }

            FloatingThemeToggleButton.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(FloatingThemeToggleButton);
            visual.Opacity = isOpen ? 1f : 0f;
        }

        private static async Task AnimateThemeIconAsync(FontIcon? icon)
        {
            if (icon == null) return;

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;

            visual.StopAnimation("Scale.X");
            visual.StopAnimation("Scale.Y");
            visual.StopAnimation("RotationAngleInDegrees");

            float cx = icon.ActualWidth > 0 ? (float)(icon.ActualWidth / 2) : 8f;
            float cy = icon.ActualHeight > 0 ? (float)(icon.ActualHeight / 2) : 8f;
            visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

            var easeIn = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.4f, 0f),
                new System.Numerics.Vector2(1f, 1f));

            var exitBatch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);

            var exitScaleX = compositor.CreateScalarKeyFrameAnimation();
            exitScaleX.InsertKeyFrame(0f, 1f);
            exitScaleX.InsertKeyFrame(1f, 0f, easeIn);
            exitScaleX.Duration = TimeSpan.FromMilliseconds(180);

            var exitScaleY = compositor.CreateScalarKeyFrameAnimation();
            exitScaleY.InsertKeyFrame(0f, 1f);
            exitScaleY.InsertKeyFrame(1f, 0f, easeIn);
            exitScaleY.Duration = TimeSpan.FromMilliseconds(180);

            var exitRotation = compositor.CreateScalarKeyFrameAnimation();
            exitRotation.InsertKeyFrame(0f, 0f);
            exitRotation.InsertKeyFrame(1f, 180f, easeIn);
            exitRotation.Duration = TimeSpan.FromMilliseconds(180);

            visual.StartAnimation("Scale.X", exitScaleX);
            visual.StartAnimation("Scale.Y", exitScaleY);
            visual.StartAnimation("RotationAngleInDegrees", exitRotation);

            var exitTcs = new TaskCompletionSource<bool>();
            exitBatch.Completed += (_, _) => exitTcs.TrySetResult(true);
            exitBatch.End();
            await exitTcs.Task;

            visual.RotationAngleInDegrees = 180f;
            visual.Scale = new System.Numerics.Vector3(0f, 0f, 1f);

            var easeOut = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0f, 0f),
                new System.Numerics.Vector2(0.2f, 1f));

            var enterBatch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);

            var enterScaleX = compositor.CreateScalarKeyFrameAnimation();
            enterScaleX.InsertKeyFrame(0.00f, 0f);
            enterScaleX.InsertKeyFrame(0.55f, 1.25f);
            enterScaleX.InsertKeyFrame(0.75f, 0.92f);
            enterScaleX.InsertKeyFrame(1.00f, 1f);
            enterScaleX.Duration = TimeSpan.FromMilliseconds(400);

            var enterScaleY = compositor.CreateScalarKeyFrameAnimation();
            enterScaleY.InsertKeyFrame(0.00f, 0f);
            enterScaleY.InsertKeyFrame(0.55f, 1.25f);
            enterScaleY.InsertKeyFrame(0.75f, 0.92f);
            enterScaleY.InsertKeyFrame(1.00f, 1f);
            enterScaleY.Duration = TimeSpan.FromMilliseconds(400);

            var enterRotation = compositor.CreateScalarKeyFrameAnimation();
            enterRotation.InsertKeyFrame(0f, 180f);
            enterRotation.InsertKeyFrame(1f, 360f, easeOut);
            enterRotation.Duration = TimeSpan.FromMilliseconds(400);

            visual.StartAnimation("Scale.X", enterScaleX);
            visual.StartAnimation("Scale.Y", enterScaleY);
            visual.StartAnimation("RotationAngleInDegrees", enterRotation);

            var enterTcs = new TaskCompletionSource<bool>();
            enterBatch.Completed += (_, _) => enterTcs.TrySetResult(true);
            enterBatch.End();
            await enterTcs.Task;

            visual.RotationAngleInDegrees = 0f;
            visual.Scale = new System.Numerics.Vector3(1f, 1f, 1f);
        }

        private static void FadeVisual(UIElement element, float targetOpacity, double durationMs)
        {
            if (targetOpacity > 0f)
                element.Visibility = Visibility.Visible;

            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1f, targetOpacity);
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);

            var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
            visual.StartAnimation("Opacity", animation);
            batch.Completed += (_, _) =>
            {
                if (targetOpacity == 0f)
                    element.Visibility = Visibility.Collapsed;
            };
            batch.End();
        }

        // Theme toggle: Scale pulse
        // Settings: Gear 180° spin
        private void SettingsNavItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateNavIconRotation(SettingsNavIcon, 180f);
        }
        private void SettingsNavItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            AnimateNavIconRotation(SettingsNavIcon, 0f);
        }

        private static void AnimateNavIconScale(FontIcon? icon, float targetScale, double durationMs)
        {
            if (icon == null) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;

            visual.StopAnimation("Scale.X");
            visual.StopAnimation("Scale.Y");

            float cx = icon.ActualWidth  > 0 ? (float)(icon.ActualWidth  / 2) : 8f;
            float cy = icon.ActualHeight > 0 ? (float)(icon.ActualHeight / 2) : 8f;
            visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

            var ease = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f));

            var sx = compositor.CreateScalarKeyFrameAnimation();
            sx.InsertKeyFrame(1f, targetScale, ease);
            sx.Duration = TimeSpan.FromMilliseconds(durationMs);

            var sy = compositor.CreateScalarKeyFrameAnimation();
            sy.InsertKeyFrame(1f, targetScale, ease);
            sy.Duration = TimeSpan.FromMilliseconds(durationMs);

            visual.StartAnimation("Scale.X", sx);
            visual.StartAnimation("Scale.Y", sy);
        }

        private static void AnimateNavIconRotation(FontIcon? icon, float targetAngle)
        {
            if (icon == null) return;
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(icon);
            var compositor = visual.Compositor;

            float cx = icon.ActualWidth  > 0 ? (float)(icon.ActualWidth  / 2) : 8f;
            float cy = icon.ActualHeight > 0 ? (float)(icon.ActualHeight / 2) : 8f;
            visual.CenterPoint = new System.Numerics.Vector3(cx, cy, 0f);

            var ease = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1f));

            var rot = compositor.CreateScalarKeyFrameAnimation();
            rot.InsertKeyFrame(1f, targetAngle, ease);
            rot.Duration = TimeSpan.FromMilliseconds(400);

            visual.StartAnimation("RotationAngleInDegrees", rot);
        }

        // ── Tray Integration ─────────────────────────────────────────────────

        private void ConfigureTray()
        {
            _windowManager.IsVisibleInTray = true;
            _windowManager.TrayIconSelected += (_, _) => RestoreFromTray();
            _windowManager.TrayIconContextMenu += (_, e) =>
            {
                var flyout = new MenuFlyout();

                var openItem = new MenuFlyoutItem { Text = "显示 SwellSSH" };
                openItem.Click += (_, _) => RestoreFromTray();
                flyout.Items.Add(openItem);

                flyout.Items.Add(new MenuFlyoutSeparator());

                var exitItem = new MenuFlyoutItem { Text = "退出程序" };
                exitItem.Click += (_, _) => ExitApplication();
                flyout.Items.Add(exitItem);

                e.Flyout = flyout;
            };

            this.AppWindow.Closing += (_, args) =>
            {
                // BUG-04: 根据缓存的设置决定是隐藏到托盘还是退出
                if (_minimizeOnClose)
                {
                    args.Cancel = true;
                    HideToTray();
                }
                // 否则不取消事件，窗口正常关闭
            };
        }

        private void HideToTray()
        {
            if (_isHiddenToTray) return;
            _isHiddenToTray = true;
            this.AppWindow.IsShownInSwitchers = false;
            this.AppWindow.Hide();
            ReleaseUiResources();
        }

        private void RestoreFromTray()
        {
            _isHiddenToTray = false;
            this.AppWindow.IsShownInSwitchers = true;
            this.Activate();
            this.AppWindow.Show();
            this.AppWindow.MoveInZOrderAtTop();
        }

        private void ExitApplication()
        {
            // TODO Phase 5: gracefully close all active SSH sessions
            this.AppWindow.Closing -= null;
            Application.Current.Exit();
        }

        // ── Memory Optimization ──────────────────────────────────────────────

        private static void ReleaseUiResources()
        {
            Task.Run(() =>
            {
                try
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized,
                        blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();

                    using var process = Process.GetCurrentProcess();
                    SetProcessWorkingSetSize(process.Handle, (IntPtr)(-1), (IntPtr)(-1));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Tray] ReleaseUiResources: {ex.Message}");
                }
            });
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);

        // ── Utilities ────────────────────────────────────────────────────────

        private void SetIconSafe()
        {
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "tray-icon.ico");
                if (File.Exists(path)) this.AppWindow.SetIcon(path);
            }
            catch { }
        }
    }
}
