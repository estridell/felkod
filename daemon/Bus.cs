using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using EdiabasLib;

namespace Felkod.Daemon;

// What the daemon last saw of the K-line, for `status` without touching the bus.
sealed class LinkState
{
    readonly object gate = new();
    string state = "unknown";
    DateTime? lastOk;
    DateTime? lastErrorAt;
    string? lastError;

    public void Ok()
    {
        lock (gate)
        {
            state = "up";
            lastOk = DateTime.UtcNow;
        }
    }

    public void Failed(string error, bool isLink)
    {
        lock (gate)
        {
            if (isLink)
            {
                state = "down";
            }
            lastError = error;
            lastErrorAt = DateTime.UtcNow;
        }
    }

    public JsonObject Snapshot()
    {
        lock (gate)
        {
            return new JsonObject
            {
                ["state"] = state,
                ["last_ok"] = lastOk?.ToString("o"),
                ["last_error"] = lastError,
                ["last_error_at"] = lastErrorAt?.ToString("o"),
            };
        }
    }
}

// The one thread that talks to the ECU. Requests queue up and run between logging steps,
// so a running log never blocks `faults read` or `ident` for longer than one job.
sealed class Bus : IDisposable
{
    record Work(Func<JsonNode?> Run, TaskCompletionSource<JsonNode?> Done);

    // DS2's first-byte timeout as the MS43 SGBD sets it (ms), and the gap allowed between reply bytes.
    const int Ds2FirstByteTimeoutMs = 2000;
    const int Ds2InterByteTimeoutMs = 20;

    readonly Config config;
    readonly SgbdDecoder decoder;
    readonly BlockingCollection<Work> queue = new();
    readonly Thread worker;
    readonly Stopwatch jobClock = new();
    volatile bool shuttingDown;
    EdiabasNet? ediabas;
    // Native DS2 and EdiabasLib take turns on the port: opening one closes the other.
    Ds2Port? native;
    bool needsReset = true;
    string traceDir;

    public LinkState Link { get; } = new();
    public LogRun? Log { get; private set; }

    public Bus(Config config, SgbdDecoder decoder)
    {
        this.config = config;
        this.decoder = decoder;
        traceDir = config.TraceDir;
        Directory.CreateDirectory(traceDir);
        worker = new Thread(Loop) { Name = "felkod-bus", IsBackground = true };
    }

    public void Start() => worker.Start();

    public Task<JsonNode?> Submit(Func<JsonNode?> run)
    {
        var done = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (shuttingDown)
        {
            done.SetException(new FelkodException("shutting_down", "felkodd is shutting down"));
        }
        else
        {
            queue.Add(new Work(run, done));
        }
        return done.Task;
    }

    void Loop()
    {
        while (!shuttingDown)
        {
            if (queue.TryTake(out var work))
            {
                Execute(work);
                continue;
            }
            if (Log is LogRun log)
            {
                if (log.Finished)
                {
                    FinishLog();
                    continue;
                }
                bool worked;
                try
                {
                    worked = log.Step(this);
                }
                catch (Exception ex)
                {
                    // A bug in one step must not take down the daemon or lose the session; it is
                    // recorded in the session so the gap it leaves is visible.
                    Console.Error.WriteLine($"felkodd: logging step failed: {ex}");
                    log.InternalError(ex);
                    worked = false;
                }
                if (!worked && queue.TryTake(out work, 100))
                {
                    Execute(work);
                }
                continue;
            }
            if (queue.TryTake(out work, 250))
            {
                Execute(work);
            }
        }
        if (Log != null)
        {
            try
            {
                FinishLog();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"felkodd: finishing the log failed: {ex}");
            }
        }
        while (queue.TryTake(out var pending))
        {
            pending.Done.TrySetException(new FelkodException("shutting_down", "felkodd is shutting down"));
        }
        DisposeInstance();
        CloseNative();
    }

    static void Execute(Work work)
    {
        try
        {
            work.Done.TrySetResult(work.Run());
        }
        catch (Exception ex)
        {
            work.Done.TrySetException(ex);
        }
    }

    // Runs one SGBD job on the worker thread. Throws FelkodException: IsLink for communication
    // failures and deadline overruns, which also force a fresh EdiabasLib instance for the next job.
    public JsonArray RunJob(string job, string args = "")
    {
        EnsureInstance();
        var e = ediabas!;
        e.ArgString = args;
        e.ArgBinaryStd = null;
        e.ResultsRequests = "";
        jobClock.Restart();
        Exception? failure = null;
        try
        {
            e.ExecuteJob(job);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        jobClock.Stop();

        if (shuttingDown)
        {
            throw new FelkodException("shutting_down", "felkodd is shutting down");
        }
        if (jobClock.Elapsed.TotalSeconds > config.JobTimeoutS)
        {
            needsReset = true;
            Link.Failed("timeout", isLink: true);
            throw new FelkodException("timeout", $"{job}: no result within {config.JobTimeoutS} s", isLink: true);
        }
        if (failure != null)
        {
            string code = e.ErrorCodeLast.ToString();
            bool isLink = code.StartsWith("EDIABAS_IFH_");
            string message = code != "EDIABAS_ERR_NONE" ? code : failure.Message;
            if (isLink)
            {
                needsReset = true;
            }
            Link.Failed(message, isLink);
            throw new FelkodException(isLink ? "link" : code == "EDIABAS_SYS_0008" ? "unknown_job" : "ediabas", message, isLink);
        }
        if (!job.StartsWith('_'))
        {
            Link.Ok();
        }
        return Results.ToJson(e.ResultSets);
    }

    // Logging sessions trace into their own folder; switching needs a new instance.
    public void SetTraceDir(string dir)
    {
        traceDir = dir;
        needsReset = true;
    }

    public void ForceReset()
    {
        needsReset = true;
        CloseNative();
    }

    // Runs a job with native DS2 transport and SGBD decoding when the job sends a single telegram;
    // otherwise (or if the decode does not fit) through EdiabasLib. Returns the raw exchange if native.
    public (JsonArray Sets, Ds2Exchange? Raw) RunJobNative(string job, string args, int gapMs)
    {
        var request = decoder.RequestFor(job, args);
        if (request == null)
        {
            return (RunJob(job, args), null);
        }
        var exchange = RawExchange(request, gapMs);
        byte status = exchange.Response[2];
        if (status == 0xA1)
        {
            // The SGBD answers BUSY by sending the telegram again; report it rather than decode it.
            throw new FelkodException("ecu_rejected", "JOB_STATUS=BUSY (DS2 status A1)");
        }
        var sets = decoder.Decode(job, args, request, exchange.Response);
        if (sets != null)
        {
            return (sets, exchange);
        }
        if (status != 0xA0)
        {
            throw new FelkodException("ecu_rejected", $"DS2 status {status:X2}; the SGBD could not decode the reply");
        }
        // An OK reply the job could not finish on: it really needs more than one telegram.
        decoder.MarkNotNative(job, args);
        return (RunJob(job, args), null);
    }

    // One telegram on the wire, native. IsLink failures close the port so the next call reopens it.
    public Ds2Exchange RawExchange(byte[] request, int gapMs)
    {
        if (shuttingDown)
        {
            throw new FelkodException("shutting_down", "felkodd is shutting down");
        }
        DisposeInstance();
        try
        {
            native ??= new Ds2Port(config.Port);
            Ds2Exchange exchange;
            try
            {
                exchange = native.Transact(request, gapMs, Ds2FirstByteTimeoutMs, Ds2InterByteTimeoutMs);
            }
            catch (FelkodException ex) when (ex.Code == Ds2Port.Corrupt)
            {
                // one garbled reply is noise, not a lost link: try once more (input is flushed first)
                exchange = native.Transact(request, gapMs, Ds2FirstByteTimeoutMs, Ds2InterByteTimeoutMs);
            }
            Link.Ok();
            return exchange;
        }
        catch (FelkodException ex) when (ex.IsLink)
        {
            CloseNative();
            Link.Failed(ex.Message, isLink: true);
            throw;
        }
    }

    void CloseNative()
    {
        native?.Dispose();
        native = null;
    }

    void EnsureInstance()
    {
        if (native != null)
        {
            CloseNative();
            needsReset = true;
        }
        if (ediabas != null && !needsReset)
        {
            return;
        }
        DisposeInstance();
        var e = new EdiabasNet();
        e.EdInterfaceClass = new EdInterfaceObd { ComPort = config.Port };
        e.SetConfigProperty("EcuPath", config.EcuPath);
        e.SetConfigProperty("TracePath", traceDir);
        e.SetConfigProperty("AppendTrace", "1");
        e.SetConfigProperty("ApiTrace", "0");
        // Level 1 keeps the raw telegrams (replay fixtures) without the ~14 MB/s interpreter trace.
        e.SetConfigProperty("IfhTrace", "1");
        e.SetConfigProperty("IfhTraceBuffering", "0");
        e.NoInitForVJobs = true;
        e.AbortJobFunc = () => shuttingDown || (jobClock.IsRunning && jobClock.Elapsed.TotalSeconds > config.JobTimeoutS);
        e.ResolveSgbdFile(config.Sgbd);
        ediabas = e;
        needsReset = false;
    }

    void DisposeInstance()
    {
        try
        {
            // Closes EdiabasLib's static serial port and stops its foreground comm thread.
            ediabas?.Dispose();
        }
        catch (Exception)
        {
            // a half-dead instance has nothing left to save
        }
        ediabas = null;
    }

    public void StartLog(LogRun run)
    {
        if (Log != null)
        {
            throw new FelkodException("log_running", $"session {Log.Id} is already logging; stop it first");
        }
        SetTraceDir(run.Dir);
        Log = run;
    }

    public JsonObject StopLog()
    {
        if (Log == null)
        {
            throw new FelkodException("no_log", "no logging session is running");
        }
        Log.RequestStop();
        return FinishLog();
    }

    JsonObject FinishLog()
    {
        var run = Log!;
        Log = null;
        var summary = run.Finish();
        SetTraceDir(config.TraceDir);
        return summary;
    }

    public void Stop()
    {
        shuttingDown = true;
        worker.Join(TimeSpan.FromSeconds(config.JobTimeoutS + 5));
    }

    public void Dispose() => Stop();
}

// Offline access to the SGBD's own metadata (_JOBS, _RESULTS, ...). A separate instance so job
// listings never wait behind a running log. It never connects, and it is never disposed:
// disposing any EdInterfaceObd closes the serial port shared by all instances.
sealed class Catalog
{
    readonly object gate = new();
    readonly EdiabasNet ediabas;
    HashSet<string>? jobNames;

    public Catalog(Config config)
    {
        ediabas = new EdiabasNet();
        ediabas.EdInterfaceClass = new EdInterfaceObd { ComPort = "none" };
        ediabas.SetConfigProperty("EcuPath", config.EcuPath);
        ediabas.SetConfigProperty("ApiTrace", "0");
        ediabas.SetConfigProperty("IfhTrace", "0");
        ediabas.NoInitForVJobs = true;
        ediabas.ResolveSgbdFile(config.Sgbd);
    }

    public string SgbdFile => ediabas.SgbdFileName;

    public JsonArray Run(string vjob, string args = "")
    {
        lock (gate)
        {
            ediabas.ArgString = args;
            ediabas.ArgBinaryStd = null;
            ediabas.ResultsRequests = "";
            ediabas.ExecuteJob(vjob);
            return Results.ToJson(ediabas.ResultSets);
        }
    }

    public HashSet<string> JobNames()
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
}
