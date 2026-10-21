using System.Globalization;
using System.Text.Json.Nodes;
using EdiabasLib;

namespace Felkod.Daemon;

// A failed request, with a stable code the CLI passes through to agents.
// Link errors (cable or ECU gone) are what logging treats as an outage.
sealed class FelkodException(string code, string message, bool isLink = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool IsLink { get; } = isLink;
}

static class Results
{
    // EdiabasLib result sets as JSON. Set 0 is EDIABAS's own bookkeeping (variant, job name, ...).
    public static JsonArray ToJson(List<Dictionary<string, EdiabasNet.ResultData>> resultSets)
    {
        var sets = new JsonArray();
        foreach (var set in resultSets)
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

    // The SGBD reports ECU rejections (negative responses) through JOB_STATUS, with the value
    // results left as zeros or garbage; such results must never be treated as data.
    public static void EnsureOkay(JsonArray sets)
    {
        // set 0 is EDIABAS's own; a job that returned nothing beyond it produced no data to trust
        if (sets.Count < 2)
        {
            throw new FelkodException("no_results", "the job returned no result sets");
        }
        foreach (var set in sets)
        {
            if (set!["JOB_STATUS"] is JsonNode status && !IsOkStatus(status.GetValue<string>()))
            {
                throw new FelkodException("ecu_rejected", $"JOB_STATUS={status.GetValue<string>()}");
            }
        }
    }

    // OKAY, or BMW's "function ended; data valid" (FUNCTION BEENDET; DATEN GUELTIG) for system checks;
    // not "...UNGUELTIG" (invalid).
    public static bool IsOkStatus(string text) =>
        text == "OKAY" || (text.Contains("GUELTIG") && !text.Contains("UNGUELTIG"));

    // Job results without set 0, each split into values and units. The SGBD pairs a value result
    // NAME_WERT with its unit NAME_EINH; units are moved out so every value stays a plain scalar.
    public static JsonArray ValueSets(JsonArray sets)
    {
        var output = new JsonArray();
        foreach (var set in sets.Skip(1))
        {
            var values = new JsonObject();
            var units = new JsonObject();
            foreach (var (name, value) in set!.AsObject())
            {
                if (name.EndsWith("_EINH"))
                {
                    continue;
                }
                values[name] = value?.DeepClone();
                if (UnitOf(set.AsObject(), name) is string unit)
                {
                    units[name] = unit;
                }
            }
            output.Add(new JsonObject { ["values"] = values, ["units"] = units });
        }
        return output;
    }

    public static string? UnitOf(JsonObject set, string name) =>
        name.EndsWith("_WERT") && set[name[..^"_WERT".Length] + "_EINH"] is JsonNode unit ? unit.GetValue<string>() : null;

    // Jobs that only read. Anything else (STEUERN_*, *_LOESCHEN, *_PROGRAMMIEREN, ...) changes the car.
    public static bool IsMetadataJob(string job) => job.ToUpperInvariant() is
        "_JOBS" or "_JOBCOMMENTS" or "_ARGUMENTS" or "_RESULTS" or "_VERSIONINFO" or "_TABLES" or "_TABLE";

    public static bool IsReadOnlyJob(string job)
    {
        string name = job.ToUpperInvariant();
        return IsMetadataJob(name) || name.StartsWith("STATUS_") || name.StartsWith("LESEN_") || name.EndsWith("_LESEN")
            || name is "IDENT" or "IDENT_AIF" or "INFO" or "ECU_CONFIG";
    }
}
