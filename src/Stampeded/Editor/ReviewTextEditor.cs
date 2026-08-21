// Copyright (c) 2026 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;

using Stampeded.Themes;

namespace Stampeded.Editor
{
	/// <summary>
	/// <see cref="TextEditor"/> subclass carrying the shared code-view look, so every surface
	/// showing code (diff views, source viewers) renders identically:
	/// (a) listens for <see cref="ThemeManager.ThemeChanged"/> and forces a TextView redraw
	///     so an already-rendered editor picks up the new palette without needing the user
	///     to scroll or reselect;
	/// (b) uses the themed editor background and selection highlight.
	/// </summary>
	public class ReviewTextEditor : TextEditor
	{
		// Avalonia resolves the control template via the runtime type; subclasses of a
		// templated control inherit the base template only when StyleKeyOverride is
		// pointed at the base. Without this override AvaloniaEdit's template doesn't
		// apply to us — meaning no ScrollViewer is installed, scroll offsets stay 0,
		// and Copy can't reach the editor's TextArea via the template lookup chain.
		protected override Type StyleKeyOverride => typeof(TextEditor);

		public ReviewTextEditor()
		{
			// The fontconfig generic family resolves to the system's monospace font on
			// Linux; named fonts first for platforms without fontconfig aliases.
			FontFamily = new FontFamily("Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace");
			FontSize = 13;
			// Selected text keeps its syntax colours (ports icsharpcode/ILSpy#2938):
			// SelectionForeground stays unset, and the selection is a flat, translucent
			// highlight (square corners, no border) instead of a recoloured run.
			TextArea.SelectionCornerRadius = 0;
			TextArea.Bind(TextArea.SelectionBrushProperty, this.GetResourceObservable("Stampeded.EditorSelectionBrush"));
			this.Bind(BackgroundProperty, this.GetResourceObservable("Stampeded.EditorBackground"));
		}

		void OnThemeChanged(object? sender, EventArgs e)
		{
			// Already-painted lines cache their colour decisions; a Redraw discards those
			// caches and re-runs the colorizer pipeline against the new IsDarkTheme value.
			TextArea?.TextView?.Redraw();
		}

		protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
		{
			base.OnAttachedToVisualTree(e);
			ThemeManager.Current.ThemeChanged += OnThemeChanged;
			TextArea?.TextView?.Redraw();
		}

		protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
		{
			ThemeManager.Current.ThemeChanged -= OnThemeChanged;
			base.OnDetachedFromVisualTree(e);
		}
	}
}
