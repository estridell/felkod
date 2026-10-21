using System.Text.Json.Nodes;
using EdiabasLib;

namespace Felkod.Daemon;

// An EdiabasLib interface with no hardware behind it. In capture mode it records the telegrams an SGBD
// job sends, answering each with a dummy "OK" reply so a job that needs more than one telegram goes on
// to send the next; in decode mode it answers the job's one telegram with a reply captured on the wire.
// Running the job against it lets the SGBD itself scale and name the values, so natively transported
// data is decoded exactly as an EdiabasLib job would decode it.
sealed class DecodeInterface : EdInterfaceBase
{
    const int MaxCaptured = 5;

    public List<byte[]> Captured { get; } = new();
    bool capturing;
    byte[]? expectedRequest;
    byte[]? pendingResponse;
    public string? Problem { get; private set; }

    public void Capture()
    {
        capturing = true;
        Captured.Clear();
        expectedRequest = null;
        pendingResponse = null;
        Problem = null;
    }

    public void Answer(byte[] request, byte[] response)
    {
        capturing = false;
        expectedRequest = request;
        pendingResponse = response;
        Problem = null;
    }

    public override bool TransmitData(byte[] sendData, out byte[] receiveData)
    {
        receiveData = [];
        var request = Ds2Port.FullTelegram(sendData);
        if (capturing)
        {
            Captured.Add(request);
            if (Captured.Count > MaxCaptured)
            {
                Ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0009);
                return false;
            }
            // address, length, 0xA0 (acknowledge), 32 zero bytes, checksum
            var dummy = new byte[36];
            dummy[0] = request[0];
            dummy[1] = (byte)dummy.Length;
            dummy[2] = 0xA0;
            dummy[^1] = Ds2Port.Checksum(dummy.AsSpan(0, dummy.Length - 1));
            receiveData = dummy;
            return true;
        }
        if (pendingResponse == null)
        {
            Problem = "job sends more than one telegram";
            Ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0009);
            return false;
        }
        if (!request.AsSpan().SequenceEqual(expectedRequest))
        {
            Problem = $"job sent {Convert.ToHexString(request)}, expected {Convert.ToHexString(expectedRequest!)}";
            Ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0009);
            return false;
        }
        receiveData = pendingResponse;
        pendingResponse = null;
        return true;
    }

    public override bool IsValidInterfaceName(string name) => name == "DECODE";
    public override bool InterfaceConnect()
    {
        base.InterfaceConnect();
        return true;
    }
    public override bool InterfaceReset() => true;
    public override bool InterfaceBoot() => true;
    public override bool TransmitFrequent(byte[] sendData) => false;
    public override bool ReceiveFrequent(out byte[] receiveData)
    {
        receiveData = [];
        return false;
    }
    public override bool StopFrequent() => true;
    public override bool RawData(byte[] sendData, out byte[] receiveData)
    {
        receiveData = [];
        return false;
    }
    public override bool TransmitCancel(bool cancel) => true;
    public override bool BmwFastProtocol => false;
    public override string InterfaceType => "DECODE";
    public override uint InterfaceVersion => 0;
    public override string InterfaceName => "DECODE";
    public override byte[] KeyBytes => [];
    public override byte[] State => [];
    public override uint LoopTest => 0;
    // Not measured here; the SGBD's decoding of a captured reply does not depend on them.
    public override long BatteryVoltage => -1;
    public override long IgnitionVoltage => -1;
    public override int? AdapterVersion => null;
    public override byte[] AdapterSerial => [];
    public override double? AdapterVoltage => null;
    public override long GetPort(uint index) => 0;
    public override void SetPort(uint index, uint value) { }
    public override void SetProgramVoltage(uint voltage) { }
    public override void SwitchSiRelais(uint time) { }
    public override bool Connected => true;
    public override bool IsEcuConnected => true;
}

// Runs SGBD jobs against DecodeInterface: learns the single telegram a job sends, then decodes
// replies captured by Ds2Port. Jobs that send more than one telegram are reported as not native.
sealed class SgbdDecoder
{
    readonly EdiabasNet ediabas;
    readonly DecodeInterface decode = new();
    readonly Dictionary<string, byte[]?> requests = new();
    // Used by the bus thread (native logging) and by RPC threads (offline decode).
    readonly object gate = new();

    public SgbdDecoder(Config config)
    {
        lock (Sgbd.Gate)
        {
            ediabas = new EdiabasNet();
            ediabas.EdInterfaceClass = decode;
            ediabas.SetConfigProperty("EcuPath", config.EcuPath);
            ediabas.SetConfigProperty("ApiTrace", "0");
            ediabas.SetConfigProperty("IfhTrace", "0");
            ediabas.ResolveSgbdFile(config.Sgbd);
        }
    }

    // The telegram a job sends, or null if it sends none or more than one (not usable natively).
    public byte[]? RequestFor(string job, string args = "")
    {
        lock (gate)
        {
            return RequestForLocked(job, args);
        }
    }

    byte[]? RequestForLocked(string job, string args)
    {
        string key = job + "\0" + args;
        if (requests.TryGetValue(key, out var known))
        {
            return known;
        }
        decode.Capture();
        lock (Sgbd.Gate)
        {
            Execute(job, args);
        }
        byte[]? request = decode.Captured.Count == 1 ? decode.Captured[0] : null;
        requests[key] = request;
        return request;
    }

    public void MarkNotNative(string job, string args = "")
    {
        lock (gate)
        {
            requests[job + "\0" + args] = null;
        }
    }

    // Decodes a captured reply into the job's result sets; null if the job did not fit it
    // (e.g. it wanted another telegram). Whether that makes the job not native is the caller's call.
    public JsonArray? Decode(string job, string args, byte[] request, byte[] response)
    {
        lock (gate)
        {
            decode.Answer(request, response);
            lock (Sgbd.Gate)
            {
                bool ok = Execute(job, args);
                return ok && decode.Problem == null ? Results.ToJson(ediabas.ResultSets) : null;
            }
        }
    }

    bool Execute(string job, string args)
    {
        ediabas.ArgString = args;
        ediabas.ArgBinaryStd = null;
        ediabas.ResultsRequests = "";
        try
        {
            ediabas.ExecuteJob(job);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
