namespace QwenProxy.Logging;

/// <summary>
/// Port of logger.py: colored console output + file logging.
/// Log files live under &lt;baseDir&gt;/logs/ (baseDir defaults to the app content root).
/// </summary>
public sealed class QwenLogger
{
    private static readonly object WriteLock = new();

    public string LogFile { get; }

    public QwenLogger(string? baseDir = null)
    {
        var basePath = baseDir ?? AppContext.BaseDirectory;
        LogFile = Path.Combine(basePath, "logs", "agent.log");
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private void Write(string level, string message)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                File.AppendAllText(LogFile, $"[{Timestamp()}] [{level}] {message}\n");
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[LOG ERROR] Could not write to log file: {e.Message}");
        }
    }

    public void Info(string message)
    {
        Console.WriteLine($"\u001b[38;5;75m[*]\u001b[0m [{Timestamp()}] {message}");
        Write("INFO", message);
    }

    public void Success(string message)
    {
        Console.WriteLine($"\u001b[38;5;78m[+]\u001b[0m [{Timestamp()}] {message}");
        Write("SUCCESS", message);
    }

    public void Warn(string message)
    {
        Console.WriteLine($"\u001b[38;5;214m[!]\u001b[0m [{Timestamp()}] {message}");
        Write("WARNING", message);
    }

    public void Error(string message)
    {
        Console.WriteLine($"\u001b[38;5;203m[x]\u001b[0m [{Timestamp()}] {message}");
        Write("ERROR", message);
    }

    public void Debug(string message)
    {
        Console.WriteLine($"\u001b[38;5;245m[D]\u001b[0m [{Timestamp()}] {message}");
        Write("DEBUG", message);
    }
}
