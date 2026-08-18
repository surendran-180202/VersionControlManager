using VersionControlManager.Clients;
using VersionControlManager.Configuration;
using VersionControlManager.Git;
using VersionControlManager.Logging;
using VersionControlManager.Vcs;

namespace VersionControlManager.Migration;

/// <summary>Outcome of a completed migration, for the closing summary.</summary>
internal sealed record MigrationResult(
    string SourceDescription,
    string TargetDescription,
    string TargetUrl,
    int CommitCount,
    int BranchCount,
    int TagCount,
    int NoteCount,
    bool CreatedTargetRepository,
    string? DefaultBranch);

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

    private const int TotalSteps = 8;

    #endregion

    #region Public Methods

    public async Task<MigrationResult> RunAsync(CancellationToken cancellationToken)
    {
        GitHubRepositoryReference source = GitHubRepositoryReference.Parse(options.GitHubUrl);
        AzureDevOpsProjectReference target = AzureDevOpsProjectReference.Parse(options.AzureDevOpsUrl);

        string gitHubAuth = RestSupport.BasicHeaderValue(options.GitHubUserName, options.GitHubPassword);
        string azureAuth = RestSupport.BasicHeaderValue(options.AzureDevOpsUserName, options.AzureDevOpsPassword);

        GitCommandRunner git = new GitCommandRunner();
        GitMirror mirror = new GitMirror(git);

        using GitHubClient gitHub = new GitHubClient(
            RestSupport.CreateClient(options.GitHubUserName, options.GitHubPassword), source);
        using AzureDevOpsClient azure = new AzureDevOpsClient(
            RestSupport.CreateClient(options.AzureDevOpsUserName, options.AzureDevOpsPassword), target);

        // --- 1 ------------------------------------------------------------------
        ConsoleLog.Step(1, TotalSteps, "Checking prerequisites");
        string gitVersion = await git.GetVersionAsync(cancellationToken);
        ConsoleLog.Success($"Found {gitVersion}");

        // --- 2 ------------------------------------------------------------------
        ConsoleLog.Step(2, TotalSteps, $"Reading source repository {source}");
        GitHubRepositoryInfo sourceInfo = await gitHub.GetRepositoryAsync(cancellationToken);
        ConsoleLog.Success($"{sourceInfo.FullName} ({(sourceInfo.IsPrivate ? "private" : "public")})");
        ConsoleLog.Info($"Default branch: {sourceInfo.DefaultBranch ?? "(none)"}");
        ConsoleLog.Info($"Reported size:  {Describe.Kilobytes(sourceInfo.SizeKilobytes)}");

        // --- 3 ------------------------------------------------------------------
        ConsoleLog.Step(3, TotalSteps, $"Reading target project {target}");
        AzureProjectInfo projectInfo = await azure.GetProjectAsync(cancellationToken);
        ConsoleLog.Success($"Project '{projectInfo.Name}' found in {target.CollectionUrl}");

        // --- 4 ------------------------------------------------------------------
        string repositoryName = ResolveTargetRepositoryName(source, target);
        ConsoleLog.Step(4, TotalSteps, $"Preparing target repository '{repositoryName}'");

        (AzureRepositoryInfo repository, bool created) = await PrepareTargetRepositoryAsync(
            azure, repositoryName, projectInfo.Id, cancellationToken);

        // --- 5 ------------------------------------------------------------------
        ConsoleLog.Step(5, TotalSteps, "Cloning full history from GitHub");

        using TemporaryWorkspace workspace = TemporaryWorkspace.Create(
            options.WorkingDirectory, repositoryName, options.KeepWorkingCopy);
        ConsoleLog.Info($"Mirror: {workspace.MirrorPath}");

        await mirror.CloneAsync(source.CloneUrl, workspace.MirrorPath, gitHubAuth, cancellationToken);

        MirrorSummary summary = await mirror.SummariseAsync(workspace.MirrorPath, cancellationToken);

        ConsoleLog.Success(
            $"Cloned {summary.CommitCount:N0} commits, {summary.Branches.Count} branches, " +
            $"{summary.Tags.Count} tags ({Describe.Bytes(summary.SizeOnDiskBytes)} on disk)");

        if (summary.Notes.Count > 0)
        {
            ConsoleLog.Info($"Notes refs: {summary.Notes.Count}");
        }

        // --- 6 ------------------------------------------------------------------
        ConsoleLog.Step(6, TotalSteps, "Pushing history to Azure DevOps");

        if (summary.Refs.Count == 0)
        {
            ConsoleLog.Warn("The GitHub repository has no commits, so there is no history to push.");
        }
        else
        {
            await PushHistoryAsync(git, mirror, workspace.MirrorPath, repository.RemoteUrl, summary,
                gitHubAuth, azureAuth, cancellationToken);
        }

        // --- 7 ------------------------------------------------------------------
        ConsoleLog.Step(7, TotalSteps, "Setting the default branch");
        string? defaultBranch = await ApplyDefaultBranchAsync(
            azure, mirror, workspace.MirrorPath, repository, sourceInfo, summary, cancellationToken);

        // --- 8 ------------------------------------------------------------------
        ConsoleLog.Step(8, TotalSteps, "Verifying the target repository");
        await VerifyAsync(azure, repository, summary, cancellationToken);

        return new MigrationResult(
            source.WebUrl,
            $"{target.Organization}/{target.Project}/{repository.Name}",
            target.RepositoryWebUrl(repository.Name),
            summary.CommitCount,
            summary.Branches.Count,
            summary.Tags.Count,
            options.IncludeNotes ? summary.Notes.Count : 0,
            created,
            defaultBranch);
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// An explicit --target-repo wins, then a repository named in the Azure DevOps URL,
    /// then the GitHub repository's own name.
    /// </summary>
    private string ResolveTargetRepositoryName(
        GitHubRepositoryReference source,
        AzureDevOpsProjectReference target)
    {
        if (!string.IsNullOrWhiteSpace(options.TargetRepositoryName))
        {
            return options.TargetRepositoryName.Trim();
        }

        return string.IsNullOrWhiteSpace(target.RepositoryName) ? source.Name : target.RepositoryName;
    }

    private async Task<(AzureRepositoryInfo Repository, bool Created)> PrepareTargetRepositoryAsync(
        AzureDevOpsClient azure,
        string repositoryName,
        string projectId,
        CancellationToken cancellationToken)
    {
        AzureRepositoryInfo? existing = await azure.FindRepositoryAsync(repositoryName, cancellationToken);

        if (existing is null)
        {
            AzureRepositoryInfo created =
                await azure.CreateRepositoryAsync(repositoryName, projectId, cancellationToken);
            ConsoleLog.Success($"Created repository '{created.Name}'");

            return (created, true);
        }

        ConsoleLog.Info($"Repository '{existing.Name}' already exists");

        IReadOnlyList<string> refs = await azure.ListRefsAsync(existing.Id, cancellationToken);

        if (refs.Count == 0)
        {
            ConsoleLog.Success("It is empty, so it can receive the history");

            return (existing, false);
        }

        if (!options.AllowExistingTarget)
        {
            throw new MigrationException(
                ExitCode.TargetError,
                $"Repository '{existing.Name}' already contains {refs.Count} reference(s). " +
                "Refusing to push into it, because that could overwrite or conflict with existing history.",
                "Pass --target-repo <newName> to create a separate repository, or --allow-existing to push anyway.");
        }

        ConsoleLog.Warn($"It already has {refs.Count} reference(s); pushing anyway because --allow-existing was given.");

        return (existing, false);
    }

    private async Task PushHistoryAsync(
        GitCommandRunner git,
        GitMirror mirror,
        string mirrorPath,
        string remoteUrl,
        MirrorSummary summary,
        string gitHubAuth,
        string azureAuth,
        CancellationToken cancellationToken)
    {
        ConsoleLog.Info($"Target: {remoteUrl}");

        await mirror.PushAsync(
            mirrorPath, remoteUrl, summary, options.IncludeNotes, azureAuth, cancellationToken);

        string pushed = $"{summary.Branches.Count} branches and {summary.Tags.Count} tags";

        if (options.IncludeNotes && summary.Notes.Count > 0)
        {
            pushed += $" and {summary.Notes.Count} notes refs";
        }

        ConsoleLog.Success($"Pushed {pushed} ({summary.CommitCount:N0} commits)");

        await HandleLfsAsync(git, mirror, mirrorPath, remoteUrl, gitHubAuth, azureAuth, cancellationToken);
    }

    private async Task HandleLfsAsync(
        GitCommandRunner git,
        GitMirror mirror,
        string mirrorPath,
        string remoteUrl,
        string gitHubAuth,
        string azureAuth,
        CancellationToken cancellationToken)
    {
        if (!options.IncludeLfs)
        {
            if (await mirror.HasLfsContentAsync(mirrorPath, cancellationToken))
            {
                ConsoleLog.Warn(
                    "This repository tracks files with Git LFS. The history was migrated, but the " +
                    "LFS file contents were not. Re-run with --lfs to transfer them.");
            }

            return;
        }

        if (!await git.IsSubcommandAvailableAsync("lfs", cancellationToken))
        {
            throw new MigrationException(
                ExitCode.GitError,
                "--lfs was requested but git-lfs is not installed.",
                "Install Git LFS (https://git-lfs.com) and re-run, or drop --lfs.");
        }

        ConsoleLog.Info("Transferring Git LFS objects");

        // Fetch uses the source's credentials, push uses the target's.
        await mirror.PushLfsAsync(mirrorPath, remoteUrl, gitHubAuth, azureAuth, cancellationToken);

        ConsoleLog.Success("Git LFS objects transferred");
    }

    /// <summary>
    /// Mirrors GitHub's default branch onto the target, so the repository lands on the
    /// branch its users expect rather than whichever ref arrived first.
    /// </summary>
    private static async Task<string?> ApplyDefaultBranchAsync(
        AzureDevOpsClient azure,
        GitMirror mirror,
        string mirrorPath,
        AzureRepositoryInfo repository,
        GitHubRepositoryInfo sourceInfo,
        MirrorSummary summary,
        CancellationToken cancellationToken)
    {
        string? branchName = sourceInfo.DefaultBranch
                         ?? await mirror.GetHeadBranchAsync(mirrorPath, cancellationToken);

        if (string.IsNullOrWhiteSpace(branchName))
        {
            ConsoleLog.Info("No default branch to set.");
            return null;
        }

        string refName = $"refs/heads/{branchName}";

        if (!summary.Branches.Any(b => b.Name.Equals(refName, StringComparison.Ordinal)))
        {
            ConsoleLog.Warn($"Source default branch '{branchName}' was not among the migrated branches; leaving the target default unchanged.");
            return null;
        }

        if (string.Equals(repository.DefaultBranch, refName, StringComparison.Ordinal))
        {
            ConsoleLog.Success($"Default branch is already '{branchName}'");
            return branchName;
        }

        await azure.SetDefaultBranchAsync(repository.Id, refName, cancellationToken);
        ConsoleLog.Success($"Default branch set to '{branchName}'");

        return branchName;
    }

    /// <summary>
    /// Reads the refs back from Azure DevOps and compares them with the mirror, so the
    /// result is confirmed by the server rather than assumed from a zero exit code.
    /// </summary>
    private async Task VerifyAsync(
        AzureDevOpsClient azure,
        AzureRepositoryInfo repository,
        MirrorSummary summary,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> targetRefs = await azure.ListRefsAsync(repository.Id, cancellationToken);

        int targetBranches = targetRefs.Count(r => r.StartsWith("refs/heads/", StringComparison.Ordinal));
        int targetTags = targetRefs.Count(r => r.StartsWith("refs/tags/", StringComparison.Ordinal));

        ConsoleLog.Info($"Azure DevOps reports {targetBranches} branches and {targetTags} tags.");

        List<string> problems = new List<string>();

        if (targetBranches < summary.Branches.Count)
        {
            problems.Add($"{summary.Branches.Count - targetBranches} branch(es) missing");
        }

        if (targetTags < summary.Tags.Count)
        {
            problems.Add($"{summary.Tags.Count - targetTags} tag(s) missing");
        }

        if (problems.Count > 0)
        {
            // Not thrown: the push itself succeeded, so the user needs the detail, not a crash.
            ConsoleLog.Warn($"Verification found a difference: {string.Join(", ", problems)}.");
            ConsoleLog.Warn("Re-run the tool to retry the missing refs, or inspect the push output above.");

            return;
        }

        ConsoleLog.Success("Branch and tag counts match the source.");
    }

    #endregion
}
