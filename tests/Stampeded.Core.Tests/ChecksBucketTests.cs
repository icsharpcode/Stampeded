using System.Text.Json;

using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

public class ChecksBucketTests
{
	static string BucketOf(params string[] conclusions)
	{
		string json = JsonSerializer.Serialize(conclusions.Select(c => new { conclusion = c }));
		var rollup = JsonDocument.Parse(json).RootElement.Clone();
		return new PrSummary(1, "t", null, "head", "base", false, default, rollup).ChecksBucket;
	}

	[Test]
	public void FoldsConclusionsWorstFirst()
	{
		Assert.Multiple(() => {
			Assert.That(BucketOf("SUCCESS", "SUCCESS"), Is.EqualTo("green"));
			Assert.That(BucketOf("SUCCESS", "IN_PROGRESS"), Is.EqualTo("pending"));
			Assert.That(BucketOf("SUCCESS", "FAILURE"), Is.EqualTo("fail"));
			Assert.That(BucketOf("SUCCESS", "CANCELLED"), Is.EqualTo("fail"), "a cancelled run did not vouch for the change");
			Assert.That(BucketOf("SUCCESS", "ACTION_REQUIRED"), Is.EqualTo("fail"));
			Assert.That(BucketOf("SKIPPED", "NEUTRAL"), Is.EqualTo("green"));
		});
	}
}
