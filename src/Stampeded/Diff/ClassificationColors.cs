using Avalonia.Media;

using AvaloniaEdit.Highlighting;

using Microsoft.CodeAnalysis.Classification;

using Stampeded.Themes;

namespace Stampeded.Diff;

/// <summary>
/// Maps Roslyn classification types to VS-style highlighting colors, per theme. The xshd
/// grammar keeps handling keywords/strings/comments; this layer colors identifiers
/// semantically on top (the same split ILSpy uses for decompiled output).
/// </summary>
public static class ClassificationColors
{
	static readonly Dictionary<string, (string Light, string Dark)> Palette = new() {
		[ClassificationTypeNames.ClassName] = ("#2B91AF", "#4EC9B0"),
		[ClassificationTypeNames.StructName] = ("#2B91AF", "#86C691"),
		[ClassificationTypeNames.InterfaceName] = ("#2B91AF", "#B8D7A3"),
		[ClassificationTypeNames.EnumName] = ("#2B91AF", "#B8D7A3"),
		[ClassificationTypeNames.DelegateName] = ("#2B91AF", "#4EC9B0"),
		[ClassificationTypeNames.RecordClassName] = ("#2B91AF", "#4EC9B0"),
		[ClassificationTypeNames.RecordStructName] = ("#2B91AF", "#86C691"),
		[ClassificationTypeNames.TypeParameterName] = ("#1F7A8C", "#B8D7A3"),
		[ClassificationTypeNames.MethodName] = ("#74531F", "#DCDCAA"),
		[ClassificationTypeNames.ExtensionMethodName] = ("#74531F", "#DCDCAA"),
		[ClassificationTypeNames.LocalName] = ("#001080", "#9CDCFE"),
		[ClassificationTypeNames.ParameterName] = ("#001080", "#9CDCFE"),
		[ClassificationTypeNames.FieldName] = ("#001080", "#D0DCFE"),
		[ClassificationTypeNames.PropertyName] = ("#001080", "#D0DCFE"),
		[ClassificationTypeNames.EventName] = ("#001080", "#D0DCFE"),
		[ClassificationTypeNames.ConstantName] = ("#001080", "#D0A0DC"),
		[ClassificationTypeNames.EnumMemberName] = ("#001080", "#D0A0DC"),
	};

	static readonly Dictionary<(string, bool), HighlightingColor> Cache = [];

	public static HighlightingColor? Get(string classification)
	{
		bool dark = ThemeManager.Current.IsDarkTheme;
		if (!Palette.TryGetValue(classification, out var colors))
			return null;
		lock (Cache)
		{
			if (!Cache.TryGetValue((classification, dark), out var color))
			{
				color = new HighlightingColor {
					Foreground = new SimpleHighlightingBrush(Color.Parse(dark ? colors.Dark : colors.Light)),
				};
				color.Freeze();
				Cache[(classification, dark)] = color;
			}
			return color;
		}
	}
}
