using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PromptStudio;

public partial class MainWindow : Window
{
    private readonly List<Turn> _turns = [];
    private CancellationTokenSource? _request;
    private ModelReply? _brief;
    private string? _pendingQuestion;
    private bool _closing;
    private bool LibraryIsOpen => LibraryView?.Visibility == Visibility.Visible;

    public MainWindow()
    {
        InitializeComponent();
        Scenario.ItemsSource = new[] { "自动判断" }.Concat(ModelClient.Scenarios).ToArray();
        Scenario.SelectedItem = "自动判断";
        ResetConversation();
        Loaded += (_, _) => Entry.Focus();
    }

    private void ResetConversation()
    {
        _turns.Clear();
        _brief = null;
        _pendingQuestion = null;
        Messages.Children.Clear();
        Options.Children.Clear();
        Entry.Clear();
        Result.Clear();
        Brief.Text = "";
        DetectedScenario.Text = "";
        Status.Text = "";
        UpdateControls();
    }

    private void AddMessage(string author, string content, bool fromUser)
    {
        var label = new TextBlock
        {
            Text = author,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(fromUser ? 0 : 12, 0, 0, 5),
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        var message = new TextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            LineHeight = 24,
            Foreground = (Brush)FindResource("Ink"),
        };
        var bubble = new Border
        {
            Child = message,
            Padding = fromUser ? new Thickness(14, 8, 14, 9) : new Thickness(0),
            CornerRadius = fromUser ? new CornerRadius(14, 14, 5, 14) : new CornerRadius(0),
            Background = fromUser ? (Brush)FindResource("UserBubble") : Brushes.Transparent,
        };
        var block = new StackPanel
        {
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = fromUser ? new Thickness(49, 0, 0, 17) : new Thickness(0, 0, 45, 15),
        };
        block.Children.Add(label);
        block.Children.Add(bubble);
        Messages.Children.Add(block);
        ChatScroll.ScrollToBottom();
    }

    private void ShowQuestion(ModelReply reply)
    {
        _pendingQuestion = string.IsNullOrWhiteSpace(reply.Question) ? null : reply.Question.Trim();
        Options.Children.Clear();
        if (_pendingQuestion is null) return;

        AddMessage("Luna", _pendingQuestion, false);
        foreach (var option in reply.Options.Where(option => !string.IsNullOrWhiteSpace(option)).Distinct().Take(3))
        {
            var button = new Button
            {
                Content = new TextBlock { Text = option, TextWrapping = TextWrapping.Wrap, MaxWidth = 148, FontSize = 12 },
                Margin = new Thickness(0, 0, 6, 5),
                Padding = new Thickness(8, 4, 8, 4),
                MinHeight = 27,
            };
            AutomationProperties.SetName(button, option);
            button.Click += (_, _) =>
            {
                Entry.Text = option;
                Entry.CaretIndex = Entry.Text.Length;
                Entry.Focus();
            };
            Options.Children.Add(button);
        }
    }

    private void UpdateControls()
    {
        if (SendButton is null || GenerateButton is null || Status is null) return;
        var busy = _request is not null;
        var hasDraft = !string.IsNullOrWhiteSpace(Entry.Text);
        var hasMessages = Messages.Children.Count > 0;
        var isReady = _brief is not null && _pendingQuestion is null;
        var hasResult = Result.Text.Length > 0;
        SendButton.Content = _turns.Count == 0 ? "开始梳理  ↑" : "发送  ↑";
        SendButton.IsEnabled = !busy && hasDraft;
        GenerateButton.IsEnabled = !busy && !hasDraft && isReady && !hasResult;
        GenerateButton.Visibility = hasResult ? Visibility.Collapsed : Visibility.Visible;
        NewButton.IsEnabled = !busy && !LibraryIsOpen;
        Scenario.IsEnabled = !busy && !LibraryIsOpen && _turns.Count == 0;
        LibraryButton.IsEnabled = !busy;
        Entry.IsEnabled = !busy;
        EntryHint.Text = busy || _turns.Count == 0 ? "" : "回复 Luna，或补充要求…";
        EntryHint.Visibility = Entry.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Welcome.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
        ChatScroll.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;
        SetPanelVisibility(BriefPanel, isReady);
        SetPanelVisibility(ResultPanel, hasResult);
        Options.Visibility = !busy && Options.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.IsEnabled = !busy && hasResult;
    }

    private static void SetPanelVisibility(Border panel, bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (panel.Visibility == visibility) return;
        panel.Visibility = visibility;
        if (visible && SystemParameters.ClientAreaAnimation)
        {
            panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(160))
            {
                FillBehavior = FillBehavior.Stop,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    private async Task RunAsync(bool generate)
    {
        if (_request is not null) return;
        var draft = Entry.Text.Trim();
        var turns = new List<Turn>(_turns);
        if (!generate) turns.Add(new Turn("用户", draft));
        var scenario = Scenario.SelectedIndex > 0 ? Scenario.SelectedItem as string : generate ? _brief?.Scenario : null;
        using var request = new CancellationTokenSource();
        _request = request;
        var messageCount = Messages.Children.Count;
        if (!generate)
        {
            AddMessage("你", draft, true);
            Entry.Clear();
        }
        Status.Text = generate ? "Luna 正在生成提示词…" : "Luna 正在梳理需求…";
        UpdateControls();
        ChatScroll.ScrollToBottom();
        try
        {
            var reply = await ModelClient.RequestAsync(scenario, turns, _brief, generate, request.Token);
            if (_closing) return;
            if (generate)
            {
                Result.Text = reply.Prompt.Trim();
                Status.Text = "提示词已生成，可以复制";
            }
            else
            {
                _brief = reply;
                Brief.Text = reply.Summary.Trim();
                DetectedScenario.Text = scenario ?? reply.Scenario;
                _turns.Add(new Turn("用户", draft));
                ShowQuestion(reply);
                if (_pendingQuestion is not null) _turns.Add(new Turn("工作台", _pendingQuestion));
                else _turns.Add(new Turn("工作台", $"待确认需求：{reply.Summary}"));
                Result.Clear();
                Status.Text = _pendingQuestion is null ? "等待确认需求" : "等待你的回复";
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or
            System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
        {
            if (_closing) return;
            if (!generate)
            {
                if (Messages.Children.Count > messageCount) Messages.Children.RemoveAt(messageCount);
                Entry.Text = draft;
                Entry.CaretIndex = Entry.Text.Length;
            }
            Status.Text = request.IsCancellationRequested ? "已停止，可以继续" : exception switch
            {
                InvalidOperationException => exception.Message,
                JsonException => "Luna 返回的格式有误，可以重试。",
                _ => "模型连接失败，可以重试。",
            };
        }
        finally
        {
            _request = null;
            if (!_closing)
            {
                UpdateControls();
                if (generate && Result.Text.Length > 0)
                {
                    ResultPanel.UpdateLayout();
                    ResultPanel.BringIntoView(new Rect(0, 0, 1, 46));
                }
                else
                {
                    ChatScroll.ScrollToBottom();
                    if (!generate) Entry.Focus();
                }
            }
        }
    }

    private async void SendClick(object sender, RoutedEventArgs e) => await RunAsync(false);
    private async void GenerateClick(object sender, RoutedEventArgs e) => await RunAsync(true);
    private void CancelClick(object sender, RoutedEventArgs e) => _request?.Cancel();
    private void NewConversation(object sender, RoutedEventArgs e)
    {
        ResetConversation();
        Entry.Focus();
    }
    private void EntryChanged(object sender, TextChangedEventArgs e) => UpdateControls();

    private void ToggleLibrary(object sender, RoutedEventArgs e) => SetLibraryOpen(!LibraryIsOpen);

    private void SetLibraryOpen(bool open)
    {
        if (_request is not null) return;

        LibraryView.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ConversationArea.Visibility = Composer.Visibility = ConversationFooter.Visibility =
            open ? Visibility.Collapsed : Visibility.Visible;
        LibraryButton.Content = open ? "返回对话" : "提示词库";
        UpdateControls();
        if (open)
        {
            if (LibraryList.ItemsSource is null) RefreshLibrary();
            LibrarySearch.Focus();
            LibrarySearch.SelectAll();
        }
        else
        {
            Entry.Focus();
        }
    }

    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && _request is null)
        {
            e.Handled = true;
            SetLibraryOpen(!LibraryIsOpen);
        }
        else if (e.Key == Key.Escape && LibraryIsOpen)
        {
            e.Handled = true;
            SetLibraryOpen(false);
        }
    }

    private void LibrarySearchChanged(object sender, TextChangedEventArgs e)
    {
        if (LibraryList is not null) RefreshLibrary();
    }

    private void RefreshLibrary()
    {
        var matches = PromptLibrary.Search(LibrarySearch.Text);
        SearchHint.Visibility = LibrarySearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryCount.Text = $"共 {matches.Length} 条";
        LibraryList.ItemsSource = matches;
        LibraryList.SelectedIndex = matches.Length > 0 ? 0 : -1;
        UpdateTemplatePreview();
    }

    private void LibrarySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTemplatePreview();

    private void UpdateTemplatePreview()
    {
        if (TemplatePreview is null || TemplateSource is null) return;
        var template = LibraryList.SelectedItem as PromptTemplate;
        TemplateTitle.Text = template?.Title ?? "没有匹配的提示词";
        TemplatePreview.Text = template?.Prompt ?? "";
        TemplatePreview.ScrollToHome();
        UseTemplateButton.IsEnabled = template is not null;
        TemplateSource.IsEnabled = template?.Source is not null;
        OriginalSeparator.Text = TemplateSource.IsEnabled ? " · " : "";
        OriginalLabel.Text = TemplateSource.IsEnabled ? "原出处" : "";
    }

    private void UseTemplate(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not PromptTemplate template) return;
        var reference = $"请以「{template.Title}」模板为参考，优化这次要用的提示词。\n\n{template.Prompt}";
        Entry.Text = string.IsNullOrWhiteSpace(Entry.Text) ? reference : $"{Entry.Text.TrimEnd()}\n\n{reference}";
        SetLibraryOpen(false);
        Entry.CaretIndex = Entry.Text.Length;
        Entry.ScrollToEnd();
    }

    private void OpenAiShort(object sender, RoutedEventArgs e) => OpenSource(PromptLibrary.SourceUrl);

    private void OpenTemplateSource(object sender, RoutedEventArgs e) =>
        OpenSource((LibraryList.SelectedItem as PromptTemplate)?.Source);

    private void OpenSource(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { LibraryCount.Text = "暂时无法打开来源链接"; }
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private async void EntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control || !SendButton.IsEnabled) return;
        e.Handled = true;
        await RunAsync(false);
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Result.Text);
            Status.Text = "已复制到剪贴板";
        }
        catch (ExternalException)
        {
            Status.Text = "剪贴板暂时不可用，请再试一次。";
        }
    }

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _request?.Cancel();
    }
}
