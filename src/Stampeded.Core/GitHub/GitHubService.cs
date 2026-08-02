using CliWrap.Buffered;

using System.Text.Json;
using System.Text.Json.Serialization;

using Stampeded.Core.Infra;

namespace Stampeded.Core.GitHub;

public sealed record PrAuthor(string Login);

public sealed record PrSummary(
	int Number,
	string Title,
	PrAuthor? Author,
	string HeadRefName,
	string BaseRefName,
	bool IsDraft,
	DateTimeOffset UpdatedAt)
{
	public string NumberDisplay => $"#{Number}";
}

public sealed record PrDetail(
	int Number,
	string Title,
	string? Body,
	string BaseRefName,
	string HeadRefName,
	string State);

public sealed record CheckRun(string Name, string State, string Bucket, string? Link, string? Workflow);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(IReadOnlyList<PrSummary>))]
[JsonSerializable(typeof(PrDetail))]
[JsonSerializable(typeof(IReadOnlyList<CheckRun>))]
partial class GitHubJsonContext : JsonSerializerContext
{
}

/// <summary>
/// GitHub access through the `gh` CLI, run in the repository directory so gh resolves
/// the repo from origin. Auth, SSO and token refresh ride on the user's gh login.
/// </summary>
public sealed class GitHubService(string repoPath)
{
	static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
		TypeInfoResolver = GitHubJsonContext.Default,
	};

	async Task<T> JsonAsync<T>(CancellationToken ct, params string[] args)
	{
		string output = await ExternalTool.RunAsync("gh", args, repoPath, ct);
		return JsonSerializer.Deserialize<T>(output, JsonOptions)
			?? throw new InvalidOperationException($"gh returned null JSON for: {string.Join(' ', args)}");
	}

	public Task<IReadOnlyList<PrSummary>> ListOpenPrsAsync(CancellationToken ct = default)
		=> JsonAsync<IReadOnlyList<PrSummary>>(ct,
			"pr", "list",
			"--json", "number,title,author,headRefName,baseRefName,isDraft,updatedAt",
			"--limit", "50");

	public Task<PrDetail> GetPrAsync(int number, CancellationToken ct = default)
		=> JsonAsync<PrDetail>(ct,
			"pr", "view", number.ToString(),
			"--json", "number,title,body,baseRefName,headRefName,state");

	/// <summary>Check runs for a PR. `gh pr checks` exits non-zero when checks failed or
	/// are pending, so the JSON is taken from stdout regardless of exit code.</summary>
	public async Task<IReadOnlyList<CheckRun>> GetChecksAsync(int number, CancellationToken ct = default)
	{
		var result = await CliWrap.Cli.Wrap("gh")
			.WithArguments(["pr", "checks", number.ToString(), "--json", "name,state,link,bucket,workflow"])
			.WithWorkingDirectory(repoPath)
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		if (string.IsNullOrWhiteSpace(result.StandardOutput))
		{
			if (result.ExitCode != 0)
				throw new ToolFailedException("gh", result.ExitCode, result.StandardError);
			return [];
		}
		return JsonSerializer.Deserialize<IReadOnlyList<CheckRun>>(result.StandardOutput, JsonOptions) ?? [];
	}

	/// <summary>Log lines of the failed steps of a workflow run.</summary>
	public Task<string> GetFailedLogAsync(long runId, CancellationToken ct = default)
		=> ExternalTool.RunAsync("gh", ["run", "view", runId.ToString(), "--log-failed"], repoPath, ct);
}
