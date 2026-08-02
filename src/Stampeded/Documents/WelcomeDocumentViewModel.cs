using Dock.Model.Mvvm.Controls;

namespace Stampeded.Documents;

/// <summary>
/// Placeholder document shown until a review is opened; renders a sample source file so
/// the editor stack (highlighting, search, theming) is exercised from the first launch.
/// </summary>
public class WelcomeDocumentViewModel : Document
{
	public string SampleText { get; }

	public WelcomeDocumentViewModel()
	{
		CanClose = false;
		var sample = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Program.cs");
		SampleText = File.Exists(sample)
			? File.ReadAllText(sample)
			: "// Stampeded shell - sample file not found\nclass Placeholder { }\n";
	}
}
