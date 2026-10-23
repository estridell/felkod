using Felkod.Daemon;

static class TransportChecks
{
    public static void Run(Config config, Catalog catalog, SgbdDecoder decoder, Action<bool, string> check)
    {
        // Config permits any finite positive deadline. Thread.Join accepts at most Int32.MaxValue
        // milliseconds, and a very large number of seconds cannot even become a TimeSpan.
        foreach (double seconds in new[] { 3_000_000.0, 1e300 })
        {
            using var bus = new Bus(config with { JobTimeoutS = seconds }, decoder, catalog);
            bus.Start();
            bus.Stop();
            check(true, $"idle bus stops with job timeout {seconds}");
        }
    }
}
