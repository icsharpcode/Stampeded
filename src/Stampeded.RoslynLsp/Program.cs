using Stampeded.Core.Infra;

namespace Stampeded.RoslynLsp;

/// <summary>
/// Roslyn as a language server: the same <c>RoslynWorkspaceService</c> the app used to host
/// in-process, reached over stdin and stdout instead of by method call.
///
/// It exists to prove the interface the review now asks its questions through is really the
/// protocol's shape - if C# can be answered this way, a language nobody wrote a Roslyn for
/// can be answered the same way - and it takes MSBuild, the design-time build and a
/// solution's worth of compilations out of the window's process while doing it.
/// </summary>
static class Program
{
	static async Task<int> Main(string[] args)
	{
		// stdout is the protocol from here on. Everything that writes for a human - the
		// workspace's own load log included - goes to stderr, which the client copies into
		// its Log pane.
		var protocol = Console.OpenStandardOutput();
		Console.SetOut(Console.Error);

		// Before any Roslyn assembly loads, so MSBuild resolves from the installed SDK.
		Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
		Environment.SetEnvironmentVariable("OPENSSL_ENABLE_SHA1_SIGNATURES", "1");

		if (args.Contains("--version"))
		{
			await Console.Error.WriteLineAsync("Stampeded Roslyn language server");
			return 0;
		}

		using var server = new RoslynLspServer(Console.OpenStandardInput(), protocol);
		try
		{
			await server.RunAsync(CancellationToken.None);
			return 0;
		}
		catch (Exception ex)
		{
			CliLog.Write("roslyn-lsp", $"stopped: {ex.Message}");
			return 1;
		}
	}
}
