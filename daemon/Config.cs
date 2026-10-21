using System.Globalization;

namespace Felkod.Daemon;

sealed record Config(string Port, string EcuPath, string Sgbd, string DataDir, string SocketPath, double JobTimeoutS)
{
    public string SessionsDir => Path.Combine(DataDir, "sessions");
    // Raw telegram traces for work outside logging sessions (sessions trace into their own folder).
    public string TraceDir => Path.Combine(DataDir, "trace");
    public string AuditPath => Path.Combine(DataDir, "audit.jsonl");

    public static string DefaultSocketPath()
    {
        if (Environment.GetEnvironmentVariable("FELKOD_SOCKET") is string socket && socket.Length > 0)
        {
            return socket;
        }
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrEmpty(runtime)
            ? Path.Combine(Path.GetTempPath(), $"felkod-{Environment.UserName}.sock")
            : Path.Combine(runtime, "felkod.sock");
    }

    public static string DefaultDataDir()
    {
        string? dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
        {
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }
        return Path.Combine(dataHome, "felkod");
    }

    // Returns null (after printing usage) on bad arguments.
    public static Config? FromArgs(string[] args)
    {
        var opts = new Dictionary<string, string>
        {
            ["port"] = "/dev/felkod-kline",
            // bin/<config>/net10.0/felkodd -> repo root
            ["ecu-path"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../vendor/bmw/ediabas/ecu")),
            ["sgbd"] = "ms430ds0",
            ["data-dir"] = DefaultDataDir(),
            ["socket"] = DefaultSocketPath(),
            ["job-timeout"] = "15",
        };
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i].StartsWith("--") ? args[i][2..] : "";
            if (!opts.ContainsKey(key) || i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"felkodd: unknown option or missing value: {args[i]}");
                Console.Error.WriteLine("usage: felkodd [--port DEV] [--ecu-path DIR] [--sgbd NAME] [--data-dir DIR] [--socket PATH] [--job-timeout SEC]");
                return null;
            }
            opts[key] = args[++i];
        }
        if (!double.TryParse(opts["job-timeout"], CultureInfo.InvariantCulture, out double timeout)
            || !double.IsFinite(timeout) || timeout <= 0)
        {
            Console.Error.WriteLine("felkodd: --job-timeout must be a finite number > 0");
            return null;
        }
        return new Config(opts["port"], opts["ecu-path"], opts["sgbd"], opts["data-dir"], opts["socket"], timeout);
    }
}
