using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Felkod.Daemon;

// Line-delimited JSON over a unix socket. Request: {"id": 1, "method": "faults.read", "params": {...}}.
// Response: {"id": 1, "ok": true, "data": ..., "warnings": [...]} or
//           {"id": 1, "ok": false, "error": {"code": "link", "message": "EDIABAS_IFH_0009"}}.
sealed class RpcServer(string path, Handlers handlers)
{
    readonly CancellationTokenSource cts = new();
    Socket? listener;
    FileStream? ownership;
    bool bound;

    // statx has a fixed layout across Linux architectures. File.Exists also accepts regular
    // files; a refused connect alone is not proof that it is safe to unlink the path.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    struct FileStatus
    {
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    static extern int Statx(int directory, string path, int flags, uint mask, out FileStatus status);

    static bool IsSocket(string candidate)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("stale socket recovery requires Linux");
        }
        // AT_FDCWD, AT_SYMLINK_NOFOLLOW, STATX_TYPE; S_IFMT / S_IFSOCK.
        if (Statx(-100, candidate, 0x100, 1, out var status) != 0)
        {
            throw new IOException($"cannot inspect socket path {candidate} (errno {Marshal.GetLastPInvokeError()})");
        }
        return (status.Mode & 0xF000) == 0xC000;
    }

    public void Start()
    {
        // Hold ownership for the daemon's lifetime. Otherwise two auto-starts can both decide a
        // socket is stale, and one can unlink the other's newly bound, live socket.
        ownership = new FileStream(path + ".lock", new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        try
        {
            ownership.Lock(0, 1);
        }
        catch
        {
            ownership.Dispose();
            ownership = null;
            throw;
        }
        if (new FileInfo(path).LinkTarget != null)
        {
            throw new IOException($"refusing to replace a symlink at {path}");
        }
        if (File.Exists(path))
        {
            if (!IsSocket(path))
            {
                throw new IOException($"refusing to replace a non-socket file at {path}");
            }
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.Connect(new UnixDomainSocketEndPoint(path));
                throw new InvalidOperationException($"another felkodd is already listening on {path}");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                File.Delete(path);  // stale socket from a daemon that did not shut down cleanly
            }
        }
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        bound = true;
        // The socket can issue writes to the car; never inherit a permissive caller umask.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        _ = AcceptLoop();
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener!.AcceptAsync(cts.Token);
            }
            catch (Exception) when (cts.IsCancellationRequested)
            {
                return;
            }
            _ = Serve(client);
        }
    }

    async Task Serve(Socket client)
    {
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);
            using var reader = new StreamReader(stream);
            await using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\n" };
            while (await reader.ReadLineAsync(cts.Token) is string line)
            {
                var response = await Handle(line);
                await writer.WriteLineAsync(response.ToJsonString());
            }
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // client went away
        }
    }

    async Task<JsonObject> Handle(string line)
    {
        JsonNode? id = null;
        try
        {
            var request = JsonNode.Parse(line)?.AsObject() ?? throw new FelkodException("bad_request", "empty request");
            id = request["id"]?.DeepClone();
            string method = request["method"]?.GetValue<string>() ?? throw new FelkodException("bad_request", "missing method");
            var parameters = request["params"]?.AsObject() ?? new JsonObject();
            var warnings = new JsonArray();
            var data = await handlers.Call(method, parameters, warnings);
            return new JsonObject { ["id"] = id, ["ok"] = true, ["data"] = data, ["warnings"] = warnings };
        }
        catch (Exception ex)
        {
            var (code, message) = ex switch
            {
                FelkodException f => (f.Code, f.Message),
                JsonException => ("bad_request", ex.Message),
                InvalidOperationException => ("bad_request", ex.Message),
                FormatException or OverflowException => ("bad_request", ex.Message),
                _ => ("internal", $"{ex.GetType().Name}: {ex.Message}"),
            };
            return new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
        }
    }

    public void Stop()
    {
        cts.Cancel();
        listener?.Dispose();
        try
        {
            if (bound)
            {
                File.Delete(path);
                bound = false;
            }
        }
        catch (IOException)
        {
        }
        // Keep the lock file: deleting it permits two processes to lock different inodes.
        ownership?.Dispose();
    }
}
