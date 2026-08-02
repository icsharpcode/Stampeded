using CliWrap;
using CliWrap.Buffered;

namespace Stampeded.Core.Infra;

public sealed class ToolFailedException(string tool, int exitCode, string stdErr)
	: Exception($"{tool} exited with code {exitCode}: {stdErr.Trim()}")
{
	public string Tool { get; } = tool;
	public int ExitCode { get; } = exitCode;
	public string StdErr { get; } = stdErr;
}

public static class ExternalTool
{
	/// <summary>Runs a CLI tool, throwing <see cref="ToolFailedException"/> (with stderr) on a
	/// non-zero exit; returns stdout. Cancellation kills the process tree (CliWrap).</summary>
	public static async Task<string> RunAsync(
		string exe, IReadOnlyList<string> args, string workingDir, CancellationToken ct = default)
	{
		var result = await CliWrap.Cli.Wrap(exe)
			.WithArguments(args)
			.WithWorkingDirectory(workingDir)
			.WithValidation(CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		if (result.ExitCode != 0)
			throw new ToolFailedException(exe, result.ExitCode, result.StandardError);
		return result.StandardOutput;
	}
}
