using Avalonia.Controls;

using AvaloniaEdit.Search;

using Stampeded.Editor;

namespace Stampeded;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();
		SearchPanel.Install(Editor);
		HighlightingService.EnsureRegistered();
		// M1 smoke content: show this app's own Program.cs with C# highlighting.
		var sample = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Program.cs");
		Editor.SyntaxHighlighting = HighlightingService.GetByExtension(".cs");
		Editor.Text = File.Exists(sample)
			? File.ReadAllText(sample)
			: "// Stampeded M1 shell - sample file not found\nclass Placeholder { }\n";
	}
}
