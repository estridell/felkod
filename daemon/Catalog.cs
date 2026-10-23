using System.Text.Json.Nodes;
using EdiabasLib;

namespace Felkod.Daemon;

// Offline SGBD work shares one interpreter and a hardware-free interface: metadata, request
// capture and reply decoding. The bus owns the separate interpreter that can talk to the ECU.
sealed class Catalog
{
    readonly object gate = new();
    readonly EdiabasNet ediabas;
    readonly DecodeInterface decode = new();
    // Only args-free requests are cached; RPC argument strings would grow this without bound.
    readonly Dictionary<string, byte[]?> requests = new();
    readonly Dictionary<byte, string> statusTexts = new();
    HashSet<string>? jobNames;

    public Catalog(Config config)
    {
        lock (Sgbd.Gate)
        {
            ediabas = new EdiabasNet();
            ediabas.EdInterfaceClass = decode;
            ediabas.SetConfigProperty("EcuPath", config.EcuPath);
            ediabas.SetConfigProperty("ApiTrace", "0");
            ediabas.SetConfigProperty("IfhTrace", "0");
            // Skip init only for virtual metadata jobs; normal capture/replay still initializes.
            ediabas.NoInitForVJobs = true;
            ediabas.ResolveSgbdFile(config.Sgbd);
        }
        try
        {
            foreach (var row in Run("_TABLE", "JOBRESULT").Skip(2))
            {
                var cells = row!.AsObject().Select(cell => cell.Value?.ToString()).ToList();
                if (cells.Count >= 2 && cells[0] is string sb && sb.StartsWith("0x"))
                {
                    statusTexts[Convert.ToByte(sb[2..], 16)] = cells[1] ?? "";
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"felkodd: no usable JOBRESULT table in {SgbdFile}: {ex.Message}");
        }
    }

    // BMW's text for a DS2 status byte; without a JOBRESULT table only 0xA0 counts as OKAY.
    public string StatusText(byte status) =>
        statusTexts.TryGetValue(status, out var text) ? text : status == 0xA0 && statusTexts.Count == 0 ? "OKAY" : $"unknown status {status:X2}";

    public string SgbdFile => ediabas.SgbdFileName;

    public JsonArray Run(string vjob, string args = "")
    {
        lock (gate)
        {
            return Sgbd.Use(ediabas, () =>
            {
                ediabas.ArgString = args;
                ediabas.ExecuteJob(vjob);
                return Results.ToJson(ediabas.ResultSets);
            });
        }
    }

    public IReadOnlySet<string> JobNames()
    {
        lock (gate)
        {
            jobNames ??= Run("_JOBS").Skip(1)
                .Select(set => set!["JOBNAME"]?.GetValue<string>())
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return jobNames;
        }
    }

    public void RequireJobs(IEnumerable<string> jobs)
    {
        var unknown = jobs.Where(job => !JobNames().Contains(job)).ToList();
        if (unknown.Count > 0)
        {
            throw new FelkodException("unknown_job", $"not in {SgbdFile}: {string.Join(", ", unknown)}");
        }
    }

    // The telegram a job sends, or null if it sends none or more than one (not usable natively).
    public byte[]? RequestFor(string job, string args = "")
    {
        lock (gate)
        {
            if (args == "" && requests.TryGetValue(job, out var known))
            {
                return known;
            }
            decode.Capture();
            TryExecute(job, args);
            byte[]? request = decode.Captured.Count == 1 ? decode.Captured[0] : null;
            if (args == "")
            {
                requests[job] = request;
            }
            return request;
        }
    }

    public void MarkNotNative(string job, string args = "")
    {
        lock (gate)
        {
            if (args == "")
            {
                requests[job] = null;
            }
        }
    }

    // Decodes a captured reply into the job's result sets; null if the job did not fit it
    // (e.g. it wanted another telegram). Whether that makes the job not native is the caller's call.
    public JsonArray? Decode(string job, string args, byte[] request, byte[] response)
    {
        lock (gate)
        {
            decode.Answer(request, response);
            bool ok = TryExecute(job, args);
            return ok && !decode.Failed ? Results.ToJson(ediabas.ResultSets) : null;
        }
    }

    bool TryExecute(string job, string args) => Sgbd.Use(ediabas, () =>
    {
        ediabas.ArgString = args;
        try
        {
            ediabas.ExecuteJob(job);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    });
}
