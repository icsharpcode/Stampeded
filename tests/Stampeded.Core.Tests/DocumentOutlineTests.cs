using NUnit.Framework;

using Stampeded.Core.Roslyn;

namespace Stampeded.Core.Tests;

public class DocumentOutlineTests
{
	[Test]
	public void OutlinesTypesAndMembersWithFlattenedNamespaces()
	{
		string source = """
			namespace N
			{
				class C<T>
				{
					int field;
					public C(int x) { }
					void M(string s, int i) { }
					public int P { get; set; }
					public event System.EventHandler E;
				}
				enum Color { Red }
			}
			""";
		var outline = DocumentOutline.Compute(source);

		Assert.That(outline, Has.Count.EqualTo(2), "namespace flattened to its two types");
		var c = outline[0];
		Assert.That(c.Title, Is.EqualTo("class C<T>"));
		Assert.That(c.StartLine, Is.EqualTo(3));
		Assert.That(c.Children.Select(m => m.Title), Is.EqualTo(new[] {
			"field", "C(int)", "M(string, int)", "P", "E",
		}));
		Assert.That(c.Children.Select(m => m.Kind), Is.EqualTo(new[] {
			"field", "method", "method", "property", "event",
		}));
		Assert.That(outline[1].Title, Is.EqualTo("enum Color"));
	}
}
