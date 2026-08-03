using CliWrap;

namespace Stampeded.Core.Testing;

/// <summary>
/// Runs `dotnet` test commands in the review worktree, streaming output, and collects
/// results from the .trx files the run produced.
/// </summary>
public sealed class TestService(string worktreePath)
{
	public async Task<(int ExitCode, IReadOnlyList<TestResult> Results)> RunAsync(
		string argsLine, Action<string> onOutputLine, CancellationToken ct)
	{
		var started = DateTime.UtcNow.AddSeconds(-5);
		var command = Cli.Wrap("dotnet")
			// ponytail: whitespace arg splitting, no quote handling; the args box is a
			// developer-facing escape hatch, upgrade if quoted paths ever matter.
			.WithArguments(argsLine.Split(' ', StringSplitOptions.RemoveEmptyEntries))
			.WithWorkingDirectory(worktreePath)
			.WithEnvironmentVariables(env => {
				env.Set("OPENSSL_ENABLE_SHA1_SIGNATURES", "1");
				Stampeded.Core.Infra.ExternalTool.StripMsBuildLocatorVariables(env);
			})
			.WithValidation(CommandResultValidation.None)
			.WithStandardOutputPipe(PipeTarget.ToDelegate(onOutputLine))
			.WithStandardErrorPipe(PipeTarget.ToDelegate(onOutputLine));
		Stampeded.Core.Infra.CliLog.Write("dotnet", $"{argsLine} (test run started)");
		var result = await command.ExecuteAsync(ct);
		Stampeded.Core.Infra.CliLog.Write("dotnet", $"{argsLine} -> exit {result.ExitCode}");

		var results = new List<TestResult>();
		foreach (var trx in Directory.EnumerateFiles(worktreePath, "*.trx", SearchOption.AllDirectories))
		{
			if (File.GetLastWriteTimeUtc(trx) < started)
				continue;
			try
			{
				results.AddRange(TrxParser.Parse(File.ReadAllText(trx)));
			}
			catch (System.Xml.XmlException)
			{
				// A truncated trx from an aborted run is not worth failing the collection.
			}
		}
		return (result.ExitCode, results);
	}

	/// <summary>Parses every .trx below a directory (e.g. downloaded CI artifacts).</summary>
	public static IReadOnlyList<TestResult> ParseDirectory(string directory)
	{
		var results = new List<TestResult>();
		foreach (var trx in Directory.EnumerateFiles(directory, "*.trx", SearchOption.AllDirectories))
		{
			try
			{
				results.AddRange(TrxParser.Parse(File.ReadAllText(trx)));
			}
			catch (System.Xml.XmlException)
			{
			}
		}
		return results;
	}
}
