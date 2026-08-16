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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

using Dock.Avalonia.Controls;

namespace Stampeded.Controls;

/// <summary>
/// Lays the document tabs out in one scrolling row or in as many rows as they need, following
/// <see cref="TabRowsPreference"/>. Adapted from ILSpy's MultiRowTabStripBehavior, which solves
/// the same problem against the same version of Dock.
///
/// Dock's <see cref="DocumentTabStrip"/> hardcodes a horizontal StackPanel inside a
/// PART_ScrollViewer in its theme template and ignores an ItemsPanel set through a Style, so the
/// panel is swapped on the control itself and the scroll viewer's directions are turned around
/// to match: a WrapPanel only wraps if nothing offers it unlimited width.
///
/// The wheel over the strip sets the mode - up for several rows, down for one - because that is
/// the gesture a reader reaches for when the tabs no longer fit, and there is nothing else to
/// scroll there.
/// </summary>
public static class MultiRowTabStrip
{
	public static readonly AttachedProperty<bool> EnableProperty =
		AvaloniaProperty.RegisterAttached<DocumentTabStrip, bool>("Enable", typeof(MultiRowTabStrip));

	public static void SetEnable(DocumentTabStrip element, bool value) => element.SetValue(EnableProperty, value);

	public static bool GetEnable(DocumentTabStrip element) => element.GetValue(EnableProperty);

	static MultiRowTabStrip()
	{
		EnableProperty.Changed.AddClassHandler<DocumentTabStrip>(OnEnableChanged);
	}

	static void OnEnableChanged(DocumentTabStrip strip, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.NewValue is not true)
			return;
		// Tunnelling, and handled: the wheel here means rows, not a nudge along the strip.
		strip.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel, RoutingStrategies.Tunnel);
		void Follow() => Apply(strip);
		TabRowsPreference.Changed += Follow;
		strip.DetachedFromVisualTree += (_, _) => TabRowsPreference.Changed -= Follow;
		// The scroll viewer this reaches for is a template part, so the mode is applied again
		// whenever the template is built.
		strip.TemplateApplied += (_, _) => Apply(strip);
		Apply(strip);
	}

	static void OnPointerWheel(object? sender, PointerWheelEventArgs e)
	{
		if (e.Delta.Y == 0)
			return;
		// Set, not flipped: rolling further in the same direction settles on the mode the
		// reader is asking for instead of alternating between them.
		TabRowsPreference.Set(e.Delta.Y > 0);
		e.Handled = true;
	}

	static void Apply(DocumentTabStrip strip)
	{
		bool multiRow = TabRowsPreference.MultiRow;
		strip.ItemsPanel = new FuncTemplate<Panel?>(() => multiRow
			? new WrapPanel { Orientation = Orientation.Horizontal }
			: new StackPanel { Orientation = Orientation.Horizontal });
		if (strip.GetVisualDescendants().OfType<ScrollViewer>()
			.FirstOrDefault(s => s.Name == "PART_ScrollViewer") is not { } scroller)
		{
			return;
		}
		scroller.HorizontalScrollBarVisibility = multiRow ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
		scroller.VerticalScrollBarVisibility = multiRow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
	}
}
