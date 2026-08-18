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
	private const string SERVICE_NAME = "Azure DevOps";
	private const string API_VERSION = "7.1";
	#endregion

	#region Properties
	private string ProjectApiRoot =>
		$"{project.CollectionUrl}/{Uri.EscapeDataString(project.Project)}/_apis/git";
	#endregion

	#region Publics
	public async Task<AzureProjectInfo> GetProjectAsync(CancellationToken cancellationToken)
	{
		string strUrl = $"{project.CollectionUrl}/_apis/projects/" +
					 $"{Uri.EscapeDataString(project.Project)}?api-version={API_VERSION}";

		using HttpRequestMessage request = RestSupport.Json(HttpMethod.Get, strUrl);

		using JsonDocument? document = await RestSupport.SendAllowingNotFoundAsync(
			client, request, SERVICE_NAME, ExitCode.TargetError, cancellationToken) ?? throw new MigrationException(
				ExitCode.TargetError,
				$"Azure DevOps has no project '{project.Project}' in {project.CollectionUrl}.",
				"Check the project name and that your token has access to it.");
		JsonElement root = document.RootElement;

		return new AzureProjectInfo(
			RestSupport.RequiredString(root, "id", SERVICE_NAME),
			RestSupport.StringOrNull(root, "name") ?? project.Project);
	}

	/// <summary>Returns the repository, or null if it does not exist yet.</summary>
	public async Task<AzureRepositoryInfo?> FindRepositoryAsync(string strName, CancellationToken cancellationToken)
	{
		string strUrl = $"{this.ProjectApiRoot}/repositories/{Uri.EscapeDataString(strName)}?api-version={API_VERSION}";

		using HttpRequestMessage request = RestSupport.Json(HttpMethod.Get, strUrl);

		using JsonDocument? document = await RestSupport.SendAllowingNotFoundAsync(
			client, request, SERVICE_NAME, ExitCode.TargetError, cancellationToken);

		return document is null ? null : ReadRepository(document.RootElement);
	}

	public async Task<AzureRepositoryInfo> CreateRepositoryAsync(
		string strName,
		string strProjectId,
		CancellationToken cancellationToken)
	{
		string strUrl = $"{this.ProjectApiRoot}/repositories?api-version={API_VERSION}";

		using HttpRequestMessage request = RestSupport.Json(
			HttpMethod.Post,
			strUrl,
			new { strName, project = new { id = strProjectId } });

		using JsonDocument document = await RestSupport.SendAsync(
			client, request, SERVICE_NAME, ExitCode.TargetError, cancellationToken);

		return ReadRepository(document.RootElement);
	}

	/// <summary>Every ref in the target repository, e.g. "refs/heads/main".</summary>
	public async Task<IReadOnlyList<string>> ListRefsAsync(string strRepositoryId, CancellationToken cancellationToken)
	{
		string strUrl = $"{this.ProjectApiRoot}/repositories/{Uri.EscapeDataString(strRepositoryId)}" +
					 $"/refs?api-version={API_VERSION}&$top=10000";

		using HttpRequestMessage request = RestSupport.Json(HttpMethod.Get, strUrl);

		using JsonDocument document = await RestSupport.SendAsync(
			client, request, SERVICE_NAME, ExitCode.TargetError, cancellationToken);

		if(!document.RootElement.TryGetProperty("value", out JsonElement value)
			|| value.ValueKind != JsonValueKind.Array)
		{
			return [];
		}

		return [.. value
			.EnumerateArray()
			.Select(jsonElement => RestSupport.StringOrNull(jsonElement, "name"))
			.Where(strName => !string.IsNullOrEmpty(strName))
			.Select(strName => strName!)];
	}

	/// <summary>
	/// Points the target's default branch at the same branch GitHub used, so the migrated
	/// repository opens on the branch people expect.
	/// </summary>
	public async Task SetDefaultBranchAsync(
		string strRepositoryId,
		string strBranchRefName,
		CancellationToken cancellationToken)
	{
		string strUrl = $"{this.ProjectApiRoot}/repositories/{Uri.EscapeDataString(strRepositoryId)}?api-version={API_VERSION}";

		using HttpRequestMessage request = RestSupport.Json(
			HttpMethod.Patch,
			strUrl,
			new { defaultBranch = strBranchRefName });

		// The updated repository is returned; we only care that the call succeeded.
		(await RestSupport.SendAsync(client, request, SERVICE_NAME, ExitCode.TargetError, cancellationToken))
			.Dispose();
	}

	public void Dispose()
	{
		client.Dispose();
	}
	#endregion

	#region Privates
	private static AzureRepositoryInfo ReadRepository(JsonElement element)
	{
		string strRemoteUrl = RestSupport.RequiredString(element, "remoteUrl", SERVICE_NAME);

		return new AzureRepositoryInfo(
			RestSupport.RequiredString(element, "id", SERVICE_NAME),
			RestSupport.RequiredString(element, "name", SERVICE_NAME),
			StripUserInfo(strRemoteUrl),
			RestSupport.StringOrNull(element, "defaultBranch"));
	}

	/// <summary>
	/// Azure DevOps returns remoteUrl as https://org@dev.azure.com/... . We authenticate by
	/// header, and a username in the URL only invites git to prompt for a matching password.
	/// </summary>
	private static string StripUserInfo(string strUrl)
	{
		if(!Uri.TryCreate(strUrl, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length == 0)
		{
			return strUrl;
		}

		return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
	}
	#endregion
}
