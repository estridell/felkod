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

using var ediabas = new EdiabasNet();
var iface = new EdInterfaceObd { ComPort = opts["port"] };
ediabas.EdInterfaceClass = iface;
ediabas.SetConfigProperty("EcuPath", opts["ecu-path"]);
ediabas.SetConfigProperty("TracePath", traceDir);
ediabas.SetConfigProperty("ApiTrace", "1");
ediabas.SetConfigProperty("IfhTrace", "3");
// Metadata jobs (_JOBS, _RESULTS, ...) read the .prg only; skip the init job so they work without a car.
ediabas.NoInitForVJobs = true;

var output = new JsonObject
{
    ["command"] = string.Join(' ', rest),
    ["sgbd"] = opts["sgbd"],
    ["port"] = opts["port"],
    ["trace_dir"] = traceDir,
};

try
{
    ediabas.ResolveSgbdFile(opts["sgbd"]);
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
