using VersionControlManager.Configuration;
using VersionControlManager.Logging;
using VersionControlManager.Migration;

namespace VersionControlManager;

internal static class Program
{
    #region Private Methods
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
        {
            WriteHelp();
            return (int)ExitCode.Success;
        }

        using CancellationTokenSource cancellation = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            // Handle Ctrl+C ourselves so the temp mirror is cleaned up on the way out.
            e.Cancel = true;
            ConsoleLog.Blank();
            ConsoleLog.Warn("Cancelling...");
            cancellation.Cancel();
        };

        ConsoleLog.Banner(
            "VersionControlManager",
            "Migrates a GitHub repository, with its full check-in history, to Azure DevOps.");

        try
        {
            string settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            MigrationOptions options = OptionsBuilder.Build(args, settingsPath);

            MigrationResult result = await new MigrationRunner(options).RunAsync(cancellation.Token);

            WriteSummary(result);

            return (int)ExitCode.Success;
        }
        catch (MigrationException ex)
        {
            ConsoleLog.Blank();
            ConsoleLog.Error(ex.Message);

            if (!string.IsNullOrWhiteSpace(ex.Hint))
            {
                ConsoleLog.Info($"Hint: {ex.Hint}");
            }

            ConsoleLog.Blank();

            return (int)ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            ConsoleLog.Blank();
            ConsoleLog.Error("Cancelled before the migration finished.");
            ConsoleLog.Info("Nothing was left behind locally. Check the target repository before re-running.");
            ConsoleLog.Blank();

            return (int)ExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            // Unexpected: show the type and message, and the stack trace only when asked,
            // but never the raw exception text unredacted.
            ConsoleLog.Blank();
            ConsoleLog.Error($"Unexpected {ex.GetType().Name}: {ex.Message}");

            if (ConsoleLog.Verbose && ex.StackTrace is not null)
            {
                ConsoleLog.Detail(ex.StackTrace);
            }
            else
            {
                ConsoleLog.Info("Re-run with --verbose for more detail.");
            }

            ConsoleLog.Blank();

            return (int)ExitCode.UnexpectedError;
        }
    }

    private static void WriteSummary(MigrationResult result)
    {
        ConsoleLog.Blank();
        ConsoleLog.Success("Migration complete.");
        ConsoleLog.Blank();
        ConsoleLog.Info($"Source:    {result.SourceDescription}");
        ConsoleLog.Info($"Target:    {result.TargetDescription}{(result.CreatedTargetRepository ? " (created)" : "")}");
        ConsoleLog.Info($"Commits:   {result.CommitCount:N0}");
        ConsoleLog.Info($"Branches:  {result.BranchCount}");
        ConsoleLog.Info($"Tags:      {result.TagCount}");

        if (result.NoteCount > 0)
        {
            ConsoleLog.Info($"Notes:     {result.NoteCount}");
        }

        if (result.DefaultBranch is not null)
        {
            ConsoleLog.Info($"Default:   {result.DefaultBranch}");
        }

        ConsoleLog.Blank();
        ConsoleLog.Info($"Open it at {result.TargetUrl}");
        ConsoleLog.Blank();
    }

    private static void WriteHelp()
    {
        Console.WriteLine("""

            VersionControlManager
              Copies a GitHub repository, including its full check-in history, into Azure DevOps.

            USAGE
              VersionControlManager [options]

              Run with no options to be prompted for everything. Passwords are never echoed.

            CONNECTION OPTIONS
              --github-url <url>         GitHub repository, e.g. https://github.com/owner/repo
              --github-user <name>       GitHub username
              --github-password <token>  GitHub personal access token ('repo' scope)

              --azure-url <url>          Azure DevOps project, e.g. https://dev.azure.com/org/project
              --azure-user <name>        Azure DevOps username
              --azure-password <token>   Azure DevOps PAT (Code: read, write, and manage)

            MIGRATION OPTIONS
              --target-repo <name>       Target repository name (default: the GitHub repo name)
              --allow-existing           Push even if the target repository already has commits
              --lfs                      Also transfer Git LFS objects (needs git-lfs installed)
              --no-notes                 Skip refs/notes/*
              --work-dir <path>          Where to put the temporary clone (default: system temp)
              --keep                     Keep the temporary clone afterwards

            BEHAVIOUR OPTIONS
              --non-interactive          Fail instead of prompting for a missing value
              --verbose                  Show the git commands being run
              --help                     Show this help

            ENVIRONMENT VARIABLES
              Each connection option can be supplied as an environment variable instead, which
              keeps tokens out of your shell history:

                VCM_GITHUB_URL   VCM_GITHUB_USER   VCM_GITHUB_PASSWORD
                VCM_AZURE_URL    VCM_AZURE_USER    VCM_AZURE_PASSWORD

              Precedence: command line, then environment, then appsettings.json, then prompt.

            NOTE ON PASSWORDS
              GitHub and Azure DevOps both stopped accepting account passwords for Git and API
              access. The value you supply must be a personal access token.

            EXIT CODES
              0 success   1 configuration   2 authentication   3 source
              4 target     5 git             6 cancelled     99 unexpected

            """);
    }
    #endregion
}
