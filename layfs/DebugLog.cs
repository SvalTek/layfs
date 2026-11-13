namespace layfs;

/// <summary>
/// Minimal trace log for diagnosing FSP callbacks. Enabled by <c>-D file</c> or by any
/// <c>-d</c> debug flags, which default to a file in the temp directory.
/// </summary>
internal static class DebugLog
{
    private static readonly Lock Gate = new();

    private static string? _path;

    public static bool Enabled { get; private set; }

    public static void Configure(string? path)
    {
        _path = path;
        Enabled = path is not null;
    }

    public static void Trace(string message)
    {
        if (!Enabled)
            return;

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,3}] {message}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                if (_path == "-")
                    Console.Error.Write(line);
                else
                    File.AppendAllText(_path!, line);
            }
            catch
            {
                // Tracing must never break the file system.
            }
        }
    }

    public static void Trace(string format, params object?[] args) => Trace(string.Format(format, args));

    public static string Hex(int value) => $"0x{(uint)value:x8}";

    public static string Hex(uint value) => $"0x{value:x8}";
}
