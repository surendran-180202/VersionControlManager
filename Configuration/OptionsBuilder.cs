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
	#region Constants
	private const string ENVIRONMENT_PREFIX = "VCM_";
	#endregion

	#region Publics
	public static MigrationOptions Build(string[] liArgs, string strSettingsFilePath)
	{
		Dictionary<string, string?> cli = ParseArguments(liArgs);
		Dictionary<string, string> file = ReadSettingsFile(strSettingsFilePath);

		MigrationOptions migrationOptions = new()
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

		ConsoleLog.Verbose = migrationOptions.Verbose;

		migrationOptions.GitHubUrl = Resolve("github-url", "GITHUB_URL", "gitHubUrl", cli, file) ?? string.Empty;
		migrationOptions.GitHubUserName = Resolve("github-user", "GITHUB_USER", "gitHubUserName", cli, file) ?? string.Empty;
		migrationOptions.GitHubPassword = Resolve("github-password", "GITHUB_PASSWORD", "gitHubPassword", cli, file) ?? string.Empty;

		migrationOptions.AzureDevOpsUrl = Resolve("azure-url", "AZURE_URL", "azureDevOpsUrl", cli, file) ?? string.Empty;
		migrationOptions.AzureDevOpsUserName = Resolve("azure-user", "AZURE_USER", "azureDevOpsUserName", cli, file) ?? string.Empty;
		migrationOptions.AzureDevOpsPassword = Resolve("azure-password", "AZURE_PASSWORD", "azureDevOpsPassword", cli, file) ?? string.Empty;

		// Mask anything already known before we prompt for or print anything else.
		RegisterSecrets(migrationOptions);

		FillGaps(migrationOptions);
		RegisterSecrets(migrationOptions);

		return migrationOptions;
	}
	#endregion

	#region Privates
	private static void RegisterSecrets(MigrationOptions migrationOptions)
	{
		ConsoleLog.RegisterSecret(migrationOptions.GitHubPassword, migrationOptions.GitHubUserName);
		ConsoleLog.RegisterSecret(migrationOptions.AzureDevOpsPassword, migrationOptions.AzureDevOpsUserName);
	}

	/// <summary>Prompts for whatever is still missing, or fails if running non-interactively.</summary>
	private static void FillGaps(MigrationOptions migrationOptions)
	{
		List<string> liMissing = [];

		void Text(string strLabel, string strCliName, Func<string> get, Action<string> set)
		{
			if(get().Length > 0) return;

			if(migrationOptions.NonInteractive)
			{
				liMissing.Add($"{strLabel} (--{strCliName})");
				return;
			}

			string? strValue = ConsolePrompt.ReadRequired(strLabel) ?? throw new MigrationException(ExitCode.Cancelled, "Input was cancelled.");
			set(strValue);
		}

		void Secret(string strLabel, string strCliName, Func<string> get, Action<string> set)
		{
			if(get().Length > 0)
			{
				return;
			}

			if(migrationOptions.NonInteractive)
			{
				liMissing.Add($"{strLabel} (--{strCliName})");
				return;
			}

			string? strValue = ConsolePrompt.ReadSecret(strLabel) ?? throw new MigrationException(ExitCode.Cancelled, "Input was cancelled.");
			set(strValue);
		}

		bool bInteractive = !migrationOptions.NonInteractive && NeedsAnyInput(migrationOptions);

		if(bInteractive)
		{
			ConsoleLog.Blank();
			ConsoleLog.Info("Enter the source and target details. Passwords are not echoed.");
			ConsoleLog.Blank();
			Console.WriteLine("  -- GitHub (source) --");
		}

		Text("GitHub repository URL", "github-url", () => migrationOptions.GitHubUrl, strNewValue => migrationOptions.GitHubUrl = strNewValue);
		Text("GitHub username", "github-user", () => migrationOptions.GitHubUserName, strNewValue => migrationOptions.GitHubUserName = strNewValue);
		Secret("GitHub password / token", "github-password", () => migrationOptions.GitHubPassword, strNewValue => migrationOptions.GitHubPassword = strNewValue);

		if(bInteractive)
		{
			Console.WriteLine();
			Console.WriteLine("  -- Azure DevOps (target) --");
		}

		Text("Azure DevOps project URL", "azure-url", () => migrationOptions.AzureDevOpsUrl, strNewValue => migrationOptions.AzureDevOpsUrl = strNewValue);
		Text("Azure DevOps username", "azure-user", () => migrationOptions.AzureDevOpsUserName, strNewValue => migrationOptions.AzureDevOpsUserName = strNewValue);
		Secret("Azure DevOps password / token", "azure-password", () => migrationOptions.AzureDevOpsPassword, strNewValue => migrationOptions.AzureDevOpsPassword = strNewValue);

		if(liMissing.Count > 0)
		{
			throw new MigrationException(
				ExitCode.ConfigurationError,
				$"Running with --non-interactive but these values were not supplied: {string.Join(", ", liMissing)}.",
				"Drop --non-interactive to be prompted, or pass the missing arguments.");
		}
	}

	private static bool NeedsAnyInput(MigrationOptions migrationOptions)
	{
		return migrationOptions.GitHubUrl.Length == 0
		|| migrationOptions.GitHubUserName.Length == 0
		|| migrationOptions.GitHubPassword.Length == 0
		|| migrationOptions.AzureDevOpsUrl.Length == 0
		|| migrationOptions.AzureDevOpsUserName.Length == 0
		|| migrationOptions.AzureDevOpsPassword.Length == 0;
	}

	private static string? Resolve(
		string strCliName,
		string strEnvironmentSuffix,
		string strSettingsName,
		Dictionary<string, string?> cli,
		Dictionary<string, string> file)
	{
		if(cli.TryGetValue(strCliName, out string? strFromCli) && !string.IsNullOrWhiteSpace(strFromCli))
		{
			return strFromCli.Trim();
		}

		string? strFromEnvironment = Environment.GetEnvironmentVariable(ENVIRONMENT_PREFIX + strEnvironmentSuffix);

		if(!string.IsNullOrWhiteSpace(strFromEnvironment))
		{
			return strFromEnvironment.Trim();
		}

		if(file.TryGetValue(strSettingsName, out string? strFromFile) && !string.IsNullOrWhiteSpace(strFromFile))
		{
			return strFromFile.Trim();
		}

		return null;
	}

	/// <summary>
	/// Reads flags and --name=value / --name value pairs. Unknown names are rejected rather
	/// than ignored, so a typo cannot silently drop a setting.
	/// </summary>
	private static Dictionary<string, string?> ParseArguments(string[] liArgs)
	{
		HashSet<string> known = new(StringComparer.OrdinalIgnoreCase)
		{
			"github-url", "github-user", "github-password",
			"azure-url", "azure-user", "azure-password",
			"target-repo", "work-dir",
			"allow-existing", "lfs", "no-notes", "non-interactive", "verbose", "keep",
		};

		HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase)
		{
			"allow-existing", "lfs", "no-notes", "non-interactive", "verbose", "keep",
		};

		Dictionary<string, string?> result = new(StringComparer.OrdinalIgnoreCase);

		for(int nI = 0; nI < liArgs.Length; nI++)
		{
			string strArg = liArgs[nI];

			if(!strArg.StartsWith("--", StringComparison.Ordinal))
			{
				throw new MigrationException(
					ExitCode.ConfigurationError,
					$"Unexpected argument '{strArg}'.",
					"Run with --help to see the available options.");
			}

			string strName = strArg[2..];
			string? strValue = null;

			int nEquals = strName.IndexOf('=');

			if(nEquals >= 0)
			{
				strValue = strName[(nEquals + 1)..];
				strName = strName[..nEquals];
			}

			if(!known.Contains(strName))
			{
				throw new MigrationException(
					ExitCode.ConfigurationError,
					$"Unknown option '--{strName}'.",
					"Run with --help to see the available options.");
			}

			if(flags.Contains(strName))
			{
				result[strName] = "true";
				continue;
			}

			if(strValue is null)
			{
				if(nI + 1 >= liArgs.Length || liArgs[nI + 1].StartsWith("--", StringComparison.Ordinal))
				{
					throw new MigrationException(
						ExitCode.ConfigurationError,
						$"Option '--{strName}' needs a value.");
				}

				strValue = liArgs[++nI];
			}

			result[strName] = strValue;
		}

		return result;
	}

	/// <summary>
	/// Reads optional non-secret defaults from appsettings.json. A malformed file is a
	/// warning rather than a failure -- the values can still be supplied another way.
	/// </summary>
	private static Dictionary<string, string> ReadSettingsFile(string strPath)
	{
		Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);

		if(!File.Exists(strPath))
		{
			return result;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(
				File.ReadAllText(strPath),
				new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });

			if(!document.RootElement.TryGetProperty("migration", out JsonElement migration)
				|| migration.ValueKind != JsonValueKind.Object)
			{
				return result;
			}

			foreach(JsonProperty property in migration.EnumerateObject())
			{
				if(property.Value.ValueKind == JsonValueKind.String) result[property.Name] = property.Value.GetString() ?? string.Empty;
			}
		}
		catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException)
		{
			ConsoleLog.Warn($"Ignoring {Path.GetFileName(strPath)}: {ex.Message}");
		}

		return result;
	}
	#endregion
}
