using System.Collections.ObjectModel;
using System.IO;

namespace LiteDocViewer.Models;

public sealed class FileTreeItem
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public bool IsDirectory { get; init; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<FileTreeItem> Children { get; } = [];

    public string Icon => IsDirectory ? "\uE8B7" : IsMarkdown ? "\uE8A5" : "\uE943";
    public bool IsMarkdown => !IsDirectory &&
        (Path.GetExtension(FullPath).Equals(".md", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(FullPath).Equals(".markdown", StringComparison.OrdinalIgnoreCase));
}
