using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Felkod.Daemon;

// The daemon's methods. Everything that talks to the ECU goes through the bus thread;
// metadata comes from the offline catalog. Output is the SGBD's own names, values and units.
sealed class Handlers(Config config, Bus bus, Catalog catalog, Action requestShutdown)
{
    // Below this, clearing faults is refused: a sagging battery mid-write is how ECUs get confused.
    const double MinWriteVoltage = 11.5;

    readonly DateTime startedAt = DateTime.UtcNow;

    public async Task<JsonNode?> Call(string method, JsonObject p, JsonArray warnings) => method switch
    {
        "status" => Status(),
        "ident" => await Ident(),
        "jobs.list" => JobsList(),
        "jobs.describe" => JobsDescribe(Str(p, "job")),
        "job.run" => await JobRun(Str(p, "job"), OptStr(p, "args") ?? "", OptBool(p, "allow_write")),
        "faults.read" => await FaultsRead(),
        "faults.clear" => await FaultsClear(OptBool(p, "confirm")),
        "log.start" => await LogStart(p, warnings),
        "log.stop" => await bus.Submit(bus.StopLog),
        "log.status" => bus.Log?.Status() ?? new JsonObject { ["running"] = false },
        "sessions.list" => SessionsList(),
        "daemon.stop" => Shutdown(),
        _ => throw new FelkodException("unknown_method", $"unknown method: {method}"),
    };

    static string Str(JsonObject p, string key) =>
        OptStr(p, key) ?? throw new FelkodException("bad_request", $"missing parameter: {key}");

    static string? OptStr(JsonObject p, string key) => p[key]?.GetValue<string>();

    static bool OptBool(JsonObject p, string key) => p[key]?.GetValue<bool>() ?? false;

    static string[] StrList(JsonObject p, string key) =>
        p[key] is JsonArray list ? list.Select(item => item!.GetValue<string>()).ToArray() : [];

    JsonObject Status()
    {
        string? target = null;
        try
        {
            target = new FileInfo(config.Port).LinkTarget;
        }
        catch (IOException)
        {
        }
        var log = bus.Log;
        return new JsonObject
        {
            ["daemon"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["started_at"] = startedAt.ToString("o"),
                ["socket"] = config.SocketPath,
                ["data_dir"] = config.DataDir,
            },
            ["port"] = new JsonObject { ["path"] = config.Port, ["present"] = File.Exists(config.Port), ["link_target"] = target },
            ["sgbd"] = new JsonObject { ["name"] = config.Sgbd, ["file"] = catalog.SgbdFile, ["ecu_path"] = config.EcuPath },
            ["link"] = bus.Link.Snapshot(),
            ["logging"] = log?.Status(),
        };
    }

    async Task<JsonNode?> Ident()
    {
        var sets = (JsonArray)(await bus.Submit(() => bus.RunJob("IDENT")))!;
        Results.EnsureOkay(sets);
        return new JsonObject { ["job"] = "IDENT", ["sets"] = Results.ValueSets(sets) };
    }

    JsonObject JobsList()
    {
        var names = catalog.JobNames().OrderBy(name => name, StringComparer.Ordinal).ToList();
        var jobs = new JsonArray();
        foreach (string name in names)
        {
            jobs.Add(new JsonObject { ["name"] = name, ["read_only"] = Results.IsReadOnlyJob(name) });
        }
        return new JsonObject { ["sgbd"] = catalog.SgbdFile, ["count"] = names.Count, ["jobs"] = jobs };
    }

    JsonObject JobsDescribe(string job)
    {
        catalog.RequireJobs([job]);
        JsonArray Sets(string vjob) => new(catalog.Run(vjob, job).Skip(1).Select(set => set?.DeepClone()).ToArray());
        return new JsonObject
        {
            ["job"] = job,
            ["read_only"] = Results.IsReadOnlyJob(job),
            ["comments"] = Sets("_JOBCOMMENTS"),
            ["arguments"] = Sets("_ARGUMENTS"),
            ["results"] = Sets("_RESULTS"),
        };
    }

    async Task<JsonNode?> JobRun(string job, string args, bool allowWrite)
    {
        catalog.RequireJobs([job]);
        if (!allowWrite && !Results.IsReadOnlyJob(job))
        {
            throw new FelkodException("write_not_allowed", $"{job} is not a read-only job; pass allow_write to run it");
        }
        if (!Results.IsReadOnlyJob(job))
        {
            Audit("job.run", new JsonObject { ["job"] = job, ["args"] = args });
        }
        var sets = (JsonArray)(await bus.Submit(() => bus.RunJob(job, args)))!;
        Results.EnsureOkay(sets);
        return new JsonObject { ["job"] = job, ["args"] = args, ["sets"] = Results.ValueSets(sets) };
    }

    async Task<JsonNode?> FaultsRead()
    {
        var sets = (JsonArray)(await bus.Submit(() => bus.RunJob("FS_LESEN")))!;
        Results.EnsureOkay(sets);
        return FaultList(sets);
    }

    // FS_LESEN returns one result set per stored fault, then a final set with only JOB_STATUS.
    static JsonObject FaultList(JsonArray sets)
    {
        var faults = new JsonArray();
        foreach (var set in Results.ValueSets(sets))
        {
            var values = set!["values"]!.AsObject();
            if (values.ContainsKey("F_ORT_NR"))
            {
                values.Remove("JOB_STATUS");
                faults.Add(set.DeepClone());
            }
        }
        return new JsonObject { ["job"] = "FS_LESEN", ["count"] = faults.Count, ["faults"] = faults };
    }

    async Task<JsonNode?> FaultsClear(bool confirm)
    {
        if (!confirm)
        {
            throw new FelkodException("confirm_required", "clearing faults erases the DME's fault memory; pass confirm to proceed");
        }
        return await bus.Submit(() =>
        {
            var ubattSets = bus.RunJob("STATUS_UBATT");
            Results.EnsureOkay(ubattSets);
            double ubatt = ubattSets[1]!["STAT_UBATT_WERT"]!.GetValue<double>();
            if (ubatt < MinWriteVoltage)
            {
                throw new FelkodException("low_voltage", $"battery {ubatt:F2} V is below {MinWriteVoltage} V; charge it first");
            }
            // The cleared faults are kept in the audit log, so clearing never loses them.
            var before = bus.RunJob("FS_LESEN");
            Results.EnsureOkay(before);
            var cleared = bus.RunJob("FS_LOESCHEN");
            Results.EnsureOkay(cleared);
            var faults = FaultList(before);
            Audit("faults.clear", new JsonObject { ["ubatt_v"] = ubatt, ["faults_before"] = faults.DeepClone() });
            return new JsonObject { ["cleared"] = true, ["ubatt_v"] = ubatt, ["faults_before"] = faults };
        });
    }

    async Task<JsonNode?> LogStart(JsonObject p, JsonArray warnings)
    {
        var jobs = StrList(p, "jobs");
        var slowJobs = StrList(p, "slow_jobs");
        int slowEvery = p["slow_every"]?.GetValue<int>() ?? 10;
        double durationS = p["duration_s"]?.GetValue<double>() ?? 0;
        if (jobs.Length == 0)
        {
            throw new FelkodException("bad_request", "jobs is empty");
        }
        catalog.RequireJobs(jobs.Concat(slowJobs));
        var writeJobs = jobs.Concat(slowJobs).Where(job => !Results.IsReadOnlyJob(job)).ToList();
        if (writeJobs.Count > 0)
        {
            throw new FelkodException("write_not_allowed", $"logging only runs read-only jobs: {string.Join(", ", writeJobs)}");
        }
        if (bus.Log is LogRun running)
        {
            throw new FelkodException("log_running", $"session {running.Id} is already logging; stop it first");
        }

        var meta = new JsonObject
        {
            ["started_at"] = DateTime.UtcNow.ToString("o"),
            ["note"] = OptStr(p, "note"),
            ["jobs"] = new JsonArray(jobs.Select(job => (JsonNode)job).ToArray()),
            ["slow_jobs"] = new JsonArray(slowJobs.Select(job => (JsonNode)job).ToArray()),
            ["slow_every"] = slowEvery,
            ["duration_s"] = durationS,
            ["sgbd"] = catalog.SgbdFile,
            ["port"] = config.Port,
        };
        var session = Session.Create(config.SessionsDir, meta);
        var run = new LogRun(session, jobs, slowJobs, slowEvery, durationS);
        try
        {
            await bus.Submit(() =>
            {
                bus.StartLog(run);
                return null;
            });
        }
        catch (Exception)
        {
            session.Dispose();
            Directory.Delete(session.Dir, recursive: true);
            throw;
        }
        if (!File.Exists(config.Port))
        {
            warnings.Add($"port {config.Port} does not exist; logging will wait for it");
        }
        return new JsonObject { ["session_id"] = session.Id, ["dir"] = session.Dir };
    }

    JsonObject SessionsList()
    {
        var sessions = new JsonArray();
        if (Directory.Exists(config.SessionsDir))
        {
            foreach (string dir in Directory.GetDirectories(config.SessionsDir).OrderDescending(StringComparer.Ordinal))
            {
                string metaPath = Path.Combine(dir, "meta.json");
                if (File.Exists(metaPath) && JsonNode.Parse(File.ReadAllText(metaPath)) is JsonObject meta)
                {
                    meta["dir"] = dir;
                    meta["running"] = bus.Log?.Id == meta["id"]?.GetValue<string>();
                    sessions.Add(meta);
                }
            }
        }
        return new JsonObject { ["sessions_dir"] = config.SessionsDir, ["sessions"] = sessions };
    }

    JsonObject Shutdown()
    {
        requestShutdown();
        return new JsonObject { ["stopping"] = true };
    }

    // Every request that changes the car is appended to audit.jsonl before or as it happens.
    void Audit(string action, JsonObject detail)
    {
        detail["t"] = DateTime.UtcNow.ToString("o");
        detail["action"] = action;
        File.AppendAllText(config.AuditPath, detail.ToJsonString() + "\n");
    }
}
