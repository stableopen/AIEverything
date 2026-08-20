namespace AIEverything.Server.Tests.Desktop;

public sealed class StandaloneProductContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void V020_ui_is_search_first_without_manual_roots_import_or_complex_filters()
    {
        var xaml = Read("src", "AIEverything.App", "MainWindow.xaml");
        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<shell:WindowChrome.WindowChrome>", xaml, StringComparison.Ordinal);
        Assert.Contains("Title=\"AIEverything\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId=\"TitleBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MouseLeftButtonDown=\"TitleBar_MouseLeftButtonDown\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("· 本机文件与正文搜索", xaml, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            xaml, "Text=\\\"AIEverything\\\"").Cast<System.Text.RegularExpressions.Match>());
        foreach (var id in new[] { "SettingsButton", "MinimizeButton", "MaximizeRestoreButton", "CloseButton" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SearchBox", xaml);
        Assert.Contains("全部", xaml);
        Assert.Contains("文件名", xaml);
        Assert.Contains("正文", xaml);
        Assert.Contains("预览", xaml);
        Assert.Contains("打开", xaml);
        Assert.Contains("在资源管理器中定位", xaml);
        Assert.Contains("复制路径或引用", xaml);
        Assert.Contains("ContentDisclosureBanner", xaml);
        Assert.Contains("文件名搜索已可使用", xaml);
        Assert.Contains("未单独加密的 SQLite", xaml);
        Assert.Contains("QueryStatusText", xaml);
        Assert.DoesNotContain("SearchButton", xaml);
        Assert.DoesNotContain("FooterStatusText", xaml);
        Assert.DoesNotContain("队列", xaml);
        Assert.DoesNotContain("失败", xaml);
        var mainWindowCode = Read("src", "AIEverything.App", "MainWindow.xaml.cs");
        Assert.Contains("智能排序推荐", mainWindowCode);
        Assert.DoesNotContain("SearchButton", mainWindowCode);
        var titleBarStart = xaml.IndexOf("<Border Grid.Row=\"0\"", StringComparison.Ordinal);
        var settingsButton = xaml.IndexOf("x:Name=\"SettingsButton\"", StringComparison.Ordinal);
        var minimizeButton = xaml.IndexOf("x:Name=\"MinimizeButton\"", StringComparison.Ordinal);
        var maximizeButton = xaml.IndexOf("x:Name=\"MaximizeRestoreButton\"", StringComparison.Ordinal);
        var closeButton = xaml.IndexOf("x:Name=\"CloseButton\"", StringComparison.Ordinal);
        var searchAreaStart = xaml.IndexOf("<Border Grid.Row=\"1\"", StringComparison.Ordinal);
        Assert.InRange(settingsButton, titleBarStart, searchAreaStart - 1);
        Assert.True(settingsButton < minimizeButton && minimizeButton < maximizeButton && maximizeButton < closeButton);
        var appCode = Read("src", "AIEverything.App", "App.xaml.cs");
        Assert.Contains("private const string WindowTitle = \"AIEverything\";", appCode, StringComparison.Ordinal);
        Assert.DoesNotContain("本机文件与正文搜索", appCode, StringComparison.Ordinal);
        foreach (var handler in new[]
                 {
                     "MinimizeButton_Click", "MaximizeRestoreButton_Click",
                     "CloseButton_Click", "Window_StateChanged", "TitleBar_MouseLeftButtonDown",
                     "DragMove()"
                 })
        {
            Assert.Contains(handler, mainWindowCode, StringComparison.Ordinal);
        }
        foreach (var id in new[] { "PreviewMenuItem", "OpenMenuItem", "LocateMenuItem", "CopyMenuItem" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
        foreach (var id in new[] { "PreviewButton", "OpenButton", "LocateButton", "CopyButton" })
        {
            Assert.DoesNotContain($"x:Name=\"{id}\"", xaml, StringComparison.Ordinal);
        }
        var smoke = Read("scripts", "smoke-compact-ui.ps1");
        Assert.Contains("Invoke-RightClick", smoke, StringComparison.Ordinal);
        Assert.Contains("Removed footer action is still present", smoke, StringComparison.Ordinal);
        foreach (var id in new[] { "PreviewMenuItem", "OpenMenuItem", "LocateMenuItem", "CopyMenuItem" })
            Assert.Contains(id, smoke, StringComparison.Ordinal);
        Assert.Contains("ResultsGrid_PreviewMouseRightButtonDown", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("ResultsContextMenu_Opened", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("PreviewMenuItem.IsEnabled", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBoxButton.YesNoCancel", Read("src", "AIEverything.App", "MainWindow.xaml.cs"));
        var settings = Read("src", "AIEverything.App", "ContentSettingsWindow.xaml");
        foreach (var id in new[] { "ContentSettingsWindow", "SettingsStatusText", "SettingsToggleButton", "SettingsSyncButton", "SettingsDatabasePathText", "SettingsCloseButton" })
            Assert.Contains(id, settings, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "Outlook", "Local Import", "Inbox", "剪贴板", "Teams", "微信", "AddRoot", "RootList", "FileTypeBox", "ModifiedBox", "IndexPanel" })
            Assert.DoesNotContain(forbidden, xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V020_runtime_has_no_outlook_local_import_or_manual_root_source_files()
    {
        foreach (var name in new[] { "OutlookComSelectionSource.cs", "OutlookExplorerAcquirer.cs", "OutlookImportService.cs", "LocalImportService.cs" })
            Assert.False(File.Exists(Path.Combine(Root, "src", "AIEverything.Desktop", name)), name);
        var daemon = Read("src", "AIEverything.Daemon", "ContentDaemon.cs");
        Assert.DoesNotContain("root.add", daemon, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("root.remove", daemon, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Directory.Enumerate", daemon, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("File.GetAttributes", daemon, StringComparison.Ordinal);
        Assert.Contains("SearchRaw", daemon, StringComparison.Ordinal);
        Assert.Contains("item.Attributes", daemon, StringComparison.Ordinal);
        var everything = Read("src", "AIEverything.Server", "Everything", "EverythingNativeApi.cs");
        Assert.Contains("RequestAttributes", everything, StringComparison.Ordinal);
        Assert.Contains("Everything_GetResultAttributes", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void V101_body_contract_names_only_txt_md_markdown_and_docx()
    {
        var policy = Read("src", "AIEverything.Content", "MachineIndex", "MachineTextIndexPolicy.cs");
        Assert.Contains("[\".txt\"]", policy);
        Assert.Contains("[\".md\"]", policy);
        Assert.Contains("[\".markdown\"]", policy);
        Assert.Contains("[\".docx\"]", policy);
        foreach (var forbidden in new[] { "[\".rst\"]", "[\".pdf\"]", "[\".xlsx\"]", "[\".pptx\"]" })
            Assert.DoesNotContain(forbidden, policy);
    }

    [Fact]
    public void V101_status_and_feedback_are_short_and_actionable()
    {
        var main = Read("src", "AIEverything.App", "MainWindow.xaml.cs");
        Assert.Contains("文件名搜索已就绪。开启正文索引后可搜索 Word、TXT 和 Markdown。", main, StringComparison.Ordinal);
        Assert.Contains("正在建立正文索引，已有 {status.IndexedDocuments:N0} 个文件可搜索。", main, StringComparison.Ordinal);
        Assert.Contains("正文索引已暂停，已有内容仍可搜索。", main, StringComparison.Ordinal);
        Assert.Contains("文件名服务暂时不可用，正在重试；已有正文仍可搜索。", main, StringComparison.Ordinal);
        Assert.Contains("正文索引已完成，部分文件未处理，请在设置中查看。", main, StringComparison.Ordinal);

        var settings = Read("src", "AIEverything.App", "ContentSettingsWindow.xaml");
        foreach (var id in new[] { "SettingsFailureGroupsText", "SettingsRetryFailuresButton", "SettingsReportProblemButton" })
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", settings, StringComparison.Ordinal);
        foreach (var label in new[] { "损坏", "加密/不支持", "过大", "超时", "无权限" })
            Assert.Contains(label, settings, StringComparison.Ordinal);
        Assert.Contains("https://github.com/stableye/AIEverything/issues/new", Read("src", "AIEverything.App", "ContentSettingsWindow.xaml.cs"), StringComparison.Ordinal);

        var daemon = Read("src", "AIEverything.Daemon", "ContentDaemon.cs");
        Assert.Contains("index.failures.retry", daemon, StringComparison.Ordinal);
    }

    [Fact]
    public void V021_ranking_settings_are_scrollable_disclosed_and_safe_by_default()
    {
        var settings = Read("src", "AIEverything.App", "ContentSettingsWindow.xaml");
        Assert.Contains("<ScrollViewer", settings, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", settings, StringComparison.Ordinal);
        foreach (var id in new[]
                 {
                     "SettingsScrollViewer", "BehaviorRankingToggle", "ClearBehaviorButton", "LocalModelToggle",
                     "LocalModelStatusText", "DeepSeekDisclosureCheck", "DeepSeekDisclosureText",
                     "DeepSeekToggle", "DeepSeekStatusText", "DeepSeekApiKeyBox",
                     "SaveDeepSeekCredentialButton"
                 })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", settings, StringComparison.Ordinal);
        }

        Assert.Contains("本地 ONNX 成功、无精确匹配且至少有 3 个普通候选", settings, StringComparison.Ordinal);
        Assert.Contains("分数接近、重名、名称/正文混合证据或自然语言歧义", settings, StringComparison.Ordinal);
        Assert.Contains("明确文件名或路径查询绝不联网", settings, StringComparison.Ordinal);
        Assert.Contains("文件名、完整路径和每项最多 200 字片段", settings, StringComparison.Ordinal);
        Assert.Contains("不发送匹配来源、排序层级、文件本体或行为历史", settings, StringComparison.Ordinal);
        Assert.Contains("启用 DeepSeek 歧义重排（默认关闭）", settings, StringComparison.Ordinal);
        Assert.Contains("总预算 1.5 秒", settings, StringComparison.Ordinal);
        Assert.Contains("滚动每分钟最多 10 次", settings, StringComparison.Ordinal);
        Assert.Contains("会话缓存 10 分钟", settings, StringComparison.Ordinal);
        Assert.Contains("不重试", settings, StringComparison.Ordinal);
        Assert.Contains("设置文件、ranking.db 与日志均不保存密钥", settings, StringComparison.Ordinal);
        Assert.Contains("<PasswordBox", settings, StringComparison.Ordinal);

        var credentialProvider = Read(
            "src", "AIEverything.Desktop", "Ranking", "DeepSeekCloudReranker.cs");
        Assert.Contains("CredWriteW", credentialProvider, StringComparison.Ordinal);
        Assert.Contains("#if DEBUG", credentialProvider, StringComparison.Ordinal);
        Assert.Contains("static _ => null", credentialProvider, StringComparison.Ordinal);

        Assert.Contains("随机盐加盐的文件键、父目录键、扩展名和每日权重聚合", settings, StringComparison.Ordinal);
        Assert.Contains("单独预览不计分", settings, StringComparison.Ordinal);
        Assert.Contains("清除会删除聚合并轮换随机盐", settings, StringComparison.Ordinal);

        var rankingModels = Read("src", "AIEverything.Desktop", "Ranking", "RankingModels.cs");
        Assert.Contains("new(true, true, false, false)", rankingModels, StringComparison.Ordinal);
        var preferences = Read("src", "AIEverything.Desktop", "DesktopPreferencesStore.cs");
        Assert.Contains(
            "DeepSeekEnabled = ranking.DeepSeekDisclosureAccepted && ranking.DeepSeekEnabled",
            preferences,
            StringComparison.Ordinal);
    }

    [Fact]
    public void V021_successful_behavior_clear_forces_the_main_window_to_refresh_the_active_query()
    {
        var settings = Read("src", "AIEverything.App", "ContentSettingsWindow.xaml.cs");
        Assert.Contains(
            "public bool BehaviorHistoryCleared { get; private set; }",
            settings,
            StringComparison.Ordinal);
        Assert.Contains("BehaviorHistoryCleared = true;", settings, StringComparison.Ordinal);

        var main = Read("src", "AIEverything.App", "MainWindow.xaml.cs");
        Assert.Contains("if (settings.BehaviorHistoryCleared)", main, StringComparison.Ordinal);
        Assert.Contains("await SearchAsync();", main, StringComparison.Ordinal);
    }

    [Fact]
    public void V021_behavior_disclosure_is_non_modal_persistent_and_ranking_reasons_are_visible()
    {
        var xaml = Read("src", "AIEverything.App", "MainWindow.xaml");
        foreach (var id in new[]
                 {
                     "BehaviorDisclosureBanner", "AcknowledgeBehaviorButton",
                     "DisableBehaviorButton", "BehaviorSettingsButton", "RankingReasonBadge"
                 })
        {
            Assert.Contains($"AutomationProperties.AutomationId=\"{id}\"", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("本地排序学习默认开启", xaml, StringComparison.Ordinal);
        Assert.Contains("只记录成功操作的每日权重聚合", xaml, StringComparison.Ordinal);
        Assert.Contains("保留 30 天", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding RankingReason}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding HasRankingReason}\"", xaml, StringComparison.Ordinal);

        var main = Read("src", "AIEverything.App", "MainWindow.xaml.cs");
        Assert.Contains("BehaviorDisclosureAcknowledged", main, StringComparison.Ordinal);
        Assert.Contains("RankingReason = value.RankingReason", main, StringComparison.Ordinal);
        Assert.Contains("HasRankingReason", main, StringComparison.Ordinal);

        var preferences = Read("src", "AIEverything.Desktop", "DesktopPreferencesStore.cs");
        Assert.Contains("bool BehaviorDisclosureAcknowledged", preferences, StringComparison.Ordinal);
        var behavior = Read("src", "AIEverything.Desktop", "Ranking", "SqliteRankingBehaviorStore.cs");
        Assert.Contains("\"最近常用\"", behavior, StringComparison.Ordinal);
        var coordinator = Read("src", "AIEverything.Desktop", "Ranking", "DesktopRankingCoordinator.cs");
        Assert.Contains("\\u672c\\u5730\\u8bed\\u4e49\\u5339\\u914d", coordinator, StringComparison.Ordinal);
        Assert.Contains("AI\\u4f18\\u5316", coordinator, StringComparison.Ordinal);
    }

    [Fact]
    public void V021_release_identity_model_integrity_and_privacy_are_packaging_contracts()
    {
        var appProject = Read("src", "AIEverything.App", "AIEverything.App.csproj");
        var daemonProject = Read("src", "AIEverything.Daemon", "AIEverything.Daemon.csproj");
        var workerProject = Read(
            "src", "AIEverything.ExtractorWorker", "AIEverything.ExtractorWorker.csproj");
        foreach (var project in new[] { appProject, daemonProject, workerProject })
        {
            Assert.Contains("<Version>1.0.0</Version>", project, StringComparison.Ordinal);
            Assert.Contains("<AssemblyVersion>1.0.0.0</AssemblyVersion>", project, StringComparison.Ordinal);
            Assert.Contains("<FileVersion>1.0.0.0</FileVersion>", project, StringComparison.Ordinal);
        }
        Assert.Contains("Models\\mmarco-mMiniLMv2-L12-H384-v1", appProject, StringComparison.Ordinal);
        Assert.Contains("ExcludeFromSingleFile=\"true\"", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Models\\mmarco-mMiniLMv2-L12-H384-v1",
            Read("src", "AIEverything.Desktop", "AIEverything.Desktop.csproj"),
            StringComparison.Ordinal);

        var build = Read("scripts", "build-standalone.ps1");
        Assert.Contains("AIEverything-1.0.0-win-x64.zip", build, StringComparison.Ordinal);
        Assert.Contains("AIEverything-V0.21-win-x64.zip", build, StringComparison.Ordinal);
        Assert.Contains("8FC8801E143F6D20E9D68D78ECF401CCAF7C3E7CCFC8E66D2BFEE43EA10F54D2", build, StringComparison.Ordinal);
        Assert.Contains("Assert-ModelAssets", build, StringComparison.Ordinal);
        Assert.Contains("Assert-ZipMatchesDirectory", build, StringComparison.Ordinal);
        Assert.Contains("F143532D288194D1BF9B81486301D160ABCBC22E78FFE60D6C0C15CA7CA0DF46", build, StringComparison.Ordinal);
        Assert.Contains("EAD417D6B45DAB2AA79A10F171493AC9AE41848643193F30EA08FB16319BC657", build, StringComparison.Ordinal);
        Assert.Contains("continue", build, StringComparison.Ordinal);
        Assert.Contains("scripts\\fetch-model.ps1", build, StringComparison.Ordinal);
        foreach (var executable in new[]
                 {
                     "AIEverything.exe", "AIEverything.Daemon.exe",
                     "AIEverything.ExtractorWorker.exe"
                 })
        {
            Assert.Contains($"'{executable}'", build, StringComparison.Ordinal);
        }

        var modelRoot = Path.Combine(
            Root, "src", "AIEverything.Desktop", "Models", "mmarco-mMiniLMv2-L12-H384-v1");
        var required = new[]
        {
            "config.json", "LICENSE.apache-2.0.txt", "MODEL_CARD.md",
            "model_quint8_avx2.onnx", "model-calibration.json", "model-manifest.json",
            "sentencepiece.bpe.model", "SHA256SUMS.txt", "special_tokens_map.json",
            "tokenizer_config.json"
        };
        Assert.Equal(required.Order(), Directory.GetFiles(modelRoot).Select(Path.GetFileName).Order());

        using var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(modelRoot, "model-manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("1427fd652930e4ba29e8149678df786c240d8825",
            root.GetProperty("revision").GetString());
        Assert.Equal("Apache-2.0", root.GetProperty("license").GetString());
        foreach (var entry in root.GetProperty("files").EnumerateArray())
        {
            var path = Path.Combine(modelRoot, entry.GetProperty("path").GetString()!);
            Assert.Equal(entry.GetProperty("bytes").GetInt64(), new FileInfo(path).Length);
            using var stream = File.OpenRead(path);
            Assert.Equal(
                entry.GetProperty("sha256").GetString(),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)));
        }

        var readme = Read("README.md");
        var portableReadme = Read("docs", "STANDALONE-README.txt");
        var privacy = Read("PRIVACY.md");
        Assert.Contains("DeepSeek 默认关闭", readme, StringComparison.Ordinal);
        Assert.Contains("本地 MiniLM", readme, StringComparison.Ordinal);
        foreach (var text in new[] { portableReadme, privacy })
        {
            Assert.Contains("DeepSeek 默认关闭", text, StringComparison.Ordinal);
            Assert.Contains("最多 200 字片段", text, StringComparison.Ordinal);
            Assert.Contains("至少 3 个 Eligible", text, StringComparison.Ordinal);
            Assert.Contains("无 Exact", text, StringComparison.Ordinal);
            Assert.Contains("不发送匹配来源或排序层级", text, StringComparison.Ordinal);
            Assert.Contains("1.5 秒", text, StringComparison.Ordinal);
            Assert.Contains("24 KiB", text, StringComparison.Ordinal);
            Assert.Contains("每分钟最多 10 次", text, StringComparison.Ordinal);
            Assert.Contains("缓存 10 分钟", text, StringComparison.Ordinal);
            Assert.Contains("熔断 30 秒", text, StringComparison.Ordinal);
            Assert.Contains("不重试", text, StringComparison.Ordinal);
            Assert.Contains("Windows 凭据管理器", text, StringComparison.Ordinal);
            Assert.Contains("发布版不", text, StringComparison.Ordinal);
            Assert.Contains("环境变量", text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("AIEverything 不联网", portableReadme, StringComparison.Ordinal);

        var notices = Read("THIRD_PARTY_NOTICES.md");
        Assert.Contains("Microsoft.ML.OnnxRuntime` 1.29.0", notices, StringComparison.Ordinal);
        Assert.Contains("Microsoft.ML.Tokenizers` 2.0.0", notices, StringComparison.Ordinal);
        Assert.Contains("mmarco-mMiniLMv2-L12-H384-v1", notices, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root, .. path]));
    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AIEverything.sln"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
