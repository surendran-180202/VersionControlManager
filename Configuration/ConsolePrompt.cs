using System.Text;

namespace VersionControlManager.Configuration;

/// <summary>Interactive console input, with secrets never echoed to the screen.</summary>
internal static class ConsolePrompt
{
    /// <summary>Prompts until a non-empty value is given. Returns null if the user cancels.</summary>
    public static string? ReadRequired(string label)
    {
        while (true)
        {
            Console.Write($"  {label}: ");
            string? value = Console.ReadLine();

            if (value is null)
            {
                return null;   // stdin closed
            }

            value = value.Trim();

            if (value.Length > 0)
            {
                return value;
            }

            Console.WriteLine("  A value is required.");
        }
    }

    /// <summary>
    /// Prompts for a secret, echoing nothing. Returns null if the user cancels.
    /// Falls back to a plain read when stdin is not a console (piped or redirected input).
    /// </summary>
    public static string? ReadSecret(string label)
    {
        if (Console.IsInputRedirected)
        {
            return ReadRequired(label);
        }

        while (true)
        {
            Console.Write($"  {label}: ");
            StringBuilder builder = new StringBuilder();

            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    break;
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (builder.Length > 0)
                    {
                        builder.Length--;
                    }

                    continue;
                }

                if (key.Key == ConsoleKey.Escape || (key.Modifiers == ConsoleModifiers.Control && key.Key == ConsoleKey.C))
                {
                    Console.WriteLine();
                    return null;
                }

                // Ignore control keys (arrows, function keys) that carry no character.
                if (!char.IsControl(key.KeyChar))
                {
                    builder.Append(key.KeyChar);
                }
            }

            if (builder.Length > 0)
            {
                return builder.ToString();
            }

            Console.WriteLine("  A value is required.");
        }
    }

    public static bool Confirm(string question)
    {
        Console.Write($"  {question} [y/N]: ");
        string? answer = Console.ReadLine()?.Trim();

        return answer is not null
            && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
