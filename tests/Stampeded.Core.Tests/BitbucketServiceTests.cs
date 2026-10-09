using System.Text.Json;

using NUnit.Framework;

using Stampeded.Core.Bitbucket;

namespace Stampeded.Core.Tests;

public class BitbucketServiceTests
{
	[Test]
	public void DiffStatsCountsAddedAndRemovedLines()
	{
		using var doc = JsonDocument.Parse("""
			{
			  "diffs": [
			    {
			      "source": { "toString": "old.cs" },
			      "destination": { "toString": "new.cs" },
			      "hunks": [
			        {
			          "segments": [
			            { "type": "CONTEXT", "lines": [ { "line": "class C" } ] },
			            { "type": "REMOVED", "lines": [ { "line": "old 1" }, { "line": "old 2" } ] },
			            { "type": "ADDED", "lines": [ { "line": "new 1" }, { "line": "new 2" }, { "line": "new 3" } ] }
			          ]
			        }
			      ]
			    },
			    {
			      "destination": { "toString": "added.cs" },
			      "hunks": [
			        {
			          "segments": [
			            { "type": "ADDED", "lines": [ { "line": "added" } ] }
			          ]
			        }
			      ]
			    }
			  ]
			}
			""");

		var stats = BitbucketService.DiffStats(doc.RootElement);

		Assert.Multiple(() => {
			Assert.That(stats.Additions, Is.EqualTo(4));
			Assert.That(stats.Deletions, Is.EqualTo(2));
			Assert.That(stats.ChangedFiles, Is.EqualTo(2));
		});
	}
}
