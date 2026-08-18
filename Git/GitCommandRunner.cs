using System.Diagnostics;
using System.Text;
using VersionControlManager.Logging;
using VersionControlManager.Migration;

namespace VersionControlManager.Git;

internal sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
	#region Properties
	public bool Success => this.ExitCode == 0;

	/// <summary>Whichever stream carries the failure text; git uses both inconsistently.</summary>
	public string FailureText =>
		this.StandardError.Trim().Length > 0 ? this.StandardError.Trim() : this.StandardOutput.Trim();
	#endregion
}

/// <summary>
/// Runs git as a child process.
///
/// Credentials are handed to git through GIT_CONFIG_KEY_n / GIT_CONFIG_VALUE_n
/// environment variables (git 2.31+) rather than being written to disk or passed on the
/// command line. This matters:
///   - a token in the remote URL is persisted into .git/config,
///   - a token in "git -c http.extraHeader=..." is visible to any process listing.
/// The environment of a child process is only readable by the same user, which is the
/// tightest option available to us without a credential manager.
/// </summary>
internal sealed class GitCommandRunner
{
	#region Constants
	private const string ExecutableName = "git";
	#endregion

	#region Publics
	/// <summary>Verifies git is on PATH and returns its version string.</summary>
	public async Task<string> GetVersionAsync(CancellationToken cancellationToken)
	{
		try
		{
			GitResult result = await this.RunAsync(["--version"], cancellationToken: cancellationToken);

			if(!result.Success)
			{
				throw new MigrationException(ExitCode.GitError, $"'git --version' failed: {result.FailureText}");
			}

			return result.StandardOutput.Trim();
		}
		catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or FileNotFoundException)
		{
			throw new MigrationException(
				ExitCode.GitError,
				"git was not found on PATH.",
				"Install Git (https://git-scm.com/downloads) and reopen the terminal.");
		}
	}

	/// <summary>True if the named executable subcommand responds, e.g. "lfs".</summary>
	public async Task<bool> IsSubcommandAvailableAsync(string subcommand, CancellationToken cancellationToken)
	{
		try
		{
			GitResult result = await this.RunAsync([subcommand, "version"], cancellationToken: cancellationToken);
			return result.Success;
		}
		catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or FileNotFoundException)
		{
			return false;
		}
	}

	/// <param name="arguments">Passed individually, so no shell quoting is involved.</param>
	/// <param name="workingDirectory">Directory to run in, or null for the current one.</param>
	/// <param name="authorizationHeader">A full HTTP Authorization header value, or null.</param>
	/// <param name="relayProgress">Echo git's stderr to the console as it arrives.</param>
	public async Task<GitResult> RunAsync(
		IReadOnlyList<string> arguments,
		string? workingDirectory = null,
		string? authorizationHeader = null,
		bool relayProgress = false,
		CancellationToken cancellationToken = default)
	{
		ProcessStartInfo startInfo = new()
		{
			FileName = ExecutableName,
			WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			RedirectStandardInput = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};

		foreach(string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		ApplyEnvironment(startInfo, authorizationHeader);

		ConsoleLog.Detail($"git {string.Join(' ', arguments)}");

		using Process process = new() { StartInfo = startInfo };

		StringBuilder standardOutput = new();
		StringBuilder standardError = new();

		// Git writes progress to stderr, terminated with CR. The line-based reader used by
		// these events treats CR as a line break, so progress arrives as it is produced.
		process.OutputDataReceived += (_, e) =>
		{
			if(e.Data is null)
			{
				return;
			}

			standardOutput.AppendLine(e.Data);

			if(relayProgress && e.Data.Trim().Length > 0)
			{
				ConsoleLog.Relay(e.Data.Trim());
			}
		};

		process.ErrorDataReceived += (_, e) =>
		{
			if(e.Data is null)
			{
				return;
			}

			standardError.AppendLine(e.Data);

			if(relayProgress && e.Data.Trim().Length > 0)
			{
				ConsoleLog.Relay(e.Data.Trim());
			}
		};

		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		// Close stdin so git can never block waiting on input we are not going to send.
		process.StandardInput.Close();

		try
		{
			await process.WaitForExitAsync(cancellationToken);
		}
		catch(OperationCanceledException)
		{
			TryKill(process);
			throw;
		}

		// Lets the async readers drain everything buffered before we read the builders.
		process.WaitForExit();

		return new GitResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
	}
	#endregion

	#region Privates
	private static void ApplyEnvironment(ProcessStartInfo startInfo, string? authorizationHeader)
	{
		// Never let git block on an interactive credential prompt: this is a batch tool and
		// a hidden prompt looks identical to a hang.
		startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
		startInfo.Environment["GCM_INTERACTIVE"] = "never";
		startInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";

		// Stable, parseable output regardless of the user's locale.
		startInfo.Environment["LC_ALL"] = "C";

		List<(string Key, string Value)> settings =
		[
			// Empty value resets the helper list, so Git Credential Manager cannot pop up a
			// dialog or substitute a cached identity for the one we were given.
			("credential.helper", string.Empty),
			("core.askpass", string.Empty),
		];

		if(!string.IsNullOrEmpty(authorizationHeader))
		{
			settings.Add(("http.extraheader", $"AUTHORIZATION: {authorizationHeader}"));
		}

		startInfo.Environment["GIT_CONFIG_COUNT"] = settings.Count.ToString();

		for(int i = 0; i < settings.Count; i++)
		{
			startInfo.Environment[$"GIT_CONFIG_KEY_{i}"] = settings[i].Key;
			startInfo.Environment[$"GIT_CONFIG_VALUE_{i}"] = settings[i].Value;
		}
	}

	private static void TryKill(Process process)
	{
		try
		{
			if(!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch(Exception ex) when(ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
		{
			// The process is already gone, which is the outcome we wanted.
		}
	}
	#endregion
}
