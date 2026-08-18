using System.Text.Json;
using VersionControlManager.Logging;
using VersionControlManager.Migration;

namespace VersionControlManager.Configuration;

/// <summary>
/// Assembles <see cref="MigrationOptions"/> from every supported source. Precedence,
/// highest first: command-line arguments, environment variables, appsettings.json,
/// interactive prompt.
/// </summary>
internal static class OptionsBuilder
{
    private const string EnvironmentPrefix = "VCM_";

    public static MigrationOptions Build(string[] args, string settingsFilePath)
    {
        Dictionary<string, string?> cli = ParseArguments(args);
        Dictionary<string, string> file = ReadSettingsFile(settingsFilePath);

        MigrationOptions options = new MigrationOptions
        {
            NonInteractive = cli.ContainsKey("non-interactive"),
            Verbose = cli.ContainsKey("verbose"),
            AllowExistingTarget = cli.ContainsKey("allow-existing"),
            IncludeLfs = cli.ContainsKey("lfs"),
            IncludeNotes = !cli.ContainsKey("no-notes"),
            KeepWorkingCopy = cli.ContainsKey("keep"),
            TargetRepositoryName = Resolve("target-repo", "TARGET_REPO", "targetRepository", cli, file),
            WorkingDirectory = Resolve("work-dir", "WORK_DIR", "workingDirectory", cli, file),
        };

        ConsoleLog.Verbose = options.Verbose;

        options.GitHubUrl = Resolve("github-url", "GITHUB_URL", "gitHubUrl", cli, file) ?? string.Empty;
        options.GitHubUserName = Resolve("github-user", "GITHUB_USER", "gitHubUserName", cli, file) ?? string.Empty;
        options.GitHubPassword = Resolve("github-password", "GITHUB_PASSWORD", "gitHubPassword", cli, file) ?? string.Empty;

        options.AzureDevOpsUrl = Resolve("azure-url", "AZURE_URL", "azureDevOpsUrl", cli, file) ?? string.Empty;
        options.AzureDevOpsUserName = Resolve("azure-user", "AZURE_USER", "azureDevOpsUserName", cli, file) ?? string.Empty;
        options.AzureDevOpsPassword = Resolve("azure-password", "AZURE_PASSWORD", "azureDevOpsPassword", cli, file) ?? string.Empty;

        // Mask anything already known before we prompt for or print anything else.
        RegisterSecrets(options);

        FillGaps(options);
        RegisterSecrets(options);

        return options;
    }

    private static void RegisterSecrets(MigrationOptions options)
    {
        ConsoleLog.RegisterSecret(options.GitHubPassword, options.GitHubUserName);
        ConsoleLog.RegisterSecret(options.AzureDevOpsPassword, options.AzureDevOpsUserName);
    }

    /// <summary>Prompts for whatever is still missing, or fails if running non-interactively.</summary>
    private static void FillGaps(MigrationOptions options)
    {
        List<string> missing = new List<string>();

        void Text(string label, string cliName, Func<string> get, Action<string> set)
        {
            if (get().Length > 0)
            {
                return;
            }

            if (options.NonInteractive)
            {
                missing.Add($"{label} (--{cliName})");
                return;
            }

            string? value = ConsolePrompt.ReadRequired(label);

            if (value is null)
            {
                throw new MigrationException(ExitCode.Cancelled, "Input was cancelled.");
            }

            set(value);
        }

        void Secret(string label, string cliName, Func<string> get, Action<string> set)
        {
            if (get().Length > 0)
            {
                return;
            }

            if (options.NonInteractive)
            {
                missing.Add($"{label} (--{cliName})");
                return;
            }

            string? value = ConsolePrompt.ReadSecret(label);

            if (value is null)
            {
                throw new MigrationException(ExitCode.Cancelled, "Input was cancelled.");
            }

            set(value);
        }

        bool interactive = !options.NonInteractive && NeedsAnyInput(options);

        if (interactive)
        {
            ConsoleLog.Blank();
            ConsoleLog.Info("Enter the source and target details. Passwords are not echoed.");
            ConsoleLog.Blank();
            Console.WriteLine("  -- GitHub (source) --");
        }

        Text("GitHub repository URL", "github-url", () => options.GitHubUrl, v => options.GitHubUrl = v);
        Text("GitHub username", "github-user", () => options.GitHubUserName, v => options.GitHubUserName = v);
        Secret("GitHub password / token", "github-password", () => options.GitHubPassword, v => options.GitHubPassword = v);

        if (interactive)
        {
            Console.WriteLine();
            Console.WriteLine("  -- Azure DevOps (target) --");
        }

        Text("Azure DevOps project URL", "azure-url", () => options.AzureDevOpsUrl, v => options.AzureDevOpsUrl = v);
        Text("Azure DevOps username", "azure-user", () => options.AzureDevOpsUserName, v => options.AzureDevOpsUserName = v);
        Secret("Azure DevOps password / token", "azure-password", () => options.AzureDevOpsPassword, v => options.AzureDevOpsPassword = v);

        if (missing.Count > 0)
        {
            throw new MigrationException(
                ExitCode.ConfigurationError,
                $"Running with --non-interactive but these values were not supplied: {string.Join(", ", missing)}.",
                "Drop --non-interactive to be prompted, or pass the missing arguments.");
        }
    }

    private static bool NeedsAnyInput(MigrationOptions options) =>
        options.GitHubUrl.Length == 0
        || options.GitHubUserName.Length == 0
        || options.GitHubPassword.Length == 0
        || options.AzureDevOpsUrl.Length == 0
        || options.AzureDevOpsUserName.Length == 0
        || options.AzureDevOpsPassword.Length == 0;

    private static string? Resolve(
        string cliName,
        string environmentSuffix,
        string settingsName,
        Dictionary<string, string?> cli,
        Dictionary<string, string> file)
    {
        if (cli.TryGetValue(cliName, out string? fromCli) && !string.IsNullOrWhiteSpace(fromCli))
        {
            return fromCli.Trim();
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentPrefix + environmentSuffix);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        if (file.TryGetValue(settingsName, out string? fromFile) && !string.IsNullOrWhiteSpace(fromFile))
        {
            return fromFile.Trim();
        }

        return null;
    }

    /// <summary>
    /// Reads flags and --name=value / --name value pairs. Unknown names are rejected rather
    /// than ignored, so a typo cannot silently drop a setting.
    /// </summary>
    private static Dictionary<string, string?> ParseArguments(string[] args)
    {
        HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "github-url", "github-user", "github-password",
            "azure-url", "azure-user", "azure-password",
            "target-repo", "work-dir",
            "allow-existing", "lfs", "no-notes", "non-interactive", "verbose", "keep",
        };

        HashSet<string> flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "allow-existing", "lfs", "no-notes", "non-interactive", "verbose", "keep",
        };

        Dictionary<string, string?> result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                throw new MigrationException(
                    ExitCode.ConfigurationError,
                    $"Unexpected argument '{arg}'.",
                    "Run with --help to see the available options.");
            }

            string name = arg[2..];
            string? value = null;

            int equals = name.IndexOf('=');

            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }

            if (!known.Contains(name))
            {
                throw new MigrationException(
                    ExitCode.ConfigurationError,
                    $"Unknown option '--{name}'.",
                    "Run with --help to see the available options.");
            }

            if (flags.Contains(name))
            {
                result[name] = "true";
                continue;
            }

            if (value is null)
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new MigrationException(
                        ExitCode.ConfigurationError,
                        $"Option '--{name}' needs a value.");
                }

                value = args[++i];
            }

            result[name] = value;
        }

        return result;
    }

    /// <summary>
    /// Reads optional non-secret defaults from appsettings.json. A malformed file is a
    /// warning rather than a failure -- the values can still be supplied another way.
    /// </summary>
    private static Dictionary<string, string> ReadSettingsFile(string path)
    {
        Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

            if (!document.RootElement.TryGetProperty("migration", out JsonElement migration)
                || migration.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (JsonProperty property in migration.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            ConsoleLog.Warn($"Ignoring {Path.GetFileName(path)}: {ex.Message}");
        }

        return result;
    }
}
