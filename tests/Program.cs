// Hardware-free checks against upstream's own test SGBD. No BMW files or cable required.
using System.Text.Json.Nodes;
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
void Error(Action action, string code)
{
    try { action(); }
    catch (FelkodException ex) when (ex.Code == code) { checks++; return; }
    throw new Exception($"expected {code}");
}

try
{
    foreach (string invalid in new[] { "0", "-1", "NaN", "Infinity", "abc" })
        Check(Config.FromArgs(["--job-timeout", invalid]) == null, $"timeout {invalid}");
    Check(Config.FromArgs(["--job-timeout", "1.5"])?.JobTimeoutS == 1.5, "valid timeout");
    Check(Results.IsOkStatus("OKAY"), "OK status");
    Check(Results.IsOkStatus("FUNCTION BEENDET; DATEN GUELTIG"), "valid status");
    Check(!Results.IsOkStatus("FUNCTION BEENDET; DATEN UNGUELTIG"), "invalid status");
    Check(Results.IsReadOnlyJob("_TABLE"), "known metadata allowed");
    Check(!Results.IsReadOnlyJob("_RESET"), "unknown underscore job not implicitly safe");
    Error(() => Results.EnsureOkay(new JsonArray(new JsonObject())), "no_results");
    Error(() => Results.EnsureOkay(new JsonArray(new JsonObject(), new JsonObject { ["JOB_STATUS"] = "ERROR" })), "ecu_rejected");
    Check(Ds2Port.FullTelegram([0x12, 4, 0]).SequenceEqual(new byte[] { 0x12, 4, 0, 0x16 }), "checksum added");

    // Semicolons in XDG_DATA_HOME must not become connection-string options.
    string sessions = Path.Combine(temp, "data;with=punctuation", "sessions");
    var sets = new JsonArray(new JsonObject(), new JsonObject
    {
        ["STAT_TEST_WERT"] = 12.5, ["STAT_TEST_EINH"] = "V", ["LABEL"] = "123", ["JOB_STATUS"] = "OKAY",
    });
    string dbPath;
    using (var session = Session.Create(sessions, new JsonObject { ["note"] = "123" }))
    {
        dbPath = Path.Combine(session.Dir, "session.sqlite");
        session.AddSample(DateTime.UtcNow, 0.1, 0, "STATUS_TEST", 10, sets,
            new Ds2Exchange([0x12, 4, 0, 0x16], [0x12, 4, 0xA0, 0xB6], 1, 2, 3, 4));
        session.AddEvent(DateTime.UtcNow, 0.2, "paused", detail: "test");
    }
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM series WHERE name = 'STAT_TEST_WERT' AND unit = 'V'";
        Check((double)cmd.ExecuteScalar()! == 12.5, "numeric reading and unit persisted");
        cmd.CommandText = "SELECT value FROM meta WHERE key = 'note'";
        Check((string)cmd.ExecuteScalar()! == "\"123\"", "string metadata preserved");
        cmd.CommandText = "SELECT COUNT(*) FROM raw";
        Check((long)cmd.ExecuteScalar()! == 1, "raw exchange persisted");
    }

    var config = new Config("/nonexistent/felkod-test-port", Path.Combine(repo, "vendor/ediabaslib/EdiabasLib/Test/Ecu"),
        "cmd_test1", temp, Path.Combine(temp, "daemon.sock"), 1);
    var catalog = new Catalog(config);
    var decoder = new SgbdDecoder(config);
    using var bus = new Bus(config, decoder, catalog);
    bus.Start();
    var handlers = new Handlers(config, bus, catalog, decoder, () => { });
    async Task<JsonNode?> Call(string method, JsonObject? p = null) => await handlers.Call(method, p ?? new(), new());
    async Task CallError(string method, JsonObject p, string code)
    {
        try { await Call(method, p); }
        catch (FelkodException ex) when (ex.Code == code) { checks++; return; }
        throw new Exception($"expected {code} for {method}");
    }

    // Creating the bus's instance rebinds upstream's static registers; older catalog/decoder
    // instances must still parse arguments and execute instructions using their own state.
    for (int i = 0; i < 10; i++)
    {
        var info = await Call("job.run", new JsonObject { ["job"] = "INFO" });
        Check(info!["sets"]![0]!["values"]!["ECU"]!.GetValue<string>() == "CMD_TEST1", "bus INFO");
        var parsed = catalog.Run("TEST_PARL", (1234 + i).ToString());
        Check(parsed[1]!["PARL"]!.GetValue<long>() == 1234 + i, "catalog register ownership");
        var decoded = decoder.Decode("TEST_PARL", (5678 + i).ToString(), [], []);
        Check(decoded![1]!["PARL"]!.GetValue<long>() == 5678 + i, "decoder register ownership");
    }
    Check((await Call("job.run", new JsonObject { ["job"] = "_JOBS" }))!["sets"]!.AsArray().Count > 0, "offline metadata");
    await CallError("job.run", new() { ["job"] = "TEST_PARL" }, "write_not_allowed");
    await CallError("job.run", new() { ["job"] = "TEST_PARL", ["allow_write"] = true, ["native"] = true }, "write_not_allowed");
    await CallError("log.start", new() { ["jobs"] = new JsonArray("INFO"), ["slow_every"] = 0 }, "bad_request");
    await CallError("log.start", new() { ["jobs"] = new JsonArray("INFO"), ["duration_s"] = double.NaN }, "bad_request");
    await CallError("ds2.raw", new() { ["request"] = "00", ["gap_ms"] = -1 }, "bad_request");

    var guard = new ReplyGuard(catalog);
    guard.Check([0x12, 4, 0, 0x16], [0x12, 4, 0xA0, 0xB6]);
    guard.Learn([0x12, 4, 0, 0x16], [0x12, 4, 0xA0, 0xB6]);
    Error(() => guard.Check([0x12, 4, 0, 0x16], [0x12, 5, 0xA0, 0, 0xB7]), "ecu_reply_length");
    Error(() => guard.Check([0x12, 4, 0, 0x16], [0x12, 4, 0xA2, 0xB4]), "ecu_rejected");

    async Task<string> StartLog()
    {
        try { await Call("log.start", new() { ["jobs"] = new JsonArray("INFO") }); return "started"; }
        catch (FelkodException ex) { return ex.Code; }
    }
    var starts = await Task.WhenAll(Task.Run(StartLog), Task.Run(StartLog));
    Check(starts.Count(result => result == "started") == 1 && starts.Contains("log_running"), "concurrent log starts");
    var stopped = await Call("log.stop");
    Check(stopped!["ended_at"] != null && bus.Log == null, "log stopped and finalized");
    Check(Directory.GetDirectories(config.SessionsDir).Length == 1, "no orphan session on concurrent start");

    var server = new RpcServer(config.SocketPath, handlers);
    try
    {
        server.Start();
        Check(File.GetUnixFileMode(config.SocketPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "private socket");
    }
    finally { server.Stop(); }
    Check(!File.Exists(config.SocketPath), "socket removed at stop");

    Console.WriteLine($"Passed {checks} daemon checks");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(temp, recursive: true);
}
