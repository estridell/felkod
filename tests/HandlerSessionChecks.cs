using System.Text.Json.Nodes;
using Felkod.Daemon;
using Microsoft.Data.Sqlite;

static class HandlerSessionChecks
{
    public static async Task Run(Config config, Handlers handlers, Action<bool, string> check)
    {
        async Task Fails(string method, JsonObject parameters, string code, string label)
        {
            try
            {
                await handlers.Call(method, parameters, new());
            }
            catch (FelkodException ex) when (ex.Code == code)
            {
                check(true, label);
                return;
            }
            throw new Exception($"{label}: expected {code}");
        }
        Task BadRequest(JsonObject parameters, string label) => Fails("log.start", parameters, "bad_request", label);

        await BadRequest(new() { ["jobs"] = new JsonArray((JsonNode?)null) }, "null log job refused");
        await BadRequest(new() { ["jobs"] = new JsonArray("INFO"), ["slow_jobs"] = "INFO" }, "scalar slow jobs refused");
        await BadRequest(new() { ["jobs"] = new JsonArray("INFO"), ["slow_jobs"] = new JsonArray(42) }, "numeric slow job refused");
        await Fails("job.run", new() { ["job"] = "TEST_PARL" }, "write_not_allowed", "write job requires allow_write");
        await Fails("log.start", new() { ["jobs"] = new JsonArray("TEST_PARL") }, "write_not_allowed", "logging refuses write job");
        await Fails("bench", new() { ["job"] = "TEST_PARL" }, "write_not_allowed", "benchmark refuses write job");
        await Fails("faults.clear", new(), "confirm_required", "fault clear requires confirmation before bus access");

        // Several daemon sockets may log into the same data directory. All creators must own
        // separate directories, including when they choose their timestamp simultaneously.
        string shared = Path.Combine(config.DataDir, "shared-session-creators");
        using (var start = new Barrier(12))
        {
            var creators = Enumerable.Range(0, 12).Select(i => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                using var session = Session.Create(shared, new JsonObject { ["creator"] = i });
                session.AddEvent(DateTime.UtcNow, 0, "created");
                return session.Dir;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            var directories = await Task.WhenAll(creators);
            check(directories.Distinct().Count() == creators.Length, "concurrent sessions have distinct directories");
            foreach (string directory in directories)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(directory, "session.sqlite"), Mode = SqliteOpenMode.ReadOnly }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "SELECT count(*) FROM events";
                check((long)command.ExecuteScalar()! == 1, "concurrent session survives other creators");
            }
            check(Directory.GetFiles(shared, "*.creating").Length == 0, "session reservations removed");
        }

        // One interrupted or manually damaged metadata file must not hide healthy sessions.
        string goodId;
        using (var good = Session.Create(config.SessionsDir, new JsonObject { ["note"] = "healthy" }))
        {
            goodId = good.Id;
        }
        string broken = Path.Combine(config.SessionsDir, "broken-metadata");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "meta.json"), "{incomplete");
        foreach (string name in new[] { "missing-id", "null-id" })
        {
            string directory = Path.Combine(config.SessionsDir, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "meta.json"), name == "missing-id" ? "{}" : "{\"id\":null}");
        }
        var warnings = new JsonArray();
        var listing = await handlers.Call("sessions.list", new(), warnings);
        check(listing!["sessions"]!.AsArray().Any(session => session!["id"]?.GetValue<string>() == goodId),
            "healthy sessions survive damaged metadata");
        check(warnings.Count == 1, "damaged session metadata reported as warning");
        check(listing["sessions"]!.AsArray().Where(session => session!["id"] == null)
            .All(session => !session!["running"]!.GetValue<bool>()), "metadata without an id is never marked running");

        // TEST_PARL is an upstream synthetic job; it never talks to a car. Concurrent requests
        // still need an intact audit record before each permitted write job executes.
        var writes = Enumerable.Range(0, 24).Select(i => Task.Run(() => handlers.Call("job.run",
            new JsonObject { ["job"] = "TEST_PARL", ["args"] = i.ToString(), ["allow_write"] = true }, new())));
        await Task.WhenAll(writes);
        var audit = File.ReadAllLines(config.AuditPath).Select(line => JsonNode.Parse(line)!).ToList();
        check(audit.Count(row => row["action"]!.GetValue<string>() == "job.run") == 24, "concurrent writes all audited");
        check(audit.Select(row => row["args"]!.GetValue<string>()).Distinct().Count() == 24, "write audit arguments preserved");
    }
}
