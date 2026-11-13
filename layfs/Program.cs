using Fsp;

namespace layfs;

internal sealed class LayeredFileSystemService : Service
{
    private const string ProgName = "layfs";

    private FileSystemHost? _host;

    public LayeredFileSystemService()
        : base("LayeredFileSystemService")
    {
    }

    protected override void OnStart(string[] args)
    {
        try
        {
            string? debugLogFile = null;
            uint debugFlags = 0;
            string? volumePrefix = null;
            string? writePath = null;
            string? readOnlyPath = null;
            string? mountPoint = null;

            var i = 1;
            for (; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg is not { Length: > 0 } || arg[0] != '-')
                    break;

                switch (arg[1])
                {
                    case '?':
                        throw new CommandLineUsageException();

                    case 'd':
                        ArgToUInt32(args, ref i, ref debugFlags);
                        break;

                    case 'D':
                        ArgToString(args, ref i, ref debugLogFile);
                        break;

                    case 'm':
                        ArgToString(args, ref i, ref mountPoint);
                        break;

                    case 'r':
                        ArgToString(args, ref i, ref readOnlyPath);
                        break;

                    case 'w':
                        ArgToString(args, ref i, ref writePath);
                        break;

                    case 'u':
                        ArgToString(args, ref i, ref volumePrefix);
                        break;

                    default:
                        throw new CommandLineUsageException();
                }
            }

            if (args.Length > i)
                throw new CommandLineUsageException();

            if (readOnlyPath is null || writePath is null || mountPoint is null)
                throw new CommandLineUsageException();

            if (debugLogFile is not null && FileSystemHost.SetDebugLogFile(debugLogFile) < 0)
                throw new CommandLineUsageException("cannot open debug log file");

            // WinFsp holds its own debug log open exclusively, so our trace gets its own file.
            DebugLog.Configure(debugLogFile switch
            {
                null => debugFlags != 0 ? Path.Combine(Path.GetTempPath(), "layfs-trace.log") : null,
                "-" => "-",
                _ => debugLogFile + ".layfs",
            });
            DebugLog.Trace("startup: -w {0} -r {1} -m {2} -u {3} -d {4} -D {5}", writePath, readOnlyPath, mountPoint, volumePrefix ?? "-", debugFlags, debugLogFile ?? "-");

            var fileSystem = new LayeredFileSystem(writePath, readOnlyPath);
            var host = new FileSystemHost(fileSystem)
            {
                Prefix = volumePrefix,
            };

            var mountStatus = host.Mount(mountPoint, null, true, debugFlags);
            DebugLog.Trace("mount {0} -> {1}", mountPoint, DebugLog.Hex(mountStatus));
            if (mountStatus < 0)
                throw new IOException($"cannot mount file system (status={mountStatus})");

            mountPoint = host.MountPoint();

            _host = host;

            Log(EVENTLOG_INFORMATION_TYPE, string.Format(
                "{0}{1}{2} -w {3} -r {5} -m {4}",
                ProgName,
                volumePrefix is { Length: > 0 } ? " -u " : string.Empty,
                volumePrefix is { Length: > 0 } ? volumePrefix : string.Empty,
                writePath,
                mountPoint,
                readOnlyPath));
        }
        catch (CommandLineUsageException ex)
        {
            Log(EVENTLOG_ERROR_TYPE, string.Format(
                "{0}" +
                "usage: {1} OPTIONS\n" +
                "\n" +
                "options:\n" +
                "    -d DebugFlags       [-1: enable all debug logs]\n" +
                "    -D DebugLogFile     [file path; use - for stderr]\n" +
                "    -u \\Server\\Share    [UNC prefix (single backslash)]\n" +
                "    -w Directory        [write file system]\n" +
                "    -r Directory        [read only file system]\n" +
                "    -m MountPoint       [X:|*|directory]\n",
                ex.HasMessage ? ex.Message + "\n" : string.Empty,
                ProgName));
            throw;
        }
        catch (Exception ex)
        {
            Log(EVENTLOG_ERROR_TYPE, ex.Message);
            throw;
        }
    }

    protected override void OnStop()
    {
        _host?.Unmount();
        _host = null;
    }

    private static void ArgToString(string[] args, ref int i, ref string? value)
    {
        if (args.Length > ++i)
            value = args[i];
        else
            throw new CommandLineUsageException();
    }

    private static void ArgToUInt32(string[] args, ref int i, ref uint value)
    {
        if (args.Length > ++i)
            value = int.TryParse(args[i], out var result) ? (uint)result : value;
        else
            throw new CommandLineUsageException();
    }

    private sealed class CommandLineUsageException(string? message = null) : Exception(message)
    {
        public bool HasMessage { get; } = message is not null;
    }
}

internal static class Program
{
    private static void Main(string[] args) =>
        Environment.ExitCode = new LayeredFileSystemService().Run();
}
