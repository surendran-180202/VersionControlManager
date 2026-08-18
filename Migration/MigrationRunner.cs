using VersionControlManager.Clients;
using VersionControlManager.Configuration;
using VersionControlManager.Git;
using VersionControlManager.Logging;
using VersionControlManager.Vcs;

namespace VersionControlManager.Migration;

/// <summary>Outcome of a completed migration, for the closing summary.</summary>
internal sealed record MigrationResult(string SourceDescription, string TargetDescription, string TargetUrl, int CommitCount, int BranchCount, int TagCount, int NoteCount, bool CreatedTargetRepository, string? DefaultBranch);

/// <summary>
/// Copies a GitHub repository, with its full check-in history, into Azure DevOps.
///
/// History fidelity comes from a bare mirror clone followed by a ref-for-ref push: git
/// transfers the original commit objects, so SHAs, authors, committers, dates, parents and
/// messages arrive unchanged. Nothing is replayed or rewritten.
/// </summary>
internal sealed class MigrationRunner(MigrationOptions options)
{
	#region Constants
	private const int TOTAL_STEPS = 8;
	#endregion

	#region Publics
	public async Task<MigrationResult> RunAsync(CancellationToken cancellationToken)
	{
		GitHubRepositoryReference gitHubRepositoryReference = GitHubRepositoryReference.Parse(options.GitHubUrl);
		AzureDevOpsProjectReference azureDevOpsProjectReference = AzureDevOpsProjectReference.Parse(options.AzureDevOpsUrl);

		string strGitHubAuth = RestSupport.BasicHeaderValue(options.GitHubUserName, options.GitHubPassword);
		string strAzureAuth = RestSupport.BasicHeaderValue(options.AzureDevOpsUserName, options.AzureDevOpsPassword);

		GitCommandRunner git = new();
		GitMirror mirror = new(git);

		using GitHubClient gitHub = new(RestSupport.CreateClient(options.GitHubUserName, options.GitHubPassword), gitHubRepositoryReference);
		using AzureDevOpsClient azure = new(RestSupport.CreateClient(options.AzureDevOpsUserName, options.AzureDevOpsPassword), azureDevOpsProjectReference);

		// --- 1 ------------------------------------------------------------------
		ConsoleLog.Step(1, TOTAL_STEPS, "Checking prerequisites");
		string strGitVersion = await git.GetVersionAsync(cancellationToken);
		ConsoleLog.Success($"Found {strGitVersion}");

		// --- 2 ------------------------------------------------------------------
		ConsoleLog.Step(2, TOTAL_STEPS, $"Reading source repository {gitHubRepositoryReference}");
		GitHubRepositoryInfo gitHubRepositoryInfo = await gitHub.GetRepositoryAsync(cancellationToken);
		ConsoleLog.Success($"{gitHubRepositoryInfo.FullName} ({(gitHubRepositoryInfo.IsPrivate ? "private" : "public")})");
		ConsoleLog.Info($"Default branch: {gitHubRepositoryInfo.DefaultBranch ?? "(none)"}");
		ConsoleLog.Info($"Reported size:  {Describe.Kilobytes(gitHubRepositoryInfo.SizeKilobytes)}");

		// --- 3 ------------------------------------------------------------------
		ConsoleLog.Step(3, TOTAL_STEPS, $"Reading target project {azureDevOpsProjectReference}");
		AzureProjectInfo azureProjectInfo = await azure.GetProjectAsync(cancellationToken);
		ConsoleLog.Success($"Project '{azureProjectInfo.Name}' found in {azureDevOpsProjectReference.CollectionUrl}");

		// --- 4 ------------------------------------------------------------------
		string strRepositoryName = ResolveTargetRepositoryName(gitHubRepositoryReference, azureDevOpsProjectReference);
		ConsoleLog.Step(4, TOTAL_STEPS, $"Preparing target repository '{strRepositoryName}'");

		(AzureRepositoryInfo azureRepositoryInfo, bool bCreated) = await PrepareTargetRepositoryAsync(azure, strRepositoryName, azureProjectInfo.Id, cancellationToken);

		// --- 5 ------------------------------------------------------------------
		ConsoleLog.Step(5, TOTAL_STEPS, "Cloning full history from GitHub");

		using TemporaryWorkspace workspace = TemporaryWorkspace.Create(options.WorkingDirectory, strRepositoryName, options.KeepWorkingCopy);
		ConsoleLog.Info($"Mirror: {workspace.MirrorPath}");

		await mirror.CloneAsync(gitHubRepositoryReference.CloneUrl, workspace.MirrorPath, strGitHubAuth, cancellationToken);

		MirrorSummary mirrorSummary = await mirror.SummariseAsync(workspace.MirrorPath, cancellationToken);

		ConsoleLog.Success($"Cloned {mirrorSummary.CommitCount:N0} commits, {mirrorSummary.Branches.Count} branches, " +
		                   $"{mirrorSummary.Tags.Count} tags ({Describe.Bytes(mirrorSummary.SizeOnDiskBytes)} on disk)");

		if(mirrorSummary.Notes.Count > 0) ConsoleLog.Info($"Notes refs: {mirrorSummary.Notes.Count}");

		// --- 6 ------------------------------------------------------------------
		ConsoleLog.Step(6, TOTAL_STEPS, "Pushing history to Azure DevOps");

		if(mirrorSummary.Refs.Count == 0)
		{
			ConsoleLog.Warn("The GitHub repository has no commits, so there is no history to push.");
		}
		else
		{
			await PushHistoryAsync(git, mirror, workspace.MirrorPath, azureRepositoryInfo.RemoteUrl, mirrorSummary, strGitHubAuth, strAzureAuth, cancellationToken);
		}

		// --- 7 ------------------------------------------------------------------
		ConsoleLog.Step(7, TOTAL_STEPS, "Setting the default branch");
		string? strDefaultBranch = await ApplyDefaultBranchAsync(azure, mirror, workspace.MirrorPath, azureRepositoryInfo, gitHubRepositoryInfo, mirrorSummary, cancellationToken);

		// --- 8 ------------------------------------------------------------------
		ConsoleLog.Step(8, TOTAL_STEPS, "Verifying the target repository");
		await VerifyAsync(azure, azureRepositoryInfo, mirrorSummary, cancellationToken);

		return new MigrationResult(gitHubRepositoryReference.WebUrl,
		                           $"{azureDevOpsProjectReference.Organization}/{azureDevOpsProjectReference.Project}/{azureRepositoryInfo.Name}",
		                           azureDevOpsProjectReference.RepositoryWebUrl(azureRepositoryInfo.Name),
		                           mirrorSummary.CommitCount,
		                           mirrorSummary.Branches.Count,
		                           mirrorSummary.Tags.Count,
		                           options.IncludeNotes ? mirrorSummary.Notes.Count : 0,
		                           bCreated,
		                           strDefaultBranch);
	}
	#endregion

	#region Privates
	/// <summary>
	/// An explicit --target-repo wins, then a repository named in the Azure DevOps URL,
	/// then the GitHub repository's own name.
	/// </summary>
	private string ResolveTargetRepositoryName(GitHubRepositoryReference gitHubRepositoryReference, AzureDevOpsProjectReference azureDevOpsProjectReference)
	{
		if(!string.IsNullOrWhiteSpace(options.TargetRepositoryName))
		{
			return options.TargetRepositoryName.Trim();
		}

		return string.IsNullOrWhiteSpace(azureDevOpsProjectReference.RepositoryName) ? gitHubRepositoryReference.Name : azureDevOpsProjectReference.RepositoryName;
	}

	private async Task<(AzureRepositoryInfo Repository, bool Created)> PrepareTargetRepositoryAsync(AzureDevOpsClient azure,
	                                                                                                string strRepositoryName,
	                                                                                                string strProjectId,
	                                                                                                CancellationToken cancellationToken)
	{
		AzureRepositoryInfo? azureRepositoryInfo = await azure.FindRepositoryAsync(strRepositoryName, cancellationToken);

		if(azureRepositoryInfo is null)
		{
			AzureRepositoryInfo azureRepositoryInfoNew = await azure.CreateRepositoryAsync(strRepositoryName, strProjectId, cancellationToken);
			ConsoleLog.Success($"Created repository '{azureRepositoryInfoNew.Name}'");

			return (azureRepositoryInfoNew, true);
		}

		ConsoleLog.Info($"Repository '{azureRepositoryInfo.Name}' already exists");

		IReadOnlyList<string> liRefs = await azure.ListRefsAsync(azureRepositoryInfo.Id, cancellationToken);

		if(liRefs.Count == 0)
		{
			ConsoleLog.Success("It is empty, so it can receive the history");

			return (azureRepositoryInfo, false);
		}

		if(!options.AllowExistingTarget)
		{
			throw new MigrationException(ExitCode.TargetError,
			                             $"Repository '{azureRepositoryInfo.Name}' already contains {liRefs.Count} reference(s). " +
			                             "Refusing to push into it, because that could overwrite or conflict with existing history.",
			                             "Pass --target-repo <newName> to create a separate repository, or --allow-existing to push anyway.");
		}

		ConsoleLog.Warn($"It already has {liRefs.Count} reference(s); pushing anyway because --allow-existing was given.");

		return (azureRepositoryInfo, false);
	}

	private async Task PushHistoryAsync(GitCommandRunner git, GitMirror mirror, string strMirrorPath, string strRemoteUrl, MirrorSummary mirrorSummary, string strGitHubAuth, string strAzureAuth, CancellationToken cancellationToken)
	{
		ConsoleLog.Info($"Target: {strRemoteUrl}");

		await mirror.PushAsync(strMirrorPath, strRemoteUrl, mirrorSummary, options.IncludeNotes, strAzureAuth, cancellationToken);

		string strPushed = $"{mirrorSummary.Branches.Count} branches and {mirrorSummary.Tags.Count} tags";

		if(options.IncludeNotes && mirrorSummary.Notes.Count > 0)
		{
			strPushed += $" and {mirrorSummary.Notes.Count} notes refs";
		}

		ConsoleLog.Success($"Pushed {strPushed} ({mirrorSummary.CommitCount:N0} commits)");

		await HandleLfsAsync(git, mirror, strMirrorPath, strRemoteUrl, strGitHubAuth, strAzureAuth, cancellationToken);
	}

	private async Task HandleLfsAsync(GitCommandRunner git, GitMirror mirror, string strMirrorPath, string strRemoteUrl, string strGitHubAuth, string strAzureAuth, CancellationToken cancellationToken)
	{
		if(!options.IncludeLfs)
		{
			if(await mirror.HasLfsContentAsync(strMirrorPath, cancellationToken))
			{
				ConsoleLog.Warn("This repository tracks files with Git LFS. The history was migrated, but the " + "LFS file contents were not. Re-run with --lfs to transfer them.");
			}

			return;
		}

		if(!await git.IsSubcommandAvailableAsync("lfs", cancellationToken))
		{
			throw new MigrationException(
			                             ExitCode.GitError,
			                             "--lfs was requested but git-lfs is not installed.",
			                             "Install Git LFS (https://git-lfs.com) and re-run, or drop --lfs.");
		}

		ConsoleLog.Info("Transferring Git LFS objects");

		// Fetch uses the source's credentials, push uses the target's.
		await mirror.PushLfsAsync(strMirrorPath, strRemoteUrl, strGitHubAuth, strAzureAuth, cancellationToken);

		ConsoleLog.Success("Git LFS objects transferred");
	}

	/// <summary>
	/// Mirrors GitHub's default branch onto the target, so the repository lands on the
	/// branch its users expect rather than whichever ref arrived first.
	/// </summary>
	private static async Task<string?> ApplyDefaultBranchAsync(
		AzureDevOpsClient azure,
		GitMirror mirror,
		string strMirrorPath,
		AzureRepositoryInfo azureRepositoryInfo,
		GitHubRepositoryInfo gitHubRepositoryInfo,
		MirrorSummary mirrorSummary,
		CancellationToken cancellationToken)
	{
		string? strBranchName = gitHubRepositoryInfo.DefaultBranch
		                        ?? await mirror.GetHeadBranchAsync(strMirrorPath, cancellationToken);

		if(string.IsNullOrWhiteSpace(strBranchName))
		{
			ConsoleLog.Info("No default branch to set.");
			return null;
		}

		string strRefName = $"refs/heads/{strBranchName}";

		if(!mirrorSummary.Branches.Any(b => b.Name.Equals(strRefName, StringComparison.Ordinal)))
		{
			ConsoleLog.Warn($"Source default branch '{strBranchName}' was not among the migrated branches; leaving the target default unchanged.");
			return null;
		}

		if(string.Equals(azureRepositoryInfo.DefaultBranch, strRefName, StringComparison.Ordinal))
		{
			ConsoleLog.Success($"Default branch is already '{strBranchName}'");
			return strBranchName;
		}

		await azure.SetDefaultBranchAsync(azureRepositoryInfo.Id, strRefName, cancellationToken);
		ConsoleLog.Success($"Default branch set to '{strBranchName}'");

		return strBranchName;
	}

	/// <summary>
	/// Reads the refs back from Azure DevOps and compares them with the mirror, so the
	/// result is confirmed by the server rather than assumed from a zero exit code.
	/// </summary>
	private async Task VerifyAsync(
		AzureDevOpsClient azure,
		AzureRepositoryInfo azureRepositoryInfo,
		MirrorSummary mirrorSummary,
		CancellationToken cancellationToken)
	{
		IReadOnlyList<string> liTargetRefs = await azure.ListRefsAsync(azureRepositoryInfo.Id, cancellationToken);

		int nTargetBranches = liTargetRefs.Count(r => r.StartsWith("refs/heads/", StringComparison.Ordinal));
		int nTargetTags = liTargetRefs.Count(r => r.StartsWith("refs/tags/", StringComparison.Ordinal));

		ConsoleLog.Info($"Azure DevOps reports {nTargetBranches} branches and {nTargetTags} tags.");

		List<string> liProblems = [];

		if(nTargetBranches < mirrorSummary.Branches.Count)
		{
			liProblems.Add($"{mirrorSummary.Branches.Count - nTargetBranches} branch(es) missing");
		}

		if(nTargetTags < mirrorSummary.Tags.Count) liProblems.Add($"{mirrorSummary.Tags.Count - nTargetTags} tag(s) missing");

		if(liProblems.Count > 0)
		{
			// Not thrown: the push itself succeeded, so the user needs the detail, not a crash.
			ConsoleLog.Warn($"Verification found a difference: {string.Join(", ", liProblems)}.");
			ConsoleLog.Warn("Re-run the tool to retry the missing refs, or inspect the push output above.");

			return;
		}

		ConsoleLog.Success("Branch and tag counts match the source.");
	}
	#endregion
}
