// Hardware-free checks against upstream EdiabasLib's own test SGBDs. No BMW files or cable required.
// Run: dotnet run --project tests -c Release
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
    // Read-only rule.
    foreach (string job in new[] { "_JOBS", "_table", "STATUS_TEST", "LESEN_TEST", "FS_LESEN", "IDENT", "IDENT_AIF", "INFO", "ECU_CONFIG" })
        Check(JobRules.IsReadOnlyJob(job), $"{job} read-only");
    foreach (string job in new[] { "_RESET", "STEUERN_TEST", "FS_LOESCHEN", "TEST_PARL", "IDENTX" })
        Check(!JobRules.IsReadOnlyJob(job), $"{job} not read-only");

    // DS2 status byte only for DS1/DS2; KWP2000* (byte 2 = ECU address, e.g. 0x29 for DSC MK60) and
    // other concepts rely on the SGBD's JOB_STATUS. No parameters yet counts as DS2 (stricter).
    Check(ReplyGuard.HasDs2Status(ReplyGuard.ConceptOf([0x0006, 9600])), "DS2 has a status byte");
    Check(ReplyGuard.HasDs2Status(ReplyGuard.ConceptOf([0x0005])), "DS1 has a status byte");
    Check(ReplyGuard.ConceptOf(null) == 0x0006 && ReplyGuard.ConceptOf([]) == 0x0006, "unknown concept counts as DS2");
    foreach (uint concept in new uint[] { 0x010C, 0x010D, 0x010F, 0x0110, 0x0001 })
        Check(!ReplyGuard.HasDs2Status(concept), $"concept {concept:X4} has no DS2 status byte");

    // KWP2000* frames sent over the DS2 concept (E46 DSC MK60, from a car trace): byte 2 is the address.
    byte[] kwpIdent = Convert.FromHexString("B829F1021A80F8");
    Check(ReplyGuard.IsKwp2000StarExchange(kwpIdent,
        Convert.FromHexString("B8F1291F5A80000006759045040400060006200204032400000000005D0000000000005F")),
        "MK60 IDENT is a KWP2000* exchange");
    Check(ReplyGuard.IsKwp2000StarExchange(Convert.FromHexString("B829F1041802FFFF7E"),
        Convert.FromHexString("B8F1291158055E19A05E14A05E1FA05E16205DF5A0A0")), "MK60 FS_LESEN is a KWP2000* exchange");
    Check(!ReplyGuard.IsKwp2000StarExchange(kwpIdent, Convert.FromHexString("B8F12A1F5A80")),
        "a reply from another address is not");
    // DS2, including addresses >= 0x80 (KOMBI46): the status byte still counts.
    Check(!ReplyGuard.IsKwp2000StarExchange(Convert.FromHexString("80040084"), Convert.FromHexString("8018A0FFFF")),
        "KOMBI46 DS2 is not KWP2000*");
    Check(!ReplyGuard.IsKwp2000StarExchange(Convert.FromHexString("12050B031F"), Convert.FromHexString("1205A2B5")),
        "a rejected DS2 reply is not KWP2000*");
    Check(JobRules.IsMetadataJob("_tables") && !JobRules.IsMetadataJob("_RESET"), "metadata jobs");

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
    using var bus = new Bus(config, catalog);
    bus.Start();
    var handlers = new Handlers(config, bus, catalog, () => { });
    async Task<JsonNode?> Call(string method, JsonObject? p = null) => await handlers.Call(method, p ?? new(), new());

    // KWP2000* replies are checked against their own length byte, so a fault read can shrink to
    // "no faults" after a clear (car: 22 bytes with 5 faults, then B8 F1 29 02 58 00 3A).
    {
        var kwpGuard = new ReplyGuard(catalog);
        byte[] fsRequest = Convert.FromHexString("B829F1041802FFFF7E");
        byte[] fiveFaults = Convert.FromHexString("B8F1291158055E19A05E14A05E1FA05E16205DF5A0A0");
        kwpGuard.Check(fsRequest, fiveFaults);
        kwpGuard.Learn(fsRequest, fiveFaults);
        kwpGuard.Check(fsRequest, Convert.FromHexString("B8F1290258003A"));
        checks++;
        await Fails(() => { kwpGuard.Check(fsRequest, fiveFaults[..^2]); return Task.CompletedTask; },
            "ecu_reply_length", "truncated KWP2000* reply");
        kwpGuard.Check(fsRequest, Convert.FromHexString("B8F1290258003A"), 0x010D);
        checks++;
        // ISO KWP2000 (0x010C) can put the length in the format byte (0x85: 5 data bytes), where byte 3
        // is data: such a reply must not be judged by byte 3.
        kwpGuard.Check(Convert.FromHexString("8229F11A80B6"), Convert.FromHexString("85F1295A80010203E0"), 0x010C);
        checks++;
    }

    // The bus creates its EdiabasLib instance after the offline catalog, which binds EdiabasLib's
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
        Check(catalog.Decode("TEST_PARL", (2000 + i).ToString(), [], [])![1]!["PARL"]!.GetValue<long>() == 2000 + i
            && BoundTo(Instance(catalog)), "offline decode on catalog registers");
    }

    // Requests with args come from RPC callers: only args-free ones are cached.
    var requests = (System.Collections.IDictionary)typeof(Catalog)
        .GetField("requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
    requests.Clear();
    for (int i = 0; i < 50; i++)
        catalog.RequestFor("TEST_PARL", i.ToString());
    catalog.MarkNotNative("TEST_PARL", "1");
    Check(requests.Count == 0, "requests with args not cached");
    catalog.RequestFor("INFO");
    catalog.RequestFor("INFO");
    catalog.MarkNotNative("TEST_PARL");
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

    // faults.clear's battery voltage comes from STATUS_UBATT, which cmd_test1 lacks.
    Check(Handlers.VoltageFrom("cmd_test1", catalog, own: false) == null, "no voltage source without STATUS_UBATT");
    var dme = new Catalog(config with { Sgbd = "dm338ds1_interbyte" });
    Check(Handlers.VoltageFrom("dm338ds1_interbyte", dme, own: true) is { Sgbd: "dm338ds1_interbyte", Own: true },
        "voltage source from STATUS_UBATT");

    await HandlerSessionChecks.Run(config, handlers, Check);
    await FaultsClearChecks.Run(config, Check);
    TransportChecks.Run(config, catalog, Check);
    await RpcChecks.Run(config, handlers, Check);

    Console.WriteLine($"Passed {checks} daemon checks");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(temp, recursive: true);
}
