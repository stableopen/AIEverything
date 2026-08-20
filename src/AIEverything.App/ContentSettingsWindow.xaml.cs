using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
using AIEverything.Content.Contracts;
using AIEverything.Desktop;
using AIEverything.Desktop.Mail;
using AIEverything.Desktop.Ranking;

namespace AIEverything.App;

public partial class ContentSettingsWindow : Window
{
    private readonly StandaloneSearchService _search;
    private readonly DesktopRankingCoordinator _rankingCoordinator;
    private readonly OnnxCrossEncoderReranker _localModel;
    private readonly IDeepSeekCredentialStore _deepSeekCredentials;
    private readonly IMailSearchModule _mail;
    private readonly CancellationToken _lifetime;
    private ContentIndexStatus? _status;
    private MailIndexStatus? _mailStatus;
    private bool _updatingRankingControls;

    public ContentSettingsWindow(
        StandaloneSearchService search,
        DesktopRankingCoordinator rankingCoordinator,
        OnnxCrossEncoderReranker localModel,
        IDeepSeekCredentialStore deepSeekCredentials,
        IMailSearchModule mail,
        RankingOptions rankingOptions,
        CancellationToken lifetime)
    {
        InitializeComponent();
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _rankingCoordinator = rankingCoordinator ?? throw new ArgumentNullException(nameof(rankingCoordinator));
        _localModel = localModel ?? throw new ArgumentNullException(nameof(localModel));
        _deepSeekCredentials = deepSeekCredentials ??
                               throw new ArgumentNullException(nameof(deepSeekCredentials));
        _mail = mail ?? throw new ArgumentNullException(nameof(mail));
        RankingOptions = rankingOptions ?? throw new ArgumentNullException(nameof(rankingOptions));
        _lifetime = lifetime;
        RenderRankingControls();
        Loaded += async (_, _) =>
        {
            await RefreshAsync();
            await RefreshLocalModelStatusAsync();
        };
        Closed += (_, _) => DeepSeekApiKeyBox.Clear();
    }

    public RankingOptions RankingOptions { get; private set; }
    public bool BehaviorHistoryCleared { get; private set; }

    private async Task RefreshAsync()
    {
        SetBusy(true);
        try
        {
            SettingsFeedbackText.Visibility = Visibility.Collapsed;
            try
            {
                _status = await _search.GetIndexStatusAsync(_lifetime);
                Render(_status);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ShowError($"正文服务暂不可用：{exception.Message}");
            }

            try
            {
                _mailStatus = await _mail.GetStatusAsync(_lifetime);
                RenderMail(_mailStatus);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ShowError($"邮件索引暂不可用：{exception.Message}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SettingsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_status is null) return;
        SetBusy(true);
        try
        {
            _status = !_status.Enabled || !_status.DisclosureAccepted
                ? await _search.ConfigureIndexAsync(true, true, _lifetime)
                : await _search.SetPausedAsync(!_status.Paused, _lifetime);
            Render(_status);
            SettingsFeedbackText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"更新正文状态失败：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SettingsSyncButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        SettingsFeedbackText.Text = "正在同步正文候选…";
        SettingsFeedbackText.Foreground = (Brush)FindResource("MutedTextBrush");
        SettingsFeedbackText.Visibility = Visibility.Visible;
        try
        {
            _status = await _search.SynchronizeAsync(_lifetime);
            Render(_status);
            SettingsFeedbackText.Text = "同步请求已完成。";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"同步失败：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Render(ContentIndexStatus status)
    {
        SettingsStatusText.Text = !status.Enabled
            ? "正文未启用"
            : status.Paused
                ? "正文索引已暂停"
                : status.QueuedDocuments > 0
                    ? "正文索引进行中"
                    : "正文索引已就绪";
        SettingsToggleButton.Content = !status.Enabled || !status.DisclosureAccepted
            ? "启用正文"
            : status.Paused ? "继续正文" : "暂停正文";
        SettingsIndexedText.Text = $"{status.IndexedDocuments:N0} 个";
        SettingsQueueText.Text = $"{status.QueuedDocuments:N0} 个";
        SettingsFailureText.Text = $"{status.FailedDocuments:N0} 个";
        SettingsFailureText.Foreground = status.FailedDocuments > 0
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextBrush");
        SettingsFailureGroupsText.Text =
            $"损坏 {status.CorruptFailures:N0} · 加密/不支持 {status.UnsupportedOrEncryptedFailures:N0} · " +
            $"过大 {status.TooLargeFailures:N0} · 超时 {status.TimeoutFailures:N0} · 无权限 {status.AccessDeniedFailures:N0}";
        SettingsRetryFailuresButton.IsEnabled = status.FailedDocuments > 0;
        SettingsDatabasePathText.Text = status.DatabasePath ?? "尚未创建";
        SettingsDatabaseSizeText.Text = FormatBytes(status.DatabaseBytes);
        SettingsSyncButton.IsEnabled = status.Enabled && !status.Paused;
    }

    private void SetBusy(bool busy)
    {
        SettingsToggleButton.IsEnabled = !busy && _status is not null;
        SettingsSyncButton.IsEnabled = !busy && _status is { Enabled: true, Paused: false };
        SettingsRetryFailuresButton.IsEnabled = !busy && _status is { FailedDocuments: > 0 };
        MailEnableSyncButton.IsEnabled = !busy && _mailStatus is { Enabled: false };
        MailSyncButton.IsEnabled = !busy && _mailStatus is { Enabled: true };
        MailDisableButton.IsEnabled = !busy && _mailStatus is { Enabled: true };
        MailClearButton.IsEnabled = !busy && _mailStatus is { IndexedMessages: > 0 };
    }

    private void RenderMail(MailIndexStatus status)
    {
        MailStatusText.Text = status.Enabled
            ? $"已开启 · 已索引 {status.IndexedMessages:N0} 封"
            : $"已关闭 · 本地保留 {status.IndexedMessages:N0} 封";
        if (!string.IsNullOrWhiteSpace(status.LastError))
        {
            MailDetailText.Text = $"上次同步失败：{status.LastError}";
            MailDetailText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else if (status.LastSyncAt is { } lastSync)
        {
            var skipped = status.LastSkippedMessages > 0
                ? $" · 跳过 {status.LastSkippedMessages:N0} 封异常邮件"
                : string.Empty;
            MailDetailText.Text = $"上次同步 {lastSync.ToLocalTime():yyyy-MM-dd HH:mm}{skipped}";
            MailDetailText.Foreground = (Brush)FindResource("MutedTextBrush");
        }
        else
        {
            MailDetailText.Text = "启动后自动只读同步 Classic Outlook 默认收件箱和已发送中最近 100 封邮件。";
            MailDetailText.Foreground = (Brush)FindResource("MutedTextBrush");
        }

        MailEnableSyncButton.IsEnabled = !status.Enabled;
        MailSyncButton.IsEnabled = status.Enabled;
        MailDisableButton.IsEnabled = status.Enabled;
        MailClearButton.IsEnabled = status.IndexedMessages > 0;
    }

    private async void MailEnableSyncButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteMailCommandAsync(MailIndexCommand.EnableAndSynchronize, "正在读取最近 100 封邮件…");

    private async void MailSyncButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteMailCommandAsync(MailIndexCommand.Synchronize, "正在同步最近邮件…");

    private async void MailDisableButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteMailCommandAsync(MailIndexCommand.Disable, "正在关闭邮件搜索…");

    private async void MailClearButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteMailCommandAsync(MailIndexCommand.Clear, "正在清除本地邮件索引…");

    private async Task ExecuteMailCommandAsync(MailIndexCommand command, string busyText)
    {
        SetBusy(true);
        SettingsFeedbackText.Text = busyText;
        SettingsFeedbackText.Foreground = (Brush)FindResource("MutedTextBrush");
        SettingsFeedbackText.Visibility = Visibility.Visible;
        try
        {
            var result = await _mail.ExecuteAsync(command, _lifetime);
            _mailStatus = result.Status;
            RenderMail(result.Status);
            SettingsFeedbackText.Text = !string.IsNullOrWhiteSpace(result.Status.LastError)
                ? "邮件同步未完成，请查看邮件状态。"
                : command switch
                {
                    MailIndexCommand.EnableAndSynchronize or MailIndexCommand.Synchronize =>
                        $"邮件同步完成，当前可搜索 {result.Status.IndexedMessages:N0} 封。",
                    MailIndexCommand.Disable => "邮件搜索已关闭。",
                    MailIndexCommand.Clear => "本地邮件索引已清除。",
                    _ => "操作已完成。"
                };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"邮件操作失败：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowError(string message)
    {
        SettingsFeedbackText.Text = message;
        SettingsFeedbackText.Foreground = (Brush)FindResource("DangerBrush");
        SettingsFeedbackText.Visibility = Visibility.Visible;
    }

    private async void RankingSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingRankingControls) return;
        _updatingRankingControls = true;
        try
        {
            RankingOptions = new RankingOptions(
                BehaviorRankingToggle.IsChecked == true,
                LocalModelToggle.IsChecked == true,
                DeepSeekToggle.IsChecked == true,
                true);
            RenderRankingStatus();
        }
        finally
        {
            _updatingRankingControls = false;
        }

        if (RankingOptions.LocalModelEnabled)
        {
            await RefreshLocalModelStatusAsync();
        }
    }

    private async void ClearBehaviorButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "仅清除本机 ranking.db 中的 30 天聚合使用记录，不会删除文件或正文索引。是否继续？",
                "清除行为排序历史",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        ClearBehaviorButton.IsEnabled = false;
        try
        {
            await _rankingCoordinator.ClearAsync(_lifetime);
            BehaviorHistoryCleared = true;
            SettingsFeedbackText.Text = "行为排序历史已清除。";
            SettingsFeedbackText.Foreground = (Brush)FindResource("MutedTextBrush");
            SettingsFeedbackText.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"清除行为历史失败：{exception.Message}");
        }
        finally
        {
            ClearBehaviorButton.IsEnabled = true;
        }
    }

    private async void SaveDeepSeekCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        var value = DeepSeekApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(value))
        {
            SettingsFeedbackText.Text = "未输入新密钥，现有凭据未更改。";
            SettingsFeedbackText.Foreground = (Brush)FindResource("MutedTextBrush");
            SettingsFeedbackText.Visibility = Visibility.Visible;
            return;
        }

        SaveDeepSeekCredentialButton.IsEnabled = false;
        try
        {
            if (!await _deepSeekCredentials.SaveApiKeyAsync(value, _lifetime))
            {
                ShowError("密钥格式无效或 Windows 凭据管理器保存失败；现有凭据未更改。");
                return;
            }

            DeepSeekApiKeyBox.Clear();
            SettingsFeedbackText.Text = "DeepSeek 密钥已保存或更新到 Windows 凭据管理器。";
            SettingsFeedbackText.Foreground = (Brush)FindResource("MutedTextBrush");
            SettingsFeedbackText.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError("保存 DeepSeek 密钥失败；未写入设置文件或排序数据库。");
        }
        finally
        {
            SaveDeepSeekCredentialButton.IsEnabled = true;
        }
    }

    private void RenderRankingControls()
    {
        _updatingRankingControls = true;
        try
        {
            BehaviorRankingToggle.IsChecked = RankingOptions.BehaviorEnabled;
            LocalModelToggle.IsChecked = RankingOptions.LocalModelEnabled;
            DeepSeekToggle.IsChecked = RankingOptions.DeepSeekEnabled;
            RenderRankingStatus();
        }
        finally
        {
            _updatingRankingControls = false;
        }
    }

    private async Task RefreshLocalModelStatusAsync()
    {
        if (!RankingOptions.LocalModelEnabled)
        {
            LocalModelStatusText.Text = "已关闭；搜索保持确定性行为排序。";
            return;
        }

        LocalModelStatusText.Text = "正在校验并预热本地模型…";
        try
        {
            var status = await _localModel.WarmAsync(_lifetime);
            LocalModelStatusText.Text = DescribeLocalModelStatus(status);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RenderRankingStatus()
    {
        DeepSeekStatusText.Text = RankingOptions.DeepSeekEnabled
            ? "已开启；未配置凭据时自动使用本地排序，配置后仅在原歧义门槛下调用。"
            : "已关闭，不会读取凭据或联网。";
        if (!RankingOptions.LocalModelEnabled)
        {
            LocalModelStatusText.Text = "已关闭；搜索保持确定性行为排序。";
        }
    }

    private static string DescribeLocalModelStatus(LocalModelStatus status) => status switch
    {
        LocalModelStatus.Ready => "已就绪 · 本机 ONNX · Top10→Top5",
        LocalModelStatus.UnsupportedCpu => "当前 CPU 不支持 AVX2，已回退行为排序。",
        LocalModelStatus.MissingAssets => "模型文件缺失，已回退行为排序。",
        LocalModelStatus.HashMismatch => "模型校验失败，已回退行为排序。",
        LocalModelStatus.RuntimeUnavailable => "ONNX Runtime 不可用，已回退行为排序。",
        LocalModelStatus.InvalidModel => "模型格式无效，已回退行为排序。",
        LocalModelStatus.InferenceFailed => "模型推理失败，已回退行为排序。",
        LocalModelStatus.TimedOut => "模型超过 400 ms 安全阈值，已回退行为排序。",
        _ => "等待后台预热。"
    };

    private void SettingsCloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void SettingsRetryFailuresButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            _status = await _search.RetryFailuresAsync(_lifetime);
            Render(_status);
            SettingsFeedbackText.Text = "已重新提交失败文件。";
            SettingsFeedbackText.Foreground = (Brush)FindResource("MutedTextBrush");
            SettingsFeedbackText.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"重试失败：{exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SettingsReportProblemButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/stableopen/AIEverything/issues/new")
        {
            UseShellExecute = true
        });

    private static string FormatBytes(long bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024
            ? $"{bytes / 1024d:0.0} KB"
            : $"{bytes / 1024d / 1024d:0.0} MB";
}
