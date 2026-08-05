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
		Assert.That(c.Kind, Is.EqualTo("class"));
		Assert.That(c.StartLine, Is.EqualTo(3));
		Assert.That(c.Children.Select(m => m.Title), Is.EqualTo(new[] {
			"field", "C(int)", "M(string, int)", "P", "E",
		}));
		Assert.That(c.Children.Select(m => m.Kind), Is.EqualTo(new[] {
			"field", "ctor", "method", "property", "event",
		}));
		Assert.That(outline[1].Title, Is.EqualTo("enum Color"));
		Assert.That(outline[1].Kind, Is.EqualTo("enum"));
	}

	[Test]
	public void KindsDistinguishTypeFlavorsAndSpecialMembers()
	{
		string source = """
			struct S
			{
				public static S operator +(S a, S b) => a;
				public static explicit operator int(S s) => 0;
				public int this[int i] => i;
			}
			interface I { }
			record R(int X);
			""";
		var outline = DocumentOutline.Compute(source);

		Assert.That(outline.Select(t => t.Kind), Is.EqualTo(new[] { "struct", "interface", "record" }));
		Assert.That(outline[0].Children.Select(m => m.Kind), Is.EqualTo(new[] {
			"operator", "operator", "indexer",
		}));
	}
}
