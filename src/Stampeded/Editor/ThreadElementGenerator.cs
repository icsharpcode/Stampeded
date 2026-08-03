using Avalonia.Controls;

using AvaloniaEdit.Rendering;

using Stampeded.Core.Diff;

namespace Stampeded.Editor
{
	/// <summary>
	/// Replaces comment-thread marker lines (see
	/// <see cref="DiffDocumentModel.WithThreadLines"/>) with interactive thread controls.
	/// The control renders inline, so the reserved line grows to the thread's height and
	/// the code below shifts down - a real box below the commented line, with working
	/// buttons.
	/// </summary>
	sealed class ThreadElementGenerator : VisualLineElementGenerator
	{
		/// <summary>Resolves a marker key to its thread control; null skips the marker.</summary>
		public Func<string, Control?>? ControlFactory { get; set; }

		public override int GetFirstInterestedOffset(int startOffset)
		{
			if (ControlFactory is null)
				return -1;
			var endLine = CurrentContext.VisualLine.LastDocumentLine;
			var relevant = CurrentContext.GetText(startOffset, endLine.EndOffset - startOffset);
			int index = relevant.Text.IndexOf(DiffDocumentModel.ThreadMarkerPrefix,
				relevant.Offset, relevant.Count, StringComparison.Ordinal);
			return index >= 0 ? startOffset + (index - relevant.Offset) : -1;
		}

		public override VisualLineElement? ConstructElement(int offset)
		{
			if (ControlFactory is null)
				return null;
			var endLine = CurrentContext.VisualLine.LastDocumentLine;
			var relevant = CurrentContext.GetText(offset, endLine.EndOffset - offset);
			string text = relevant.Text.Substring(relevant.Offset, relevant.Count);
			if (!text.StartsWith(DiffDocumentModel.ThreadMarkerPrefix, StringComparison.Ordinal))
				return null;
			int end = text.IndexOf(DiffDocumentModel.ThreadMarkerSuffix,
				DiffDocumentModel.ThreadMarkerPrefix.Length, StringComparison.Ordinal);
			if (end < 0)
				return null;
			string key = text[DiffDocumentModel.ThreadMarkerPrefix.Length..end];
			int markerLength = end + DiffDocumentModel.ThreadMarkerSuffix.Length;
			var control = ControlFactory(key);
			return control is null ? null : new InlineObjectElement(markerLength, control);
		}
	}
}
