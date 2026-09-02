using System.Collections.Concurrent;
using System.Text;

namespace QwenProxy.Logging;

/// <summary>
/// Port of logger.py: colored console output + file logging.
/// Log files live under &lt;baseDir&gt;/logs/ (baseDir defaults to the app content root).
///
/// Concurrency design: file writes go through a bounded in-memory queue that is
/// drained by a dedicated background thread holding a single open StreamWriter.
/// This replaces the previous per-line File.AppendAllText open/close, which
/// became a throughput bottleneck when many SSE streams log concurrently.
/// Producers never block on logging: if the queue is full the line is dropped
/// and a counter is reported on stderr.
/// </summary>
public sealed class QwenLogger : IDisposable
{
    /// <summary>
    /// Process-wide shared instance, published by DI at startup (Program.cs) so
    /// static helpers (SseStreamParser, AnswerParser, QwenFileUpload) write to
    /// the same logs/agent.log as controllers. Null until the host is built.
    /// </summary>
    public static QwenLogger? Shared { get; set; }

    private const int QueueCapacity = 20_000;

    private readonly BlockingCollection<string> _queue = new(boundedCapacity: QueueCapacity);
    private readonly Thread _writerThread;
    private long _droppedLines;

    public string LogFile { get; }

    public QwenLogger(string? baseDir = null)
    {
        var basePath = baseDir ?? AppContext.BaseDirectory;
        LogFile = Path.Combine(basePath, "logs", "agent.log");

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "QwenLogger-Writer",
        };
        _writerThread.Start();
    }

    private void WriterLoop()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            // UTF8Encoding(false): no BOM, matching Python open(..., encoding="utf-8").
            using var writer = new StreamWriter(LogFile, append: true, new UTF8Encoding(false));
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                writer.WriteLine(line);
                writer.Flush();
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[LOG ERROR] Log writer thread stopped: {e.Message}");
        }
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private void Write(string level, string message)
    {
        string line = $"[{Timestamp()}] [{level}] {message}";
        if (!_queue.TryAdd(line))
        {
            long dropped = Interlocked.Increment(ref _droppedLines);
            Console.Error.WriteLine($"[LOG ERROR] Log queue full; line dropped (total dropped: {dropped})");
        }
    }

    /// <summary>
    /// Stops accepting new lines and waits briefly for the writer thread to
    /// drain the queue. Invoked automatically by DI when the host shuts down,
    /// so buffered log lines survive a graceful shutdown.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _queue.CompleteAdding();
            _writerThread.Join(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // shutdown path: best effort only
        }
        finally
        {
            _queue.Dispose();
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
