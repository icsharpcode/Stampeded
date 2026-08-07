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
	/// <summary>
	/// Removes MSBuildLocator's process-wide variables from a child's environment.
	/// RegisterDefaults picks the SDK matching the HOST runtime and pins MSBUILD_EXE_PATH/
	/// MSBuildSDKsPath to it; a child dotnet whose global.json resolves a different SDK
	/// then gets foreign Sdks/targets forced onto it and fails its build or restore with
	/// no output at all. Children must resolve their own SDK.
	/// </summary>
	public static void StripMsBuildLocatorVariables(CliWrap.Builders.EnvironmentVariablesBuilder env)
	{
		env.Set("MSBUILD_EXE_PATH", null);
		env.Set("MSBuildSDKsPath", null);
		env.Set("MSBuildExtensionsPath", null);
	}

	/// <summary>Runs a CLI tool, throwing <see cref="ToolFailedException"/> (with stderr) on a
	/// non-zero exit; returns stdout. Cancellation kills the process tree (CliWrap).</summary>
	public static async Task<string> RunAsync(
		string exe, IReadOnlyList<string> args, string workingDir, CancellationToken ct = default,
		IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<int>? okExitCodes = null)
	{
		var watch = System.Diagnostics.Stopwatch.StartNew();
		var result = await CliWrap.Cli.Wrap(exe)
			.WithArguments(args)
			.WithWorkingDirectory(workingDir)
			.WithEnvironmentVariables(builder => {
				StripMsBuildLocatorVariables(builder);
				foreach (var (key, value) in env ?? System.Collections.Immutable.ImmutableDictionary<string, string>.Empty)
					builder.Set(key, value);
			})
			.WithValidation(CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		string argsText = string.Join(' ', args);
		if (argsText.Length > 160)
			argsText = argsText[..160] + "...";
		bool failed = result.ExitCode != 0 && okExitCodes?.Contains(result.ExitCode) != true;
		CliLog.Write(exe, $"{argsText} -> exit {result.ExitCode} ({watch.ElapsedMilliseconds} ms)"
			+ (failed ? ": " + FailureReason(result.StandardError, result.StandardOutput) : ""));
		if (failed)
			throw new ToolFailedException(exe, result.ExitCode, result.StandardError);
		return result.StandardOutput;
	}

	/// <summary>
	/// The reason to put on a failed command's log line. Without it the log records only an
	/// exit code, which says that something failed but never what - and the tools already
	/// explain themselves in one line ("fatal: 'x' is already checked out at ...",
	/// "gh: Can not approve your own pull request (HTTP 422)"). Falls back to stdout, since
	/// not every tool reports failures on stderr.
	/// </summary>
	public static string FailureReason(string stdErr, string stdOut)
	{
		string reason = FirstMeaningfulLine(stdErr) ?? FirstMeaningfulLine(stdOut) ?? "no output";
		return reason.Length > 200 ? reason[..200] + "..." : reason;

		static string? FirstMeaningfulLine(string text)
			=> text.ReplaceLineEndings("\n").Split('\n')
				.Select(line => line.Trim())
				.FirstOrDefault(line => line.Length > 0);
	}
}
