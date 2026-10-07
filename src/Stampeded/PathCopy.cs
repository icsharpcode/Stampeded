using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;

using Stampeded.Core.Diff;
using Stampeded.Documents;
using Stampeded.Panes;

namespace Stampeded;

public sealed record FilePathCopyTarget(string Name, string RelativePath, string Path);

static class PathCopy
{
	public static FilePathCopyTarget? ForRelative(string relativePath)
	{
		if (App.Workspace is not { } workspace || string.IsNullOrWhiteSpace(relativePath))
			return null;
		string root = workspace.WorktreePath ?? workspace.RepoPath;
		string normalized = relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar);
		return new FilePathCopyTarget(System.IO.Path.GetFileName(relativePath), relativePath,
			System.IO.Path.GetFullPath(System.IO.Path.Combine(root, normalized)));
	}

	public static FilePathCopyTarget? ForAbsolute(string absolutePath)
	{
		if (string.IsNullOrWhiteSpace(absolutePath))
			return null;
		string relative = absolutePath;
		if (App.Workspace?.WorktreePath is { } root)
		{
			relative = System.IO.Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
			if (relative.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative))
				relative = absolutePath;
		}
		return new FilePathCopyTarget(System.IO.Path.GetFileName(absolutePath), relative, absolutePath);
	}

	public static FilePathCopyTarget? FromDataContext(object? data)
		=> data switch {
			FileNode node => ForRelative(node.RelativePath),
			FileEntry entry => ForFile(entry.File),
			FsNode node => ForAbsolute(node.AbsPath),
			DiffDocumentViewModel document when !document.IsPatch => ForFile(document.File),
			SideBySideDocumentViewModel document => ForFile(document.File),
			_ => null,
		};

	static FilePathCopyTarget? ForFile(FileDiff file) => ForRelative(file.Path);

	public static void CopyFileName(Control owner, FilePathCopyTarget? target)
		=> Copy(owner, target?.Name);

	public static void CopyRelativePath(Control owner, FilePathCopyTarget? target)
		=> Copy(owner, target?.RelativePath);

	public static void CopyPath(Control owner, FilePathCopyTarget? target)
		=> Copy(owner, target?.Path);

	static void Copy(Control owner, string? text)
	{
		if (!string.IsNullOrEmpty(text))
			TopLevel.GetTopLevel(owner)?.Clipboard?.SetTextAsync(text).HandleExceptions();
	}
}

public static class FilePathContextMenu
{
	public static readonly AttachedProperty<bool> EnableProperty = AvaloniaProperty.RegisterAttached<Control, bool>(
		"Enable", typeof(FilePathContextMenu));

	static FilePathContextMenu()
	{
		EnableProperty.Changed.AddClassHandler<Control>((control, e) => {
			if (e.NewValue is true)
				Attach(control);
		});
	}

	public static bool GetEnable(Control control) => control.GetValue(EnableProperty);

	public static void SetEnable(Control control, bool value) => control.SetValue(EnableProperty, value);

	static void Attach(Control control)
	{
		var menu = new ContextMenu();
		var copyName = new MenuItem { Header = "Copy File Name" };
		var copyPath = new MenuItem { Header = "Copy Path" };
		var copyRelative = new MenuItem { Header = "Copy Relative Path" };
		menu.Items.Add(copyName);
		menu.Items.Add(copyPath);
		menu.Items.Add(copyRelative);
		menu.Opening += (_, _) => {
			var target = PathCopy.FromDataContext(control.DataContext);
			bool enabled = target is not null;
			copyName.IsEnabled = enabled;
			copyPath.IsEnabled = enabled;
			copyRelative.IsEnabled = enabled;
		};
		copyName.Click += (_, _) => PathCopy.CopyFileName(control, PathCopy.FromDataContext(control.DataContext));
		copyPath.Click += (_, _) => PathCopy.CopyPath(control, PathCopy.FromDataContext(control.DataContext));
		copyRelative.Click += (_, _) => PathCopy.CopyRelativePath(control, PathCopy.FromDataContext(control.DataContext));
		control.ContextMenu = menu;
	}
}
