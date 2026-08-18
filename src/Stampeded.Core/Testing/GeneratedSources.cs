using Stampeded.Core.Diff;
using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Testing;

/// <summary>
/// The C# that source generators wrote during a build, as review material.
///
/// Generated code is not in git, so it is absent from the diff even when a change is entirely
/// about what a generator emits - the reviewer sees the generator edited and has to take on
/// faith what came out of it. A build with <see cref="EmitProperty"/> leaves that output on
/// disk under each project's obj directory, and building both sides of the review turns it
/// into an ordinary before/after comparison.
/// </summary>
public static class GeneratedSources
{
	/// <summary>Makes the compiler write each generator's output to disk instead of keeping
	/// it in memory. It lands under obj/&lt;config&gt;/&lt;tfm&gt;/generated/.</summary>
	public const string EmitProperty = "-p:EmitCompilerGeneratedFiles=true";

	/// <summary>
	/// Compiles a checkout for the sake of its generator output. This is a build and not a
	/// test run: running the tests to find out what a generator emitted costs minutes for an
	/// answer the compiler already had.
	/// </summary>
	public static Task BuildAsync(string worktreePath, CancellationToken ct = default)
	{
		// Named rather than left to dotnet: a root with several solutions in it is refused
		// outright ("Specify which project or solution file to use"), and the generated
		// sources never arrived for exactly the repositories that ship an installer or an
		// extension solution beside the product's own.
		var args = new List<string> { "build" };
		if (SolutionTarget.ForRoot(worktreePath) is { } solution)
			args.Add(solution);
		args.AddRange([EmitProperty, "--nologo", "-v", "quiet",
			// Nothing here needs an assembly to come out, only the generated sources on the
			// way to one.
			"-p:GenerateDocumentationFile=false"]);
		return ExternalTool.RunAsync(
			"dotnet", args, worktreePath, ct,
			env: new Dictionary<string, string> { ["OPENSSL_ENABLE_SHA1_SIGNATURES"] = "1" });
	}

	/// <summary>
	/// Every generated file in a checkout, keyed by a path that identifies the same file in
	/// the other checkout: the project directory, then the generator's own layout below
	/// "generated". The configuration and target framework in between are dropped, so the
	/// two sides pair up even when one was built for a different configuration.
	/// </summary>
	public static IReadOnlyDictionary<string, string> Collect(string worktreePath)
	{
		var found = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!Directory.Exists(worktreePath))
			return found;
		foreach (var directory in Directory.EnumerateDirectories(worktreePath, "generated", SearchOption.AllDirectories))
		{
			if (RelativeKeyPrefix(worktreePath, directory) is not { } prefix)
				continue;
			foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
			{
				string tail = Path.GetRelativePath(directory, file).Replace('\\', '/');
				// Two projects can host the same generator, so the project has to stay in the
				// key; without it their outputs would collide and hide each other.
				found[$"{prefix}/generated/{tail}"] = file;
			}
		}
		return found;
	}

	/// <summary>The project-relative prefix for a "generated" directory that belongs to a
	/// build, or null when the directory is something else that happens to be called that -
	/// only the compiler's own output below an obj directory counts.</summary>
	static string? RelativeKeyPrefix(string worktreePath, string generatedDirectory)
	{
		string relative = Path.GetRelativePath(worktreePath, generatedDirectory).Replace('\\', '/');
		var parts = relative.Split('/');
		int obj = Array.LastIndexOf(parts, "obj");
		// .../obj/<config>/<tfm>/generated is the layout; anything else is not ours.
		if (obj < 0 || parts.Length - obj != 4 || parts[^1] != "generated")
			return null;
		return obj == 0 ? "." : string.Join('/', parts[..obj]);
	}

	/// <summary>
	/// Compares what the two checkouts generated. Files only the head has come out as added,
	/// files only the base has as deleted, and identical files not at all - a generator whose
	/// output did not move is not part of the change.
	/// </summary>
	public static async Task<IReadOnlyList<FileDiff>> DiffAsync(
		string baseWorktree, string headWorktree, CancellationToken ct = default)
	{
		var baseFiles = Collect(baseWorktree);
		var headFiles = Collect(headWorktree);
		var files = new List<FileDiff>();
		foreach (string key in baseFiles.Keys.Union(headFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
		{
			ct.ThrowIfCancellationRequested();
			baseFiles.TryGetValue(key, out string? oldFile);
			headFiles.TryGetValue(key, out string? newFile);
			var kind = oldFile is null ? FileChangeKind.Added
				: newFile is null ? FileChangeKind.Deleted
				: FileChangeKind.Modified;
			var hunks = await DiffFilesAsync(oldFile, newFile, ct);
			if (hunks.Count == 0)
				continue;
			files.Add(new FileDiff(key, key, kind, IsBinary: false, hunks, new GeneratedSource(oldFile, newFile)));
		}
		return files;
	}

	/// <summary>
	/// The hunks between two files on disk, via `git diff --no-index` so the output is the
	/// same shape the rest of the review is built from. The parsed paths are thrown away: the
	/// caller already knows which pair it asked about, and they would be absolute paths into
	/// two throwaway worktrees.
	/// </summary>
	static async Task<IReadOnlyList<DiffHunk>> DiffFilesAsync(string? oldFile, string? newFile, CancellationToken ct)
	{
		// --no-index answers "differences found" with exit 1, which is the interesting case.
		string diff = await ExternalTool.RunAsync(
			"git",
			["diff", "-U3", "--no-index", "--", oldFile ?? "/dev/null", newFile ?? "/dev/null"],
			Path.GetTempPath(), ct, okExitCodes: [1]);
		var parsed = GitDiffParser.Parse(diff);
		return parsed.Count > 0 ? parsed[0].Hunks : [];
	}
}
