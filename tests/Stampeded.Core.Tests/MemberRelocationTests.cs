using NUnit.Framework;

using Stampeded.Core.Roslyn;

namespace Stampeded.Core.Tests;

/// <summary>Where a comment goes when the line it was written on has moved or gone: the
/// member it was written in is what still identifies the place.</summary>
public class MemberRelocationTests
{
	const string Old = """
		class Builder
		{
			void First()
			{
				Prepare();
			}

			int Second(int count)
			{
				int total = count * 2;
				return total;
			}
		}
		""";

	[Test]
	public void FollowsTheLineIntoItsMemberAfterEverythingMovedDown()
	{
		string @new = """
			using System;

			class Builder
			{
				void Added()
				{
				}

				void First()
				{
					Prepare();
				}

				int Second(int count)
				{
					Log(count);
					int total = count * 2;
					return total;
				}
			}
			""";
		// "int total = count * 2;" was line 10 and is now line 17.
		var move = MemberRelocation.Locate(Old, 10, @new, "\t\tint total = count * 2;");
		Assert.That(move, Is.Not.Null);
		Assert.That(move!.Line, Is.EqualTo(17));
		Assert.That(move.FoundTheLine, Is.True);
		Assert.That(move.Member, Does.Contain("Second(int)"));
	}

	[Test]
	public void PlacesTheCommentInTheMemberWhenTheLineItselfIsGone()
	{
		string @new = """
			class Builder
			{
				void First()
				{
					Prepare();
				}

				int Second(int count)
				{
					return count + count;
				}
			}
			""";
		var move = MemberRelocation.Locate(Old, 10, @new, "\t\tint total = count * 2;");
		Assert.That(move, Is.Not.Null);
		Assert.That(move!.FoundTheLine, Is.False);
		Assert.That(move.Line, Is.InRange(8, 11), "inside Second, not at the top of the file");
	}

	[Test]
	public void TellsOverloadsApart()
	{
		string old = """
			class Builder
			{
				void Add(int x)
				{
					One(x);
				}

				void Add(string s)
				{
					Two(s);
				}
			}
			""";
		string @new = """
			class Builder
			{
				void Add(string s)
				{
					Two(s);
					Three(s);
				}

				void Add(int x)
				{
					One(x);
				}
			}
			""";
		// Line 5 is "One(x);", in Add(int).
		var move = MemberRelocation.Locate(old, 5, @new, "\t\tOne(x);");
		Assert.That(move, Is.Not.Null);
		Assert.That(move!.Line, Is.EqualTo(11));
		Assert.That(move.Member, Does.Contain("Add(int)"));
	}

	[Test]
	public void GivesUpWhenTheMemberIsGone()
	{
		string @new = """
			class Builder
			{
				void First()
				{
					Prepare();
				}
			}
			""";
		Assert.That(MemberRelocation.Locate(Old, 10, @new, "\t\tint total = count * 2;"), Is.Null);
	}

	[Test]
	public void SaysNothingAboutALineOutsideEveryMember()
	{
		Assert.That(MemberRelocation.Locate(Old, 1, "class Builder { }", "class Builder"), Is.Not.Null);
		Assert.That(MemberRelocation.Locate("using System;\nusing System.IO;\n", 2, Old, "using System.IO;"), Is.Null);
	}

	[Test]
	public void FollowsAMemberThatGainedAParameter()
	{
		string old = """
			class Builder
			{
				static Op? FromName(string name, out bool isChecked)
				{
					switch (name)
					{
						case "op_Add":
							return Op.Add;
						case "op_CheckedAdd":
							isChecked = true;
							return Op.Add;
					}
				}
			}
			""";
		string @new = """
			class Builder
			{
				static Op? FromName(string name, out bool isChecked, Settings settings)
				{
					switch (name)
					{
						case "op_Add":
							return Op.Add;
					}
				}
			}
			""";
		// Line 10 - the "op_CheckedAdd" case - is gone, and the method it was in has a
		// parameter more than it had.
		var move = MemberRelocation.Locate(old, 10, @new, "\t\t\t\tcase \"op_CheckedAdd\":");
		Assert.That(move, Is.Not.Null);
		Assert.That(move!.FoundTheLine, Is.False);
		Assert.That(move.Member, Does.Contain("FromName"));
		Assert.That(move.Line, Is.EqualTo(3), "the declaration, the method being shorter now than the remark sat deep");
	}
}
