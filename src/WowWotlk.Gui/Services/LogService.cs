namespace WowWotlk.Gui.Services;

/// <summary>
/// In-app log sink feeding the collapsible log pane, teed to
/// ~/.config/wow-wotlk-autoinstall/logs/wow-wotlk-autoinstall.log so failed runs survive an
/// app close. Thread-safe; UI marshalling is the subscriber's job.
/// </summary>
public class LogService
{
    public event Action<string>? LineAdded;

    public LogService()
        : this(Path.Join(Models.AppSettings.AppDataPath, "logs")) { }

    /// <summary>
    /// Sinks to <paramref name="logDirectory"/>, or to memory only when it is null. Anything
    /// that is not the running app — tests above all — must pass null: writing to the real
    /// log interleaves lines into the record a user reads to diagnose a failed install.
    /// </summary>
    internal LogService(string? logDirectory)
    {
        if (logDirectory is null)
        {
            _logFile = null;
            return;
        }
        try
        {
            Directory.CreateDirectory(logDirectory);
            _logFile = Path.Join(logDirectory, "wow-wotlk-autoinstall.log");
        }
        catch
        {
            _logFile = null;
        }
    }

    /// <summary>
    /// The file every line is teed to, or null when this sink is memory-only. Surfaced so the
    /// UI can point a user at the full record: the in-app pane keeps only the last 2000 lines.
    /// </summary>
    public string? LogFilePath => _logFile;

    public void Append(string line)
    {
        // One timestamp for both sinks: reading the clock twice can straddle midnight and
        // stamp the file line with a different day than the one shown in the pane.
        var now = DateTime.Now;
        var stamped = $"[{now:HH:mm:ss}] {line}";
        try
        {
            LineAdded?.Invoke(stamped);
        }
        catch (Exception)
        {
            // Append is called from inside catch blocks all over the app, including ones
            // reporting a failure the user needs to see. A throwing subscriber must not
            // replace that error with its own, or take the process down on the way past.
        }
        if (_logFile is null)
        {
            return;
        }
        lock (_fileLock)
        {
            try
            {
                RollIfOversized();
                File.AppendAllText(_logFile, $"[{now:yyyy-MM-dd}]{stamped}\n");
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }

    /// <summary>
    /// Keeps one previous log alongside the current one, so the file the UI offers to open
    /// stays openable. Without any rotation it only ever grows, and it is pointed at users
    /// precisely when something has gone wrong and they need to read it.
    /// </summary>
    private void RollIfOversized()
    {
        const long maxBytes = 4L * 1024 * 1024;
        if (_logFile is null || !File.Exists(_logFile) || new FileInfo(_logFile).Length < maxBytes)
        {
            return;
        }
        File.Move(_logFile, _logFile + ".1", overwrite: true);
    }

    private readonly string? _logFile;
    private readonly Lock _fileLock = new();
}
