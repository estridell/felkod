// Hardware-free checks against upstream's own test SGBD. No BMW files or cable required.
using System.Reflection;
using Felkod.Daemon;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("tests require Linux");
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
string temp = Path.Combine(Path.GetTempPath(), "felkod-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

try
{
    foreach (string job in new[] { "_JOBS", "STATUS_TEST", "status_test", "LESEN_TEST", "FS_LESEN", "IDENT", "IDENT_AIF",
                 "INFO", "ECU_CONFIG" })
        Check(JobRules.IsReadOnlyJob(job), $"{job} read-only");
    foreach (string job in new[] { "STEUERN_TEST", "FS_LOESCHEN", "TEST_PARL", "IDENTX" })
        Check(!JobRules.IsReadOnlyJob(job), $"{job} not read-only");
    // The spike compiles the daemon's rule instead of keeping its own copy.
    Check(File.ReadAllText(Path.Combine(repo, "spike/Spike.csproj")).Contains("Include=\"../daemon/JobRules.cs\""),
        "spike links JobRules.cs");
    Check(!File.ReadAllText(Path.Combine(repo, "spike/Program.cs")).Contains("bool IsReadOnlyJob("), "no spike copy");

    var config = new Config("/nonexistent/felkod-test-port", Path.Combine(repo, "vendor/ediabaslib/EdiabasLib/Test/Ecu"),
        "cmd_test1", temp, Path.Combine(temp, "daemon.sock"), 1);
    var decoder = new SgbdDecoder(config);
    var requests = (System.Collections.IDictionary)typeof(SgbdDecoder)
        .GetField("requests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(decoder)!;
    // ds2.request and ds2.decode pass caller-supplied args; each distinct string must not stay cached.
    for (int i = 0; i < 50; i++)
        decoder.RequestFor("TEST_PARL", i.ToString());
    Check(requests.Count == 0, "requests with args not cached");
    decoder.MarkNotNative("TEST_PARL", "1");
    Check(requests.Count == 0, "not-native with args not cached");
    decoder.RequestFor("INFO");
    decoder.RequestFor("INFO");
    Check(requests.Count == 1 && requests.Contains("INFO"), "args-free request cached once");
    decoder.MarkNotNative("TEST_PARL");
    Check(requests.Count == 2 && requests["TEST_PARL"] == null, "args-free not-native cached");

    Console.WriteLine($"Passed {checks} daemon checks");
}
finally
{
    Directory.Delete(temp, recursive: true);
}
