using System.Net.Sockets;
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

    public void Start()
    {
        if (File.Exists(path))
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.Connect(new UnixDomainSocketEndPoint(path));
                throw new InvalidOperationException($"another felkodd is already listening on {path}");
            }
            catch (SocketException)
            {
                File.Delete(path);  // stale socket from a daemon that did not shut down cleanly
            }
        }
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
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
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
