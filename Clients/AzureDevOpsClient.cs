using System.Text.Json;
using VersionControlManager.Migration;
using VersionControlManager.Vcs;

namespace VersionControlManager.Clients;

internal sealed record AzureProjectInfo(string Id, string Name);

internal sealed record AzureRepositoryInfo(string Id, string Name, string RemoteUrl, string? DefaultBranch);

/// <summary>
/// Minimal Azure DevOps REST client: verify the project, create or find the target
/// repository, set its default branch, and read back its refs for verification.
/// </summary>
internal sealed class AzureDevOpsClient(HttpClient client, AzureDevOpsProjectReference project) : IDisposable
{
    #region Constants
    private const string ServiceName = "Azure DevOps";
    private const string ApiVersion = "7.1";
    #endregion

    #region Properties
    private string ProjectApiRoot =>
        $"{project.CollectionUrl}/{Uri.EscapeDataString(project.Project)}/_apis/git";
    #endregion

    #region Publics
    public async Task<AzureProjectInfo> GetProjectAsync(CancellationToken cancellationToken)
    {
        string url = $"{project.CollectionUrl}/_apis/projects/" +
                     $"{Uri.EscapeDataString(project.Project)}?api-version={ApiVersion}";

        using HttpRequestMessage request = RestSupport.Json(HttpMethod.Get, url);

        using JsonDocument? document = await RestSupport.SendAllowingNotFoundAsync(
            client, request, ServiceName, ExitCode.TargetError, cancellationToken);

        if (document is null)
        {
            throw new MigrationException(
                ExitCode.TargetError,
                $"Azure DevOps has no project '{project.Project}' in {project.CollectionUrl}.",
                "Check the project name and that your token has access to it.");
        }

        JsonElement root = document.RootElement;

        return new AzureProjectInfo(
            RestSupport.RequiredString(root, "id", ServiceName),
            RestSupport.StringOrNull(root, "name") ?? project.Project);
    }

    /// <summary>Returns the repository, or null if it does not exist yet.</summary>
    public async Task<AzureRepositoryInfo?> FindRepositoryAsync(string name, CancellationToken cancellationToken)
    {
        string url = $"{ProjectApiRoot}/repositories/{Uri.EscapeDataString(name)}?api-version={ApiVersion}";

        using HttpRequestMessage request = RestSupport.Json(HttpMethod.Get, url);

        using JsonDocument? document = await RestSupport.SendAllowingNotFoundAsync(
            client, request, ServiceName, ExitCode.TargetError, cancellationToken);

        return document is null ? null : ReadRepository(document.RootElement);
    }

    public async Task<AzureRepositoryInfo> CreateRepositoryAsync(
        string name,
        string projectId,
        CancellationToken cancellationToken)
    {
        string url = $"{ProjectApiRoot}/repositories?api-version={ApiVersion}";

        using HttpRequestMessage request = RestSupport.Json(
            HttpMethod.Post,
            url,
            new { name, project = new { id = projectId } });

        using JsonDocument document = await RestSupport.SendAsync(
            client, request, ServiceName, ExitCode.TargetError, cancellationToken);

        return ReadRepository(document.RootElement);
    }

    /// <summary>Every ref in the target repository, e.g. "refs/heads/main".</summary>
    public async Task<IReadOnlyList<string>> ListRefsAsync(string repositoryId, CancellationToken cancellationToken)
    {
        string url = $"{ProjectApiRoot}/repositories/{Uri.EscapeDataString(repositoryId)}" +
                     $"/refs?api-version={ApiVersion}&$top=10000";

        using HttpRequestMessage request = RestSupport.Json(HttpMethod.Get, url);

        using JsonDocument document = await RestSupport.SendAsync(
            client, request, ServiceName, ExitCode.TargetError, cancellationToken);

        if (!document.RootElement.TryGetProperty("value", out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value
            .EnumerateArray()
            .Select(item => RestSupport.StringOrNull(item, "name"))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)];
    }

    /// <summary>
    /// Points the target's default branch at the same branch GitHub used, so the migrated
    /// repository opens on the branch people expect.
    /// </summary>
    public async Task SetDefaultBranchAsync(
        string repositoryId,
        string branchRefName,
        CancellationToken cancellationToken)
    {
        string url = $"{ProjectApiRoot}/repositories/{Uri.EscapeDataString(repositoryId)}?api-version={ApiVersion}";

        using HttpRequestMessage request = RestSupport.Json(
            HttpMethod.Patch,
            url,
            new { defaultBranch = branchRefName });

        // The updated repository is returned; we only care that the call succeeded.
        (await RestSupport.SendAsync(client, request, ServiceName, ExitCode.TargetError, cancellationToken))
            .Dispose();
    }

    public void Dispose() => client.Dispose();
    #endregion

    #region Privates
    private static AzureRepositoryInfo ReadRepository(JsonElement element)
    {
        string remoteUrl = RestSupport.RequiredString(element, "remoteUrl", ServiceName);

        return new AzureRepositoryInfo(
            RestSupport.RequiredString(element, "id", ServiceName),
            RestSupport.RequiredString(element, "name", ServiceName),
            StripUserInfo(remoteUrl),
            RestSupport.StringOrNull(element, "defaultBranch"));
    }

    /// <summary>
    /// Azure DevOps returns remoteUrl as https://org@dev.azure.com/... . We authenticate by
    /// header, and a username in the URL only invites git to prompt for a matching password.
    /// </summary>
    private static string StripUserInfo(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length == 0)
        {
            return url;
        }

        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
    }
    #endregion
}
