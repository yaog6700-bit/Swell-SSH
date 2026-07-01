using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SwellSSH.Models;
using SwellSSH.Services;
using SwellSSH.Terminal;

namespace SwellSSH.Controls
{
    public sealed partial class AIAssistantPane : UserControl
    {
        public event Action? CloseRequested;
        public event Action? SettingsRequested;

        private readonly AIAssistantService _aiService = new();
        private readonly ConnectionStorage _storage = new();
        
        public ObservableCollection<MessageViewModel> Messages { get; } = new();
        private List<ChatMessage> _chatHistory = new();
        private CancellationTokenSource? _cts;

        public TerminalSession? ActiveSession { get; set; }
        private AISettings? _settings;

        public AIAssistantPane()
        {
            this.InitializeComponent();
            MessagesList.ItemsSource = Messages;
            this.Loaded += AIAssistantPane_Loaded;
        }

        private async void AIAssistantPane_Loaded(object sender, RoutedEventArgs e)
        {
            await ReloadSettingsAsync();
            if (Messages.Count == 0)
            {
                Messages.Add(new MessageViewModel
                {
                    RoleName = "助手",
                    Text = "你好！我是你的 AI 助手。有什么我可以帮你的吗？",
                    IsAssistant = true,
                    BubbleBackground = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"],
                    BubbleForeground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                    Alignment = HorizontalAlignment.Left
                });
            }
        }

        public async Task ReloadSettingsAsync()
        {
            _settings = await _storage.LoadAISettingsAsync();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke();
        }

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            SettingsRequested?.Invoke();
        }

        private async void SendBtn_Click(object sender, RoutedEventArgs e)
        {
            await SendMessageAsync();
        }

        private async void InputBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter && 
                !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            {
                e.Handled = true;
                await SendMessageAsync();
            }
        }

        private async Task SendMessageAsync()
        {
            if (string.IsNullOrWhiteSpace(InputBox.Text) || _settings == null) return;
            
            string userText = InputBox.Text.Trim();
            InputBox.Text = "";

            // Prepare context
            string context = "";
            if (AttachContextCheckbox.IsChecked == true && ActiveSession != null)
            {
                int lines = _settings.ContextLineCount > 0 ? _settings.ContextLineCount : 50;
                context = ActiveSession.Buffer.GetRecentLines(lines);
            }

            string fullPrompt = userText;
            if (!string.IsNullOrEmpty(context))
            {
                fullPrompt += "\n\n<TerminalContext>\n" + context + "\n</TerminalContext>";
            }

            if (!string.IsNullOrWhiteSpace(_settings.CustomPrompt) && _chatHistory.Count == 0)
            {
                _chatHistory.Add(new ChatMessage { Role = "system", Content = _settings.CustomPrompt });
            }

            _chatHistory.Add(new ChatMessage { Role = "user", Content = fullPrompt });
            
            Messages.Add(new MessageViewModel
            {
                RoleName = "你",
                Text = userText,
                IsAssistant = false,
                BubbleBackground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                BubbleForeground = (SolidColorBrush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
                Alignment = HorizontalAlignment.Right
            });

            var env = _settings.Environments.FirstOrDefault(e => e.Id == _settings.CurrentEnvironmentId) 
                      ?? _settings.Environments.FirstOrDefault();

            if (env == null)
            {
                Messages.Add(CreateErrorMsg("请先在设置中配置 API 环境。"));
                return;
            }

            int maxTokens = env.ContextTokens > 0 ? env.ContextTokens : 128000;
            PruneChatHistory(maxTokens);

            var aiMsgVM = new MessageViewModel
            {
                RoleName = env.CurrentModel,
                Text = "",
                IsAssistant = true,
                BubbleBackground = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"],
                BubbleForeground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                Alignment = HorizontalAlignment.Left
            };
            Messages.Add(aiMsgVM);
            
            ScrollToBottom();

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            InputBox.IsEnabled = false;
            SendBtn.IsEnabled = false;
            StatusText.Text = "思考中...";
            StatusText.Visibility = Visibility.Visible;

            try
            {
                string fullResponse = "";
                await foreach (var chunk in _aiService.StreamChatAsync(env, _chatHistory, _cts.Token))
                {
                    fullResponse += chunk;
                    aiMsgVM.Text = fullResponse; // Notifies UI
                    ScrollToBottom();
                }
                
                _chatHistory.Add(new ChatMessage { Role = "assistant", Content = fullResponse });
                StatusText.Text = "就绪";
                StatusText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                aiMsgVM.Text += $"\n\n[发生错误: {ex.Message}]";
                StatusText.Text = "发生错误";
                StatusText.Visibility = Visibility.Visible;
            }
            finally
            {
                InputBox.IsEnabled = true;
                SendBtn.IsEnabled = true;
                InputBox.Focus(FocusState.Programmatic);
            }
        }

        private void PruneChatHistory(int maxTokens)
        {
            if (_chatHistory.Count == 0) return;
            
            // 为助手的回复预留 2000 tokens
            int maxAllowedTokens = Math.Max(1000, maxTokens - 2000);
            
            while (_chatHistory.Count > 1) 
            {
                // 粗略估算：1 字符 ≈ 1 token (考虑到中英文混合的保守估计)
                int estimatedTokens = _chatHistory.Sum(m => m.Content?.Length ?? 0);
                
                if (estimatedTokens <= maxAllowedTokens)
                    break;
                    
                // 从头开始删，但跳过 system prompt
                int removeIndex = _chatHistory[0].Role == "system" ? 1 : 0;
                
                if (removeIndex >= _chatHistory.Count - 1)
                    break; // 至少保留最后一条用户消息
                    
                _chatHistory.RemoveAt(removeIndex);
            }
        }

        private MessageViewModel CreateErrorMsg(string err)
        {
            return new MessageViewModel
            {
                RoleName = "系统",
                Text = err,
                IsAssistant = true,
                BubbleBackground = new SolidColorBrush(Microsoft.UI.Colors.DarkRed),
                BubbleForeground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Alignment = HorizontalAlignment.Left
            };
        }

        private void ScrollToBottom()
        {
            if (Messages.Count > 0)
            {
                MessagesList.ScrollIntoView(Messages.Last());
            }
        }

        private void CopyMsg_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string txt)
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(txt);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
        }

        private void RunShell_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string txt && ActiveSession != null)
            {
                string code = ExtractCode(txt);
                ActiveSession.SendText(code + "\n");
            }
        }

        private async void RunBg_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string txt && ActiveSession != null)
            {
                string code = ExtractCode(txt);
                try
                {
                    StatusText.Text = "正在后台执行...";
                    StatusText.Visibility = Visibility.Visible;
                    string result = await ActiveSession.ExecuteBackgroundCommandAsync(code);
                    
                    Messages.Add(new MessageViewModel
                    {
                        RoleName = "后台执行结果",
                        Text = string.IsNullOrWhiteSpace(result) ? "[无输出]" : result.Trim(),
                        IsAssistant = true,
                        BubbleBackground = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"],
                        BubbleForeground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
                        Alignment = HorizontalAlignment.Left
                    });
                    StatusText.Text = "就绪";
                    StatusText.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex)
                {
                    Messages.Add(CreateErrorMsg($"执行失败: {ex.Message}"));
                    StatusText.Text = "错误";
                    StatusText.Visibility = Visibility.Visible;
                }
            }
        }

        private string ExtractCode(string message)
        {
            // Simple markdown code block extraction
            int start = message.IndexOf("```");
            if (start >= 0)
            {
                start = message.IndexOf('\n', start);
                if (start > 0)
                {
                    int end = message.IndexOf("```", start);
                    if (end > start)
                    {
                        return message.Substring(start + 1, end - start - 1).Trim();
                    }
                }
            }
            return message.Trim();
        }
    }

    public class MessageViewModel : INotifyPropertyChanged
    {
        public string RoleName { get; set; } = "";
        
        private string _text = "";
        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(); }
        }

        public bool IsAssistant { get; set; }
        public Visibility ActionsVisibility => IsAssistant ? Visibility.Visible : Visibility.Collapsed;

        public Brush? BubbleBackground { get; set; }
        public Brush? BubbleForeground { get; set; }
        public FontFamily BubbleFont { get; set; } = new FontFamily("Segoe UI");
        public HorizontalAlignment Alignment { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
