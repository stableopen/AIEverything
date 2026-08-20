using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Content.Ipc;
using AIEverything.Content.Text;
using AIEverything.ContentClient;
using AIEverything.Desktop;
using AIEverything.Desktop.Mail;
using AIEverything.Desktop.Ranking;
using AIEverything.Everything;

namespace AIEverything.App;

public partial class MainWindow : Window
{
    private readonly EverythingNativeApi _nativeApi = new();
    private readonly EverythingEngineManager _everythingEngineManager;
    private readonly ContentDaemonClient _contentClient;
    private readonly StandaloneSearchService _search;
    private readonly MailSearchModule _mail;
    private readonly ContentDaemonManager _daemonManager;
    private readonly DesktopPreferencesStore _preferences;
    private readonly SqliteRankingBehaviorStore _behaviorStore;
    private readonly OnnxCrossEncoderReranker _localModel;
    private readonly HttpClient _deepSeekHttpClient;
    private readonly WindowsDeepSeekCredentialProvider _deepSeekCredentials;
    private readonly DesktopRankingCoordinator _rankingCoordinator;
    private readonly DesktopRankingPresentationGate _rankingGate = new();
    private readonly ObservableCollection<ResultRow> _results = [];
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _searchCancellation;
    private bool _renderingResults;
    private ContentIndexStatus? _contentStatus;
    private DesktopPreferences _currentPreferences;
    private RankingOptions _rankingOptions;

    public MainWindow()
    {
        InitializeComponent();
        var everything = new EverythingSearchService(_nativeApi);
        var localDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIEverything");
        _everythingEngineManager = new EverythingEngineManager(new SystemEverythingEngineProcessHost(
            Path.Combine(AppContext.BaseDirectory, "EverythingEngine"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIEverything", "EverythingEngine", "1.4.1.1032")));
        _contentClient = new ContentDaemonClient(ContentPipeNaming.ForCurrentUser(), TimeSpan.FromSeconds(3));
        _mail = new MailSearchModule(
            Path.Combine(localDataRoot, "mail.db"),
            new OutlookComMailSource());
        _search = new StandaloneSearchService(everything, _contentClient,
            new HybridSearchService(everything, _contentClient), _mail);
        _daemonManager = new ContentDaemonManager(Path.Combine(AppContext.BaseDirectory, "AIEverything.Daemon.exe"));
        _preferences = new DesktopPreferencesStore(Path.Combine(localDataRoot, "settings.json"));
        _currentPreferences = _preferences.Load();
        _rankingOptions = _currentPreferences.Ranking;
        _behaviorStore = new SqliteRankingBehaviorStore(Path.Combine(localDataRoot, "ranking.db"));
        _localModel = new OnnxCrossEncoderReranker(OnnxCrossEncoderReranker.DefaultAssetRoot);
        _deepSeekHttpClient = new HttpClient();
        _deepSeekCredentials = new WindowsDeepSeekCredentialProvider();
        _rankingCoordinator = new DesktopRankingCoordinator(
            _behaviorStore,
            _localModel,
            new DeepSeekCloudReranker(_deepSeekHttpClient, _deepSeekCredentials),
            TimeProvider.System);
        Width = _currentPreferences.Width;
        Height = _currentPreferences.Height;
        if (_currentPreferences.Maximized) WindowState = WindowState.Maximized;
        ResultsGrid.ItemsSource = _results;
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await SearchAsync(); };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        _statusTimer.Start();
        _ = InitializeEverythingAsync(_lifetime.Token);
        _ = InitializeContentAsync(_lifetime.Token);
        _ = InitializeMailAsync(_lifetime.Token);
        if (_rankingOptions.LocalModelEnabled)
        {
            _ = WarmLocalModelAsync(_lifetime.Token);
        }
        await Task.CompletedTask;
    }

    private async Task WarmLocalModelAsync(CancellationToken token)
    {
        try
        {
            await _localModel.WarmAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task InitializeEverythingAsync(CancellationToken token)
    {
        try
        {
            await Task.Run(() => _everythingEngineManager.EnsureReadyAsync(
                _search.GetEverythingStatus, token), token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
    }

    private async Task InitializeContentAsync(CancellationToken token)
    {
        try
        {
            await _daemonManager.EnsureRunningAsync(_contentClient, token);
            await _daemonManager.WaitUntilReadyAsync(_contentClient, TimeSpan.FromSeconds(8), token);
            _contentStatus = await _contentClient.GetStatusAsync(token);
            await RefreshStatusAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
    }

    private async Task RefreshStatusAsync()
    {
        try
        {
            _contentStatus = await _contentClient.GetStatusAsync(_lifetime.Token);
            RenderContentStatus(_contentStatus);
        }
        catch (Exception exception) when (exception is ContentIndexException or IOException) { }
    }

    private async Task InitializeMailAsync(CancellationToken token)
    {
        try
        {
            await _mail.SynchronizeOnStartupAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Outlook availability is reported only in Settings; file search stays usable.
        }
    }

    private void RenderContentStatus(ContentIndexStatus status)
    {
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        var filenameReady = _search.GetEverythingStatus().Ready;
        var message = !filenameReady
            ? "文件名服务暂时不可用，正在重试；已有正文仍可搜索。"
            : !status.Enabled || !status.DisclosureAccepted
                ? "正文搜索已关闭，可在设置中开启；文件名搜索仍可用。"
                : status.Paused
                    ? "正文索引已暂停，已有内容仍可搜索。"
                    : status.QueuedDocuments > 0
                        ? $"正在建立正文索引，已有 {status.IndexedDocuments:N0} 个文件可搜索。"
                        : status.FailedDocuments > 0
                            ? "正文索引已完成，部分文件未处理，请在设置中查看。"
                            : $"正文索引已就绪，已有 {status.IndexedDocuments:N0} 个文件可搜索。";
        SetQueryStatus(message);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        InvalidatePendingSearch();
        _debounce.Stop();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _debounce.Start(); else Reset();
    }
    private async void SearchBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        InvalidatePendingSearch();
        _debounce.Stop();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _debounce.Start();
    }

    private async Task SearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) { Reset(); return; }
        var mode = GetMode();
        var lease = _rankingGate.BeginSearch(query, mode);
        _debounce.Stop();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _searchCancellation.Token;
        SearchProgress.Visibility = Visibility.Visible;
        _results.Clear();
        ResultsGrid.SelectedItem = null;
        ResultsHeader.Visibility = Visibility.Collapsed;
        SetQueryStatus($"正在搜索“{query}”…");
        UpdateActions();
        try
        {
            var response = await _search.SearchAsync(
                new DesktopSearchRequest(query, mode), cancellationToken);
            if (!_rankingGate.IsCurrent(lease, SearchBox.Text, GetMode())) return;
            var run = await _rankingCoordinator.StartAsync(
                response, _rankingOptions, cancellationToken);
            if (!_rankingGate.IsCurrent(lease, SearchBox.Text, GetMode())) return;
            RenderResponse(run.Immediate, query, enhanced: false);
            FinishSearchBusyState(lease);

            var enhancementLease = _rankingGate.CaptureEnhancement(lease);
            var enhanced = await run.Enhancement;
            if (enhanced is not null && _rankingGate.CanApplyEnhancement(
                    enhancementLease, SearchBox.Text, GetMode()))
            {
                RenderResponse(enhanced, query, enhanced: true);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_rankingGate.IsCurrent(lease, SearchBox.Text, GetMode())) return;
            _results.Clear();
            ResultsHeader.Visibility = Visibility.Collapsed;
            SetQueryStatus($"“{query}”搜索失败：{exception.Message}");
            UpdateActions();
        }
        finally { FinishSearchBusyState(lease); }
    }

    private void RenderResponse(DesktopSearchResponse response, string query, bool enhanced)
    {
        _renderingResults = true;
        try
        {
            _results.Clear();
            ResultsGrid.SelectedItem = null;
            foreach (var item in response.Items)
            {
                _results.Add(new ResultRow(item, response.Mode));
            }
        }
        finally
        {
            _renderingResults = false;
        }

        ResultsHeader.Visibility = _results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var rankingStatus = enhanced ? " · 智能排序推荐" : string.Empty;
        SetQueryStatus(response.TotalResults == 0
            ? "找到 0 项"
            : $"找到 {response.TotalResults:N0} 项{rankingStatus}");
        UpdateActions();
    }

    private void FinishSearchBusyState(RankingSearchLease lease)
    {
        if (!_rankingGate.CanFinalize(lease)) return;
        SearchProgress.Visibility = Visibility.Collapsed;
    }

    private void InvalidatePendingSearch()
    {
        _rankingGate.InvalidateQuery();
        _searchCancellation?.Cancel();
    }

    private DesktopSearchMode GetMode() => ContentMode.IsChecked == true
        ? DesktopSearchMode.Content : NameMode.IsChecked == true ? DesktopSearchMode.FileName : DesktopSearchMode.Hybrid;

    private void Reset()
    {
        _results.Clear();
        ResultsHeader.Visibility = Visibility.Collapsed;
        if (_contentStatus is null) SetQueryStatus("找到 0 项");
        else RenderContentStatus(_contentStatus);
        UpdateActions();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;

        if (e.ClickCount == 2)
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
            e.Handled = true;
            return;
        }

        DragMove();
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeRestoreButton is null) return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeRestoreButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeRestoreButton.ToolTip = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeRestoreButton, maximized ? "还原" : "最大化");
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _rankingGate.MarkInteraction();
        try
        {
            var settings = new ContentSettingsWindow(
                _search,
                _rankingCoordinator,
                _localModel,
                _deepSeekCredentials,
                _mail,
                _rankingOptions,
                _lifetime.Token) { Owner = this };
            settings.ShowDialog();
            var rankingOptionsChanged = settings.RankingOptions != _rankingOptions;
            if (rankingOptionsChanged)
            {
                _rankingOptions = settings.RankingOptions;
                _currentPreferences = _currentPreferences with { Ranking = _rankingOptions };
                _preferences.Save(_currentPreferences);
                if (_rankingOptions.LocalModelEnabled)
                {
                    _ = WarmLocalModelAsync(_lifetime.Token);
                }
            }

            if (settings.BehaviorHistoryCleared)
            {
                InvalidatePendingSearch();
                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    await SearchAsync();
                }
            }
            else if (rankingOptionsChanged)
            {
                InvalidatePendingSearch();
                if (!string.IsNullOrWhiteSpace(SearchBox.Text))
                {
                    _debounce.Stop();
                    _debounce.Start();
                }
            }
            await RefreshStatusAsync();
        }
        catch (Exception exception) { SetQueryStatus($"设置失败：{exception.Message}"); }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { e.Handled = true; try { await _contentClient.SynchronizeAsync(_lifetime.Token); await RefreshStatusAsync(); } catch (Exception ex) { SetQueryStatus($"同步失败：{ex.Message}"); } }
        else if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); }
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_renderingResults) _rankingGate.MarkInteraction();
        UpdateActions();
    }
    private void ResultsGrid_UserInteraction(object sender, InputEventArgs e) =>
        _rankingGate.MarkInteraction();
    private void ResultsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _rankingGate.MarkInteraction();
        var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            ResultsGrid.SelectedItem = null;
            return;
        }

        ResultsGrid.SelectedItem = row.Item;
        row.IsSelected = true;
        row.Focus();
    }
    private void ResultsContextMenu_Opened(object sender, RoutedEventArgs e) => UpdateActions();
    private async void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        await OpenAsync();
    private async void PreviewButton_Click(object sender, RoutedEventArgs e) => await PreviewAsync();
    private async void OpenButton_Click(object sender, RoutedEventArgs e) => await OpenAsync();
    private async void LocateButton_Click(object sender, RoutedEventArgs e) => await LocateAsync();
    private async void CopyButton_Click(object sender, RoutedEventArgs e) => await CopyReferenceAsync();

    private ResultRow? Selected => ResultsGrid.SelectedItem as ResultRow;
    private void UpdateActions()
    {
        var selected = Selected;
        PreviewMenuItem.IsEnabled = selected?.CanPreview == true;
        OpenMenuItem.IsEnabled = selected is not null;
        OpenMenuItem.Header = selected?.IsMail == true ? "在 Outlook 中打开" : "打开";
        LocateMenuItem.IsEnabled = selected is not null && !selected.IsMail;
        CopyMenuItem.IsEnabled = selected is not null;
        CopyMenuItem.Header = selected?.IsMail == true ? "复制邮件引用" : "复制路径或引用";
    }

    private static T? FindVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
    private async Task OpenAsync()
    {
        _rankingGate.MarkInteraction();
        if (Selected is not { } item) return;
        try
        {
            if (item.Value.MailIdentity is { } identity)
            {
                await _mail.OpenAsync(identity, _lifetime.Token);
            }
            else
            {
                Process.Start(new ProcessStartInfo(item.Value.FullPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
            return;
        }

        await RecordActionAsync(item, RankingActionType.Open);
    }

    private async Task LocateAsync()
    {
        _rankingGate.MarkInteraction();
        if (Selected is not { } item || item.IsMail) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe")
                { UseShellExecute = true, ArgumentList = { $"/select,{item.Value.FullPath}" } });
        }
        catch (Exception ex)
        {
            SetQueryStatus($"定位失败：{ex.Message}");
            return;
        }

        await RecordActionAsync(item, RankingActionType.Locate);
    }

    private async Task CopyReferenceAsync()
    {
        _rankingGate.MarkInteraction();
        if (Selected is not { } item) return;
        try
        {
            Clipboard.SetText(item.Reference);
            SetQueryStatus(item.IsMail ? "已复制邮件引用" : "已复制路径/位置引用");
        }
        catch (Exception ex)
        {
            SetQueryStatus($"复制失败：{ex.Message}");
            return;
        }

        await RecordActionAsync(item, RankingActionType.CopyReference);
    }

    private async Task PreviewAsync()
    {
        _rankingGate.MarkInteraction();
        if (Selected is not { } item) return;
        var window = new Window { Title = $"预览 · {item.Name}", Owner = this, Width = 760, Height = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var viewer = new FlowDocumentScrollViewer { Padding = new Thickness(20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var document = new FlowDocument();
        document.Blocks.Add(new Paragraph(new Bold(new Run(item.Name))));
        document.Blocks.Add(new Paragraph(new Run(item.Reference)) { Foreground = Brushes.SlateGray });
        document.Blocks.Add(new Paragraph(new Run(item.Snippet ?? "此结果没有正文片段。")));
        viewer.Document = document;
        window.Content = viewer;
        var rendered = false;
        window.ContentRendered += (_, _) => rendered = true;
        try
        {
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            SetQueryStatus($"预览失败：{ex.Message}");
            return;
        }

        if (!rendered) return;
        item.WasPreviewed = true;
        await RecordActionAsync(item, RankingActionType.PreviewConfirmed);
    }

    private async Task RecordActionAsync(ResultRow item, RankingActionType action)
    {
        var presentedRank = _results.IndexOf(item) + 1;
        if (presentedRank <= 0) return;
        try
        {
            await _rankingCoordinator.RecordAsync(DesktopRankingFeedbackFactory.Create(
                item.Value,
                item.Mode,
                action,
                presentedRank,
                item.WasPreviewed && action != RankingActionType.PreviewConfirmed),
                _rankingOptions,
                _lifetime.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _lifetime.Cancel(); _statusTimer.Stop(); _debounce.Stop(); _searchCancellation?.Cancel();
        _currentPreferences = _currentPreferences with
        {
            Width = ActualWidth,
            Height = ActualHeight,
            Maximized = WindowState == WindowState.Maximized,
            Ranking = _rankingOptions
        };
        _preferences.Save(_currentPreferences);
        _localModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _behaviorStore.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _mail.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _deepSeekHttpClient.Dispose();
        _nativeApi.Dispose();
        _searchCancellation?.Dispose();
        _lifetime.Dispose();
    }

    private void SetQueryStatus(string text)
    {
        QueryStatusText.Text = text;
    }

    private sealed class ResultRow
    {
        public ResultRow(DesktopSearchItem value, DesktopSearchMode mode)
        {
            Value = value;
            Mode = mode;
            Name = value.Name; FullPath = value.Detail ?? value.FullPath; Snippet = value.Snippet;
            RankingReason = value.RankingReason;
            HasRankingReason = !string.IsNullOrWhiteSpace(RankingReason);
            IsMail = value.MailIdentity is not null;
            IsContentResult = IsMail || value.MatchSource != "name" && !string.IsNullOrWhiteSpace(value.Snippet);
            Location = IsMail
                ? value.LocationLabel ?? "邮件"
                : IsContentResult
                ? value.LocationLabel ?? value.HeadingPath ?? (value.StartLine is { } line ? $"第 {line} 行" : "正文")
                : "文件名";
            Reference = value.CopyText ?? (IsContentResult && !string.IsNullOrWhiteSpace(Location)
                ? $"{FullPath} · {Location}"
                : FullPath);
            CanPreview = IsContentResult;
        }
        public DesktopSearchItem Value { get; }
        public DesktopSearchMode Mode { get; }
        public string Name { get; }
        public string FullPath { get; }
        public string? Snippet { get; }
        public string? RankingReason { get; }
        public bool HasRankingReason { get; }
        public string? SnippetToolTip => string.IsNullOrWhiteSpace(Snippet) ? null : Snippet;
        public string Location { get; }
        public string Reference { get; }
        public bool CanPreview { get; }
        public bool IsContentResult { get; }
        public bool IsMail { get; }
        public bool WasPreviewed { get; set; }
    }
}
