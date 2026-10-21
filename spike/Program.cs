// felkod Phase 0 spike: can EdiabasLib talk to the MS43 over a K+DCAN cable on Linux?
// Output is JSON on stdout; raw EDIABAS/IFH traces go to the trace directory.
using System.Diagnostics;
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
    if (args[i].StartsWith("--") && i + 1 < args.Length && opts.ContainsKey(args[i][2..]))
    {
        opts[args[i][2..]] = args[++i];
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
          log                      run --jobs in a loop until Ctrl+C or --duration seconds,
                                   --slow-jobs every --slow-every cycles; one JSON line per job
                                   to <trace-dir>/log.jsonl, flushed immediately; keeps retrying
                                   through stalls, ignition cycles and cable replugs
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

EdiabasNet ediabas = CreateEdiabas();

var output = new JsonObject
{
    ["command"] = string.Join(' ', rest),
    ["sgbd"] = opts["sgbd"],
    ["port"] = opts["port"],
    ["trace_dir"] = traceDir,
};

try
{
    output["sgbd_file"] = ediabas.SgbdFileName;

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
        {
            int n = rest.Count > 2 ? int.Parse(rest[2]) : 20;
            var times = new List<double>();
            JsonArray last = new();
            for (int i = 0; i < n; i++)
            {
                var sw = Stopwatch.StartNew();
                last = RunJob(Arg(1), "");
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            double firstRun = times[0];
            times.Sort();
            output["bench"] = new JsonObject
            {
                ["runs"] = n,
                ["jobs_per_s"] = Math.Round(n / (times.Sum() / 1000.0), 2),
                ["ms_min"] = Math.Round(times[0], 1),
                ["ms_median"] = Math.Round(times[n / 2], 1),
                ["ms_max"] = Math.Round(times[^1], 1),
                ["first_run_ms"] = Math.Round(firstRun, 1),
            };
            output["last_result_sets"] = last;
            break;
        }
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

Console.WriteLine(output.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
return output["ok"]!.GetValue<bool>() ? 0 : 1;

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
    // Metadata jobs (_JOBS, _RESULTS, ...) read the .prg only; skip the init job so they work without a car.
    instance.NoInitForVJobs = true;
    instance.ResolveSgbdFile(opts["sgbd"]);
    return instance;
}

// Logs until Ctrl+C or --duration, surviving stalls, ignition cycles and cable replugs.
// While the link is up: one line per job. When a job fails: one link_lost event, then a
// fresh EdiabasLib instance (new port handle, init job rerun) is probed once a second,
// with a still_down event every 10 s, until a job succeeds again (link_restored).
JsonObject RunLog()
{
    string[] SplitJobs(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    string[] jobs = SplitJobs(opts["jobs"]);
    string[] slowJobs = SplitJobs(opts["slow-jobs"]);
    int slowEvery = Math.Max(1, int.Parse(opts["slow-every"]));
    double duration = double.Parse(opts["duration"], System.Globalization.CultureInfo.InvariantCulture);
    string logPath = Path.Combine(traceDir, "log.jsonl");

    bool stop = false;
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };
    Console.Error.WriteLine($"logging to {logPath}, Ctrl+C to stop");

    using var writer = new StreamWriter(logPath) { AutoFlush = true };
    var clock = Stopwatch.StartNew();
    var counts = new Dictionary<string, int>();
    var outages = new JsonArray();
    int cycle = 0;
    bool linkUp = true;
    double downSince = 0;
    double lastStillDown = 0;

    bool Running() => !stop && (duration <= 0 || clock.Elapsed.TotalSeconds < duration);
    double Now() => Math.Round(clock.Elapsed.TotalSeconds, 3);
    JsonObject Stamp(JsonObject line)
    {
        line["t"] = DateTime.UtcNow.ToString("o");
        line["elapsed_s"] = Now();
        return line;
    }
    void Write(JsonObject line) => writer.WriteLine(line.ToJsonString());

    // Runs one job and writes its line; returns false (writing nothing) if it failed.
    bool Sample(string job)
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
        return true;
    }

    string ErrorText(Exception ex) =>
        ediabas.ErrorCodeLast.ToString() is var code && code != "EDIABAS_ERR_NONE" ? code : ex.Message;

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
                catch (Exception ex)
                {
                    linkUp = false;
                    downSince = lastStillDown = clock.Elapsed.TotalSeconds;
                    Write(Stamp(new JsonObject { ["event"] = "link_lost", ["job"] = job, ["error"] = ErrorText(ex) }));
                    Console.Error.WriteLine($"[{Now():F1}s] link lost: {ErrorText(ex)}; retrying");
                    break;
                }
            }
            cycle++;
            continue;
        }

        Thread.Sleep(1000);
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
        Console.Error.WriteLine($"[{Now():F1}s] link restored after {downFor}s");
    }

    var perJob = new JsonObject();
    foreach (var (job, n) in counts)
    {
        perJob[job] = n;
    }
    return new JsonObject
    {
        ["file"] = logPath,
        ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
        ["cycles"] = cycle,
        ["samples"] = perJob,
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
