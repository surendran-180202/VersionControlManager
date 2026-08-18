using VersionControlManager.Logging;

namespace VersionControlManager.Migration;

/// <summary>
/// A scratch folder holding the bare mirror clone, removed when the migration ends.
/// The mirror is a full copy of the source repository, so it is deleted on both the
/// success and failure paths rather than left behind to fill the disk.
/// </summary>
internal sealed class TemporaryWorkspace : IDisposable
{
	#region Fields
	private readonly bool _keep;
	#endregion

	#region Constructors
	private TemporaryWorkspace(string strRootPath, string strMirrorPath, bool bKeep)
	{
		this.RootPath = strRootPath;
		this.MirrorPath = strMirrorPath;
		this._keep = bKeep;
	}
	#endregion

	#region Properties
	public string RootPath { get; }

	/// <summary>Path the bare mirror is cloned into. Does not exist until git creates it.</summary>
	public string MirrorPath { get; }
	#endregion

	#region Publics
	public static TemporaryWorkspace Create(string? strParentDirectory, string strRepositoryName, bool bKeep)
	{
		string strParent = string.IsNullOrWhiteSpace(strParentDirectory)
			? Path.GetTempPath()
			: strParentDirectory.Trim();

		string strRoot = Path.Combine(
			strParent,
			$"vcm-{Sanitise(strRepositoryName)}-{DateTime.Now:yyyyMMdd-HHmmss}");

		try
		{
			Directory.CreateDirectory(strRoot);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or NotSupportedException)
		{
			throw new MigrationException(
				ExitCode.ConfigurationError,
				$"Could not create a working folder at '{strRoot}': {ex.Message}",
				"Pass --work-dir <path> to choose a writable location.");
		}

		// The trailing .git is the convention for a bare repository.
		return new TemporaryWorkspace(strRoot, Path.Combine(strRoot, $"{Sanitise(strRepositoryName)}.git"), bKeep);
	}

	public void Dispose()
	{
		if(this._keep)
		{
			ConsoleLog.Info($"Working copy kept at {this.RootPath}");
			return;
		}

		TryDelete(this.RootPath);
	}
	#endregion

	#region Privates
	/// <summary>Strips characters that are not valid in a path segment on any host OS.</summary>
	private static string Sanitise(string strName)
	{
		string strCleaned = new([.. strName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)]);

		return strCleaned.Trim('.', ' ') is { Length: > 0 } strResult ? strResult : "repository";
	}

	/// <summary>
	/// Deletes the tree, clearing read-only attributes first: git marks files in the object
	/// store read-only, which makes a plain recursive delete fail on Windows.
	/// </summary>
	private static void TryDelete(string strPath)
	{
		if(!Directory.Exists(strPath))
		{
			return;
		}

		try
		{
			foreach(string strFile in Directory.EnumerateFiles(strPath, "*", SearchOption.AllDirectories))
			{
				FileAttributes attributes = File.GetAttributes(strFile);

				if(attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(strFile, attributes & ~FileAttributes.ReadOnly);
			}

			Directory.Delete(strPath, recursive: true);
		}
		catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
		{
			// Cleanup is best-effort: the migration itself already succeeded or failed on
			// its own merits, and a locked temp folder should not change that verdict.
			ConsoleLog.Warn($"Could not remove the working folder '{strPath}': {ex.Message}");
		}
	}
	#endregion
}
