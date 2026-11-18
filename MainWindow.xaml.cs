using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LiteDocViewer.Models;
using LiteDocViewer.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LiteDocViewer;

public partial class MainWindow : Window
{
    private const string VirtualHostName = "local-preview.test";
    private const string DocumentHostName = "document-preview.test";

    private readonly ObservableCollection<FileTreeItem> _treeItems = [];
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private readonly string? _startupFile;
    private readonly HashSet<string> _changedFolders = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private FileTreeItem? _folderRoot;
    private string? _previewFolder;
    private string? _currentDocumentPath;
    private string? _changedDocumentPath;
    private int _previewMappingVersion;
    private bool _webViewReady;
    private ViewerTheme _theme;
    private int _themeChangeVersion;

    public MainWindow(string? startupFile)
    {
        InitializeComponent();
        _startupFile = startupFile;
        FileTree.ItemsSource = _treeItems;
        _settings = AppSettingsService.Load();
        _theme = ViewerTheme.All.FirstOrDefault(theme => theme.Id == _settings.Theme) ??
            ViewerTheme.All[_settings.IsDarkTheme ? 1 : 0];
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _refreshTimer.Tick += RefreshTimer_Tick;

        InitializeThemeMenu();
        ApplyTheme();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
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
            Preview.CoreWebView2.AddWebResourceRequestedFilter($"https://{DocumentHostName}/*",
                CoreWebView2WebResourceContext.Document, CoreWebView2WebResourceRequestSourceKinds.All);
            Preview.CoreWebView2.WebResourceRequested += DocumentPreview_Requested;
            UpdateWebViewTheme();
        }
        catch (Exception ex)
        {
            ShowError("WebView2를 초기화할 수 없습니다. WebView2 Runtime 설치 상태를 확인하세요.", ex);
            return;
        }

        OpenFolderButton.IsEnabled = true;
        if (_startupFile is not null)
            OpenSingleDocument(_startupFile);
        else if (Directory.Exists(_settings.LastFolder))
            OpenFolder(_settings.LastFolder!);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!_webViewReady)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "문서 폴더 선택",
            Multiselect = false,
            InitialDirectory = _folderRoot?.FullPath ?? _previewFolder
        };

        if (dialog.ShowDialog(this) == true)
            OpenFolder(dialog.FolderName);
    }

    private void OpenDocumentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocumentPath is not null && Path.GetDirectoryName(_currentDocumentPath) is { } folder)
            OpenFolder(folder);
    }

    private void OpenSingleDocument(string file)
    {
        string path;
        try
        {
            path = Path.GetFullPath(file);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            StatusText.Text = "파일 경로를 확인하세요.";
            return;
        }

        if (!DocumentPreview.IsSupported(path) || !File.Exists(path))
        {
            StatusText.Text = "지원하는 문서 파일을 찾을 수 없습니다.";
            return;
        }

        SetPreviewFolder(Path.GetDirectoryName(path)!);
        _treeItems.Add(new FileTreeItem { Name = Path.GetFileName(path), FullPath = path });
        DisplayDocument(path);
    }

    private void OpenFolder(string folder)
    {
        var normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (_folderRoot?.FullPath.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase) == true)
        {
            _ = RefreshExpandedFoldersAsync(_folderRoot);
            return;
        }

        var keepDocument = _currentDocumentPath is not null &&
            IsInsideFolder(_currentDocumentPath, normalizedFolder);
        if (!keepDocument && !TryCloseEditor(refreshPreview: false))
            return;

        var changePreviewFolder = !keepDocument || !IsInsidePreviewFolder(normalizedFolder);
        if (changePreviewFolder)
            SetPreviewFolder(normalizedFolder);

        if (!keepDocument)
        {
            SetCurrentDocument(null);
            PathText.Text = normalizedFolder;
            StatusText.Text = "준비";
            EmptyState.Visibility = Visibility.Visible;
            if (Preview.Source is not null)
                Preview.CoreWebView2.Navigate("about:blank");
        }

        _settings.LastFolder = normalizedFolder;
        AppSettingsService.Save(_settings);

        var root = CreateDirectoryItem(new DirectoryInfo(normalizedFolder), isExpanded: true);
        _folderRoot = root;
        StartWatching(_previewFolder!, includeSubdirectories: true);
        _treeItems.Clear();
        _treeItems.Add(root);

        if (changePreviewFolder && _currentDocumentPath is not null)
            DisplayDocument(_currentDocumentPath);
    }

    private void SetPreviewFolder(string folder)
    {
        if (string.Equals(_previewFolder, folder, StringComparison.OrdinalIgnoreCase))
            return;

        Preview.CoreWebView2.ClearVirtualHostNameToFolderMapping(VirtualHostName);
        Preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
            VirtualHostName, folder, CoreWebView2HostResourceAccessKind.DenyCors);
        _previewFolder = folder;
        ++_previewMappingVersion;
    }

    private static FileTreeItem CreateDirectoryItem(DirectoryInfo directory, bool isExpanded = false)
    {
        var item = new FileTreeItem
        {
            Name = directory.Name,
            FullPath = directory.FullName,
            IsDirectory = true,
            IsExpanded = isExpanded
        };
        item.Children.Add(CreatePlaceholder(directory.FullName, "불러오는 중…"));
        return item;
    }

    private static FileTreeItem CreatePlaceholder(string folder, string message) =>
        new() { Name = message, FullPath = folder, IsPlaceholder = true };

    private static List<FileTreeItem> ReadDirectory(string path) =>
        new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Where(entry => entry is DirectoryInfo
                ? (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
                : DocumentPreview.IsSupported(entry.Name))
            .OrderBy(entry => entry is FileInfo)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => entry is DirectoryInfo directory
                ? CreateDirectoryItem(directory)
                : new FileTreeItem { Name = entry.Name, FullPath = entry.FullName })
            .ToList();

    private async void FileTree_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: FileTreeItem { IsDirectory: true } folder })
            await LoadDirectoryAsync(folder);
    }

    private async Task LoadDirectoryAsync(FileTreeItem folder)
    {
        var loadVersion = ++folder.LoadVersion;
        List<FileTreeItem> entries;
        try
        {
            entries = await Task.Run(() => ReadDirectory(folder.FullPath));
            if (entries.Count == 0)
                entries.Add(CreatePlaceholder(folder.FullPath, "문서나 하위 폴더가 없습니다."));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            entries = [CreatePlaceholder(folder.FullPath, "폴더를 읽을 수 없습니다.")];
        }

        if (loadVersion != folder.LoadVersion)
            return;

        // Reuse existing items so a refresh preserves the selection and expanded folders.
        var entriesByKey = entries.ToDictionary(TreeItemKey);
        var children = folder.Children;
        for (var i = children.Count - 1; i >= 0; --i)
        {
            if (!entriesByKey.ContainsKey(TreeItemKey(children[i])))
                children.RemoveAt(i);
        }

        var existing = children.ToDictionary(TreeItemKey);
        for (var i = 0; i < entries.Count; ++i)
        {
            var entry = entries[i];
            if (!existing.TryGetValue(TreeItemKey(entry), out var current))
                children.Insert(i, entry);
            else if (!ReferenceEquals(children[i], current))
                children.Move(children.IndexOf(current), i);
        }
    }

    private static (string Name, bool IsDirectory, bool IsPlaceholder) TreeItemKey(FileTreeItem item) =>
        (item.Name, item.IsDirectory, item.IsPlaceholder);

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_restoringTreeSelection || e.NewValue is not FileTreeItem { IsDirectory: false, IsPlaceholder: false } item)
            return;

        if (_editDocument is not null)
        {
            if (string.Equals(_editDocument.Path, item.FullPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (!TryCloseEditor(refreshPreview: false))
            {
                RestoreTreeSelection(e.OldValue as FileTreeItem, item);
                return;
            }
        }

        DisplayDocument(item.FullPath);
    }

    private void SetCurrentDocument(string? path)
    {
        var changed = !string.Equals(_currentDocumentPath, path, StringComparison.OrdinalIgnoreCase);
        _currentDocumentPath = path;
        OpenDocumentFolderButton.IsEnabled = path is not null;
        UpdateEditButton();
        if (path is not null)
        {
            PathText.Text = path;
            if (changed && _folderRoot is null)
                StartWatching(Path.GetDirectoryName(path)!, includeSubdirectories: false, filter: Path.GetFileName(path));
        }
    }

    private void DisplayDocument(string path)
    {
        if (_editDocument is not null || !_webViewReady || _previewFolder is null || !DocumentPreview.IsSupported(path) ||
            !IsInsidePreviewFolder(path) || !File.Exists(path))
            return;

        SetCurrentDocument(path);
        EmptyState.Visibility = Visibility.Collapsed;
        StatusText.Text = "불러오는 중…";

        var relativePath = EncodeRelativePath(Path.GetRelativePath(_previewFolder, path).Replace('\\', '/'));
        var url = DocumentPreview.IsGenerated(path)
            ? $"https://{DocumentHostName}/{_previewMappingVersion}/{relativePath}"
            : $"https://{VirtualHostName}/{relativePath}";
        Preview.CoreWebView2.Navigate(url);
    }

    private async void DocumentPreview_Requested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var environment = Preview.CoreWebView2.Environment;
        var deferral = e.GetDeferral();
        try
        {
            var status = 404;
            var contentType = "text/plain";
            var body = "문서를 읽을 수 없습니다.";

            if (_previewFolder is { } previewFolder && TryMapDocumentUrl(e.Request.Uri, out var path))
            {
                var theme = _theme;
                var relativeFolder = Path.GetRelativePath(previewFolder, Path.GetDirectoryName(path)!).Replace('\\', '/');
                var baseUrl = relativeFolder == "."
                    ? $"https://{VirtualHostName}/"
                    : $"https://{VirtualHostName}/{EncodeRelativePath(relativeFolder)}/";
                try
                {
                    var text = await File.ReadAllTextAsync(path);
                    body = await Task.Run(() => DocumentPreview.BuildPage(path, baseUrl, text, theme));
                    status = 200;
                    contentType = "text/html";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Return a readable error in the preview if a file disappeared or could not be opened.
                }
            }

            e.Response = environment.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes(body), writable: false),
                status, status == 200 ? "OK" : "Not Found",
                $"Content-Type: {contentType}; charset=utf-8\r\nCache-Control: no-store");
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // The window can close while the document is being read.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_editDocument is not null)
        {
            e.Cancel = true;
            return;
        }

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            uri.Host.Equals(DocumentHostName, StringComparison.OrdinalIgnoreCase))
        {
            if (TryMapDocumentUrl(e.Uri, out var path))
                SetCurrentDocument(path);
            else
                e.Cancel = true;
            return;
        }

        if (TryMapLocalUrl(e.Uri, out var localPath))
        {
            if (!IsInsidePreviewFolder(localPath))
                e.Cancel = true;
            else if (DocumentPreview.IsGenerated(localPath))
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(() => DisplayDocument(localPath));
            }
            else if (DocumentPreview.IsSupported(localPath))
                SetCurrentDocument(localPath);

            return;
        }

        if (uri?.Scheme is "http" or "https")
        {
            e.Cancel = true;
            OpenInDefaultBrowser(e.Uri);
        }
    }

    private void CoreWebView2_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (TryMapLocalUrl(e.Uri, out var localPath) && IsInsidePreviewFolder(localPath))
            DisplayDocument(localPath);
        else
            OpenInDefaultBrowser(e.Uri);
    }

    private async void CoreWebView2_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        BackButton.IsEnabled = Preview.CanGoBack;
        ForwardButton.IsEnabled = Preview.CanGoForward;
        if (_editDocument is null)
            StatusText.Text = e.IsSuccess ? "준비" : $"페이지 오류: {e.WebErrorStatus}";
        if (e.IsSuccess)
            await ApplyDocumentThemeAsync(_theme);
    }

    private bool TryMapDocumentUrl(string url, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !uri.Host.Equals(DocumentHostName, StringComparison.OrdinalIgnoreCase))
            return false;

        var prefix = $"/{_previewMappingVersion}/";
        return uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) &&
               TryMapPreviewPath(uri.AbsolutePath[prefix.Length..], out path) &&
               IsInsidePreviewFolder(path) && DocumentPreview.IsGenerated(path);
    }

    private bool TryMapLocalUrl(string url, out string path)
    {
        path = string.Empty;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               uri.Host.Equals(VirtualHostName, StringComparison.OrdinalIgnoreCase) &&
               TryMapPreviewPath(uri.AbsolutePath.TrimStart('/'), out path);
    }

    private bool TryMapPreviewPath(string urlPath, out string path)
    {
        path = string.Empty;
        if (_previewFolder is null)
            return false;

        try
        {
            var relative = Uri.UnescapeDataString(urlPath).Replace('/', Path.DirectorySeparatorChar);
            path = Path.GetFullPath(Path.Combine(_previewFolder, relative));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsInsidePreviewFolder(string path) =>
        _previewFolder is not null && IsInsideFolder(path, _previewFolder);

    private static bool IsInsideFolder(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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

    private void StartWatching(string folder, bool includeSubdirectories, string filter = "*")
    {
        _refreshTimer.Stop();
        _changedFolders.Clear();
        _changedDocumentPath = null;
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(folder, filter)
            {
                IncludeSubdirectories = includeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += Watcher_Changed;
            _watcher.Created += Watcher_Changed;
            _watcher.Deleted += Watcher_Changed;
            _watcher.Renamed += Watcher_Changed;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void Watcher_Changed(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(sender, _watcher))
                return;

            var oldPath = (e as RenamedEventArgs)?.OldFullPath;
            if (_currentDocumentPath is not null &&
                (_currentDocumentPath.Equals(e.FullPath, StringComparison.OrdinalIgnoreCase) ||
                 _currentDocumentPath.Equals(oldPath, StringComparison.OrdinalIgnoreCase)))
                _changedDocumentPath = _currentDocumentPath;

            if (_folderRoot is not null && e.ChangeType != WatcherChangeTypes.Changed)
            {
                if (Path.GetDirectoryName(e.FullPath) is { } folder)
                    _changedFolders.Add(folder);
                if (oldPath is not null && Path.GetDirectoryName(oldPath) is { } oldFolder)
                    _changedFolders.Add(oldFolder);
            }

            if (_changedDocumentPath is null && _changedFolders.Count == 0)
                return;

            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        var changedFolders = _changedFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _changedFolders.Clear();
        var changedDocumentPath = _changedDocumentPath;
        _changedDocumentPath = null;

        var refreshTree = _folderRoot is not null && changedFolders.Count > 0
            ? RefreshExpandedFoldersAsync(_folderRoot, changedFolders)
            : Task.CompletedTask;
        if (changedDocumentPath is not null &&
            changedDocumentPath.Equals(_currentDocumentPath, StringComparison.OrdinalIgnoreCase))
            DisplayDocument(changedDocumentPath);
        await refreshTree;
    }

    private async Task RefreshExpandedFoldersAsync(FileTreeItem folder, HashSet<string>? changedFolders = null)
    {
        if (!folder.IsExpanded)
            return;

        if (changedFolders is null || changedFolders.Contains(folder.FullPath))
            await LoadDirectoryAsync(folder);

        if (folder.IsExpanded)
            await Task.WhenAll(folder.Children.Where(child => child.IsDirectory && child.IsExpanded)
                .Select(child => RefreshExpandedFoldersAsync(child, changedFolders)));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshCurrentAsync();

    private async Task RefreshCurrentAsync()
    {
        var refreshTree = _folderRoot is not null ? RefreshExpandedFoldersAsync(_folderRoot) : Task.CompletedTask;
        if (_editDocument is null)
        {
            if (_currentDocumentPath is not null && File.Exists(_currentDocumentPath))
                DisplayDocument(_currentDocumentPath);
            else if (_webViewReady && Preview.Source is not null)
                Preview.Reload();
        }
        await refreshTree;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Preview.CanGoBack && TryCloseEditor(refreshPreview: false))
            Preview.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Preview.CanGoForward && TryCloseEditor(refreshPreview: false))
            Preview.GoForward();
    }

    private void InitializeThemeMenu()
    {
        foreach (var theme in ViewerTheme.All)
        {
            var item = new MenuItem { Header = theme.Name, Tag = theme, IsCheckable = true };
            item.Click += ThemeMenuItem_Click;
            ThemeButton.ContextMenu.Items.Add(item);
        }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var menu = ThemeButton.ContextMenu;
        menu.PlacementTarget = ThemeButton;
        menu.IsOpen = true;
    }

    private async void ThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selected = (ViewerTheme)((MenuItem)sender).Tag;
        var version = ++_themeChangeVersion;
        _theme = selected;
        if (_editDocument is null)
            await ApplyDocumentThemeAsync(selected);
        if (version != _themeChangeVersion)
            return;

        ApplyTheme();
        UpdateWebViewTheme();
        _settings.Theme = selected.Id;
        _settings.IsDarkTheme = selected.IsDark;
        AppSettingsService.Save(_settings);
    }

    private void ApplyTheme()
    {
        var palette = _theme.CreateResources();
        Resources = palette;
        ThemeButton.ContextMenu.Resources = palette;
        ThemeName.Text = _theme.Name;
        foreach (MenuItem item in ThemeButton.ContextMenu.Items)
            item.IsChecked = ReferenceEquals(item.Tag, _theme);

        var background = (Color)ColorConverter.ConvertFromString(_theme.Page);
        Preview.DefaultBackgroundColor = System.Drawing.Color.FromArgb(background.R, background.G, background.B);
    }

    private async Task ApplyDocumentThemeAsync(ViewerTheme theme)
    {
        if (!_webViewReady || Preview.Source is not { } uri || uri.Host != DocumentHostName)
            return;

        try
        {
            var css = JsonSerializer.Serialize(theme.PreviewCss);
            await Preview.CoreWebView2.ExecuteScriptAsync($$"""
                (() => {
                    const style = document.getElementById('litedoc-theme');
                    if (style && style.textContent !== {{css}}) style.textContent = {{css}};
                })();
                """);
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            // Navigation or window closing may interrupt the script.
        }
    }

    private void UpdateWebViewTheme()
    {
        if (_webViewReady)
            Preview.CoreWebView2.Profile.PreferredColorScheme = _theme.IsDark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && _editDocument is not null)
        {
            SaveEdit_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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
        if (!TryCloseEditor(refreshPreview: false))
        {
            e.Cancel = true;
            return;
        }

        _closingWindow = true;
        ++_themeChangeVersion;
        _refreshTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
    }

    private void ShowError(string message, Exception exception)
    {
        StatusText.Text = message;
        MessageBox.Show(this, $"{message}\n\n{exception.Message}", "LiteDoc Viewer",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
