namespace Stampeded.Core.Git;

public enum BranchSyncState
{
	/// <summary>The local branch and the pull request's head are the same commit.</summary>
	InSync,
	/// <summary>The local branch has commits the pull request does not show: unpushed work.</summary>
	Ahead,
	/// <summary>The pull request has commits the local branch does not have.</summary>
	Behind,
	/// <summary>Both sides have commits the other lacks.</summary>
	Diverged,
	/// <summary>The heads differ, but the pull request's head is not in the local object
	/// database, so by how much cannot be said without fetching.</summary>
	Unfetched,
}

/// <summary>
/// How a local branch stands against the head its pull request shows. Reviewing a local
/// branch that is not the pull request's head reviews something nobody else can see, so
/// the distinction is worth stating precisely rather than as "differs".
/// </summary>
public sealed record BranchSync(BranchSyncState State, int Ahead, int Behind)
{
	public static readonly BranchSync InSync = new(BranchSyncState.InSync, 0, 0);
	public static readonly BranchSync Unfetched = new(BranchSyncState.Unfetched, 0, 0);

	public static BranchSync From(int ahead, int behind) => (ahead, behind) switch {
		(0, 0) => InSync,
		( > 0, 0) => new(BranchSyncState.Ahead, ahead, behind),
		(0, > 0) => new(BranchSyncState.Behind, ahead, behind),
		_ => new(BranchSyncState.Diverged, ahead, behind),
	};

	public string Display => State switch {
		BranchSyncState.InSync => "in sync",
		BranchSyncState.Ahead => $"{Ahead} ahead",
		BranchSyncState.Behind => $"{Behind} behind",
		BranchSyncState.Diverged => $"{Ahead} ahead, {Behind} behind",
		_ => "differs",
	};

	public string Explanation => State switch {
		BranchSyncState.InSync => "The local branch is exactly the head this pull request shows.",
		BranchSyncState.Ahead => $"The local branch has {Ahead} commit(s) the pull request does not show - unpushed work.",
		BranchSyncState.Behind => $"The pull request has {Behind} commit(s) the local branch does not have - fetch before reviewing locally.",
		BranchSyncState.Diverged => $"The branch and the pull request have diverged: {Ahead} local commit(s) and {Behind} remote commit(s) are not shared.",
		_ => "The heads differ; the pull request's head is not in the local object database, so fetch to see by how much.",
	};
}
