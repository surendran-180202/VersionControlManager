using VersionControlManager.Migration;

namespace VersionControlManager.Vcs;

/// <summary>
/// An Azure DevOps team project identified from a user-supplied URL, plus the repository
/// name if the URL happened to name one.
/// </summary>
/// <param name="CollectionUrl">Account/collection root, e.g. https://dev.azure.com/contoso.</param>
/// <param name="Organization">Display name of the organisation or collection.</param>
/// <param name="Project">Team project name, already URL-decoded.</param>
/// <param name="RepositoryName">Repository named by the URL, if any.</param>
internal sealed record AzureDevOpsProjectReference(string CollectionUrl, string Organization, string Project, string? RepositoryName)
{
	#region Properties
	public string ProjectWebUrl => $"{this.CollectionUrl}/{Uri.EscapeDataString(this.Project)}";
	#endregion

	#region Publics
	public string RepositoryWebUrl(string strRepositoryName)
	{
		return $"{this.ProjectWebUrl}/_git/{Uri.EscapeDataString(strRepositoryName)}";
	}

	public override string ToString()
	{
		return $"{this.Organization}/{this.Project}";
	}

	/// <summary>
	/// Handles the hosted forms (dev.azure.com/org/project, org.visualstudio.com/project)
	/// and on-premises Azure DevOps Server (host/tfs/collection/project), with or without
	/// a /_git/repository suffix, and with credentials or a query string attached.
	/// </summary>
	public static AzureDevOpsProjectReference Parse(string strValue)
	{
		string strInput = (strValue ?? string.Empty).Trim().Trim('"');

		if(strInput.Length == 0) throw Invalid(strInput, "the value is empty");

		// Accept a bare host so users can paste from a browser without the scheme.
		if(!strInput.Contains("://", StringComparison.Ordinal)) strInput = $"https://{strInput}";

		if(!Uri.TryCreate(strInput, UriKind.Absolute, out Uri? uri)) throw Invalid(strInput, "it is not a well-formed URL");

		// Drop any userinfo -- the clone dialog emits https://org@dev.azure.com/... and we
		// authenticate by header, so a username in the URL only invites a credential prompt.
		string strOrigin = uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";

		// Legacy {org}.visualstudio.com permanently redirects to dev.azure.com/{org}, and
		// .NET strips the Authorization header when a redirect crosses origins -- which would
		// surface as a puzzling authentication failure. Use the canonical host from the start.
		if(IsLegacyHost(uri.Host)) strOrigin = $"https://dev.azure.com/{Uri.EscapeDataString(uri.Host[..uri.Host.IndexOf('.')])}";

		string[] liSegments = [.. uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Uri.UnescapeDataString)];

		int nMinimumCollectionSegments = MinimumCollectionSegments(uri.Host);

		string strProject;
		string? strRepository = null;
		string[] liCollectionSegments;

		int nGitIndex = Array.FindIndex(liSegments, strSegment => strSegment.Equals("_git", StringComparison.OrdinalIgnoreCase));

		if(nGitIndex >= 0)
		{
			if(nGitIndex + 1 >= liSegments.Length)
			{
				throw Invalid(strInput, "the URL ends at '_git' without naming a repository");
			}

			strRepository = StripGitSuffix(liSegments[nGitIndex + 1]);

			if(nGitIndex - 1 >= nMinimumCollectionSegments)
			{
				strProject = liSegments[nGitIndex - 1];
				liCollectionSegments = liSegments[..(nGitIndex - 1)];
			}
			else
			{
				// Azure DevOps allows .../_git/<name> when the project and repository
				// share a name, so the project segment is absent.
				strProject = strRepository;
				liCollectionSegments = liSegments[..nGitIndex];
			}
		}
		else
		{
			if(liSegments.Length < nMinimumCollectionSegments + 1)
			{
				throw Invalid(strInput, "no team project was found in the path");
			}

			strProject = liSegments[^1];
			liCollectionSegments = liSegments[..^1];
		}

		if(strProject.Length == 0)
		{
			throw Invalid(strInput, "the team project name is empty");
		}

		string strCollectionUrl = liCollectionSegments.Length == 0 ? strOrigin : $"{strOrigin}/{string.Join('/', liCollectionSegments.Select(Uri.EscapeDataString))}";

		return new AzureDevOpsProjectReference(strCollectionUrl, ResolveOrganization(uri.Host, liCollectionSegments), strProject, strRepository);
	}
	#endregion

	#region Privates
	/// <summary>The retired {org}.visualstudio.com form, which carries the org in the hostname.</summary>
	private static bool IsLegacyHost(string strHost)
	{
		return strHost.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && strHost.IndexOf('.') > 0;
	}

	/// <summary>
	/// How many leading path segments belong to the collection rather than the project.
	/// dev.azure.com needs the organisation; visualstudio.com carries it in the hostname;
	/// on-premises servers need at least a collection name.
	/// </summary>
	private static int MinimumCollectionSegments(string strHost)
	{
		return IsLegacyHost(strHost) ? 0 : 1;
	}

	private static string ResolveOrganization(string strHost, string[] liCollectionSegments)
	{
		if(IsLegacyHost(strHost)) return strHost[..strHost.IndexOf('.')];

		return liCollectionSegments.Length > 0 ? liCollectionSegments[^1] : strHost;
	}

	private static string StripGitSuffix(string strName)
	{
		return strName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? strName[..^4] : strName;
	}

	private static MigrationException Invalid(string strInput, string strReason)
	{
		return new(ExitCode.ConfigurationError,
		           $"Could not read an Azure DevOps project from '{strInput}' -- {strReason}.",
		           "Expected something like https://dev.azure.com/organisation/project");
	}
	#endregion
}
