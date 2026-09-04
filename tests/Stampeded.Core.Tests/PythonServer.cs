using Stampeded.Core.Infra;
using Stampeded.Core.Lsp;

namespace Stampeded.Core.Tests;

/// <summary>
/// The Python language server a test talks to, with its download kept out of the test's own
/// time budget.
///
/// On a machine with node and no pyright, <see cref="LanguageServers.Python"/> answers with
/// npx, and the first npx start downloads the package from the npm registry before the server
/// says a word. A test hands one three-minute token to the handshake and its requests, which
/// is generous for a language server and nothing at all for a registry having a slow morning:
/// the download alone has been measured at 16 seconds and at 109 seconds on the same runner
/// image. Every CI runner is fresh, so the first pyright test in a run always paid for it, and
/// failed with a cancellation inside initialize that named neither the download nor its
/// duration.
///
/// So the download happens here, once per test assembly, under a budget of its own, through
/// <see cref="ExternalTool.RunAsync"/> - which logs the command with its elapsed time, so a
/// slow registry shows up as a number in the test output. npx keys its cache on the package
/// spec, so <c>pyright --version</c> puts the package where <c>pyright-langserver</c> then
/// finds it. A download that fails or runs out its budget fails every pyright test with that
/// reason, rather than being skipped: a registry that cannot be reached is a fact about the
/// run, not about the machine.
/// </summary>
static class PythonServer
{
	static readonly Lazy<Task> downloaded = new(DownloadAsync);

	/// <summary>The server spec, or null on a machine with no way to run one; for the npx
	/// form, only after the package is in the npm cache.</summary>
	public static async Task<LspServerSpec?> ResolveAsync()
	{
		if (LanguageServers.Python() is not { } spec)
			return null;
		if (spec.Arguments.Contains("--package"))
			await downloaded.Value;
		return spec;
	}

	static async Task DownloadAsync()
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
		await ExternalTool.RunAsync(LanguageServers.OnPath("npx")!,
			["--yes", "--package", "pyright", "--", "pyright", "--version"],
			Path.GetTempPath(), timeout.Token);
	}
}
