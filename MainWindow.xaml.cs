using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiteDocViewer.Models;
using LiteDocViewer.Services;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LiteDocViewer;

public partial class MainWindow : Window
{
    private const string VirtualHostName = "local-preview.test";
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".html", ".htm" };

    private readonly ObservableCollection<FileTreeItem> _treeItems = [];
    private readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private FileSystemWatcher? _watcher;
    private string? _rootFolder;
    private string? _currentDocumentPath;
    private bool _webViewReady;
    private bool _isInternalMarkdownNavigation;

    public MainWindow()
    {
        InitializeComponent();
        FileTree.ItemsSource = _treeItems;
        _settings = AppSettingsService.Load();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick += RefreshTimer_Tick;

        ApplyTheme();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var webViewDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiteDocViewer", "WebView2");
            Directory.CreateDirectory(webViewDataFolder);
            var webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataFolder);
            await Preview.EnsureCoreWebView2Async(webViewEnvironment);
            _webViewReady = true;
            Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Preview.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Preview.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;
            Preview.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Preview.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Preview.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            Preview.CoreWebView2.DownloadStarting += (_, args) => args.Cancel = true;
            UpdateWebViewTheme();

            if (!string.IsNullOrWhiteSpace(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
                OpenFolder(_settings.LastFolder);
        }
        catch (Exception ex)
        {
            ShowError("WebView2를 초기화할 수 없습니다. WebView2 Runtime 설치 상태를 확인하세요.", ex);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Markdown/HTML 문서 폴더 선택",
            Multiselect = false,
            InitialDirectory = Directory.Exists(_rootFolder) ? _rootFolder : null
        };

        if (dialog.ShowDialog(this) == true)
            OpenFolder(dialog.FolderName);
    }

    private void OpenFolder(string folder)
    {
        var normalizedFolder = Path.GetFullPath(folder);
        _rootFolder = normalizedFolder;
        _settings.LastFolder = normalizedFolder;
        AppSettingsService.Save(_settings);

        if (_webViewReady)
        {
            Preview.CoreWebView2.ClearVirtualHostNameToFolderMapping(VirtualHostName);
            Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName, normalizedFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        }

        BuildTree();
        StartWatching(normalizedFolder);
        PathText.Text = normalizedFolder;
        StatusText.Text = $"{CountDocuments(_treeItems)}개 문서";
    }

    private void BuildTree()
    {
        _treeItems.Clear();
        if (_rootFolder is null || !Directory.Exists(_rootFolder))
            return;

        var root = BuildDirectoryNode(new DirectoryInfo(_rootFolder), true);
        if (root is not null)
            _treeItems.Add(root);
    }

    private static FileTreeItem? BuildDirectoryNode(DirectoryInfo directory, bool isRoot = false)
    {
        var node = new FileTreeItem
        {
            Name = directory.Name,
            FullPath = directory.FullName,
            IsDirectory = true,
            IsExpanded = isRoot
        };

        try
        {
            foreach (var childDirectory in directory.EnumerateDirectories()
                         .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                         .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var childNode = BuildDirectoryNode(childDirectory);
                if (childNode is not null)
                    node.Children.Add(childNode);
            }

            foreach (var file in directory.EnumerateFiles()
                         .Where(f => SupportedExtensions.Contains(f.Extension))
                         .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                node.Children.Add(new FileTreeItem
                {
                    Name = file.Name,
                    FullPath = file.FullName,
                    IsDirectory = false
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return node.Children.Count > 0 || isRoot ? node : null;
    }

    private async void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileTreeItem { IsDirectory: false } item)
            await DisplayDocumentAsync(item.FullPath);
    }

    private async Task DisplayDocumentAsync(string path)
    {
        if (!_webViewReady || _rootFolder is null || !File.Exists(path) || !IsInsideRoot(path))
            return;

        _currentDocumentPath = path;
        PathText.Text = path;
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = "불러오는 중…";

        var extension = Path.GetExtension(path);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var markdown = await File.ReadAllTextAsync(path);
                var body = Markdown.ToHtml(markdown, _markdownPipeline);
                var relativeDirectory = Path.GetRelativePath(_rootFolder, Path.GetDirectoryName(path) ?? _rootFolder)
                    .Replace('\\', '/');
                var baseUrl = relativeDirectory == "."
                    ? $"https://{VirtualHostName}/"
                    : $"https://{VirtualHostName}/{EncodeRelativePath(relativeDirectory)}/";

                _isInternalMarkdownNavigation = true;
                Preview.NavigateToString(BuildMarkdownPage(body, baseUrl, Path.GetFileName(path)));
                _isInternalMarkdownNavigation = false;
            }
            catch (Exception ex)
            {
                ShowError("Markdown 파일을 읽을 수 없습니다.", ex);
            }
        }
        else
        {
            var relativePath = Path.GetRelativePath(_rootFolder, path).Replace('\\', '/');
            Preview.CoreWebView2.Navigate($"https://{VirtualHostName}/{EncodeRelativePath(relativePath)}");
        }
    }

    private string BuildMarkdownPage(string body, string baseUrl, string title)
    {
        var dark = _settings.IsDarkTheme;
        var background = dark ? "#0d1117" : "#ffffff";
        var foreground = dark ? "#e6edf3" : "#24292f";
        var muted = dark ? "#8b949e" : "#57606a";
        var border = dark ? "#30363d" : "#d0d7de";
        var code = dark ? "#161b22" : "#f6f8fa";
        var link = dark ? "#58a6ff" : "#0969da";

        return $$"""
            <!doctype html>
            <html lang="ko">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <base href="{{WebUtility.HtmlEncode(baseUrl)}}">
              <title>{{WebUtility.HtmlEncode(title)}}</title>
              <style>
                :root { color-scheme: {{(dark ? "dark" : "light")}}; }
                * { box-sizing: border-box; }
                body { max-width: 980px; margin: 0 auto; padding: 36px 48px 80px; background: {{background}}; color: {{foreground}}; font: 16px/1.65 -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; overflow-wrap: break-word; }
                h1, h2 { border-bottom: 1px solid {{border}}; padding-bottom: .3em; }
                h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.5em 0 .65em; }
                h1:first-child { margin-top: 0; }
                a { color: {{link}}; text-decoration: none; } a:hover { text-decoration: underline; }
                img, video { max-width: 100%; height: auto; }
                pre { padding: 16px; overflow: auto; background: {{code}}; border-radius: 6px; }
                code { font-family: Consolas, "Cascadia Code", monospace; background: {{code}}; border-radius: 4px; padding: .15em .35em; }
                pre code { padding: 0; background: transparent; }
                blockquote { margin-left: 0; padding: 0 1em; color: {{muted}}; border-left: .25em solid {{border}}; }
                table { border-collapse: collapse; display: block; width: max-content; max-width: 100%; overflow: auto; }
                th, td { padding: 6px 13px; border: 1px solid {{border}}; }
                tr:nth-child(2n) { background: {{code}}; }
                hr { height: 1px; border: 0; background: {{border}}; }
                input[type=checkbox] { margin-right: .5em; }
                @media (max-width: 700px) { body { padding: 24px 22px 60px; } }
              </style>
            </head>
            <body>{{body}}</body>
            </html>
            """;
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_isInternalMarkdownNavigation || e.Uri == "about:blank" || e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        if (TryMapLocalUrl(e.Uri, out var localPath))
        {
            var extension = Path.GetExtension(localPath);
            if (SupportedExtensions.Contains(extension) &&
                (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)))
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(async () => await DisplayDocumentAsync(localPath));
            }
            else if (!IsInsideRoot(localPath))
            {
                e.Cancel = true;
            }
            else if (SupportedExtensions.Contains(extension))
            {
                _currentDocumentPath = localPath;
                PathText.Text = localPath;
            }

            return;
        }

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            e.Cancel = true;
            OpenInDefaultBrowser(e.Uri);
        }
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (TryMapLocalUrl(e.Uri, out var localPath) && IsInsideRoot(localPath))
            _ = DisplayDocumentAsync(localPath);
        else
            OpenInDefaultBrowser(e.Uri);
    }

    private void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        BackButton.IsEnabled = Preview.CanGoBack;
        ForwardButton.IsEnabled = Preview.CanGoForward;
        StatusText.Text = e.IsSuccess ? "준비" : $"페이지 오류: {e.WebErrorStatus}";
    }

    private bool TryMapLocalUrl(string url, out string path)
    {
        path = string.Empty;
        if (_rootFolder is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals(VirtualHostName, StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);
            path = Path.GetFullPath(Path.Combine(_rootFolder, relative));
            return true;
        }
        catch (Exception)
        {
            path = string.Empty;
            return false;
        }
    }

    private bool IsInsideRoot(string path)
    {
        if (_rootFolder is null)
            return false;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_rootFolder));
        var candidate = Path.GetFullPath(path);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string EncodeRelativePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

    private static void OpenInDefaultBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Invalid or blocked links are ignored rather than crashing the viewer.
        }
    }

    private void StartWatching(string folder)
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += Watcher_Changed;
        _watcher.Created += Watcher_Changed;
        _watcher.Deleted += Watcher_Changed;
        _watcher.Renamed += Watcher_Changed;
    }

    private void Watcher_Changed(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        BuildTree();
        if (_currentDocumentPath is not null && File.Exists(_currentDocumentPath))
            await DisplayDocumentAsync(_currentDocumentPath);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshCurrentAsync();

    private async Task RefreshCurrentAsync()
    {
        BuildTree();
        if (_currentDocumentPath is not null && File.Exists(_currentDocumentPath))
            await DisplayDocumentAsync(_currentDocumentPath);
        else if (_webViewReady && Preview.Source is not null)
            Preview.Reload();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Preview.CanGoBack) Preview.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Preview.CanGoForward) Preview.GoForward();
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.IsDarkTheme = !_settings.IsDarkTheme;
        AppSettingsService.Save(_settings);
        ApplyTheme();
        UpdateWebViewTheme();
        if (_currentDocumentPath is not null && File.Exists(_currentDocumentPath) &&
            (Path.GetExtension(_currentDocumentPath).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
             Path.GetExtension(_currentDocumentPath).Equals(".markdown", StringComparison.OrdinalIgnoreCase)))
            _ = DisplayDocumentAsync(_currentDocumentPath);
    }

    private void ApplyTheme()
    {
        var dark = _settings.IsDarkTheme;
        SetBrush("WindowBackgroundBrush", dark ? "#0D1117" : "#F7F8FA");
        SetBrush("PanelBackgroundBrush", dark ? "#161B22" : "#FFFFFF");
        SetBrush("ToolbarBackgroundBrush", dark ? "#161B22" : "#FFFFFF");
        SetBrush("BorderBrush", dark ? "#30363D" : "#E1E4E8");
        SetBrush("PrimaryTextBrush", dark ? "#E6EDF3" : "#24292F");
        SetBrush("SecondaryTextBrush", dark ? "#8B949E" : "#6E7781");
        SetBrush("AccentBrush", dark ? "#58A6FF" : "#0969DA");
        SetBrush("HoverBrush", dark ? "#21262D" : "#F0F3F6");
        ThemeIcon.Text = dark ? "\uE708" : "\uE706";
        Preview.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(13, 17, 23) : System.Drawing.Color.White;
    }

    private void UpdateWebViewTheme()
    {
        if (_webViewReady)
            Preview.CoreWebView2.Profile.PreferredColorScheme = _settings.IsDarkTheme
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
    }

    private static void SetBrush(string key, string color) =>
        CurrentApp.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private static Application CurrentApp => Application.Current;

    private static int CountDocuments(IEnumerable<FileTreeItem> items) =>
        items.Sum(item => item.IsDirectory ? CountDocuments(item.Children) : 1);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OpenFolder_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = RefreshCurrentAsync();
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _watcher?.Dispose();
        AppSettingsService.Save(_settings);
    }

    private void ShowError(string message, Exception exception)
    {
        StatusText.Text = message;
        MessageBox.Show(this, $"{message}\n\n{exception.Message}", "LiteDoc Viewer",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
