using NUnit.Framework;

using Stampeded.Core.Lsp;

namespace Stampeded.Core.Tests;

/// <summary>
/// The machine that has never read Python before. Nothing is on PATH, there may not even be
/// node, and the reader is not going to be told to go and install something - so the tool
/// builds itself a server and reads with that.
///
/// Explicit because it downloads a few hundred megabytes and takes minutes; run it when the
/// install path changes, which is the only time it can break.
/// </summary>
[Explicit("installs a real language server into a temporary cache")]
public class PythonServerInstallTests
{
	[Test]
	public async Task AServerIsBuiltFromNothingAndAnswersWithoutNode()
	{
		string cache = NewTempDir();
		string repo = NewTempDir();
		string? previousCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
		string? previousPath = Environment.GetEnvironmentVariable("PATH");
		LspConnection? connection = null;
		try
		{
			Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cache);
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));

			var spec = await LanguageServers.InstallPythonAsync(repo, timeout.Token);

			Assert.That(spec, Is.Not.Null, "a machine with a Python interpreter can have a server");
			Assert.That(spec!.Executable, Does.StartWith(cache), "and it lands in this tool's cache");

			File.WriteAllText(Path.Combine(repo, "greeting.py"), """
				def greet(name):
					return "hi " + name


				def shout(name):
					return greet(name).upper()
				""");
			// The point of this server over the one npx fetches: the wheel carries its own
			// node, so it runs on a machine that has none.
			Environment.SetEnvironmentVariable("PATH", "");
			connection = await LspConnection.StartAsync(spec, repo, timeout.Token);
			using var provider = new LspSemanticProvider(connection, repo, spec.Name);

			int position = (await provider.GetPositionAsync("greeting.py", 6, 9, timeout.Token))!.Value;
			var symbol = await provider.GetSymbolAtAsync("greeting.py", position, timeout.Token);
			var definition = await provider.GetDefinitionAsync(symbol!, timeout.Token);

			Assert.That(definition?.Line, Is.EqualTo(1), "the call resolves to the def above it");
		}
		finally
		{
			Environment.SetEnvironmentVariable("PATH", previousPath);
			Environment.SetEnvironmentVariable("XDG_CACHE_HOME", previousCache);
			connection?.Dispose();
			Directory.Delete(cache, recursive: true);
			Directory.Delete(repo, recursive: true);
		}
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-pyinstall-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
