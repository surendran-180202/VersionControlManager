using System.Text.Json;
using VersionControlManager.Migration;
using VersionControlManager.Vcs;

namespace VersionControlManager.Clients;

/// <summary>What GitHub tells us about the source repository.</summary>
internal sealed record GitHubRepositoryInfo(
    string FullName,
    string? DefaultBranch,
    bool IsPrivate,
    bool IsEmpty,
    long SizeKilobytes);

/// <summary>
/// Minimal GitHub REST client: just enough to confirm the repository exists and is
/// reachable with the supplied credentials before we spend time cloning it.
/// </summary>
internal sealed class GitHubClient(HttpClient client, GitHubRepositoryReference repository) : IDisposable
{
    #region Constants
    private const string ServiceName = "GitHub";
    #endregion

    #region Public Methods
    public async Task<GitHubRepositoryInfo> GetRepositoryAsync(CancellationToken cancellationToken)
    {
        string url = $"{repository.ApiBaseUrl}/repos/" +
                     $"{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}";

        using HttpRequestMessage request = RestSupport.Json(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using JsonDocument? document = await RestSupport.SendAllowingNotFoundAsync(
            client, request, ServiceName, ExitCode.SourceError, cancellationToken);

        if (document is null)
        {
            throw new MigrationException(
                ExitCode.SourceError,
                $"GitHub has no repository '{repository}' visible to this account.",
                "Check the URL for typos, and that the token has access if the repository is private.");
        }

        JsonElement root = document.RootElement;

        long size = root.TryGetProperty("size", out JsonElement sizeElement)
                    && sizeElement.TryGetInt64(out long sizeValue)
            ? sizeValue
            : 0;

        return new GitHubRepositoryInfo(
            RestSupport.StringOrNull(root, "full_name") ?? repository.ToString(),
            RestSupport.StringOrNull(root, "default_branch"),
            root.TryGetProperty("private", out JsonElement isPrivate) && isPrivate.ValueKind == JsonValueKind.True,
            size == 0,
            size);
    }

    public void Dispose() => client.Dispose();
    #endregion
}
