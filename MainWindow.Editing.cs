using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LiteDocViewer.Models;
using LiteDocViewer.Services;

namespace LiteDocViewer;

public partial class MainWindow
{
    private EditableDocument? _editDocument;
    private bool _loadingEditor;
    private bool _closingWindow;
    private bool _restoringTreeSelection;

    private void UpdateEditButton() =>
        EditButton.IsEnabled = _currentDocumentPath is not null && _editDocument is null && !_loadingEditor;

    private async void EditDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocumentPath is not { } path || _editDocument is not null || _loadingEditor)
            return;

        _loadingEditor = true;
        UpdateEditButton();
        try
        {
            var document = await EditableDocument.OpenAsync(path);
            if (_closingWindow || !string.Equals(_currentDocumentPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            SourceEditor.IsUndoEnabled = false;
            SourceEditor.Text = document.Text;
            SourceEditor.IsUndoEnabled = true;
            _editDocument = document;
            Preview.Visibility = Visibility.Collapsed;
            EditorPanel.Visibility = Visibility.Visible;
            SourceEditor.Focus();
            SourceEditor.CaretIndex = 0;
            UpdateEditorStatus();
        }
        catch (DecoderFallbackException ex)
        {
            if (!_closingWindow && string.Equals(_currentDocumentPath, path, StringComparison.OrdinalIgnoreCase))
                ShowError("UTF-8 또는 BOM이 있는 유니코드 파일을 편집할 수 있습니다.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_closingWindow && string.Equals(_currentDocumentPath, path, StringComparison.OrdinalIgnoreCase))
                ShowError("편집할 문서를 열 수 없습니다.", ex);
        }
        finally
        {
            _loadingEditor = false;
            if (!_closingWindow)
                UpdateEditButton();
        }
    }

    private bool EditorHasChanges() =>
        _editDocument is { } document && !string.Equals(SourceEditor.Text, document.Text, StringComparison.Ordinal);

    private void SourceEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_editDocument is not null)
            UpdateEditorStatus();
    }

    private void UpdateEditorStatus()
    {
        SaveEditButton.IsEnabled = EditorHasChanges();
        StatusText.Text = SaveEditButton.IsEnabled ? "저장하지 않은 변경 내용" : "편집 중";
    }

    private void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (EditorHasChanges() && TrySaveEdit())
            ExitEditor(refreshPreview: true);
    }

    private void CloseEditor_Click(object sender, RoutedEventArgs e) => TryCloseEditor(refreshPreview: true);

    private bool TryCloseEditor(bool refreshPreview)
    {
        if (_editDocument is null)
            return true;

        if (EditorHasChanges())
        {
            var choice = MessageBox.Show(this, "변경 내용을 저장하시겠습니까?", "문서 편집",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel || (choice == MessageBoxResult.Yes && !TrySaveEdit()))
                return false;
        }

        ExitEditor(refreshPreview);
        return true;
    }

    private bool TrySaveEdit()
    {
        try
        {
            _editDocument!.Save(SourceEditor.Text);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            ShowError("변경 내용을 저장할 수 없습니다.", ex);
            SourceEditor.Focus();
            return false;
        }
    }

    private void ExitEditor(bool refreshPreview)
    {
        var path = _editDocument!.Path;
        _editDocument = null;
        EditorPanel.Visibility = Visibility.Collapsed;
        SourceEditor.Clear();
        SaveEditButton.IsEnabled = false;
        Preview.Visibility = Visibility.Visible;
        UpdateEditButton();

        if (refreshPreview)
        {
            if (File.Exists(path))
                DisplayDocument(path);
            else
                StatusText.Text = "문서 파일을 찾을 수 없습니다.";
        }
    }

    private void RestoreTreeSelection(FileTreeItem? oldItem, FileTreeItem rejectedItem)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_editDocument is null || !ReferenceEquals(FileTree.SelectedItem, rejectedItem))
                return;

            _restoringTreeSelection = true;
            try
            {
                if (oldItem is not null && FindTreeContainer(FileTree, oldItem) is { } previous)
                    previous.IsSelected = true;
                else if (FindTreeContainer(FileTree, rejectedItem) is { } current)
                    current.IsSelected = false;
            }
            finally
            {
                _restoringTreeSelection = false;
            }
        });
    }

    private static TreeViewItem? FindTreeContainer(ItemsControl parent, FileTreeItem item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
            return container;

        foreach (var folder in parent.Items.OfType<FileTreeItem>().Where(child => child.IsDirectory && child.IsExpanded))
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(folder) is TreeViewItem folderContainer &&
                FindTreeContainer(folderContainer, item) is { } nested)
                return nested;
        }

        return null;
    }
}
