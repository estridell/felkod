namespace Felkod.Daemon;

// Which jobs run without allow_write.
static class JobRules
{
    // EDIABAS's built-in metadata jobs: they read the SGBD file itself and never talk to the ECU.
    public static bool IsMetadataJob(string job) => job.ToUpperInvariant() is
        "_JOBS" or "_JOBCOMMENTS" or "_ARGUMENTS" or "_RESULTS" or "_VERSIONINFO" or "_TABLES" or "_TABLE";

    // Jobs that only read. Anything else (STEUERN_*, *_LOESCHEN, *_PROGRAMMIEREN, other _* jobs, ...)
    // may change the car.
    public static bool IsReadOnlyJob(string job)
    {
        string name = job.ToUpperInvariant();
        return IsMetadataJob(name) || name.StartsWith("STATUS_") || name.StartsWith("LESEN_") || name.EndsWith("_LESEN")
            || name is "IDENT" or "IDENT_AIF" or "INFO" or "ECU_CONFIG";
    }
}
