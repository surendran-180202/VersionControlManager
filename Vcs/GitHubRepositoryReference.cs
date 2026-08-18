using System.Text.RegularExpressions;
using VersionControlManager.Migration;

namespace VersionControlManager.Vcs;

/// <summary>A GitHub repository identified from a user-supplied URL.</summary>
internal sealed record GitHubRepositoryReference(string Host, string Owner, string Name, string ApiBaseUrl)
{
    #region Properties

    /// <summary>The HTTPS URL git clones from. Credentials are supplied by header, never here.</summary>
    public string CloneUrl => $"https://{Host}/{Owner}/{Name}.git";

    public string WebUrl => $"https://{Host}/{Owner}/{Name}";

    #endregion

    #region Public Methods

    public override string ToString() => $"{Owner}/{Name}";

    /// <summary>
    /// Parses the shapes people actually paste: an HTTPS URL, an SSH URL, a "gh"-style
    /// owner/repo pair, with or without a .git suffix, on github.com or GitHub Enterprise.
    /// </summary>
    public static GitHubRepositoryReference Parse(string value)
    {
        string input = (value ?? string.Empty).Trim().Trim('"');

        if (input.Length == 0)
        {
            throw Invalid(input, "the value is empty");
        }

        string host;
        string path;

        // git@github.com:owner/repo.git  and  ssh://git@github.com/owner/repo.git
        Match scpMatch = Regex.Match(input, @"^(?:ssh://)?[^@/]+@(?<host>[^:/]+)[:/](?<path>.+)$");

        if (scpMatch.Success && !input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            host = scpMatch.Groups["host"].Value;
            path = scpMatch.Groups["path"].Value;
        }
        else if (input.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out Uri? uri))
            {
                throw Invalid(input, "it is not a well-formed URL");
            }

            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else if (Regex.IsMatch(input, @"^[^/\s]+/[^/\s]+/?$"))
        {
            // Bare "owner/repo" is unambiguous enough to accept.
            host = "github.com";
            path = input;
        }
        else
        {
            throw Invalid(input, "it is not a recognised GitHub URL");
        }

        host = NormaliseHost(host);

        string[] segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        if (segments.Length < 2)
        {
            throw Invalid(input, "no owner/repository pair was found in the path");
        }

        string owner = segments[0];
        string name = StripGitSuffix(segments[1]);

        if (owner.Length == 0 || name.Length == 0)
        {
            throw Invalid(input, "the owner or repository name is empty");
        }

        return new GitHubRepositoryReference(host, owner, name, BuildApiBaseUrl(host));
    }

    #endregion

    #region Private Methods

    private static string NormaliseHost(string host)
    {
        host = host.Trim().TrimEnd('.');

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            host = host[4..];
        }

        return host;
    }

    private static string StripGitSuffix(string name) =>
        name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>
    /// github.com serves its API from a separate host; GitHub Enterprise Server serves it
    /// from /api/v3 on the same host.
    /// </summary>
    private static string BuildApiBaseUrl(string host) =>
        host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? "https://api.github.com"
            : $"https://{host}/api/v3";

    private static MigrationException Invalid(string input, string reason) =>
        new(ExitCode.ConfigurationError,
            $"Could not read a GitHub repository from '{input}' -- {reason}.",
            "Expected something like https://github.com/owner/repository");

    #endregion
}
