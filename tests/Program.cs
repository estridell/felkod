// Hardware-free checks against upstream EdiabasLib's own test SGBDs. No BMW files or cable required.
// Run: dotnet run --project tests -c Release
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json.Nodes;
using EdiabasLib;
using Felkod.Daemon;
using Microsoft.Data.Sqlite;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("tests require Linux");
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
string temp = Path.Combine(Path.GetTempPath(), "felkod-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}
async Task Fails(Func<Task> action, string code, string label)
{
    try
    {
        await action();
    }
    catch (FelkodException ex) when (ex.Code == code)
    {
        checks++;
        return;
    }
    throw new Exception($"{label}: expected {code}");
}

try
{
    // Read-only rule: one definition, shared with the spike.
    foreach (string job in new[] { "_JOBS", "_table", "STATUS_TEST", "LESEN_TEST", "FS_LESEN", "IDENT", "IDENT_AIF", "INFO", "ECU_CONFIG" })
        Check(JobRules.IsReadOnlyJob(job), $"{job} read-only");
    foreach (string job in new[] { "_RESET", "STEUERN_TEST", "FS_LOESCHEN", "TEST_PARL", "IDENTX" })
        Check(!JobRules.IsReadOnlyJob(job), $"{job} not read-only");
    Check(JobRules.IsMetadataJob("_tables") && !JobRules.IsMetadataJob("_RESET"), "metadata jobs");
    Check(File.ReadAllText(Path.Combine(repo, "spike/Spike.csproj")).Contains("Include=\"../daemon/JobRules.cs\""), "spike links JobRules.cs");
    Check(!File.ReadAllText(Path.Combine(repo, "spike/Program.cs")).Contains("bool IsReadOnlyJob("), "no spike copy");

    foreach (string invalid in new[] { "0", "-1", "NaN", "Infinity", "abc" })
        Check(Config.FromArgs(["--job-timeout", invalid]) == null, $"job timeout {invalid} refused");
    Check(Config.FromArgs(["--job-timeout", "1.5"])?.JobTimeoutS == 1.5, "job timeout 1.5");

    // A ';' in the data directory must not become a connection-string option.
    using (var session = Session.Create(Path.Combine(temp, "data;Mode=ReadOnly", "sessions"), new JsonObject { ["note"] = "x" }))
    {
        session.AddEvent(DateTime.UtcNow, 0, "paused");
        Check(File.Exists(Path.Combine(session.Dir, "session.sqlite")), "session database at its path");
    }

    var config = new Config("/nonexistent/felkod-test-port", Path.Combine(repo, "vendor/ediabaslib/EdiabasLib/Test/Ecu"),
        "cmd_test1", temp, Path.Combine(temp, "daemon.sock"), 1);
    var catalog = new Catalog(config);
    var decoder = new SgbdDecoder(config);
    using var bus = new Bus(config, decoder, catalog);
    bus.Start();
    var handlers = new Handlers(config, bus, catalog, decoder, () => { });
    async Task<JsonNode?> Call(string method, JsonObject? p = null) => await handlers.Call(method, p ?? new(), new());

    // The bus creates its EdiabasLib instance after the catalog and decoder, which binds EdiabasLib's
    // shared registers to it; every job must run on the registers of its own instance.
    var registers = ((Array)typeof(EdiabasNet).GetField("RegisterList", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
        .Cast<object>().ToList();
    var registerOwner = registers[0].GetType().GetField("_ediabas", BindingFlags.NonPublic | BindingFlags.Instance)!;
    EdiabasNet Instance(object owner) =>
        (EdiabasNet)owner.GetType().GetField("ediabas", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    bool BoundTo(EdiabasNet instance) => registers.All(register => registerOwner.GetValue(register) == instance);
    for (int i = 0; i < 3; i++)
    {
        var info = await Call("job.run", new JsonObject { ["job"] = "INFO" });
        Check(info!["sets"]![0]!["values"]!["ECU"]!.GetValue<string>() == "CMD_TEST1" && BoundTo(Instance(bus)), "bus job on its registers");
        Check(catalog.Run("TEST_PARL", (1000 + i).ToString())[1]!["PARL"]!.GetValue<long>() == 1000 + i && BoundTo(Instance(catalog)),
            "catalog job on its registers");
        Check(decoder.Decode("TEST_PARL", (2000 + i).ToString(), [], [])![1]!["PARL"]!.GetValue<long>() == 2000 + i
            && BoundTo(Instance(decoder)), "decoder job on its registers");
    }

    // Requests with args come from RPC callers: only args-free ones are cached.
    var requests = (System.Collections.IDictionary)typeof(SgbdDecoder)
        .GetField("requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decoder)!;
    requests.Clear();
    for (int i = 0; i < 50; i++)
        decoder.RequestFor("TEST_PARL", i.ToString());
    decoder.MarkNotNative("TEST_PARL", "1");
    Check(requests.Count == 0, "requests with args not cached");
    decoder.RequestFor("INFO");
    decoder.RequestFor("INFO");
    decoder.MarkNotNative("TEST_PARL");
    Check(requests.Count == 2 && requests.Contains("INFO") && requests["TEST_PARL"] == null, "args-free requests cached");

    await Fails(() => Call("job.run", new() { ["job"] = "TEST_PARL", ["allow_write"] = true, ["native"] = true }),
        "write_not_allowed", "native write job");
    await Fails(() => Call("log.start", new() { ["jobs"] = new JsonArray("INFO"), ["slow_every"] = 0 }), "bad_request", "slow_every 0");
    await Fails(() => Call("log.start", new() { ["jobs"] = new JsonArray("INFO"), ["duration_s"] = JsonNode.Parse("1e400") }),
        "bad_request", "infinite duration");

    // Two starts at once: one log, one session directory.
    async Task<string> StartLog()
    {
        try
        {
            await Call("log.start", new() { ["jobs"] = new JsonArray("INFO") });
            return "started";
        }
        catch (FelkodException ex)
        {
            return ex.Code;
        }
    }
    var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(StartLog)));
    Check(starts.Count(result => result == "started") == 1 && starts.Count(result => result == "log_running") == 3, "concurrent log starts");
    Check(Directory.GetDirectories(config.SessionsDir).Length == 1, "one session directory");
    Check((await Call("log.stop"))!["ended_at"] != null && bus.Log == null, "log stopped");

    // Work that starts a log is not a pause of that log, however long it takes. The first item makes
    // the bus pick up the second while busy, which is when it times work as a possible pause.
    var ahead = bus.Submit(() => { Thread.Sleep(100); return null; });
    var slowStart = bus.Submit(() =>
    {
        Thread.Sleep(400);
        var session = Session.Create(config.SessionsDir, new JsonObject());
        bus.StartLog(new LogRun(session, ["INFO"], [], 1, 0, null));
        return session.Dir;
    });
    await ahead;
    string slowDir = (string)(await slowStart)!;
    await Call("log.stop");
    using (var db = new SqliteConnection($"Data Source={Path.Combine(slowDir, "session.sqlite")}"))
    {
        db.Open();
        using var count = db.CreateCommand();
        count.CommandText = "SELECT count(*) FROM events WHERE kind = 'paused'";
        Check((long)count.ExecuteScalar()! == 0, "log start not recorded as a pause");
    }

    await HandlerSessionChecks.Run(config, handlers, Check);
    TransportChecks.Run(config, catalog, decoder, Check);
    await RpcChecks.Run(config, handlers, Check);

    // The socket: owner only, and a second server neither starts nor removes the first one's socket.
    var server = new RpcServer(config.SocketPath, handlers);
    server.Start();
    try
    {
        Check(File.GetUnixFileMode(config.SocketPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "socket mode 0600");
        var second = new RpcServer(config.SocketPath, handlers);
        try
        {
            second.Start();
            Check(false, "second server started");
        }
        catch (InvalidOperationException)
        {
        }
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        client.Connect(new UnixDomainSocketEndPoint(config.SocketPath));
        Check(client.Connected, "first server still reachable");
    }
    finally
    {
        server.Stop();
    }
    Check(!File.Exists(config.SocketPath), "socket removed at stop");

    Console.WriteLine($"Passed {checks} daemon checks");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(temp, recursive: true);
}
