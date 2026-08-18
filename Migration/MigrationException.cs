namespace VersionControlManager.Migration;

/// <summary>Process exit codes, so the tool can be driven from a script or pipeline.</summary>
internal enum ExitCode
{
    Success = 0,
    ConfigurationError = 1,
    AuthenticationError = 2,
    SourceError = 3,
    TargetError = 4,
    GitError = 5,
    Cancelled = 6,

    /// <summary>Anything we did not anticipate. Deliberately distinct from the codes above,
    /// which all describe a failure the tool understood and explained.</summary>
    UnexpectedError = 99,
}

/// <summary>
/// An expected, explainable failure. These are reported as a single clear line rather than
/// a stack trace -- the user needs to know what to fix, not where we threw.
/// </summary>
internal sealed class MigrationException(ExitCode exitCode, string message, string? hint = null)
    : Exception(message)
{
    #region Properties
    public ExitCode ExitCode { get; } = exitCode;

    /// <summary>Optional follow-up telling the user how to resolve the failure.</summary>
    public string? Hint { get; } = hint;
    #endregion
}
