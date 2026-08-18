using System.Text;

namespace VersionControlManager.Logging;

/// <summary>
/// Console output for the migration. Every message is passed through <see cref="Redact"/>
/// so a registered secret can never reach the screen, a log file, or a CI transcript --
/// including secrets that appear inside git's own stdout/stderr.
/// </summary>
internal static class ConsoleLog
{
	#region Constants
	private const string Mask = "***";

	/// <summary>Shortest string we are willing to treat as a secret. Redacting a
	/// two-character token would blank out unrelated text and hide real errors.</summary>
	private const int MinimumSecretLength = 6;
	#endregion

	#region Fields
	private static readonly List<string> Secrets = [];
	private static readonly bool UseColour = !Console.IsOutputRedirected;
	#endregion

	#region Properties
	public static bool Verbose { get; set; }
	#endregion

	#region Publics
	/// <summary>
	/// Registers a value to be masked from all future output. Also registers the base64
	/// Basic-auth encodings we build from it, because those appear in git trace output.
	/// </summary>
	public static void RegisterSecret(string? secret, string? userName = null)
	{
		if(string.IsNullOrEmpty(secret) || secret.Length < MinimumSecretLength)
		{
			return;
		}

		Add(secret);
		Add(Convert.ToBase64String(Encoding.UTF8.GetBytes($":{secret}")));
		Add(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName ?? string.Empty}:{secret}")));
	}

	public static string Redact(string? text)
	{
		if(string.IsNullOrEmpty(text))
		{
			return string.Empty;
		}

		// Longest first, so a secret containing another secret is fully masked.
		foreach(string secret in Secrets.OrderByDescending(s => s.Length))
		{
			text = text.Replace(secret, Mask, StringComparison.Ordinal);
		}

		return text;
	}

	public static void Banner(string title, string subtitle)
	{
		Console.WriteLine();
		Write(ConsoleColor.Cyan, title);
		Write(ConsoleColor.DarkGray, subtitle);
		Console.WriteLine();
	}

	public static void Step(int number, int total, string message)
	{
		Console.WriteLine();
		Write(ConsoleColor.Cyan, $"[{number}/{total}] {Redact(message)}");
	}

	public static void Info(string message)
	{
		Console.WriteLine($"        {Redact(message)}");
	}

	public static void Detail(string message)
	{
		if(Verbose)
		{
			Write(ConsoleColor.DarkGray, $"        {Redact(message)}");
		}
	}

	/// <summary>Live output relayed from a child git process.</summary>
	public static void Relay(string message)
	{
		Write(ConsoleColor.DarkGray, $"      | {Redact(message)}");
	}

	public static void Success(string message)
	{
		Write(ConsoleColor.Green, $"        {Redact(message)}");
	}

	public static void Warn(string message)
	{
		Write(ConsoleColor.Yellow, $"  warn  {Redact(message)}");
	}

	public static void Error(string message)
	{
		Write(ConsoleColor.Red, $"  ERROR {Redact(message)}", toError: true);
	}

	public static void Blank()
	{
		Console.WriteLine();
	}
	#endregion

	#region Privates
	private static void Add(string value)
	{
		if(!Secrets.Contains(value, StringComparer.Ordinal))
		{
			Secrets.Add(value);
		}
	}

	private static void Write(ConsoleColor colour, string message, bool toError = false)
	{
		TextWriter writer = toError ? Console.Error : Console.Out;

		if(!UseColour)
		{
			writer.WriteLine(message);
			return;
		}

		ConsoleColor previous = Console.ForegroundColor;
		Console.ForegroundColor = colour;
		writer.WriteLine(message);
		Console.ForegroundColor = previous;
	}
	#endregion
}
