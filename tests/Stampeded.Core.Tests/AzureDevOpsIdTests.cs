using NUnit.Framework;

using Stampeded.Core.AzureDevOps;

namespace Stampeded.Core.Tests;

/// <summary>
/// Azure DevOps numbers a thread's comments from 1 again in every thread, so neither number
/// alone identifies a comment - and the review carries exactly one number per comment. These
/// pin that the two survive being packed into it and taken back out.
/// </summary>
public class AzureDevOpsIdTests
{
	[TestCase(1, 1)]
	[TestCase(42, 7)]
	[TestCase(999_999, 999_999)]
	public void PacksAndSplits(int thread, int comment)
	{
		var (t, c) = AzureDevOpsService.SplitId(AzureDevOpsService.PackId(thread, comment));
		Assert.Multiple(() => {
			Assert.That(t, Is.EqualTo(thread));
			Assert.That(c, Is.EqualTo(comment));
		});
	}

	[Test]
	public void DifferentThreadsWithTheSameCommentNumberStayApart()
		=> Assert.That(AzureDevOpsService.PackId(3, 1), Is.Not.EqualTo(AzureDevOpsService.PackId(4, 1)));
}
