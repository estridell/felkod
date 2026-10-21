using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Felkod.Daemon;

// A background logging session, advanced one job at a time by the bus thread.
// While the link is up: runs --jobs every cycle and --slow-jobs every slow-every cycles, one sample
// per successful job. A link failure records link_lost (stamped at the failing job's start), then a
// fresh EdiabasLib instance is probed about once a second (still_down every 10 s) until a job
// succeeds (link_restored). Other job failures (e.g. the ECU rejecting a request) record job_error
// and logging continues after a short back-off.
sealed class LogRun
{
    readonly Session session;
    readonly string[] jobs;
    readonly string[] slowJobs;
    readonly int slowEvery;
    readonly double durationS;
    // Native DS2 transport (SGBD-decoded) with this gap before each request, or EdiabasLib jobs when null.
    readonly int? nativeGapMs;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DateTime startedAt = DateTime.UtcNow;
    readonly Dictionary<string, int> samples = new();
    readonly Dictionary<string, int> jobErrors = new();
    readonly JsonArray outages = new();
    readonly object gate = new();
    int cycle;
    int position;
    bool linkUp = true;
    double downSince;
    double lastStillDown;
    double lastProbe;
    double backoffUntil;
    string lastError = "";
    volatile bool stopRequested;

    public string Id => session.Id;
    public string Dir => session.Dir;
    public bool Finished => stopRequested || (durationS > 0 && clock.Elapsed.TotalSeconds >= durationS);

    public LogRun(Session session, string[] jobs, string[] slowJobs, int slowEvery, double durationS, int? nativeGapMs)
    {
        this.nativeGapMs = nativeGapMs;
        this.session = session;
        this.jobs = jobs;
        this.slowJobs = slowJobs;
        this.slowEvery = Math.Max(1, slowEvery);
        this.durationS = durationS;
    }

    public void RequestStop() => stopRequested = true;

    double Elapsed => Math.Round(clock.Elapsed.TotalSeconds, 3);

    string[] CycleJobs() => cycle % slowEvery == 0 ? [.. jobs, .. slowJobs] : jobs;

    // Does one unit of work. Returns false when it is only waiting (link down between probes,
    // or backing off after a job error), so the bus can serve requests meanwhile.
    public bool Step(Bus bus)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (now < backoffUntil)
        {
            return false;
        }
        if (linkUp)
        {
            var cycleJobs = CycleJobs();
            string job = cycleJobs[position];
            if (++position >= cycleJobs.Length)
            {
                position = 0;
                cycle++;
            }
            var startedAt = DateTime.UtcNow;
            double startedElapsed = Elapsed;
            try
            {
                Sample(bus, job, startedAt, startedElapsed);
            }
            catch (FelkodException ex) when (ex.Code == "shutting_down")
            {
                // the bus finishes the session on its way out
            }
            catch (FelkodException ex) when (ex.IsLink)
            {
                lock (gate)
                {
                    linkUp = false;
                    downSince = lastStillDown = lastProbe = startedElapsed;
                    lastError = ex.Message;
                }
                session.AddEvent(startedAt, startedElapsed, "link_lost", job, $"{ex.Message}; detected at {Elapsed:F3} s");
                position = 0;
            }
            catch (FelkodException ex) when (ex.Code != "shutting_down")
            {
                session.AddEvent(startedAt, startedElapsed, "job_error", job, ex.Message);
                lock (gate)
                {
                    jobErrors[job] = jobErrors.GetValueOrDefault(job) + 1;
                }
                backoffUntil = clock.Elapsed.TotalSeconds + 0.2;
            }
            return true;
        }

        if (now - lastProbe < 1)
        {
            return false;
        }
        lastProbe = now;
        if (now - lastStillDown >= 10)
        {
            lastStillDown = now;
            session.AddEvent(DateTime.UtcNow, Elapsed, "still_down", null, $"down {now - downSince:F1} s; last error {lastError}");
        }
        bus.ForceReset();
        try
        {
            Sample(bus, jobs[0], DateTime.UtcNow, Elapsed);
        }
        catch (FelkodException ex)
        {
            lock (gate)
            {
                lastError = ex.Message;
            }
            return true;
        }
        double downFor = Math.Round(clock.Elapsed.TotalSeconds - downSince, 1);
        lock (gate)
        {
            linkUp = true;
            outages.Add(new JsonObject { ["at_s"] = Math.Round(downSince, 1), ["down_s"] = downFor });
        }
        session.AddEvent(DateTime.UtcNow, Elapsed, "link_restored", null, $"down {downFor} s");
        // the probe ran jobs[0]; continue the cycle after it
        position = jobs.Length > 1 ? 1 : 0;
        return true;
    }

    void Sample(Bus bus, string job, DateTime startedAt, double startedElapsed)
    {
        var sw = Stopwatch.StartNew();
        var (sets, exchange) = nativeGapMs is int gap ? bus.RunJobNative(job, "", gap) : (bus.RunJob(job), null);
        Results.EnsureOkay(sets);
        session.AddSample(startedAt, startedElapsed, cycle, job, Math.Round(sw.Elapsed.TotalMilliseconds, 1), sets, exchange);
        lock (gate)
        {
            samples[job] = samples.GetValueOrDefault(job) + 1;
        }
    }

    public JsonObject Status()
    {
        lock (gate)
        {
            var perJob = new JsonObject();
            foreach (var (job, n) in samples)
            {
                perJob[job] = n;
            }
            var perJobErrors = new JsonObject();
            foreach (var (job, n) in jobErrors)
            {
                perJobErrors[job] = n;
            }
            var currentOutages = (JsonArray)outages.DeepClone();
            if (!linkUp)
            {
                currentOutages.Add(new JsonObject
                {
                    ["at_s"] = Math.Round(downSince, 1),
                    ["down_s"] = Math.Round(clock.Elapsed.TotalSeconds - downSince, 1),
                    ["open"] = true,
                    ["last_error"] = lastError,
                });
            }
            double seconds = clock.Elapsed.TotalSeconds;
            return new JsonObject
            {
                ["session_id"] = session.Id,
                ["dir"] = session.Dir,
                ["started_at"] = startedAt.ToString("o"),
                ["seconds"] = Math.Round(seconds, 1),
                ["cycles"] = cycle,
                ["cycles_per_s"] = seconds > 0 ? Math.Round(cycle / seconds, 2) : 0,
                ["link_up"] = linkUp,
                ["samples"] = perJob,
                ["job_errors"] = perJobErrors,
                ["outages"] = currentOutages,
            };
        }
    }

    public JsonObject Finish()
    {
        var summary = Status();
        summary["ended_at"] = DateTime.UtcNow.ToString("o");
        session.SetMeta("ended_at", summary["ended_at"]);
        session.SetMeta("summary", summary.DeepClone());
        session.Dispose();
        return summary;
    }
}
