using NUnit.Framework;

using Stampeded.Core.Decompilation;

namespace Stampeded.Core.Tests;

public class DecompilationServiceTests
{
	[Test]
	public void DecompilesTypeAndLocatesMemberByToken()
	{
		var assembly = typeof(DecompilationService).Assembly;
		var method = typeof(DecompilationService).GetMethod(nameof(DecompilationService.DecompileType))!;

		var result = DecompilationService.DecompileType(
			assembly.Location, typeof(DecompilationService).FullName!, method.MetadataToken);

		Assert.That(result.Text, Does.Contain("class DecompilationService"));
		Assert.That(result.MemberLine, Is.GreaterThan(1));
		string[] lines = result.Text.Split('\n');
		Assert.That(lines[result.MemberLine - 1], Does.Contain("DecompileType"));
	}

	[Test]
	public void UnknownTokenFallsBackToLineOne()
	{
		var assembly = typeof(DecompilationService).Assembly;

		var result = DecompilationService.DecompileType(
			assembly.Location, typeof(DecompilationService).FullName!, 0);

		Assert.That(result.MemberLine, Is.EqualTo(1));
	}

	[Test]
	public void LocatesMemberBelowDocumentationComments()
	{
		// Documentation comments end their own lines without going through the writer's
		// NewLine(), so a member used to be reported one line too early per comment line
		// above it - which for a documented BCL type is a jump into a different member.
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-decomp-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(dir);
		try
		{
			string source = typeof(DecompilationService).Assembly.Location;
			string copy = Path.Combine(dir, Path.GetFileName(source));
			File.Copy(source, copy);
			File.WriteAllText(Path.ChangeExtension(copy, ".xml"),
				"""
				<?xml version="1.0"?>
				<doc>
				    <assembly><name>ASSEMBLY</name></assembly>
				    <members>
				        <member name="T:TYPE">
				            <summary>First line.
				            Second line.
				            Third line.</summary>
				        </member>
				    </members>
				</doc>
				"""
				.Replace("ASSEMBLY", Path.GetFileNameWithoutExtension(source))
				.Replace("TYPE", typeof(DecompilationService).FullName));
			var method = typeof(DecompilationService).GetMethod(nameof(DecompilationService.DecompileType))!;

			var result = DecompilationService.DecompileType(
				copy, typeof(DecompilationService).FullName!, method.MetadataToken);

			Assert.That(result.Text, Does.Contain("/// <summary>"));
			string[] lines = result.Text.Split('\n');
			Assert.That(lines[result.MemberLine - 1], Does.Contain("DecompileType"));
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}
}
