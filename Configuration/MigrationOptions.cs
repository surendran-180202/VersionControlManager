namespace VersionControlManager.Configuration;

/// <summary>
/// Fully resolved settings for one migration. Populated from command-line arguments,
/// environment variables, appsettings.json, and interactive prompts -- in that order.
/// </summary>
internal sealed class MigrationOptions
{
    #region Properties

    public string GitHubUrl { get; set; } = string.Empty;

    public string GitHubUserName { get; set; } = string.Empty;

    /// <summary>GitHub personal access token. GitHub no longer accepts account passwords.</summary>
    public string GitHubPassword { get; set; } = string.Empty;

    public string AzureDevOpsUrl { get; set; } = string.Empty;

    public string AzureDevOpsUserName { get; set; } = string.Empty;

    /// <summary>Azure DevOps personal access token with Code (read, write, manage) scope.</summary>
    public string AzureDevOpsPassword { get; set; } = string.Empty;

    /// <summary>Target repository name. Defaults to the name of the GitHub repository.</summary>
    public string? TargetRepositoryName { get; set; }

    /// <summary>Permits pushing into an Azure DevOps repository that already has commits.</summary>
    public bool AllowExistingTarget { get; set; }

    /// <summary>Copy refs/notes/* in addition to branches and tags.</summary>
    public bool IncludeNotes { get; set; } = true;

    /// <summary>Also transfer Git LFS objects (requires git-lfs on PATH).</summary>
    public bool IncludeLfs { get; set; }

    /// <summary>Fail instead of prompting when a value is missing.</summary>
    public bool NonInteractive { get; set; }

    public bool Verbose { get; set; }

    /// <summary>Where the temporary mirror clone is created. Defaults to the system temp folder.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Leave the mirror clone on disk after a successful migration.</summary>
    public bool KeepWorkingCopy { get; set; }

    #endregion
}
