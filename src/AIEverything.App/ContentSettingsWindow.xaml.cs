using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
using AIEverything.Content.Contracts;
using AIEverything.Desktop;
using AIEverything.Desktop.Ranking;

namespace AIEverything.App;

public partial class ContentSettingsWindow : Window
{
    private readonly StandaloneSearchService _search;
    private readonly DesktopRankingCoordinator _rankingCoordinator;
    private readonly OnnxCrossEncoderReranker _localModel;
    private readonly IDeepSeekCredentialStore _deepSeekCredentials;
    private readonly CancellationToken _lifetime;
    private ContentIndexStatus? _status;
    private bool _updatingRankingControls;

    public ContentSettingsWindow(
        StandaloneSearchService search,
        DesktopRankingCoordinator rankingCoordinator,
        OnnxCrossEncoderReranker localModel,
        IDeepSeekCredentialStore deepSeekCredentials,
        RankingOptions rankingOptions,
        CancellationToken lifetime)
    {
        InitializeComponent();
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _rankingCoordinator = rankingCoordinator ?? throw new ArgumentNullException(nameof(rankingCoordinator));
        _localModel = localModel ?? throw new ArgumentNullException(nameof(localModel));
        _deepSeekCredentials = deepSeekCredentials ??
                               throw new ArgumentNullException(nameof(deepSeekCredentials));
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
            _status = await _search.GetIndexStatusAsync(_lifetime);
            Render(_status);
            SettingsFeedbackText.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"正文服务暂不可用：{exception.Message}");
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
            if (DeepSeekDisclosureCheck.IsChecked != true)
            {
                DeepSeekToggle.IsChecked = false;
            }

            DeepSeekToggle.IsEnabled = DeepSeekDisclosureCheck.IsChecked == true;
            RankingOptions = new RankingOptions(
                BehaviorRankingToggle.IsChecked == true,
                LocalModelToggle.IsChecked == true,
                DeepSeekToggle.IsChecked == true && DeepSeekDisclosureCheck.IsChecked == true,
                DeepSeekDisclosureCheck.IsChecked == true);
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
            DeepSeekDisclosureCheck.IsChecked = RankingOptions.DeepSeekDisclosureAccepted;
            DeepSeekToggle.IsChecked = RankingOptions.DeepSeekEnabled &&
                                       RankingOptions.DeepSeekDisclosureAccepted;
            DeepSeekToggle.IsEnabled = RankingOptions.DeepSeekDisclosureAccepted;
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
        DeepSeekStatusText.Text = !RankingOptions.DeepSeekDisclosureAccepted
            ? "未授权，不会读取凭据或联网。"
            : RankingOptions.DeepSeekEnabled
                ? "已开启；仅在本地模型有效但低置信度时按需调用。"
                : "已了解披露但当前关闭，不会联网。";
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
        Process.Start(new ProcessStartInfo("https://github.com/stableye/AIEverything/issues/new")
        {
            UseShellExecute = true
        });

    private static string FormatBytes(long bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024
            ? $"{bytes / 1024d:0.0} KB"
            : $"{bytes / 1024d / 1024d:0.0} MB";
}
