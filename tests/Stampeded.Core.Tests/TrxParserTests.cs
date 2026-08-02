using NUnit.Framework;

using Stampeded.Core.Testing;

namespace Stampeded.Core.Tests;

[TestFixture]
public class TrxParserTests
{
	const string Trx = """
		<?xml version="1.0" encoding="utf-8"?>
		<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
		  <Results>
		    <UnitTestResult testName="Namespace.Fixture.PassingTest" outcome="Passed" duration="00:00:00.1234567" />
		    <UnitTestResult testName="Namespace.Fixture.FailingTest(UseRoslyn)" outcome="Failed" duration="00:00:02.0000000">
		      <Output>
		        <ErrorInfo>
		          <Message>Expected: 1 But was: 2</Message>
		          <StackTrace>   at Namespace.Fixture.FailingTest() in /home/user/repo/src/FixtureTests.cs:line 42</StackTrace>
		        </ErrorInfo>
		      </Output>
		    </UnitTestResult>
		    <UnitTestResult testName="Namespace.Fixture.SkippedTest" outcome="NotExecuted" duration="00:00:00" />
		  </Results>
		</TestRun>
		""";

	[Test]
	public void ParsesOutcomesAndErrorInfo()
	{
		var results = TrxParser.Parse(Trx);

		Assert.That(results, Has.Count.EqualTo(3));

		var passing = results.Single(r => r.TestName.Contains("PassingTest"));
		Assert.That(passing.Outcome, Is.EqualTo(TestOutcome.Passed));

		var failing = results.Single(r => r.TestName.Contains("FailingTest"));
		Assert.That(failing.Outcome, Is.EqualTo(TestOutcome.Failed));
		Assert.That(failing.ErrorMessage, Does.Contain("Expected: 1"));
		Assert.That(failing.StackTrace, Does.Contain("FixtureTests.cs:line 42"));

		var skipped = results.Single(r => r.TestName.Contains("SkippedTest"));
		Assert.That(skipped.Outcome, Is.EqualTo(TestOutcome.Skipped));
	}

	[Test]
	public void ExtractsSourceLocationFromStackTrace()
	{
		var results = TrxParser.Parse(Trx);
		var failing = results.Single(r => r.Outcome == TestOutcome.Failed);

		var location = failing.TryGetSourceLocation();
		Assert.That(location, Is.Not.Null);
		Assert.That(location!.Value.FilePath, Is.EqualTo("/home/user/repo/src/FixtureTests.cs"));
		Assert.That(location.Value.Line, Is.EqualTo(42));
	}

	[Test]
	public void EmptyDocumentYieldsNoResults()
	{
		Assert.That(TrxParser.Parse("""<?xml version="1.0"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" />"""), Is.Empty);
	}
}
