using System.Diagnostics;
using System.Text;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Git;

/// <summary>
/// Reads file contents out of the object database, without a checkout.
///
/// One `git cat-file --batch` per repository, kept alive and asked for one blob at a time.
/// A review needs the text of a few dozen files at a revision nobody has checked out, and a
/// process per file costs more than reading them all does: every .cs file of a mid-sized
/// repository comes back through one batch in well under a tenth of a second, where a
/// checkout to serve the same reads takes hundreds of milliseconds and a copy of the tree.
///
/// This is deliberately a long-lived process where the rest of the tool runs one command per
/// operation. The trade is the point of the thing - it is the only way the batch protocol
/// pays - so it is owned by the review that started it and dies with it.
/// </summary>
public sealed class GitBlobReader : IDisposable
{
	readonly string repoPath;
	readonly SemaphoreSlim gate = new(1, 1);
	Process? git;
	StreamWriter? requests;
	Stream? responses;

	public GitBlobReader(string repoPath)
	{
		this.repoPath = repoPath;
	}

	/// <summary>
	/// The text of a path at a revision, or null when the revision does not have it. Null is
	/// an answer, not a failure: a file the change adds is absent from the base, and asking
	/// is how that is discovered.
	/// </summary>
	public async Task<string?> ReadAsync(string revision, string relativePath, CancellationToken ct = default)
	{
		await gate.WaitAsync(ct);
		try
		{
			if (!Ensure())
				return null;
			await requests!.WriteLineAsync($"{revision}:{relativePath}");
			await requests.FlushAsync(ct);
			string header = await ReadLineAsync(ct);
			// "<oid> <type> <size>" for a blob; anything else - "missing", "ambiguous" - is
			// the object database saying there is nothing to read.
			var parts = header.Split(' ');
			if (parts.Length != 3 || parts[1] != "blob" || !int.TryParse(parts[2], out int size))
				return null;
			var content = new byte[size];
			int read = 0;
			while (read < size)
			{
				int got = await responses!.ReadAsync(content.AsMemory(read, size - read), ct);
				if (got <= 0)
					break;
				read += got;
			}
			// The batch protocol ends every object with a newline of its own.
			await ReadByteAsync(ct);
			return Encoding.UTF8.GetString(content, 0, read);
		}
		catch (Exception ex) when (ex is IOException or InvalidOperationException)
		{
			// A reader that has died takes its answers with it; the next request starts a new
			// process rather than failing everything after it.
			CliLog.Write("git", $"blob reader restarting: {ex.Message}");
			Stop();
			return null;
		}
		finally
		{
			gate.Release();
		}
	}

	bool Ensure()
	{
		if (git is { HasExited: false })
			return true;
		Stop();
		var info = new ProcessStartInfo("git", ["cat-file", "--batch"]) {
			WorkingDirectory = repoPath,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			// Windows gives a console process its own console window when the process starting
			// it has none, which a desktop application does not. This one outlives the review
			// that started it, so without this the reader gets a black window in their face for
			// as long as they are reading. Ignored everywhere else.
			CreateNoWindow = true,
		};
		git = Process.Start(info);
		if (git is null)
			return false;
		requests = git.StandardInput;
		responses = git.StandardOutput.BaseStream;
		return true;
	}

	/// <summary>Reads one line of the batch's own framing, byte by byte: the header shares a
	/// stream with blob content, which is read by length and may hold anything at all.</summary>
	async Task<string> ReadLineAsync(CancellationToken ct)
	{
		var line = new List<byte>(64);
		while (await ReadByteAsync(ct) is { } next && next != (byte)'\n')
			line.Add(next);
		return Encoding.UTF8.GetString([.. line]);
	}

	async Task<byte?> ReadByteAsync(CancellationToken ct)
	{
		var one = new byte[1];
		return await responses!.ReadAsync(one.AsMemory(0, 1), ct) == 1 ? one[0] : null;
	}

	void Stop()
	{
		try
		{
			if (git is { HasExited: false })
				git.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
		}
		git?.Dispose();
		git = null;
		requests = null;
		responses = null;
	}

	public void Dispose()
	{
		Stop();
		gate.Dispose();
	}
}
