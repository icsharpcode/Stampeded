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
}
