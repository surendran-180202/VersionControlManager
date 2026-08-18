using System.Text;

namespace VersionControlManager.Configuration;

/// <summary>Interactive console input, with secrets never echoed to the screen.</summary>
internal static class ConsolePrompt
{
	#region Publics
	/// <summary>Prompts until a non-empty value is given. Returns null if the user cancels.</summary>
	public static string? ReadRequired(string strLabel)
	{
		while(true)
		{
			Console.Write($"  {strLabel}: ");
			string? strValue = Console.ReadLine();

			if(strValue is null) return null;   // stdin closed

			strValue = strValue.Trim();

			if(strValue.Length > 0) return strValue;

			Console.WriteLine("  A value is required.");
		}
	}

	/// <summary>
	/// Prompts for a secret, echoing nothing. Returns null if the user cancels.
	/// Falls back to a plain read when stdin is not a console (piped or redirected input).
	/// </summary>
	public static string? ReadSecret(string strLabel)
	{
		if(Console.IsInputRedirected) return ReadRequired(strLabel);

		while(true)
		{
			Console.Write($"  {strLabel}: ");
			StringBuilder builder = new();

			while(true)
			{
				ConsoleKeyInfo key = Console.ReadKey(intercept: true);

				if(key.Key == ConsoleKey.Enter)
				{
					Console.WriteLine();
					break;
				}

				if(key.Key == ConsoleKey.Backspace)
				{
					if(builder.Length > 0)
					{
						builder.Length--;
					}

					continue;
				}

				if(key.Key == ConsoleKey.Escape || (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.C))
				{
					Console.WriteLine();
					return null;
				}

				// Ignore control keys (arrows, function keys) that carry no character.
				if(!char.IsControl(key.KeyChar))
				{
					builder.Append(key.KeyChar);
				}
			}

			if(builder.Length > 0) return builder.ToString();

			Console.WriteLine("  A value is required.");
		}
	}

	public static bool Confirm(string strQuestion)
	{
		Console.Write($"  {strQuestion} [y/N]: ");
		string? strAnswer = Console.ReadLine()?.Trim();

		return strAnswer is not null && (strAnswer.Equals("y", StringComparison.OrdinalIgnoreCase) || strAnswer.Equals("yes", StringComparison.OrdinalIgnoreCase));
	}
	#endregion
}
