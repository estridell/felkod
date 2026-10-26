using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Felkod.Daemon;

[SupportedOSPlatform("linux")]
static class RpcChecks
{
    public static async Task Run(Config config, Handlers handlers, Action<bool, string> check)
    {
        string path = Path.Combine(config.DataDir, "rpc-check.sock");
        var server = new RpcServer(path, handlers);
        server.Start();
        try
        {
            check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "socket mode 0600");
            new RpcServer(path, handlers).Stop();
            check(File.Exists(path), "stopping an unstarted server preserves the live socket");
            var rejected = new RpcServer(path, handlers);
            try
            {
                rejected.Start();
                throw new Exception("second server should fail");
            }
            catch (InvalidOperationException) { }
            rejected.Stop();
            check(File.Exists(path), "stopping a failed server preserves the live socket");

            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
            using var stream = new NetworkStream(client);
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };
            foreach (string request in new[]
            {
                "null", "[]", "{", "{\"method\":12}", "{\"method\":\"status\",\"params\":[]}",
                "{\"method\":\"status\",\"method\":\"daemon.stop\"}",
                "{\"method\":\"status\",\"params\":{\"confirm\":false,\"confirm\":true}}",
                "{\"id\":7,\"method\":\"bench\",\"params\":{\"job\":\"INFO\",\"n\":1e100}}",
                "{\"id\":7,\"method\":\"job.run\",\"params\":{\"job\":\"INFO\",\"gap_ms\":1e100}}",
            })
            {
                await writer.WriteLineAsync(request);
                string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                var result = JsonNode.Parse(line!)!;
                check(result["ok"]!.GetValue<bool>() == false && result["error"]!["code"]!.GetValue<string>() == "bad_request",
                    $"malformed RPC request returns bad_request: {request}");
                if (request.Contains("\"id\":7")) check(result["id"]!.GetValue<int>() == 7, "RPC error preserves request id");
            }
            await writer.WriteLineAsync("{\"id\":8,\"method\":\"status\"}");
            var status = JsonNode.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!)!;
            check(status["ok"]!.GetValue<bool>() && status["id"]!.GetValue<int>() == 8,
                "RPC connection survives malformed requests");
        }
        finally { server.Stop(); }
        check(!File.Exists(path), "socket removed at stop");

        File.WriteAllText(path, "preserve this file");
        var fileServer = new RpcServer(path, handlers);
        try
        {
            fileServer.Start();
            throw new Exception("regular file must not be replaced by a socket");
        }
        catch (IOException) { }
        finally { fileServer.Stop(); }
        check(File.ReadAllText(path) == "preserve this file", "socket startup preserves regular files");
        File.Delete(path);

        string target = path + ".target";
        File.WriteAllText(target, "preserve target");
        File.CreateSymbolicLink(path, target);
        var linkServer = new RpcServer(path, handlers);
        try
        {
            linkServer.Start();
            throw new Exception("symlink must not be replaced by a socket");
        }
        catch (IOException) { }
        finally { linkServer.Stop(); }
        check(new FileInfo(path).LinkTarget == target && File.ReadAllText(target) == "preserve target",
            "socket startup preserves symlinks and their targets");
    }
}
