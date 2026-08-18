using System.Text.RegularExpressions;
using VersionControlManager.Migration;

namespace VersionControlManager.Vcs;

/// <summary>A GitHub repository identified from a user-supplied URL.</summary>
internal sealed record GitHubRepositoryReference(string Host, string Owner, string Name, string ApiBaseUrl)
{
	#region Properties
	/// <summary>The HTTPS URL git clones from. Credentials are supplied by header, never here.</summary>
	public string CloneUrl => $"https://{this.Host}/{this.Owner}/{this.Name}.git";

	public string WebUrl => $"https://{this.Host}/{this.Owner}/{this.Name}";
	#endregion

	#region Publics
	public override string ToString()
	{
		return $"{this.Owner}/{this.Name}";
	}

	/// <summary>
	/// Parses the shapes people actually paste: an HTTPS URL, an SSH URL, a "gh"-style
	/// owner/repo pair, with or without a .git suffix, on github.com or GitHub Enterprise.
	/// </summary>
	public static GitHubRepositoryReference Parse(string strValue)
	{
		string strInput = (strValue ?? string.Empty).Trim().Trim('"');

		if(strInput.Length == 0) throw Invalid(strInput, "the value is empty");

		string strHost;
		string strPath;

		// git@github.com:owner/repo.git  and  ssh://git@github.com/owner/repo.git
		Match scpMatch = Regex.Match(strInput, @"^(?:ssh://)?[^@/]+@(?<host>[^:/]+)[:/](?<path>.+)$");

		if(scpMatch.Success && !strInput.StartsWith("http", StringComparison.OrdinalIgnoreCase))
		{
			strHost = scpMatch.Groups["host"].Value;
			strPath = scpMatch.Groups["path"].Value;
		}
		else if(strInput.Contains("://", StringComparison.Ordinal))
		{
			if(!Uri.TryCreate(strInput, UriKind.Absolute, out Uri? uri))
			{
				throw Invalid(strInput, "it is not a well-formed URL");
			}

			strHost = uri.Host;
			strPath = uri.AbsolutePath;
		}
		else if(Regex.IsMatch(strInput, @"^[^/\s]+/[^/\s]+/?$"))
		{
			// Bare "owner/repo" is unambiguous enough to accept.
			strHost = "github.com";
			strPath = strInput;
		}
		else
		{
			throw Invalid(strInput, "it is not a recognised GitHub URL");
		}

		strHost = NormaliseHost(strHost);

		string[] liSegments = [.. strPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Uri.UnescapeDataString)];

		if(liSegments.Length < 2)
		{
			throw Invalid(strInput, "no owner/repository pair was found in the path");
		}

		string strOwner = liSegments[0];
		string strName = StripGitSuffix(liSegments[1]);

		if(strOwner.Length == 0 || strName.Length == 0) throw Invalid(strInput, "the owner or repository name is empty");

		return new GitHubRepositoryReference(strHost, strOwner, strName, BuildApiBaseUrl(strHost));
	}
	#endregion

	#region Privates
	private static string NormaliseHost(string strHost)
	{
		strHost = strHost.Trim().TrimEnd('.');

		if(strHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) strHost = strHost[4..];

		return strHost;
	}

	private static string StripGitSuffix(string strName)
	{
		return strName.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? strName[..^4] : strName;
	}

	/// <summary>
	/// github.com serves its API from a separate host; GitHub Enterprise Server serves it
	/// from /api/v3 on the same host.
	/// </summary>
	private static string BuildApiBaseUrl(string strHost)
	{
		return strHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? "https://api.github.com" : $"https://{strHost}/api/v3";
	}

	private static MigrationException Invalid(string strInput, string strReason)
	{
		return new(ExitCode.ConfigurationError, $"Could not read a GitHub repository from '{strInput}' -- {strReason}.", "Expected something like https://github.com/owner/repository");
	}
	#endregion
}
