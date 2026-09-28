using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

public class GitServiceTests
{
	[Test]
	public void EmptyRevisionIsRefusedBeforeGitIsAsked()
	{
		var git = new GitService(TestContext.CurrentContext.WorkDirectory);

		Assert.ThrowsAsync<RefusedException>(async () => await git.RevParseAsync(""));
		Assert.ThrowsAsync<RefusedException>(async () => await git.GetMergeBaseAsync("main", ""));
		Assert.ThrowsAsync<RefusedException>(async () => await git.FetchBranchAsync(""));
		Assert.ThrowsAsync<RefusedException>(async () => await git.FetchPrHeadAsync("", 17));
	}
}
