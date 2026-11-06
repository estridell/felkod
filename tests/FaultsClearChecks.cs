using System.Text.Json.Nodes;
using Felkod.Daemon;

// faults.clear end to end through EdiabasLib, with its simulation answering the DS2 DME test SGBD's
// telegrams from a .sim file (a pty cannot stand in: EdiabasLib's serial port needs DTR and RTS).
static class FaultsClearChecks
{
    const string Sgbd = "dm338ds1_interbyte";
    const string SgbdFile = Sgbd + ".prg";
    // STATUS_UBATT's first data byte times 0.0681 V
    const byte Volts12_26 = 0xB4, Volts8_72 = 0x80;
    static readonly Dictionary<byte, string> Jobs = new() { [0x06] = "STATUS_UBATT", [0x04] = "FS_LESEN", [0x05] = "FS_LOESCHEN" };

    record Outcome(JsonNode? Result, FelkodException? Error, List<string> Sent, List<JsonObject> Audit);

    public static async Task Run(Config testConfig, Action<bool, string> check)
    {
        async Task<Outcome> Clear(JsonObject p, byte volts = Volts12_26, byte clearStatus = 0xA0)
        {
            string dir = Path.Combine(testConfig.DataDir, "faults-clear-" + Guid.NewGuid().ToString("N"));
            string sim = Path.Combine(dir, "sim");
            Directory.CreateDirectory(sim);
            // requests without their checksum, replies with it; one stored fault
            File.WriteAllText(Path.Combine(sim, "obd.sim"), $"""
                [REQUEST]
                UBATT_0=12,09,06,00,00,00,D8,02
                FS_LESEN_0=12,05,04,01
                FS_LOESCHEN_0=12,04,05

                [RESPONSE]
                UBATT_0={Telegram(0x12, 0xA0, volts, 0x00)}
                FS_LESEN_0={Telegram(0x12, 0xA0, 0x01, 0x00, 0x00, 0x00)}
                FS_LOESCHEN_0={Telegram(0x12, clearStatus)}
                """);
            var config = testConfig with { Sgbd = Sgbd, DataDir = dir };
            var catalog = new Catalog(config);
            var bus = new Bus(config, catalog, e =>
            {
                e.SetConfigProperty("Simulation", "1");
                e.SetConfigProperty("SimulationPath", sim);
            });
            bus.Start();
            var handlers = new Handlers(config, bus, catalog, () => { });
            JsonNode? result = null;
            FelkodException? error = null;
            try
            {
                result = await handlers.Call("faults.clear", p, new());
            }
            catch (FelkodException ex)
            {
                error = ex;
            }
            bus.Stop();
            // what went to the ECU, in order, from EdiabasLib's telegram trace
            string trace = Path.Combine(config.TraceDir, "ifh.trc");
            var sent = (File.Exists(trace) ? File.ReadAllLines(trace) : [])
                .Where(line => line.Contains("(Send sim):"))
                .Select(line => Convert.FromHexString(line[(line.IndexOf("):") + 2)..].Replace(" ", "")))
                .Select(request => Jobs.GetValueOrDefault(request[2], Convert.ToHexString(request)))
                .ToList();
            var audit = (File.Exists(config.AuditPath) ? File.ReadAllLines(config.AuditPath) : [])
                .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
            return new Outcome(result, error, sent, audit);
        }

        bool Sent(Outcome outcome, params string[] jobs) => outcome.Sent.SequenceEqual(jobs);
        bool Refused(Outcome outcome, string code) => outcome.Result == null && outcome.Error?.Code == code;

        var cleared = await Clear(new() { ["confirm"] = true });
        check(cleared.Result?["cleared"]?.GetValue<bool>() == true && Sent(cleared, "STATUS_UBATT", "FS_LESEN", "FS_LOESCHEN"),
            "clear reads the voltage, then the faults, then clears");
        check(cleared.Result!["sgbd"]!.GetValue<string>() == SgbdFile && cleared.Result["ubatt_sgbd"]!.GetValue<string>() == SgbdFile
            && cleared.Result["voltage_checked"]!.GetValue<bool>() && Math.Abs(cleared.Result["ubatt_v"]!.GetValue<double>() - 12.258) < 1e-3,
            "clear reports the module and where its voltage came from");
        check(cleared.Audit.Select(entry => entry["stage"]!.GetValue<string>()).SequenceEqual(["sending", "done"])
            && cleared.Audit[0]["sgbd"]!.GetValue<string>() == SgbdFile
            && cleared.Audit[0]["faults_before"]!["count"]!.GetValue<int>() == 1
            && cleared.Audit[0]["fs_lesen_sets"]![0]!["values"]!["F_ANZ_NR"]!.GetValue<long>() == 1,
            "faults saved to the audit before clearing, with all FS_LESEN sets");

        var low = await Clear(new() { ["confirm"] = true }, volts: Volts8_72);
        check(Refused(low, "low_voltage") && low.Error!.Message.Contains("8.72 V") && low.Error.Message.Contains(SgbdFile)
            && Sent(low, "STATUS_UBATT") && low.Audit.Count == 0, "low voltage refused before reading or clearing faults");

        var other = await Clear(new() { ["confirm"] = true, ["voltage_sgbd"] = Sgbd });
        check(other.Result?["ubatt_sgbd"]?.GetValue<string>() == SgbdFile && Sent(other, "STATUS_UBATT", "FS_LESEN", "FS_LOESCHEN")
            && other.Audit[0]["ubatt_sgbd"]!.GetValue<string>() == SgbdFile, "voltage from a named SGBD on its own instance");
        var otherLow = await Clear(new() { ["confirm"] = true, ["voltage_sgbd"] = Sgbd }, volts: Volts8_72);
        check(Refused(otherLow, "low_voltage") && Sent(otherLow, "STATUS_UBATT"), "low voltage from a named SGBD refused");

        // allow_no_voltage only allows a missing source: a source that is named or readable is still used
        var noUbatt = await Clear(new() { ["confirm"] = true, ["voltage_sgbd"] = "cmd_test1", ["allow_no_voltage"] = true });
        check(Refused(noUbatt, "no_voltage_source") && noUbatt.Error!.Message.Contains("cmd_test1.prg") && Sent(noUbatt),
            "named SGBD without STATUS_UBATT refused offline");
        var allowedLow = await Clear(new() { ["confirm"] = true, ["allow_no_voltage"] = true }, volts: Volts8_72);
        check(Refused(allowedLow, "low_voltage") && Sent(allowedLow, "STATUS_UBATT"), "allow_no_voltage still checks a readable voltage");

        check(Refused(await Clear(new() { ["confirm"] = true, ["voltage_sgbd"] = "no_such_sgbd" }), "unknown_sgbd"), "unknown voltage SGBD");
        check(Refused(await Clear(new() { ["confirm"] = true, ["voltage_sgbd"] = "../Ecu/" + Sgbd }), "bad_request"), "voltage SGBD path refused");

        var unconfirmed = await Clear(new());
        check(Refused(unconfirmed, "confirm_required") && unconfirmed.Error!.Message.Contains(SgbdFile)
            && !unconfirmed.Error.Message.Contains("DME") && Sent(unconfirmed), "confirmation names the module, before bus access");

        var rejected = await Clear(new() { ["confirm"] = true }, clearStatus: 0xA2);
        check(Refused(rejected, "ecu_rejected") && Sent(rejected, "STATUS_UBATT", "FS_LESEN", "FS_LOESCHEN")
            && rejected.Audit.Select(entry => entry["stage"]!.GetValue<string>()).SequenceEqual(["sending", "failed"]),
            "a rejected clear is recorded after the saved faults");
    }

    // A DS2 telegram: address, total length, data, XOR checksum.
    static string Telegram(params byte[] body)
    {
        byte[] telegram = [body[0], (byte)(body.Length + 2), .. body[1..]];
        byte checksum = 0;
        foreach (byte b in telegram)
        {
            checksum ^= b;
        }
        return string.Join(",", telegram.Append(checksum).Select(b => b.ToString("X2")));
    }
}
