using VersionControlManager.Logging;
using VersionControlManager.Migration;

namespace VersionControlManager.Git;

/// <summary>A single git reference in the mirror.</summary>
internal sealed record GitRef(string Name, string ObjectType)
{
    public bool IsBranch => Name.StartsWith("refs/heads/", StringComparison.Ordinal);

    public bool IsTag => Name.StartsWith("refs/tags/", StringComparison.Ordinal);

    public bool IsNote => Name.StartsWith("refs/notes/", StringComparison.Ordinal);

    public string ShortName => Name.Split('/', 3) is [_, _, string rest] ? rest : Name;
}

/// <summary>What the mirror clone contains, used for reporting and verification.</summary>
internal sealed record MirrorSummary(
    IReadOnlyList<GitRef> Refs,
    int CommitCount,
    long SizeOnDiskBytes)
{
    public IReadOnlyList<GitRef> Branches => [.. Refs.Where(r => r.IsBranch)];

    public IReadOnlyList<GitRef> Tags => [.. Refs.Where(r => r.IsTag)];

    public IReadOnlyList<GitRef> Notes => [.. Refs.Where(r => r.IsNote)];
}

/// <summary>
/// Mirror clone and push operations. A mirror clone (--mirror) is what preserves the
/// check-in history: it copies every object and every ref verbatim, so commit SHAs,
/// authors, dates, parents, and messages are identical on the target.
/// </summary>
internal sealed class GitMirror(GitCommandRunner git)
{
    /// <summary>Clones every ref and object from <paramref name="cloneUrl"/> into a bare mirror.</summary>
    public async Task CloneAsync(
        string cloneUrl,
        string destination,
        string authorizationHeader,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            ["clone", "--mirror", "--progress", cloneUrl, destination],
            authorizationHeader: authorizationHeader,
            relayProgress: true,
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            throw new MigrationException(
                ExitCode.SourceError,
                $"Mirror clone of {cloneUrl} failed.{Environment.NewLine}{ConsoleLog.Redact(result.FailureText)}",
                DescribeCloneFailure(result.FailureText));
        }
    }

    public async Task<MirrorSummary> SummariseAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        GitResult refsResult = await git.RunAsync(
            ["for-each-ref", "--format=%(refname)%09%(objecttype)"],
            repositoryPath,
            cancellationToken: cancellationToken);

        if (!refsResult.Success)
        {
            throw new MigrationException(
                ExitCode.GitError,
                $"Could not list references in the mirror: {refsResult.FailureText}");
        }

        GitRef[] refs = refsResult.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2)
            .Select(parts => new GitRef(parts[0], parts[1]))
            .ToArray();

        int commitCount = 0;

        if (refs.Length > 0)
        {
            GitResult countResult = await git.RunAsync(
                ["rev-list", "--all", "--count"],
                repositoryPath,
                cancellationToken: cancellationToken);

            if (countResult.Success
                && int.TryParse(countResult.StandardOutput.Trim(), out int parsed))
            {
                commitCount = parsed;
            }
        }

        return new MirrorSummary(refs, commitCount, MeasureDirectory(repositoryPath));
    }

    /// <summary>
    /// Pushes history to the target. Branches, tags and notes are pushed with explicit
    /// wildcard refspecs rather than "push --mirror": a GitHub mirror also contains
    /// refs/pull/*, which Azure DevOps rejects as a reserved namespace and which would
    /// otherwise fail the whole push.
    /// </summary>
    public async Task PushAsync(
        string repositoryPath,
        string targetUrl,
        MirrorSummary summary,
        bool includeNotes,
        string authorizationHeader,
        CancellationToken cancellationToken)
    {
        List<string> arguments = new List<string> { "push", "--porcelain", "--progress", targetUrl };

        if (summary.Branches.Count > 0)
        {
            arguments.Add("refs/heads/*:refs/heads/*");
        }

        if (summary.Tags.Count > 0)
        {
            arguments.Add("refs/tags/*:refs/tags/*");
        }

        if (includeNotes && summary.Notes.Count > 0)
        {
            arguments.Add("refs/notes/*:refs/notes/*");
        }

        GitResult result = await git.RunAsync(
            arguments,
            repositoryPath,
            authorizationHeader,
            relayProgress: true,
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            throw new MigrationException(
                ExitCode.TargetError,
                $"Push to Azure DevOps failed.{Environment.NewLine}{ConsoleLog.Redact(result.FailureText)}",
                DescribePushFailure(result.FailureText));
        }
    }

    /// <summary>
    /// True if the default branch tracks files through Git LFS. Used only to warn when
    /// --lfs was not requested, so checking HEAD's .gitattributes is sufficient: a repo
    /// using LFS declares it there. Reading the blob directly works in a bare mirror.
    /// </summary>
    public async Task<bool> HasLfsContentAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            ["cat-file", "-p", "HEAD:.gitattributes"],
            repositoryPath,
            cancellationToken: cancellationToken);

        // A non-zero exit just means there is no .gitattributes on the default branch.
        return result.Success
            && result.StandardOutput.Contains("filter=lfs", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Transfers LFS objects. These live outside the git object store, so a mirror push
    /// moves only the pointer files -- the payloads need their own fetch and push, each
    /// against its own host and therefore its own credentials.
    /// </summary>
    public async Task PushLfsAsync(
        string repositoryPath,
        string targetUrl,
        string sourceAuthorizationHeader,
        string targetAuthorizationHeader,
        CancellationToken cancellationToken)
    {
        GitResult fetch = await git.RunAsync(
            ["lfs", "fetch", "--all", "origin"],
            repositoryPath,
            sourceAuthorizationHeader,
            relayProgress: true,
            cancellationToken: cancellationToken);

        if (!fetch.Success)
        {
            throw new MigrationException(
                ExitCode.SourceError,
                $"Could not fetch Git LFS objects from the source.{Environment.NewLine}{ConsoleLog.Redact(fetch.FailureText)}");
        }

        GitResult push = await git.RunAsync(
            ["lfs", "push", "--all", targetUrl],
            repositoryPath,
            targetAuthorizationHeader,
            relayProgress: true,
            cancellationToken: cancellationToken);

        if (!push.Success)
        {
            throw new MigrationException(
                ExitCode.TargetError,
                $"Could not push Git LFS objects to the target.{Environment.NewLine}{ConsoleLog.Redact(push.FailureText)}");
        }
    }

    /// <summary>The branch the mirror's HEAD points at, e.g. "main". Null if detached.</summary>
    public async Task<string?> GetHeadBranchAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            ["symbolic-ref", "--short", "HEAD"],
            repositoryPath,
            cancellationToken: cancellationToken);

        string value = result.StandardOutput.Trim();

        return result.Success && value.Length > 0 ? value : null;
    }

    private static long MeasureDirectory(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string? DescribeCloneFailure(string failureText)
    {
        if (failureText.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || failureText.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || failureText.Contains("403", StringComparison.Ordinal))
        {
            return "Check the GitHub token is valid and has 'repo' scope for a private repository.";
        }

        if (failureText.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || failureText.Contains("404", StringComparison.Ordinal))
        {
            return "Check the repository URL, and that the token can see it.";
        }

        return null;
    }

    private static string? DescribePushFailure(string failureText)
    {
        if (failureText.Contains("TF401019", StringComparison.Ordinal)
            || failureText.Contains("denied", StringComparison.OrdinalIgnoreCase)
            || failureText.Contains("403", StringComparison.Ordinal))
        {
            return "The Azure DevOps token needs Code (read, write, and manage) scope.";
        }

        if (failureText.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase)
            || failureText.Contains("fetch first", StringComparison.OrdinalIgnoreCase))
        {
            return "The target repository already contains conflicting history. Use an empty repository, or --target-repo to create a new one.";
        }

        if (failureText.Contains("VS403636", StringComparison.Ordinal)
            || failureText.Contains("too large", StringComparison.OrdinalIgnoreCase))
        {
            return "A file in history exceeds the Azure DevOps size limit. Consider Git LFS, or rewriting history before migrating.";
        }

        return null;
    }
}
