using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClipThat.Engine.Diagnostics;

/// <summary>
/// Thread-safe, non-throwing diagnostic file logger writing engine startup, GPU adapter selection,
/// Media Foundation encoder negotiation, audio initialization, and fatal errors to
/// <c>%LocalAppData%\ClipThat\engine.log</c>.
/// </summary>
public static class EngineLogger
{
    private const long MaxLogBytes = 5L * 1024L * 1024L;
    private static readonly object WriteLock = new();

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClipThat");

    public static string LogFilePath => Path.Combine(LogDirectory, "engine.log");

    public static void Info(string component, string message)
        => WriteEntry("INFO", component, message, exception: null);

    public static void Warn(string component, string message, Exception? exception = null)
        => WriteEntry("WARN", component, message, exception);

    public static void Error(string component, string message, Exception? exception = null)
        => WriteEntry("ERROR", component, message, exception);

    private static void WriteEntry(string level, string component, string message, Exception? exception)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var builder = new StringBuilder(256);
        builder.Append('[').Append(timestamp).Append("] [").Append(level).Append("] [")
            .Append(component).Append("] ").AppendLine(message);

        if (exception is not null)
        {
            builder.AppendLine(exception.ToString());
        }

        string formatted = builder.ToString();
        Debug.Write(formatted);

        lock (WriteLock)
        {
            try
            {
                string logDir = LogDirectory;
                Directory.CreateDirectory(logDir);

                string logPath = LogFilePath;
                RotateIfOversizedLocked(logPath);
                File.AppendAllText(logPath, formatted, System.Text.Encoding.UTF8);
            }
            catch
            {
                // Logging must never crash or interfere with capture/encoding/saving.
            }
        }
    }

    private static void RotateIfOversizedLocked(string logPath)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return;
            }

            var info = new FileInfo(logPath);
            if (info.Length < MaxLogBytes)
            {
                return;
            }

            string archivePath = logPath + ".old";
            File.Move(logPath, archivePath, overwrite: true);
        }
        catch
        {
            // Ignore rotation errors and keep appending if possible.
        }
    }
}
