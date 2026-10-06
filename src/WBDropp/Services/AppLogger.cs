namespace WBDropp.Services;

public static class AppLogger
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WBDropp", "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "wb-dropp.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    public static string CurrentLogPath => LogPath;

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never interrupt photo processing.
        }
    }
}
