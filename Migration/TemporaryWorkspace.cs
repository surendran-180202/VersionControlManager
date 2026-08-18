using VersionControlManager.Logging;

namespace VersionControlManager.Migration;

/// <summary>
/// A scratch folder holding the bare mirror clone, removed when the migration ends.
/// The mirror is a full copy of the source repository, so it is deleted on both the
/// success and failure paths rather than left behind to fill the disk.
/// </summary>
internal sealed class TemporaryWorkspace : IDisposable
{
    private readonly bool _keep;

    private TemporaryWorkspace(string rootPath, string mirrorPath, bool keep)
    {
        RootPath = rootPath;
        MirrorPath = mirrorPath;
        _keep = keep;
    }

    public string RootPath { get; }

    /// <summary>Path the bare mirror is cloned into. Does not exist until git creates it.</summary>
    public string MirrorPath { get; }

    public static TemporaryWorkspace Create(string? parentDirectory, string repositoryName, bool keep)
    {
        string parent = string.IsNullOrWhiteSpace(parentDirectory)
            ? Path.GetTempPath()
            : parentDirectory.Trim();

        string root = Path.Combine(
            parent,
            $"vcm-{Sanitise(repositoryName)}-{DateTime.Now:yyyyMMdd-HHmmss}");

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new MigrationException(
                ExitCode.ConfigurationError,
                $"Could not create a working folder at '{root}': {ex.Message}",
                "Pass --work-dir <path> to choose a writable location.");
        }

        // The trailing .git is the convention for a bare repository.
        return new TemporaryWorkspace(root, Path.Combine(root, $"{Sanitise(repositoryName)}.git"), keep);
    }

    /// <summary>Strips characters that are not valid in a path segment on any host OS.</summary>
    private static string Sanitise(string name)
    {
        string cleaned = new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)]);

        return cleaned.Trim('.', ' ') is { Length: > 0 } result ? result : "repository";
    }

    public void Dispose()
    {
        if (_keep)
        {
            ConsoleLog.Info($"Working copy kept at {RootPath}");
            return;
        }

        TryDelete(RootPath);
    }

    /// <summary>
    /// Deletes the tree, clearing read-only attributes first: git marks files in the object
    /// store read-only, which makes a plain recursive delete fail on Windows.
    /// </summary>
    private static void TryDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                FileAttributes attributes = File.GetAttributes(file);

                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort: the migration itself already succeeded or failed on
            // its own merits, and a locked temp folder should not change that verdict.
            ConsoleLog.Warn($"Could not remove the working folder '{path}': {ex.Message}");
        }
    }
}
