using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Felkod.Daemon;

// A background logging session, advanced one job at a time by the bus thread.
// While the link is up: runs --jobs every cycle and --slow-jobs every slow-every cycles, one sample
// per successful job. A link failure records link_lost (stamped at the failing job's start), then a
// fresh EdiabasLib instance is probed about once a second (still_down every 10 s) until a job
// succeeds (link_restored). Other job failures (e.g. the ECU rejecting a request) record job_error
// and logging continues after a short back-off. Native logs record not_native the first time a job
// has to fall back to EdiabasLib, and count those samples.
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
    // native logging: samples a job had to take through EdiabasLib instead
    readonly Dictionary<string, int> fallbacks = new();
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

    public string Id => session.Id;
    public string Dir => session.Dir;
    public bool Finished => durationS > 0 && clock.Elapsed.TotalSeconds >= durationS;

    public LogRun(Session session, string[] jobs, string[] slowJobs, int slowEvery, double durationS, int? nativeGapMs)
    {
        this.nativeGapMs = nativeGapMs;
        this.session = session;
        this.jobs = jobs;
        this.slowJobs = slowJobs;
        this.slowEvery = slowEvery;
        this.durationS = durationS;
    }

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
            int sampleCycle = cycle;
            if (++position >= cycleJobs.Length)
            {
                position = 0;
                cycle++;
            }
            var startedAt = DateTime.UtcNow;
            double startedElapsed = Elapsed;
            try
            {
                Sample(bus, job, sampleCycle, startedAt, startedElapsed);
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
                // the interrupted cycle is over; after the outage a new one starts, so no cycle holds a job twice
                if (position != 0)
                {
                    cycle++;
                }
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
        var probeAt = DateTime.UtcNow;
        double probeElapsed = Elapsed;
        try
        {
            Sample(bus, jobs[0], cycle, probeAt, probeElapsed);
        }
        catch (FelkodException ex) when (!ex.IsLink && ex.Code != "shutting_down")
        {
            // the ECU answered (e.g. rejected the request): the link is back, the job just failed
            session.AddEvent(probeAt, probeElapsed, "job_error", jobs[0], ex.Message);
            lock (gate)
            {
                jobErrors[jobs[0]] = jobErrors.GetValueOrDefault(jobs[0]) + 1;
            }
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
        // the probe ran jobs[0] as the first job of the current cycle; advance exactly as a sample would
        position = 1;
        if (position >= CycleJobs().Length)
        {
            position = 0;
            cycle++;
        }
        return true;
    }

    void Sample(Bus bus, string job, int sampleCycle, DateTime startedAt, double startedElapsed)
    {
        var sw = Stopwatch.StartNew();
        var (sets, exchange) = nativeGapMs is int gap ? bus.RunJobNative(job, "", gap) : (bus.RunJob(job), null);
        Results.EnsureOkay(sets);
        if (exchange != null)
        {
            // native: stamp the moment the final request went out, after the gap wait, opening the port
            // or a resend; elapsed_s from the monotonic clock, so a wall clock step can't move it
            startedAt = exchange.SentAt;
            startedElapsed = Math.Round((clock.Elapsed - Stopwatch.GetElapsedTime(exchange.SentTimestamp)).TotalSeconds, 3);
        }
        session.AddSample(startedAt, startedElapsed, sampleCycle, job, Math.Round(sw.Elapsed.TotalMilliseconds, 1), sets, exchange);
        bool firstFallback = false;
        lock (gate)
        {
            samples[job] = samples.GetValueOrDefault(job) + 1;
            if (nativeGapMs != null && exchange == null)
            {
                fallbacks[job] = fallbacks.GetValueOrDefault(job) + 1;
                firstFallback = fallbacks[job] == 1;
            }
        }
        if (firstFallback)
        {
            session.AddEvent(startedAt, startedElapsed, "not_native", job,
                "the job needs more than one telegram; its samples are taken through EdiabasLib");
        }
    }

    // An unexpected failure inside a step: recorded so the gap it leaves is visible, then a pause.
    public void InternalError(Exception ex)
    {
        backoffUntil = clock.Elapsed.TotalSeconds + 1;
        try
        {
            session.AddEvent(DateTime.UtcNow, Elapsed, "internal_error", null, $"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception)
        {
            // the session itself is failing (e.g. disk full); stderr already has the error
        }
    }

    // Other work held the bus (e.g. a faults read, a bench): no samples were taken meanwhile.
    public void Paused(double seconds, string what)
    {
        // stamped when the pause began
        session.AddEvent(DateTime.UtcNow.AddSeconds(-seconds), Math.Round(Elapsed - seconds, 3), "paused", null,
            $"{what} held the bus for {seconds:F2} s");
    }

    public JsonObject Status()
    {
        lock (gate)
        {
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
                ["samples"] = JsonSerializer.SerializeToNode(samples),
                ["job_errors"] = JsonSerializer.SerializeToNode(jobErrors),
                ["native_fallbacks"] = JsonSerializer.SerializeToNode(fallbacks),
                ["outages"] = currentOutages,
            };
        }
    }

    public JsonObject Finish()
    {
        var summary = Status();
        summary["ended_at"] = DateTime.UtcNow.ToString("o");
        try
        {
            session.SetMeta("ended_at", summary["ended_at"]);
            session.SetMeta("summary", summary);
        }
        finally
        {
            session.Dispose();
        }
        return summary;
    }
}
