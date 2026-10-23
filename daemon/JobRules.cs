namespace Felkod.Daemon;

// Which jobs run without allow_write. Compiled into the spike too (spike/Spike.csproj), so both agree.
static class JobRules
{
    // Jobs that only read. Anything else (STEUERN_*, *_LOESCHEN, *_PROGRAMMIEREN, ...) changes the car.
    public static bool IsReadOnlyJob(string job)
    {
        string name = job.ToUpperInvariant();
        return name.StartsWith('_') || name.StartsWith("STATUS_") || name.StartsWith("LESEN_") || name.EndsWith("_LESEN")
            || name is "IDENT" or "IDENT_AIF" or "INFO" or "ECU_CONFIG";
    }
}
