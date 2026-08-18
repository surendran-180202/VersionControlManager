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
internal sealed record AzureDevOpsProjectReference(
    string CollectionUrl,
    string Organization,
    string Project,
    string? RepositoryName)
{
    public string ProjectWebUrl => $"{CollectionUrl}/{Uri.EscapeDataString(Project)}";

    public string RepositoryWebUrl(string repositoryName) =>
        $"{ProjectWebUrl}/_git/{Uri.EscapeDataString(repositoryName)}";

    public override string ToString() => $"{Organization}/{Project}";

    /// <summary>
    /// Handles the hosted forms (dev.azure.com/org/project, org.visualstudio.com/project)
    /// and on-premises Azure DevOps Server (host/tfs/collection/project), with or without
    /// a /_git/repository suffix, and with credentials or a query string attached.
    /// </summary>
    public static AzureDevOpsProjectReference Parse(string value)
    {
        var input = (value ?? string.Empty).Trim().Trim('"');

        if (input.Length == 0)
        {
            throw Invalid(input, "the value is empty");
        }

        // Accept a bare host so users can paste from a browser without the scheme.
        if (!input.Contains("://", StringComparison.Ordinal))
        {
            input = $"https://{input}";
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            throw Invalid(input, "it is not a well-formed URL");
        }

        // Drop any userinfo -- the clone dialog emits https://org@dev.azure.com/... and we
        // authenticate by header, so a username in the URL only invites a credential prompt.
        var origin = uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";

        // Legacy {org}.visualstudio.com permanently redirects to dev.azure.com/{org}, and
        // .NET strips the Authorization header when a redirect crosses origins -- which would
        // surface as a puzzling authentication failure. Use the canonical host from the start.
        if (IsLegacyHost(uri.Host))
        {
            origin = $"https://dev.azure.com/{Uri.EscapeDataString(uri.Host[..uri.Host.IndexOf('.')])}";
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();

        var minimumCollectionSegments = MinimumCollectionSegments(uri.Host);

        string project;
        string? repository = null;
        string[] collectionSegments;

        var gitIndex = Array.FindIndex(segments, s => s.Equals("_git", StringComparison.OrdinalIgnoreCase));

        if (gitIndex >= 0)
        {
            if (gitIndex + 1 >= segments.Length)
            {
                throw Invalid(input, "the URL ends at '_git' without naming a repository");
            }

            repository = StripGitSuffix(segments[gitIndex + 1]);

            if (gitIndex - 1 >= minimumCollectionSegments)
            {
                project = segments[gitIndex - 1];
                collectionSegments = segments[..(gitIndex - 1)];
            }
            else
            {
                // Azure DevOps allows .../_git/<name> when the project and repository
                // share a name, so the project segment is absent.
                project = repository;
                collectionSegments = segments[..gitIndex];
            }
        }
        else
        {
            if (segments.Length < minimumCollectionSegments + 1)
            {
                throw Invalid(input, "no team project was found in the path");
            }

            project = segments[^1];
            collectionSegments = segments[..^1];
        }

        if (project.Length == 0)
        {
            throw Invalid(input, "the team project name is empty");
        }

        var collectionUrl = collectionSegments.Length == 0
            ? origin
            : $"{origin}/{string.Join('/', collectionSegments.Select(Uri.EscapeDataString))}";

        return new AzureDevOpsProjectReference(
            collectionUrl,
            ResolveOrganization(uri.Host, collectionSegments),
            project,
            repository);
    }

    /// <summary>The retired {org}.visualstudio.com form, which carries the org in the hostname.</summary>
    private static bool IsLegacyHost(string host) =>
        host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
        && host.IndexOf('.') > 0;

    /// <summary>
    /// How many leading path segments belong to the collection rather than the project.
    /// dev.azure.com needs the organisation; visualstudio.com carries it in the hostname;
    /// on-premises servers need at least a collection name.
    /// </summary>
    private static int MinimumCollectionSegments(string host) => IsLegacyHost(host) ? 0 : 1;

    private static string ResolveOrganization(string host, string[] collectionSegments)
    {
        if (IsLegacyHost(host))
        {
            return host[..host.IndexOf('.')];
        }

        return collectionSegments.Length > 0 ? collectionSegments[^1] : host;
    }

    private static string StripGitSuffix(string name) =>
        name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static MigrationException Invalid(string input, string reason) =>
        new(ExitCode.ConfigurationError,
            $"Could not read an Azure DevOps project from '{input}' -- {reason}.",
            "Expected something like https://dev.azure.com/organisation/project");
}
