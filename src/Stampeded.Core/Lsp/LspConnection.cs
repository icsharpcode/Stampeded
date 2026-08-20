using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Lsp;

/// <summary>What to run to get a language server, and what to call it in the log.</summary>
public sealed record LspServerSpec(string Name, string Executable, IReadOnlyList<string> Arguments);

/// <summary>
/// A language server as a child process, spoken to in JSON-RPC over its stdin and stdout.
///
/// This is the one external tool that is not a command with an exit code, so it does not go
/// through <see cref="ExternalTool"/>: a server is started once and answers for as long as
/// the review is open. Everything it says still reaches <see cref="CliLog"/> - the command
/// line, the requests that fail or take a noticeable while, and every line of its stderr -
/// because a reviewer who has to explain "go to definition did nothing" needs the same
/// evidence there as for a failed git call.
/// </summary>
public sealed class LspConnection : IDisposable
{
	internal static readonly JsonSerializerOptions Json = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	/// <summary>
	/// For responses, where a null result is an answer and not an absence: JSON-RPC requires
	/// a response to carry either a result or an error, and a serializer that drops null
	/// properties turns "nothing found" into a message the other end rejects outright.
	/// </summary>
	static readonly JsonSerializerOptions ResponseJson = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	readonly LspServerSpec spec;
	readonly Process process;
	readonly Stream toServer;
	readonly SemaphoreSlim writeLock = new(1, 1);
	readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
	readonly CancellationTokenSource stopping = new();
	int nextId;
	bool disposed;

	/// <summary>Server-initiated notifications: diagnostics, progress, log messages.</summary>
	public event Action<string, JsonElement>? Notification;

	/// <summary>Whatever the server answered <c>initialize</c> with, for capability checks.</summary>
	public JsonElement Capabilities { get; private set; }

	LspConnection(LspServerSpec spec, Process process)
	{
		this.spec = spec;
		this.process = process;
		toServer = process.StandardInput.BaseStream;
	}

	/// <summary>
	/// Starts the server and completes the LSP handshake against <paramref name="rootPath"/>.
	/// Throws <see cref="ToolFailedException"/> when the executable cannot be started, which
	/// is the common case - a server nobody installed.
	/// </summary>
	public static async Task<LspConnection> StartAsync(
		LspServerSpec spec, string rootPath, CancellationToken ct)
	{
		var startInfo = new ProcessStartInfo(spec.Executable) {
			WorkingDirectory = rootPath,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		foreach (var argument in spec.Arguments)
			startInfo.ArgumentList.Add(argument);
		// The MSBuild variables this process pins would be inherited by a server that runs
		// dotnet itself, and would point it at the wrong SDK.
		foreach (var variable in new[] { "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildExtensionsPath" })
			startInfo.Environment.Remove(variable);

		Process process;
		try
		{
			process = Process.Start(startInfo) ?? throw new ToolFailedException(spec.Executable, -1, "no process");
		}
		catch (Exception ex) when (ex is not ToolFailedException)
		{
			CliLog.Write(spec.Name, $"start FAILED: {ex.Message}");
			throw new ToolFailedException(spec.Executable, -1, ex.Message);
		}
		CliLog.Write(spec.Name, $"{string.Join(' ', spec.Arguments)} -> started (pid {process.Id})");

		var connection = new LspConnection(spec, process);
		connection.PumpStdErrAsync().HandleFailure(spec.Name);
		connection.ReadLoopAsync().HandleFailure(spec.Name);

		var initialize = await connection.RequestAsync("initialize", new {
			processId = Environment.ProcessId,
			rootUri = LspUri.FromPath(rootPath),
			capabilities = ClientCapabilities,
			workspaceFolders = new[] { new { uri = LspUri.FromPath(rootPath), name = Path.GetFileName(rootPath) } },
		}, ct);
		connection.Capabilities = initialize.TryGetProperty("capabilities", out var capabilities)
			? capabilities.Clone()
			: default;
		connection.Notify("initialized", new { });
		return connection;
	}

	/// <summary>
	/// What this client understands. Deliberately small: the features the review actually
	/// asks for, all without dynamic registration, so a server does not announce handlers
	/// nothing here would ever call.
	/// </summary>
	static object ClientCapabilities => new {
		textDocument = new {
			synchronization = new { didSave = false, willSave = false },
			definition = new { linkSupport = false },
			references = new { },
			hover = new { contentFormat = new[] { "plaintext", "markdown" } },
			documentHighlight = new { },
			documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
			semanticTokens = new {
				requests = new { full = true },
				tokenTypes = Array.Empty<string>(),
				tokenModifiers = Array.Empty<string>(),
				formats = new[] { "relative" },
			},
			callHierarchy = new { },
		},
		workspace = new { symbol = new { }, workspaceFolders = true, configuration = true },
		window = new { workDoneProgress = true },
	};

	/// <summary>Sends a request and waits for its answer. A server that never answers stops
	/// the caller's cancellation token, not the connection.</summary>
	public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct)
	{
		if (disposed)
			return default;
		int id = Interlocked.Increment(ref nextId);
		var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		pending[id] = completion;
		var watch = Stopwatch.StartNew();
		try
		{
			await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
			using var registration = ct.Register(() => completion.TrySetCanceled(ct));
			var result = await completion.Task;
			// Only what a reader would want explained: a request nobody noticed is noise.
			if (watch.ElapsedMilliseconds > 500)
				CliLog.Write(spec.Name, $"{method} -> {watch.ElapsedMilliseconds} ms");
			return result;
		}
		catch (OperationCanceledException)
		{
			Notify("$/cancelRequest", new { id });
			throw;
		}
		finally
		{
			pending.TryRemove(id, out _);
		}
	}

	public void Notify(string method, object? parameters)
	{
		if (disposed)
			return;
		SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, CancellationToken.None)
			.HandleFailure(spec.Name);
	}

	async Task SendAsync(object message, CancellationToken ct)
	{
		var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
		await writeLock.WaitAsync(ct);
		try
		{
			await LspStream.WriteMessageAsync(toServer, payload, ct);
		}
		finally
		{
			writeLock.Release();
		}
	}

	async Task ReadLoopAsync()
	{
		var stream = process.StandardOutput.BaseStream;
		while (!stopping.IsCancellationRequested)
		{
			if (await LspStream.ReadMessageAsync(stream, stopping.Token) is not { } payload)
				break;
			Dispatch(JsonDocument.Parse(payload).RootElement.Clone());
		}
	}

	void Dispatch(JsonElement message)
	{
		bool hasId = message.TryGetProperty("id", out var id);
		bool hasMethod = message.TryGetProperty("method", out var method);
		if (hasId && !hasMethod)
		{
			if (!id.TryGetInt32(out int requestId) || !pending.TryRemove(requestId, out var completion))
				return;
			if (message.TryGetProperty("error", out var error))
			{
				CliLog.Write(spec.Name, $"request {requestId} FAILED: "
					+ (error.TryGetProperty("message", out var text) ? text.GetString() : error.ToString()));
				completion.TrySetResult(default);
				return;
			}
			completion.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
			return;
		}
		if (!hasMethod)
			return;
		string name = method.GetString() ?? "";
		if (hasId)
		{
			AnswerServerRequest(id, name, message.TryGetProperty("params", out var request) ? request : default);
			return;
		}
		if (name == "window/logMessage" && message.TryGetProperty("params", out var log)
			&& log.TryGetProperty("message", out var logText))
		{
			CliLog.Write(spec.Name, logText.GetString() ?? "");
		}
		Notification?.Invoke(name, message.TryGetProperty("params", out var parameters) ? parameters : default);
	}

	/// <summary>
	/// Answers the handful of requests a server makes of its client. None of them is
	/// declined by silence: a server that asked for configuration and heard nothing back
	/// waits, and everything after it waits too.
	/// </summary>
	void AnswerServerRequest(JsonElement id, string method, JsonElement parameters)
	{
		object? result = method switch {
			// One entry per item asked about, in the order asked: a server that gets fewer
			// than it asked for cannot tell which setting it was told about.
			"workspace/configuration" => Enumerable
				.Repeat(new object(), parameters.ValueKind == JsonValueKind.Object
					&& parameters.TryGetProperty("items", out var items)
					&& items.ValueKind == JsonValueKind.Array
					? items.GetArrayLength()
					: 1)
				.ToArray(),
			_ => null,
		};
		SendResponseAsync(id, result).HandleFailure(spec.Name);
	}

	async Task SendResponseAsync(JsonElement id, object? result)
	{
		var payload = JsonSerializer.SerializeToUtf8Bytes(
			new { jsonrpc = "2.0", id = id.Clone(), result }, ResponseJson);
		await writeLock.WaitAsync();
		try
		{
			await LspStream.WriteMessageAsync(toServer, payload, CancellationToken.None);
		}
		finally
		{
			writeLock.Release();
		}
	}

	async Task PumpStdErrAsync()
	{
		while (await process.StandardError.ReadLineAsync(stopping.Token) is { } line)
		{
			if (line.Trim().Length > 0)
				CliLog.Write(spec.Name, line.Trim());
		}
	}

	public void Dispose()
	{
		if (disposed)
			return;
		disposed = true;
		stopping.Cancel();
		try
		{
			// Asked to leave before being killed: a server that is mid-write to a cache
			// leaves it broken otherwise.
			SendAsync(new { jsonrpc = "2.0", id = 0, method = "shutdown" }, CancellationToken.None).Wait(500);
			SendAsync(new { jsonrpc = "2.0", method = "exit" }, CancellationToken.None).Wait(200);
			process.WaitForExit(1000);
		}
		catch (Exception ex)
		{
			CliLog.Write(spec.Name, $"shutdown FAILED: {ex.Message}");
		}
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already gone; nothing to kill.
		}
		process.Dispose();
		stopping.Dispose();
	}
}

/// <summary>file: URIs as a language server wants them, and back.</summary>
public static class LspUri
{
	public static string FromPath(string absolutePath) => new Uri(absolutePath).AbsoluteUri;

	/// <summary>The local path a server named, or null for a URI that is not a local file
	/// (a decompiled or generated document a server invented).</summary>
	public static string? ToPath(string uri)
	{
		if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
			return null;
		return parsed.LocalPath;
	}
}

static class TaskLogExtensions
{
	/// <summary>Runs a background pump to its end, reporting rather than swallowing what
	/// stopped it. Cancellation during shutdown is the expected end, and says nothing.</summary>
	internal static void HandleFailure(this Task task, string name)
	{
		_ = task.ContinueWith(
			t => CliLog.Write(name, $"connection FAILED: {t.Exception?.GetBaseException().Message}"),
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}
}
