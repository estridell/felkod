// felkodd: the felkod daemon. Owns the K-line to the car, runs SGBD jobs through EdiabasLib and
// logging sessions into SQLite, and serves the felkod CLI over a unix socket (see Rpc.cs).
using System.Runtime.InteropServices;
using Felkod.Daemon;

var config = Config.FromArgs(args);
if (config == null)
{
    return 2;
}

// EdiabasLib reads SGBD text as Windows-1252, which .NET only ships as an opt-in provider.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
Directory.CreateDirectory(config.DataDir);
Directory.CreateDirectory(config.SessionsDir);

var shutdown = new ManualResetEventSlim();
// The registrations must stay referenced, or the GC unregisters them.
var signalHandlers = new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP }
    .Select(signal => PosixSignalRegistration.Create(signal, context =>
    {
        context.Cancel = true;
        shutdown.Set();
    }))
    .ToList();

var catalog = new Catalog(config);
var decoder = new SgbdDecoder(config);
var bus = new Bus(config, decoder);
var server = new RpcServer(config.SocketPath, new Handlers(config, bus, catalog, decoder, shutdown.Set));
try
{
    server.Start();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"felkodd: {ex.Message}");
    return 1;
}
bus.Start();
Console.Error.WriteLine($"felkodd {Environment.ProcessId}: listening on {config.SocketPath}, port {config.Port}, sgbd {catalog.SgbdFile}");

shutdown.Wait();
Console.Error.WriteLine("felkodd: shutting down");
server.Stop();
// Finishes a running log (summary written, session closed) and closes the serial port.
bus.Stop();
GC.KeepAlive(signalHandlers);
// Exit explicitly: an EdiabasLib comm thread stuck in serial I/O must not keep the process alive.
Environment.Exit(0);
return 0;
