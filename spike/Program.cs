// felkod Phase 0 spike: can EdiabasLib talk to the MS43 over a K+DCAN cable on Linux?
// Output is JSON on stdout; raw EDIABAS/IFH traces go to the trace directory.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdiabasLib;

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
        usage: felkod-spike [--port DEV] [--ecu-path DIR] [--sgbd NAME] [--trace-dir DIR] <command>
          jobs                     list the SGBD's jobs and comments (offline)
          results <job>            list a job's results (offline)
          args <job>               list a job's arguments (offline)
          run <job> [argstring]    run a job, print result sets
          bench <job> [n]          run a job n times (default 20), report jobs/s
          log                      run --jobs in a loop until stopped or --duration seconds,
                                   --slow-jobs every --slow-every cycles; one JSON line per job
                                   to <trace-dir>/log.jsonl, flushed immediately; keeps retrying
                                   through stalls, ignition cycles and cable replugs
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

// EdiabasLib reads SGBD text as Windows-1252, which .NET only ships as an opt-in provider.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

// First signal asks loops to finish cleanly (summary printed, traces flushed); a second one exits.
bool stop = false;
var signalHandlers = new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP }
    .Select(signal => PosixSignalRegistration.Create(signal, context =>
    {
        context.Cancel = true;
        if (stop)
        {
            Environment.Exit(130);
        }
        stop = true;
        Console.Error.WriteLine($"{context.Signal}: stopping after the current job");
    }))
    .ToList();

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
            output["result_sets"] = RunJob(Arg(1), rest.Count > 2 ? rest[2] : "");
            break;
        case "bench":
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
        ["message"] = ex.Message,
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
    instance.SetConfigProperty("IfhTrace", "3");
    // Unbuffered, so the raw telegrams survive a kill; they are the replay fixtures.
    instance.SetConfigProperty("IfhTraceBuffering", "0");
    instance.SetConfigProperty("TraceBuffering", "0");
    // Metadata jobs (_JOBS, _RESULTS, ...) read the .prg only; skip the init job so they work without a car.
    instance.NoInitForVJobs = true;
    instance.ResolveSgbdFile(opts["sgbd"]);
    return instance;
}

// A communication failure (IFH_*) means the ECU or cable went away; anything else is a job problem.
bool IsLinkError() => ediabas.ErrorCodeLast.ToString().StartsWith("EDIABAS_IFH_");

string ErrorText(Exception ex) =>
    ediabas.ErrorCodeLast.ToString() is var code && code != "EDIABAS_ERR_NONE" ? code : ex.Message;

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
    for (int i = 0; i < n && !stop; i++)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            RunJob(job, "");
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
        ["runs"] = n,
        ["ok"] = times.Count + (firstRun == null ? 0 : 1),
        ["failed"] = errors.Count,
        ["first_run_ms"] = firstRun == null ? null : Math.Round(firstRun.Value, 1),
        ["errors"] = errors,
        ["note"] = "stats exclude the first successful run; tracing is on, so rates are a lower bound",
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
// While the link is up: one line per job. A communication failure writes one link_lost event,
// then a fresh EdiabasLib instance (new port handle, init job rerun) is probed once a second,
// with a still_down event every 10 s, until a job succeeds again (link_restored).
// Other job failures write a job_error line and logging continues.
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

    Console.Error.WriteLine($"logging to {logPath}; stop with Ctrl+C or SIGTERM");

    using var writer = new StreamWriter(logPath) { AutoFlush = true };
    var clock = Stopwatch.StartNew();
    var counts = new Dictionary<string, int>();
    var jobErrors = new Dictionary<string, int>();
    var outages = new JsonArray();
    int cycle = 0;
    bool linkUp = true;
    double downSince = 0;
    double lastStillDown = 0;

    bool Running() => !stop && (duration <= 0 || clock.Elapsed.TotalSeconds < duration);
    string Now() => clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
    JsonObject Stamp(JsonObject line)
    {
        line["t"] = DateTime.UtcNow.ToString("o");
        line["elapsed_s"] = Math.Round(clock.Elapsed.TotalSeconds, 3);
        return line;
    }
    void Write(JsonObject line) => writer.WriteLine(line.ToJsonString());

    // Runs one job and writes its line; throws if the job failed (nothing written).
    void Sample(string job)
    {
        var line = Stamp(new JsonObject { ["cycle"] = cycle, ["job"] = job });
        var sw = Stopwatch.StartNew();
        var values = new JsonObject();
        foreach (var set in RunJob(job, "").Skip(1))
        {
            foreach (var (name, value) in set!.AsObject())
            {
                values[name] = value?.DeepClone();
            }
        }
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
                try
                {
                    Sample(job);
                }
                catch (Exception ex) when (IsLinkError())
                {
                    linkUp = false;
                    downSince = lastStillDown = clock.Elapsed.TotalSeconds;
                    Write(Stamp(new JsonObject { ["event"] = "link_lost", ["job"] = job, ["error"] = ErrorText(ex) }));
                    Console.Error.WriteLine($"[{Now()}s] link lost: {ErrorText(ex)}; retrying");
                    break;
                }
                catch (Exception ex)
                {
                    Write(Stamp(new JsonObject { ["event"] = "job_error", ["cycle"] = cycle, ["job"] = job, ["error"] = ErrorText(ex) }));
                    jobErrors[job] = jobErrors.GetValueOrDefault(job) + 1;
                }
            }
            cycle++;
            continue;
        }

        Thread.Sleep(1000);
        if (!Running())
        {
            break;
        }
        if (clock.Elapsed.TotalSeconds - lastStillDown >= 10)
        {
            lastStillDown = clock.Elapsed.TotalSeconds;
            Write(Stamp(new JsonObject { ["event"] = "still_down", ["down_s"] = Math.Round(lastStillDown - downSince, 1) }));
        }
        try
        {
            ediabas.Dispose();
            ediabas = CreateEdiabas();
            Sample(jobs[0]);
        }
        catch (Exception)
        {
            continue;
        }
        double downFor = Math.Round(clock.Elapsed.TotalSeconds - downSince, 1);
        linkUp = true;
        outages.Add(new JsonObject { ["at_s"] = Math.Round(downSince, 1), ["down_s"] = downFor });
        Write(Stamp(new JsonObject { ["event"] = "link_restored", ["down_s"] = downFor }));
        Console.Error.WriteLine($"[{Now()}s] link restored after {downFor}s");
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
    ediabas.ExecuteJob(job);

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
