// felkod Phase 0 spike: can EdiabasLib talk to the MS43 over a K+DCAN cable on Linux?
// Output is JSON on stdout; raw EDIABAS/IFH traces go to the trace directory.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdiabasLib;
using Felkod.Daemon;

var opts = new Dictionary<string, string>
{
    ["port"] = "/dev/felkod-kline",
    ["ecu-path"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../vendor/bmw/ediabas/ecu")),
    ["sgbd"] = "ms430ds0",
    ["trace-dir"] = "",
    ["jobs"] = "STATUS_MESSWERTEBLOCK",
    ["slow-jobs"] = "",
    ["slow-every"] = "10",
    ["duration"] = "0",
    ["job-timeout"] = "15",
    ["allow-write"] = "no",
};
var rest = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--"))
    {
        string key = args[i][2..];
        if (!opts.ContainsKey(key) || i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"unknown option or missing value: {args[i]}");
            return 2;
        }
        opts[key] = args[++i];
    }
    else
    {
        rest.Add(args[i]);
    }
}

if (rest.Count == 0)
{
    Console.Error.WriteLine("""
        usage: felkod-spike [options] <command>
          jobs                     list the SGBD's jobs and comments (offline)
          results <job>            list a job's results (offline)
          args <job>               list a job's arguments (offline)
          run <job> [argstring]    run a job, print result sets
          bench <job> [n]          run a job n times (default 20), report jobs/s
          log                      run --jobs in a loop until stopped or --duration seconds,
                                   --slow-jobs every --slow-every cycles; one JSON line per job
                                   appended to <trace-dir>/log.jsonl, flushed immediately; keeps
                                   retrying through stalls, ignition cycles and cable replugs
        options: --port DEV --ecu-path DIR --sgbd NAME --trace-dir DIR --job-timeout SEC (15)
                 --allow-write yes   permit jobs that change the car (STEUERN_*, *_LOESCHEN, ...)
        Stop with Ctrl+C, or SIGTERM/SIGHUP when running in the background (background shells
        ignore SIGINT). A second signal exits immediately.
        """);
    return 2;
}

string traceDir = opts["trace-dir"];
if (traceDir.Length == 0)
{
    traceDir = Path.Combine(Directory.GetCurrentDirectory(), "traces", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
}
Directory.CreateDirectory(traceDir);
double jobTimeoutS = double.Parse(opts["job-timeout"], CultureInfo.InvariantCulture);
bool allowWrite = opts["allow-write"] == "yes";

// EdiabasLib reads SGBD text as Windows-1252, which .NET only ships as an opt-in provider.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// First signal asks loops to finish cleanly (current job aborted, summary printed, traces flushed);
// a second one exits. The registrations must stay referenced, or the GC unregisters them.
var stopSource = new CancellationTokenSource();
bool Stopping() => stopSource.IsCancellationRequested;
var signalHandlers = new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP }
    .Select(signal => PosixSignalRegistration.Create(signal, context =>
    {
        context.Cancel = true;
        if (Stopping())
        {
            Environment.Exit(130);
        }
        stopSource.Cancel();
        try
        {
            Console.Error.WriteLine($"{context.Signal}: stopping");
        }
        catch (Exception)
        {
            // stderr may be gone (SIGHUP from a closed terminal)
        }
    }))
    .ToList();

// Times the running job; EdiabasLib polls AbortJobFunc, which ends jobs past the deadline
// (e.g. an ECU answering BUSY forever) and jobs interrupted by a stop signal.
var jobClock = new Stopwatch();
EdiabasNet ediabas = CreateEdiabas();

var output = new JsonObject
{
    ["command"] = string.Join(' ', rest),
    ["sgbd"] = opts["sgbd"],
    ["port"] = opts["port"],
    ["trace_dir"] = traceDir,
    ["sgbd_file"] = ediabas.SgbdFileName,
};

try
{
    switch (rest[0])
    {
        case "jobs":
            output["result_sets"] = RunJob("_JOBS", "");
            break;
        case "results":
            output["result_sets"] = RunJob("_RESULTS", Arg(1));
            break;
        case "args":
            output["result_sets"] = RunJob("_ARGUMENTS", Arg(1));
            break;
        case "run":
        {
            CheckWriteAllowed(Arg(1));
            var sets = RunJob(Arg(1), rest.Count > 2 ? rest[2] : "");
            // rejected results are zeros or garbage, never data
            EnsureOkay(sets);
            output["result_sets"] = sets;
            break;
        }
        case "bench":
            CheckWriteAllowed(Arg(1));
            output["bench"] = RunBench(Arg(1), rest.Count > 2 ? int.Parse(rest[2], CultureInfo.InvariantCulture) : 20);
            if (output["bench"]!["ok"]!.GetValue<int>() == 0)
            {
                throw new InvalidOperationException("every bench run failed");
            }
            break;
        case "log":
            output["log"] = RunLog();
            break;
        default:
            throw new ArgumentException($"unknown command: {rest[0]}");
    }
    output["ok"] = true;
}
catch (Exception ex)
{
    output["ok"] = false;
    output["error"] = new JsonObject
    {
        ["type"] = ex.GetType().Name,
        ["message"] = ErrorText(ex),
        ["ediabas_error"] = ediabas.ErrorCodeLast.ToString(),
    };
}
finally
{
    // Dispose stops EdiabasLib's foreground comm thread, closes the port and flushes the traces.
    try
    {
        ediabas.Dispose();
    }
    catch (Exception)
    {
        // nothing left to save
    }
}

Console.WriteLine(output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
GC.KeepAlive(signalHandlers);
// Exit explicitly: a comm thread stuck in serial I/O must not keep the process alive.
Environment.Exit(output["ok"]!.GetValue<bool>() ? 0 : 1);
return 0;

string Arg(int index) =>
    rest.Count > index ? rest[index] : throw new ArgumentException($"{rest[0]} needs a job name");

EdiabasNet CreateEdiabas()
{
    var instance = new EdiabasNet();
    instance.EdInterfaceClass = new EdInterfaceObd { ComPort = opts["port"] };
    instance.SetConfigProperty("EcuPath", opts["ecu-path"]);
    instance.SetConfigProperty("TracePath", traceDir);
    instance.SetConfigProperty("AppendTrace", "1");
    instance.SetConfigProperty("ApiTrace", "1");
    // Level 1 keeps the raw telegrams (the replay fixtures) without the interpreter's op-level
    // trace, which runs at ~14 MB/s.
    instance.SetConfigProperty("IfhTrace", "1");
    // Unbuffered, so the telegrams survive a kill.
    instance.SetConfigProperty("IfhTraceBuffering", "0");
    instance.SetConfigProperty("TraceBuffering", "0");
    // Metadata jobs (_JOBS, _RESULTS, ...) read the .prg only; skip the init job so they work without a car.
    instance.NoInitForVJobs = true;
    instance.AbortJobFunc = () => Stopping() || (jobClock.IsRunning && jobClock.Elapsed.TotalSeconds > jobTimeoutS);
    instance.ResolveSgbdFile(opts["sgbd"]);
    return instance;
}

// Jobs that are not read-only (daemon/JobRules.cs) change the car and need --allow-write yes.
void CheckWriteAllowed(string job)
{
    if (!allowWrite && !JobRules.IsReadOnlyJob(job))
    {
        throw new InvalidOperationException($"{job} is not a read-only job; pass --allow-write yes to run it");
    }
}

// A communication failure (IFH_*) or a job past its deadline means the ECU or cable went away;
// anything else (rejected request, unknown job) is a job problem.
bool IsLinkError(Exception ex) =>
    ex is TimeoutException || ediabas.ErrorCodeLast.ToString().StartsWith("EDIABAS_IFH_");

string ErrorText(Exception ex) => ex switch
{
    JobStatusException or TimeoutException => ex.Message,
    _ when ediabas.ErrorCodeLast.ToString() is var code && code != "EDIABAS_ERR_NONE" => code,
    _ => ex.Message,
};

// The SGBD reports ECU rejections (negative responses) through JOB_STATUS, with the value
// results left as zeros or garbage; such results must never be treated as data.
void EnsureOkay(JsonArray sets)
{
    foreach (var set in sets)
    {
        if (set!["JOB_STATUS"] is JsonNode status && status.GetValue<string>() != "OKAY")
        {
            throw new JobStatusException(status.GetValue<string>());
        }
    }
}

// The first run includes connecting and the SGBD init job, so it is reported separately.
JsonObject RunBench(string job, int n)
{
    if (n < 2)
    {
        throw new ArgumentException("bench needs at least 2 runs");
    }
    var times = new List<double>();
    var errors = new JsonArray();
    double? firstRun = null;
    int attempted = 0;
    for (int i = 0; i < n && !Stopping(); i++)
    {
        attempted++;
        var sw = Stopwatch.StartNew();
        try
        {
            EnsureOkay(RunJob(job, ""));
            if (firstRun == null)
            {
                firstRun = sw.Elapsed.TotalMilliseconds;
            }
            else
            {
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex)
        {
            errors.Add(new JsonObject { ["run"] = i, ["error"] = ErrorText(ex), ["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1) });
        }
    }
    var result = new JsonObject
    {
        ["runs"] = attempted,
        ["ok"] = times.Count + (firstRun == null ? 0 : 1),
        ["failed"] = errors.Count,
        ["first_run_ms"] = firstRun == null ? null : Math.Round(firstRun.Value, 1),
        ["errors"] = errors,
        ["note"] = "stats exclude the first successful run",
    };
    if (times.Count > 0)
    {
        times.Sort();
        double median = times.Count % 2 == 1
            ? times[times.Count / 2]
            : (times[times.Count / 2 - 1] + times[times.Count / 2]) / 2;
        result["jobs_per_s"] = Math.Round(times.Count / (times.Sum() / 1000.0), 2);
        result["ms_min"] = Math.Round(times[0], 1);
        result["ms_median"] = Math.Round(median, 1);
        result["ms_max"] = Math.Round(times[^1], 1);
    }
    return result;
}

// Logs until stopped or --duration, surviving stalls, ignition cycles and cable replugs.
// While the link is up: one line per successful job (`t` = when the job started). A communication
// failure writes one link_lost event (stamped with the failing job's start), then a fresh
// EdiabasLib instance (new port handle, init job rerun) is probed about once a second, with a
// still_down event every 10 s, until a job succeeds again (link_restored).
// Other job failures (e.g. the ECU rejecting a request) write a job_error line and logging continues.
JsonObject RunLog()
{
    string[] SplitJobs(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    string[] jobs = SplitJobs(opts["jobs"]);
    string[] slowJobs = SplitJobs(opts["slow-jobs"]);
    int slowEvery = Math.Max(1, int.Parse(opts["slow-every"], CultureInfo.InvariantCulture));
    double duration = double.Parse(opts["duration"], CultureInfo.InvariantCulture);
    string logPath = Path.Combine(traceDir, "log.jsonl");
    if (jobs.Length == 0)
    {
        throw new ArgumentException("--jobs is empty");
    }
    var known = RunJob("_JOBS", "").Skip(1).Select(set => set!["JOBNAME"]?.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unknown = jobs.Concat(slowJobs).Where(job => !known.Contains(job)).ToList();
    if (unknown.Count > 0)
    {
        throw new ArgumentException($"not in {ediabas.SgbdFileName}: {string.Join(", ", unknown)}");
    }
    foreach (string job in jobs.Concat(slowJobs))
    {
        CheckWriteAllowed(job);
    }

    Console.Error.WriteLine($"logging to {logPath}; stop with Ctrl+C or SIGTERM");

    using var writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
    var clock = Stopwatch.StartNew();
    var counts = new Dictionary<string, int>();
    var jobErrors = new Dictionary<string, int>();
    var outages = new JsonArray();
    int cycle = 0;
    bool linkUp = true;
    double downSince = 0;
    double lastStillDown = 0;
    string lastProbeError = "";

    bool Running() => !Stopping() && (duration <= 0 || clock.Elapsed.TotalSeconds < duration);
    double Elapsed() => Math.Round(clock.Elapsed.TotalSeconds, 3);
    string Now() => clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
    JsonObject Stamp(JsonObject line, DateTime? at = null, double? elapsed = null)
    {
        line["t"] = (at ?? DateTime.UtcNow).ToString("o");
        line["elapsed_s"] = elapsed ?? Elapsed();
        return line;
    }
    void Write(JsonObject line) => writer.WriteLine(line.ToJsonString());

    // Runs one job and writes its line; throws if the job failed (nothing written).
    void Sample(string job)
    {
        var startedAt = DateTime.UtcNow;
        double startedElapsed = Elapsed();
        var sw = Stopwatch.StartNew();
        var sets = RunJob(job, "");
        EnsureOkay(sets);
        var values = new JsonObject();
        foreach (var set in sets.Skip(1))
        {
            foreach (var (name, value) in set!.AsObject())
            {
                values[name] = value?.DeepClone();
            }
        }
        var line = Stamp(new JsonObject { ["cycle"] = cycle, ["job"] = job }, startedAt, startedElapsed);
        line["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
        line["values"] = values;
        Write(line);
        counts[job] = counts.GetValueOrDefault(job) + 1;
    }

    while (Running())
    {
        if (linkUp)
        {
            var cycleJobs = cycle % slowEvery == 0 ? jobs.Concat(slowJobs) : jobs;
            foreach (string job in cycleJobs)
            {
                if (!Running())
                {
                    break;
                }
                var startedAt = DateTime.UtcNow;
                double startedElapsed = Elapsed();
                try
                {
                    Sample(job);
                }
                catch (Exception) when (Stopping())
                {
                    break;
                }
                catch (Exception ex) when (IsLinkError(ex))
                {
                    linkUp = false;
                    downSince = lastStillDown = startedElapsed;
                    lastProbeError = ErrorText(ex);
                    var line = Stamp(new JsonObject { ["event"] = "link_lost", ["job"] = job, ["error"] = ErrorText(ex) }, startedAt, startedElapsed);
                    line["detected_elapsed_s"] = Elapsed();
                    Write(line);
                    Console.Error.WriteLine($"[{Now()}s] link lost: {ErrorText(ex)}; retrying");
                    break;
                }
                catch (Exception ex)
                {
                    Write(Stamp(new JsonObject { ["event"] = "job_error", ["cycle"] = cycle, ["job"] = job, ["error"] = ErrorText(ex) }, startedAt, startedElapsed));
                    jobErrors[job] = jobErrors.GetValueOrDefault(job) + 1;
                    Thread.Sleep(200);
                }
            }
            cycle++;
            continue;
        }

        stopSource.Token.WaitHandle.WaitOne(1000);
        if (!Running())
        {
            break;
        }
        if (clock.Elapsed.TotalSeconds - lastStillDown >= 10)
        {
            lastStillDown = clock.Elapsed.TotalSeconds;
            Write(Stamp(new JsonObject { ["event"] = "still_down", ["down_s"] = Math.Round(lastStillDown - downSince, 1), ["last_error"] = lastProbeError }));
        }
        try
        {
            ediabas.Dispose();
            ediabas = CreateEdiabas();
            Sample(jobs[0]);
        }
        catch (Exception ex)
        {
            lastProbeError = ErrorText(ex);
            continue;
        }
        double downFor = Math.Round(clock.Elapsed.TotalSeconds - downSince, 1);
        linkUp = true;
        outages.Add(new JsonObject { ["at_s"] = Math.Round(downSince, 1), ["down_s"] = downFor });
        Write(Stamp(new JsonObject { ["event"] = "link_restored", ["down_s"] = downFor }));
        Console.Error.WriteLine($"[{Now()}s] link restored after {downFor}s");
    }
    if (!linkUp)
    {
        outages.Add(new JsonObject
        {
            ["at_s"] = Math.Round(downSince, 1),
            ["down_s"] = Math.Round(clock.Elapsed.TotalSeconds - downSince, 1),
            ["open"] = true,
            ["last_error"] = lastProbeError,
        });
    }

    var perJob = new JsonObject();
    foreach (var (job, n) in counts)
    {
        perJob[job] = n;
    }
    var perJobErrors = new JsonObject();
    foreach (var (job, n) in jobErrors)
    {
        perJobErrors[job] = n;
    }
    return new JsonObject
    {
        ["file"] = logPath,
        ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
        ["cycles"] = cycle,
        ["samples"] = perJob,
        ["job_errors"] = perJobErrors,
        ["outages"] = outages,
        ["link_up_at_end"] = linkUp,
    };
}

JsonArray RunJob(string job, string argString)
{
    ediabas.ArgString = argString;
    ediabas.ArgBinaryStd = null;
    ediabas.ResultsRequests = "";
    jobClock.Restart();
    try
    {
        ediabas.ExecuteJob(job);
    }
    catch (Exception) when (!Stopping() && jobClock.Elapsed.TotalSeconds > jobTimeoutS)
    {
        throw new TimeoutException($"{job}: no result within {jobTimeoutS} s");
    }
    finally
    {
        jobClock.Stop();
    }
    if (!Stopping() && jobClock.Elapsed.TotalSeconds > jobTimeoutS)
    {
        throw new TimeoutException($"{job}: no result within {jobTimeoutS} s");
    }

    var sets = new JsonArray();
    foreach (var set in ediabas.ResultSets)
    {
        var obj = new JsonObject();
        foreach (var (name, data) in set)
        {
            obj[name] = data.OpData switch
            {
                byte[] bytes => Convert.ToHexString(bytes),
                // JSON has no NaN/Infinity; keep them as strings rather than failing the job.
                double d when !double.IsFinite(d) => d.ToString(CultureInfo.InvariantCulture),
                double d => d,
                long l => l,
                string s => s,
                null => null,
                var other => other.ToString(),
            };
        }
        sets.Add(obj);
    }
    return sets;
}

class JobStatusException(string status) : Exception($"JOB_STATUS={status}")
{
    public string Status { get; } = status;
}
