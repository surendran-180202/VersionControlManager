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
	private const string MASK = "***";

	/// <summary>Shortest string we are willing to treat as a secret. Redacting a
	/// two-character token would blank out unrelated text and hide real errors.</summary>
	private const int MINIMUM_SECRET_LENGTH = 6;
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
	public static void RegisterSecret(string? strSecret, string? strUserName = null)
	{
		if(string.IsNullOrEmpty(strSecret) || strSecret.Length < MINIMUM_SECRET_LENGTH)
		{
			return;
		}

		Add(strSecret);
		Add(Convert.ToBase64String(Encoding.UTF8.GetBytes($":{strSecret}")));
		Add(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{strUserName ?? string.Empty}:{strSecret}")));
	}

	public static string Redact(string? strText)
	{
		if(string.IsNullOrEmpty(strText))
		{
			return string.Empty;
		}

		// Longest first, so a secret containing another secret is fully masked.
		foreach(string strSecret in Secrets.OrderByDescending(s => s.Length))
		{
			strText = strText.Replace(strSecret, MASK, StringComparison.Ordinal);
		}

		return strText;
	}

	public static void Banner(string strTitle, string strSubtitle)
	{
		Console.WriteLine();
		Write(ConsoleColor.Cyan, strTitle);
		Write(ConsoleColor.DarkGray, strSubtitle);
		Console.WriteLine();
	}

	public static void Step(int nNumber, int nTotal, string strMessage)
	{
		Console.WriteLine();
		Write(ConsoleColor.Cyan, $"[{nNumber}/{nTotal}] {Redact(strMessage)}");
	}

	public static void Info(string strMessage)
	{
		Console.WriteLine($"        {Redact(strMessage)}");
	}

	public static void Detail(string strMessage)
	{
		if(Verbose)
		{
			Write(ConsoleColor.DarkGray, $"        {Redact(strMessage)}");
		}
	}

	/// <summary>Live output relayed from a child git process.</summary>
	public static void Relay(string strMessage)
	{
		Write(ConsoleColor.DarkGray, $"      | {Redact(strMessage)}");
	}

	public static void Success(string strMessage)
	{
		Write(ConsoleColor.Green, $"        {Redact(strMessage)}");
	}

	public static void Warn(string strMessage)
	{
		Write(ConsoleColor.Yellow, $"  warn  {Redact(strMessage)}");
	}

	public static void Error(string strMessage)
	{
		Write(ConsoleColor.Red, $"  ERROR {Redact(strMessage)}", bToError: true);
	}

	public static void Blank()
	{
		Console.WriteLine();
	}
	#endregion

	#region Privates
	private static void Add(string strValue)
	{
		if(!Secrets.Contains(strValue, StringComparer.Ordinal))
		{
			Secrets.Add(strValue);
		}
	}

	private static void Write(ConsoleColor colour, string strMessage, bool bToError = false)
	{
		TextWriter writer = bToError ? Console.Error : Console.Out;

		if(!UseColour)
		{
			writer.WriteLine(strMessage);
			return;
		}

		ConsoleColor previous = Console.ForegroundColor;
		Console.ForegroundColor = colour;
		writer.WriteLine(strMessage);
		Console.ForegroundColor = previous;
	}
	#endregion
}
