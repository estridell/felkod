using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Felkod.Daemon;

// The daemon's methods. Everything that talks to the ECU goes through the bus thread;
// metadata comes from the offline catalog. Output is the SGBD's own names, values and units.
sealed class Handlers(Config config, Bus bus, Catalog catalog, SgbdDecoder decoder, Action requestShutdown)
{
    // BMW's regeneration time for MS43 DS2; the native default until a shorter gap is measured safe.
    const int DefaultGapMs = 100;
    const int BenchMaxSeconds = 60;
    HashSet<string>? readOnlyTelegrams;

    // Below this, clearing faults is refused: a sagging battery mid-write is how ECUs get confused.
    const double MinWriteVoltage = 11.5;

    readonly DateTime startedAt = DateTime.UtcNow;

    public Task<JsonNode?> Call(string method, JsonObject p, JsonArray warnings)
    {
        // names the bus work this call queues, for paused events in a running log
        Bus.RequestLabel.Value = method;
        return Dispatch(method, p, warnings);
    }

    // Builds the ds2.raw allowlist (the telegram of every read-only SGBD job) at startup: it takes
    // about 2 s of SGBD work, which must not happen in the middle of a log.
    public void WarmUp() => ReadOnlyTelegrams();

    HashSet<string> ReadOnlyTelegrams() => readOnlyTelegrams ??= catalog.JobNames().Where(JobRules.IsReadOnlyJob)
        .Select(job => decoder.RequestFor(job)).OfType<byte[]>()
        .Select(Convert.ToHexString).ToHashSet();

    async Task<JsonNode?> Dispatch(string method, JsonObject p, JsonArray warnings) => method switch
    {
        "status" => Status(),
        "ident" => await Ident(),
        "jobs.list" => JobsList(),
        "jobs.describe" => JobsDescribe(Str(p, "job")),
        "job.run" => await JobRun(Str(p, "job"), OptStr(p, "args") ?? "", OptBool(p, "allow_write"), OptBool(p, "native"), Gap(p)),
        "faults.read" => await FaultsRead(),
        "faults.clear" => await FaultsClear(OptBool(p, "confirm")),
        "log.start" => await LogStart(p, warnings),
        "bench" => await Bench(Str(p, "job"), p["n"]?.GetValue<int>() ?? 20, OptBool(p, "native"), Gap(p)),
        "ds2.request" => Ds2Request(Str(p, "job"), OptStr(p, "args") ?? ""),
        "ds2.decode" => Ds2Decode(Str(p, "job"), OptStr(p, "args") ?? "", Str(p, "response")),
        "ds2.raw" => await Ds2Raw(Str(p, "request"), Gap(p), OptBool(p, "allow_write")),
        "log.stop" => await bus.Submit(bus.StopLog),
        "log.status" => bus.Log?.Status() ?? new JsonObject { ["running"] = false },
        "sessions.list" => SessionsList(warnings),
        "daemon.stop" => Shutdown(),
        _ => throw new FelkodException("unknown_method", $"unknown method: {method}"),
    };

    static string Str(JsonObject p, string key) =>
        OptStr(p, key) ?? throw new FelkodException("bad_request", $"missing parameter: {key}");

    static string? OptStr(JsonObject p, string key) => p[key]?.GetValue<string>();

    static bool OptBool(JsonObject p, string key) => p[key]?.GetValue<bool>() ?? false;

    static int Gap(JsonObject p)
    {
        int gap = p["gap_ms"]?.GetValue<int>() ?? DefaultGapMs;
        return gap is >= 0 and <= 5000 ? gap : throw new FelkodException("bad_request", "gap_ms must be 0-5000");
    }

    static byte[] Hex(string text)
    {
        try
        {
            return Convert.FromHexString(text.Replace(" ", ""));
        }
        catch (FormatException)
        {
            throw new FelkodException("bad_request", $"not hex: {text}");
        }
    }

    static string[] StrList(JsonObject p, string key)
    {
        if (p[key] == null)
        {
            return [];
        }
        if (p[key] is not JsonArray list || list.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _)))
        {
            throw new FelkodException("bad_request", $"{key} must be an array of strings");
        }
        return list.Select(item => item!.GetValue<string>()).ToArray();
    }

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
            jobs.Add(new JsonObject { ["name"] = name, ["read_only"] = JobRules.IsReadOnlyJob(name) });
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
            ["read_only"] = JobRules.IsReadOnlyJob(job),
            ["comments"] = Sets("_JOBCOMMENTS"),
            ["arguments"] = Sets("_ARGUMENTS"),
            ["results"] = Sets("_RESULTS"),
        };
    }

    async Task<JsonNode?> JobRun(string job, string args, bool allowWrite, bool native, int gapMs)
    {
        if (JobRules.IsMetadataJob(job))
        {
            // offline, from the catalog: no bus, no ECU, works without the car
            var metadata = catalog.Run(job.ToUpperInvariant(), args);
            return new JsonObject { ["job"] = job, ["args"] = args, ["sets"] = Results.ValueSets(metadata) };
        }
        catalog.RequireJobs([job]);
        if (!allowWrite && !JobRules.IsReadOnlyJob(job))
        {
            throw new FelkodException("write_not_allowed", $"{job} is not a read-only job; pass allow_write to run it");
        }
        if (!JobRules.IsReadOnlyJob(job))
        {
            // native resends a request after a garbled reply, which a job that changes the car must not get
            if (native)
            {
                throw new FelkodException("write_not_allowed", $"{job} is not a read-only job; native only runs read-only jobs");
            }
        }
        var sets = (JsonArray)(await bus.Submit(() =>
        {
            if (!JobRules.IsReadOnlyJob(job))
            {
                Audit("job.run", new JsonObject { ["job"] = job, ["args"] = args });
            }
            return native ? bus.RunJobNative(job, args, gapMs).Sets : bus.RunJob(job, args);
        }))!;
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
            // The faults are saved to the audit log before the clear is sent, so clearing never loses them,
            // even if the clear's reply is lost.
            var before = bus.RunJob("FS_LESEN");
            Results.EnsureOkay(before);
            var faults = FaultList(before);
            Audit("faults.clear", new JsonObject { ["stage"] = "sending", ["ubatt_v"] = ubatt, ["faults_before"] = faults.DeepClone() });
            try
            {
                var cleared = bus.RunJob("FS_LOESCHEN");
                Results.EnsureOkay(cleared);
            }
            catch (Exception ex)
            {
                // the ECU may or may not have cleared; the audit says what is known
                Audit("faults.clear", new JsonObject { ["stage"] = "failed", ["error"] = ex.Message });
                throw;
            }
            Audit("faults.clear", new JsonObject { ["stage"] = "done" });
            return new JsonObject { ["cleared"] = true, ["ubatt_v"] = ubatt, ["faults_before"] = faults };
        });
    }

    async Task<JsonNode?> LogStart(JsonObject p, JsonArray warnings)
    {
        var jobs = StrList(p, "jobs");
        var slowJobs = StrList(p, "slow_jobs");
        int slowEvery = p["slow_every"]?.GetValue<int>() ?? 10;
        double durationS = p["duration_s"]?.GetValue<double>() ?? 0;
        // JSON's 1e400 reads as infinity, which meta.json could not store
        if (!double.IsFinite(durationS) || durationS < 0)
        {
            throw new FelkodException("bad_request", "duration_s must be a finite number >= 0 (0 = until log.stop)");
        }
        if (slowEvery < 1)
        {
            throw new FelkodException("bad_request", "slow_every must be >= 1");
        }
        if (jobs.Length == 0)
        {
            throw new FelkodException("bad_request", "jobs is empty");
        }
        catalog.RequireJobs(jobs.Concat(slowJobs));
        var writeJobs = jobs.Concat(slowJobs).Where(job => !JobRules.IsReadOnlyJob(job)).ToList();
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
        int? nativeGapMs = OptBool(p, "native") ? Gap(p) : null;
        meta["native_gap_ms"] = nativeGapMs;
        if (nativeGapMs != null)
        {
            var notNative = jobs.Concat(slowJobs).Where(job => decoder.RequestFor(job) == null).ToList();
            if (notNative.Count > 0)
            {
                warnings.Add($"not single-telegram jobs, logged through EdiabasLib: {string.Join(", ", notNative)}");
            }
        }
        // Created on the bus thread: two concurrent starts must not pick the same session directory,
        // and the one refused by StartLog must not delete the other's.
        var started = await bus.Submit(() =>
        {
            var session = Session.Create(config.SessionsDir, meta);
            try
            {
                bus.StartLog(new LogRun(session, jobs, slowJobs, slowEvery, durationS, nativeGapMs));
            }
            catch (Exception)
            {
                session.Dispose();
                Directory.Delete(session.Dir, recursive: true);
                throw;
            }
            return new JsonObject { ["session_id"] = session.Id, ["dir"] = session.Dir };
        });
        if (!File.Exists(config.Port))
        {
            warnings.Add($"port {config.Port} does not exist; logging will wait for it");
        }
        return started;
    }

    // Times a job n times. Native runs also report the ECU's reply latency (first byte after the echo).
    async Task<JsonNode?> Bench(string job, int n, bool native, int gapMs)
    {
        catalog.RequireJobs([job]);
        if (!JobRules.IsReadOnlyJob(job))
        {
            throw new FelkodException("write_not_allowed", $"bench only runs read-only jobs: {job}");
        }
        if (n < 2)
        {
            throw new FelkodException("bad_request", "bench needs n >= 2");
        }
        if (native && decoder.RequestFor(job) == null)
        {
            throw new FelkodException("not_native", $"{job} does not send exactly one telegram; bench it without native");
        }
        return await bus.Submit(() =>
        {
            var totals = new List<double>();
            var latencies = new List<double>();
            var errors = new JsonArray();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int linkFailuresInARow = 0;
            string? aborted = null;
            for (int i = 0; i < n; i++)
            {
                // stay well inside the CLI's 90 s wait, and stop hammering an ECU that is not answering
                if (clock.Elapsed.TotalSeconds > BenchMaxSeconds)
                {
                    aborted = $"stopped after {BenchMaxSeconds} s";
                    break;
                }
                if (linkFailuresInARow >= 3)
                {
                    aborted = "stopped after 3 link failures in a row";
                    break;
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var (sets, exchange) = native ? bus.RunJobNative(job, "", gapMs) : (bus.RunJob(job), null);
                    Results.EnsureOkay(sets);
                    totals.Add(sw.Elapsed.TotalMilliseconds);
                    linkFailuresInARow = 0;
                    if (exchange != null)
                    {
                        latencies.Add(exchange.FirstByteMs);
                    }
                }
                catch (FelkodException ex) when (ex.Code != "shutting_down")
                {
                    errors.Add(new JsonObject { ["run"] = i, ["code"] = ex.Code, ["error"] = ex.Message });
                    linkFailuresInARow = ex.IsLink ? linkFailuresInARow + 1 : 0;
                }
                catch (Exception ex) when (ex is not FelkodException)
                {
                    errors.Add(new JsonObject { ["run"] = i, ["code"] = "internal", ["error"] = $"{ex.GetType().Name}: {ex.Message}" });
                    linkFailuresInARow = 0;
                }
            }
            double seconds = clock.Elapsed.TotalSeconds;
            if (totals.Count == 0)
            {
                throw new FelkodException("bench_failed", errors.Count > 0
                    ? $"all {errors.Count} runs failed; first: {errors[0]!["error"]}"
                    : $"no runs completed ({aborted ?? "stopped"})");
            }
            return new JsonObject
            {
                ["job"] = job,
                ["native"] = native,
                ["gap_ms"] = native ? gapMs : null,
                ["runs"] = totals.Count + errors.Count,
                ["requested"] = n,
                ["aborted"] = aborted,
                ["ok"] = totals.Count,
                ["failed"] = errors.Count,
                ["per_s"] = Math.Round(totals.Count / seconds, 2),
                ["job_ms"] = Stats(totals),
                ["ecu_first_byte_ms"] = native ? Stats(latencies) : null,
                ["errors"] = errors,
            };
        });
    }

    static JsonObject? Stats(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }
        var sorted = values.Order().ToList();
        double Pct(double q) => Math.Round(sorted[(int)Math.Round(q * (sorted.Count - 1))], 2);
        return new JsonObject { ["min"] = Pct(0), ["median"] = Pct(0.5), ["p90"] = Pct(0.9), ["max"] = Pct(1) };
    }

    JsonObject Ds2Request(string job, string args)
    {
        catalog.RequireJobs([job]);
        var request = decoder.RequestFor(job, args);
        return new JsonObject
        {
            ["job"] = job,
            ["args"] = args,
            ["native"] = request != null,
            ["request"] = request == null ? null : Convert.ToHexString(request),
        };
    }

    JsonObject Ds2Decode(string job, string args, string responseHex)
    {
        catalog.RequireJobs([job]);
        var request = decoder.RequestFor(job, args)
            ?? throw new FelkodException("not_native", $"{job} does not send exactly one telegram");
        var response = Hex(responseHex);
        // The same checks a live reply gets: framing, BMW's status and known length, then job status.
        if (response.Length < 4 || response[1] != response.Length || response[0] != request[0]
            || Ds2Port.Checksum(response.AsSpan(0, response.Length - 1)) != response[^1])
        {
            throw new FelkodException("bad_request",
                $"not a complete DS2 reply from address {request[0]:X2} (length byte, checksum and address must match)");
        }
        bus.CheckReply(request, response);
        var sets = decoder.Decode(job, args, request, response)
            ?? throw new FelkodException("decode_failed", $"{job} could not decode that reply");
        Results.EnsureOkay(sets);
        return new JsonObject { ["job"] = job, ["args"] = args, ["sets"] = Results.ValueSets(sets) };
    }

    // A raw telegram. Without allow_write only telegrams that a read-only SGBD job sends are allowed.
    async Task<JsonNode?> Ds2Raw(string requestHex, int gapMs, bool allowWrite)
    {
        var request = Hex(requestHex);
        if (request.Length >= 2 && request.Length == request[1] - 1)
        {
            request = [.. request, Ds2Port.Checksum(request)];
        }
        if (request.Length < 4 || request.Length != request[1] || Ds2Port.Checksum(request.AsSpan(0, request.Length - 1)) != request[^1])
        {
            throw new FelkodException("bad_request", $"not a DS2 telegram: {Convert.ToHexString(request)}");
        }
        if (!allowWrite)
        {
            if (!ReadOnlyTelegrams().Contains(Convert.ToHexString(request)))
            {
                throw new FelkodException("write_not_allowed",
                    "not a telegram any read-only SGBD job sends; pass allow_write to send it anyway");
            }
        }
        return await bus.Submit(() =>
        {
            if (allowWrite)
            {
                Audit("ds2.raw", new JsonObject { ["request"] = Convert.ToHexString(request) });
            }
            var exchange = bus.RawExchange(request, gapMs, allowRetry: false);
            return new JsonObject
            {
                ["request"] = Convert.ToHexString(exchange.Request),
                ["response"] = Convert.ToHexString(exchange.Response),
                ["status"] = $"{exchange.Response[2]:X2}",
                ["echo_ms"] = exchange.EchoMs,
                ["first_byte_ms"] = exchange.FirstByteMs,
                ["total_ms"] = exchange.TotalMs,
                ["waited_ms"] = exchange.WaitedMs,
            };
        });
    }

    JsonObject SessionsList(JsonArray warnings)
    {
        var sessions = new JsonArray();
        if (Directory.Exists(config.SessionsDir))
        {
            foreach (string dir in Directory.GetDirectories(config.SessionsDir).OrderDescending(StringComparer.Ordinal))
            {
                string metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath))
                {
                    continue;
                }
                try
                {
                    var meta = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject
                        ?? throw new JsonException("metadata is not an object");
                    meta["dir"] = dir;
                    meta["running"] = bus.Log?.Id is string runningId && meta["id"]?.GetValue<string>() == runningId;
                    sessions.Add(meta);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    warnings.Add($"could not read session metadata {metaPath}: {ex.Message}");
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
