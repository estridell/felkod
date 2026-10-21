using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Felkod.Daemon;

// One logging session: <sessions>/<id>/session.sqlite (data), meta.json (for cheap listing)
// and ifh.trc (raw telegrams). WAL mode, so readers can query while the log is running.
sealed class Session : IDisposable
{
    const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
        -- one row per successful job execution; t = when the job started (UTC)
        CREATE TABLE samples (id INTEGER PRIMARY KEY, t TEXT NOT NULL, elapsed_s REAL NOT NULL,
                              cycle INTEGER NOT NULL, job TEXT NOT NULL, ms REAL NOT NULL);
        -- the job's results, names exactly as the SGBD returns them; numbers in value, strings in text;
        -- set_index = which result set (1-based; jobs like FS_LESEN return one set per fault)
        CREATE TABLE readings (sample_id INTEGER NOT NULL REFERENCES samples (id), set_index INTEGER NOT NULL,
                               name TEXT NOT NULL, value REAL, text TEXT, unit TEXT);
        CREATE INDEX readings_by_name ON readings (name, sample_id);
        -- the telegram exchange behind a natively transported sample (hex, with checksums; times in ms
        -- from the start of writing the request: echo complete, first reply byte after the echo, reply
        -- complete; waited_ms = gap waited before sending; retries = resends after a garbled reply)
        CREATE TABLE raw (sample_id INTEGER PRIMARY KEY REFERENCES samples (id), request TEXT NOT NULL,
                          response TEXT NOT NULL, echo_ms REAL, first_byte_ms REAL, total_ms REAL, waited_ms REAL,
                          retries INTEGER NOT NULL DEFAULT 0);
        -- link_lost, still_down, link_restored, job_error, not_native, internal_error, paused
        CREATE TABLE events (id INTEGER PRIMARY KEY, t TEXT NOT NULL, elapsed_s REAL NOT NULL,
                             kind TEXT NOT NULL, job TEXT, detail TEXT);
        CREATE VIEW series AS
            SELECT s.t, s.elapsed_s, s.job, r.set_index, r.name, r.value, r.text, r.unit
            FROM readings r JOIN samples s ON s.id = r.sample_id;
        """;

    readonly SqliteConnection db;
    readonly string metaPath;
    readonly JsonObject meta;

    public string Id { get; }
    public string Dir { get; }

    Session(string id, string dir, SqliteConnection db, JsonObject meta)
    {
        Id = id;
        Dir = dir;
        this.db = db;
        this.meta = meta;
        metaPath = Path.Combine(dir, "meta.json");
    }

    public static Session Create(string sessionsDir, JsonObject meta)
    {
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string dir = Path.Combine(sessionsDir, id);
        for (int n = 2; Directory.Exists(dir); n++)
        {
            dir = Path.Combine(sessionsDir, $"{id}-{n}");
        }
        id = Path.GetFileName(dir);
        Directory.CreateDirectory(dir);

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dir, "session.sqlite"),
        }.ToString());
        try
        {
            db.Open();
            Exec(db, Schema);
            meta["id"] = id;
            var session = new Session(id, dir, db, meta);
            foreach (var (key, value) in meta.ToList())
            {
                session.SetMeta(key, value);
            }
            return session;
        }
        catch
        {
            db.Dispose();
            Directory.Delete(dir, recursive: true);
            throw;
        }
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void SetMeta(string key, JsonNode? value)
    {
        meta[key] = value?.DeepClone();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        // every value as JSON text, so "123" (a note) and 123 (a number) stay distinguishable
        object stored = value != null ? value.ToJsonString() : DBNull.Value;
        cmd.Parameters.AddWithValue("$v", stored);
        cmd.ExecuteNonQuery();
        // write-then-rename, so a reader (felkod sessions) never sees a half-written file
        File.WriteAllText(metaPath + ".tmp", meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(metaPath + ".tmp", metaPath, overwrite: true);
    }

    public void AddSample(DateTime t, double elapsed, int cycle, string job, double ms, JsonArray sets, Ds2Exchange? exchange = null)
    {
        using var tx = db.BeginTransaction();
        using var sample = db.CreateCommand();
        sample.CommandText = "INSERT INTO samples (t, elapsed_s, cycle, job, ms) VALUES ($t, $e, $c, $j, $ms) RETURNING id";
        sample.Parameters.AddWithValue("$t", t.ToString("o"));
        sample.Parameters.AddWithValue("$e", elapsed);
        sample.Parameters.AddWithValue("$c", cycle);
        sample.Parameters.AddWithValue("$j", job);
        sample.Parameters.AddWithValue("$ms", ms);
        long id = (long)sample.ExecuteScalar()!;

        using var reading = db.CreateCommand();
        reading.CommandText = "INSERT INTO readings (sample_id, set_index, name, value, text, unit) VALUES ($id, $si, $n, $v, $x, $u)";
        var pName = reading.Parameters.Add("$n", SqliteType.Text);
        var pValue = reading.Parameters.Add("$v", SqliteType.Real);
        var pText = reading.Parameters.Add("$x", SqliteType.Text);
        var pUnit = reading.Parameters.Add("$u", SqliteType.Text);
        reading.Parameters.AddWithValue("$id", id);
        var pSet = reading.Parameters.Add("$si", SqliteType.Integer);
        int setIndex = 0;
        foreach (var set in sets.Skip(1))
        {
            pSet.Value = ++setIndex;
            var obj = set!.AsObject();
            foreach (var (name, node) in obj)
            {
                if (name.EndsWith("_EINH") || name == "JOB_STATUS")
                {
                    continue;
                }
                pName.Value = name;
                pValue.Value = node is JsonValue v && v.TryGetValue<double>(out var d) ? d
                    : node is JsonValue l && l.TryGetValue<long>(out var n) ? n
                    : DBNull.Value;
                pText.Value = node is JsonValue s && s.TryGetValue<string>(out var text) ? text : DBNull.Value;
                pUnit.Value = (object?)Results.UnitOf(obj, name) ?? DBNull.Value;
                reading.ExecuteNonQuery();
            }
        }
        if (exchange != null)
        {
            using var raw = db.CreateCommand();
            raw.CommandText = "INSERT INTO raw (sample_id, request, response, echo_ms, first_byte_ms, total_ms, waited_ms, retries) "
                + "VALUES ($id, $rq, $rs, $e, $f, $t, $w, $r)";
            raw.Parameters.AddWithValue("$id", id);
            raw.Parameters.AddWithValue("$rq", Convert.ToHexString(exchange.Request));
            raw.Parameters.AddWithValue("$rs", Convert.ToHexString(exchange.Response));
            raw.Parameters.AddWithValue("$e", exchange.EchoMs);
            raw.Parameters.AddWithValue("$f", exchange.FirstByteMs);
            raw.Parameters.AddWithValue("$t", exchange.TotalMs);
            raw.Parameters.AddWithValue("$w", exchange.WaitedMs);
            raw.Parameters.AddWithValue("$r", exchange.Retries);
            raw.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void AddEvent(DateTime t, double elapsed, string kind, string? job = null, string? detail = null)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO events (t, elapsed_s, kind, job, detail) VALUES ($t, $e, $k, $j, $d)";
        cmd.Parameters.AddWithValue("$t", t.ToString("o"));
        cmd.Parameters.AddWithValue("$e", elapsed);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$j", (object?)job ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        db.Close();
        db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
