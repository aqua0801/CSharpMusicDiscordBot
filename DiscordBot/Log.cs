using System.Runtime.CompilerServices;

namespace DiscordBot;

/// <summary>
/// Minimal console logger: timestamp + level + source; exceptions always print the full stack trace.
/// Uses a lock so concurrent writes from multiple threads don't interleave colors/lines.
/// </summary>
public static class Log
{
    private static readonly Lock _lock = new();

    public static void Info(string message, [CallerFilePath] string file = "")
        => Write("INFO", ConsoleColor.Gray, message, null, file);

    public static void Warn(string message, Exception? ex = null, [CallerFilePath] string file = "")
        => Write("WARN", ConsoleColor.Yellow, message, ex, file);

    public static void Error(string message, Exception? ex = null, [CallerFilePath] string file = "")
        => Write("ERROR", ConsoleColor.Red, message, ex, file);

    private static void Write(string level, ConsoleColor color, string message, Exception? ex, string file)
    {
        string source = Path.GetFileNameWithoutExtension(file);
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] [{source}] {message}";

        lock (_lock)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(line);
            if (ex is not null)
                Console.WriteLine(ex);
            Console.ForegroundColor = old;
        }
    }
}
