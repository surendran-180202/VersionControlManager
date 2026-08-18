using VersionControlManager.Logging;
using VersionControlManager.Migration;

namespace VersionControlManager.Git;

/// <summary>A single git reference in the mirror.</summary>
internal sealed record GitRef(string Name, string ObjectType)
{
	#region Properties
	public bool IsBranch => this.Name.StartsWith("refs/heads/", StringComparison.Ordinal);

	public bool IsTag => this.Name.StartsWith("refs/tags/", StringComparison.Ordinal);

	public bool IsNote => this.Name.StartsWith("refs/notes/", StringComparison.Ordinal);

	public string ShortName => this.Name.Split('/', 3) is [_, _, string strRest] ? strRest : this.Name;
	#endregion
}

/// <summary>What the mirror clone contains, used for reporting and verification.</summary>
internal sealed record MirrorSummary(
	IReadOnlyList<GitRef> Refs,
	int CommitCount,
	long SizeOnDiskBytes)
{
	#region Properties
	public IReadOnlyList<GitRef> Branches => [.. this.Refs.Where(r => r.IsBranch)];

	public IReadOnlyList<GitRef> Tags => [.. this.Refs.Where(r => r.IsTag)];

	public IReadOnlyList<GitRef> Notes => [.. this.Refs.Where(r => r.IsNote)];
	#endregion
}

/// <summary>
/// Mirror clone and push operations. A mirror clone (--mirror) is what preserves the
/// check-in history: it copies every object and every ref verbatim, so commit SHAs,
/// authors, dates, parents, and messages are identical on the target.
/// </summary>
internal sealed class GitMirror(GitCommandRunner git)
{
	#region Publics
	/// <summary>Clones every ref and object from <paramref name="cloneUrl"/> into a bare mirror.</summary>
	public async Task CloneAsync(
		string strCloneUrl,
		string strDestination,
		string strAuthorizationHeader,
		CancellationToken cancellationToken)
	{
		GitResult gitResult = await git.RunAsync(
			["clone", "--mirror", "--progress", strCloneUrl, strDestination],
			strAuthorizationHeader: strAuthorizationHeader,
			bRelayProgress: true,
			cancellationToken: cancellationToken);

		if(!gitResult.Success)
		{
			throw new MigrationException(
				ExitCode.SourceError,
				$"Mirror clone of {strCloneUrl} failed.{Environment.NewLine}{ConsoleLog.Redact(gitResult.FailureText)}",
				DescribeCloneFailure(gitResult.FailureText));
		}
	}

	public async Task<MirrorSummary> SummariseAsync(string strRepositoryPath, CancellationToken cancellationToken)
	{
		GitResult gitResultRefs = await git.RunAsync(
			["for-each-ref", "--format=%(refname)%09%(objecttype)"],
			strRepositoryPath,
			cancellationToken: cancellationToken);

		if(!gitResultRefs.Success)
		{
			throw new MigrationException(
				ExitCode.GitError,
				$"Could not list references in the mirror: {gitResultRefs.FailureText}");
		}

		GitRef[] liRefs = [.. gitResultRefs.StandardOutput
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(strLine => strLine.Split('\t'))
			.Where(liParts => liParts.Length == 2)
			.Select(liParts => new GitRef(liParts[0], liParts[1]))];

		int nCommitCount = 0;

		if(liRefs.Length > 0)
		{
			GitResult gitResultCount = await git.RunAsync(
				["rev-list", "--all", "--count"],
				strRepositoryPath,
				cancellationToken: cancellationToken);

			if(gitResultCount.Success
				&& int.TryParse(gitResultCount.StandardOutput.Trim(), out int nParsed))
			{
				nCommitCount = nParsed;
			}
		}

		return new MirrorSummary(liRefs, nCommitCount, MeasureDirectory(strRepositoryPath));
	}

	/// <summary>
	/// Pushes history to the target. Branches, tags and notes are pushed with explicit
	/// wildcard refspecs rather than "push --mirror": a GitHub mirror also contains
	/// refs/pull/*, which Azure DevOps rejects as a reserved namespace and which would
	/// otherwise fail the whole push.
	/// </summary>
	public async Task PushAsync(
		string strRepositoryPath,
		string strTargetUrl,
		MirrorSummary mirrorSummary,
		bool bIncludeNotes,
		string strAuthorizationHeader,
		CancellationToken cancellationToken)
	{
		List<string> liArguments = ["push", "--porcelain", "--progress", strTargetUrl];

		if(mirrorSummary.Branches.Count > 0)
		{
			liArguments.Add("refs/heads/*:refs/heads/*");
		}

		if(mirrorSummary.Tags.Count > 0) liArguments.Add("refs/tags/*:refs/tags/*");

		if(bIncludeNotes && mirrorSummary.Notes.Count > 0) liArguments.Add("refs/notes/*:refs/notes/*");

		GitResult gitResult = await git.RunAsync(
			liArguments,
			strRepositoryPath,
			strAuthorizationHeader,
			bRelayProgress: true,
			cancellationToken: cancellationToken);

		if(!gitResult.Success)
		{
			throw new MigrationException(
				ExitCode.TargetError,
				$"Push to Azure DevOps failed.{Environment.NewLine}{ConsoleLog.Redact(gitResult.FailureText)}",
				DescribePushFailure(gitResult.FailureText));
		}
	}

	/// <summary>
	/// True if the default branch tracks files through Git LFS. Used only to warn when
	/// --lfs was not requested, so checking HEAD's .gitattributes is sufficient: a repo
	/// using LFS declares it there. Reading the blob directly works in a bare mirror.
	/// </summary>
	public async Task<bool> HasLfsContentAsync(string strRepositoryPath, CancellationToken cancellationToken)
	{
		GitResult gitResult = await git.RunAsync(
			["cat-file", "-p", "HEAD:.gitattributes"],
			strRepositoryPath,
			cancellationToken: cancellationToken);

		// A non-zero exit just means there is no .gitattributes on the default branch.
		return gitResult.Success
			&& gitResult.StandardOutput.Contains("filter=lfs", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Transfers LFS objects. These live outside the git object store, so a mirror push
	/// moves only the pointer files -- the payloads need their own fetch and push, each
	/// against its own host and therefore its own credentials.
	/// </summary>
	public async Task PushLfsAsync(
		string strRepositoryPath,
		string strTargetUrl,
		string sourceAuthorizationHeader,
		string targetAuthorizationHeader,
		CancellationToken cancellationToken)
	{
		GitResult gitResultFetch = await git.RunAsync(
			["lfs", "fetch", "--all", "origin"],
			strRepositoryPath,
			sourceAuthorizationHeader,
			bRelayProgress: true,
			cancellationToken: cancellationToken);

		if(!gitResultFetch.Success)
		{
			throw new MigrationException(
				ExitCode.SourceError,
				$"Could not fetch Git LFS objects from the source.{Environment.NewLine}{ConsoleLog.Redact(gitResultFetch.FailureText)}");
		}

		GitResult gitResultPush = await git.RunAsync(
			["lfs", "push", "--all", strTargetUrl],
			strRepositoryPath,
			targetAuthorizationHeader,
			bRelayProgress: true,
			cancellationToken: cancellationToken);

		if(!gitResultPush.Success)
		{
			throw new MigrationException(
				ExitCode.TargetError,
				$"Could not push Git LFS objects to the target.{Environment.NewLine}{ConsoleLog.Redact(gitResultPush.FailureText)}");
		}
	}

	/// <summary>The branch the mirror's HEAD points at, e.g. "main". Null if detached.</summary>
	public async Task<string?> GetHeadBranchAsync(string strRepositoryPath, CancellationToken cancellationToken)
	{
		GitResult gitResult = await git.RunAsync(
			["symbolic-ref", "--short", "HEAD"],
			strRepositoryPath,
			cancellationToken: cancellationToken);

		string strValue = gitResult.StandardOutput.Trim();

		return gitResult.Success && strValue.Length > 0 ? strValue : null;
	}
	#endregion

	#region Privates
	private static long MeasureDirectory(string strPath)
	{
		try
		{
			return new DirectoryInfo(strPath)
				.EnumerateFiles("*", SearchOption.AllDirectories)
				.Sum(f => f.Length);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
		{
			return 0;
		}
	}

	private static string? DescribeCloneFailure(string strFailureText)
	{
		if(strFailureText.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
			|| strFailureText.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
			|| strFailureText.Contains("403", StringComparison.Ordinal))
		{
			return "Check the GitHub token is valid and has 'repo' scope for a private repository.";
		}

		if(strFailureText.Contains("not found", StringComparison.OrdinalIgnoreCase)
			|| strFailureText.Contains("404", StringComparison.Ordinal))
		{
			return "Check the repository URL, and that the token can see it.";
		}

		return null;
	}

	private static string? DescribePushFailure(string strFailureText)
	{
		if(strFailureText.Contains("TF401019", StringComparison.Ordinal)
			|| strFailureText.Contains("denied", StringComparison.OrdinalIgnoreCase)
			|| strFailureText.Contains("403", StringComparison.Ordinal))
		{
			return "The Azure DevOps token needs Code (read, write, and manage) scope.";
		}

		if(strFailureText.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
			|| strFailureText.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
		{
			return "The target repository already contains conflicting history. Use an empty repository, or --target-repo to create a new one.";
		}

		if(strFailureText.Contains("VS403636", StringComparison.Ordinal)
			|| strFailureText.Contains("too large", StringComparison.OrdinalIgnoreCase))
		{
			return "A file in history exceeds the Azure DevOps size limit. Consider Git LFS, or rewriting history before migrating.";
		}

		return null;
	}
	#endregion
}
