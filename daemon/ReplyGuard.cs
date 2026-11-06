using EdiabasLib;

namespace Felkod.Daemon;

// One rule for both transports, applied to jobs that send a single telegram (multi-telegram jobs
// such as FS_LESEN legitimately get replies whose length varies with the fault count):
// - for DS1/DS2 (concept 0x0005/0x0006), the reply's status byte must be one BMW's JOBRESULT table
//   calls OKAY or "data valid": some SGBD jobs report OKAY (with zeros) for a rejected reply. Other
//   concepts, and KWP2000* frames that an SGBD sends over the DS2 concept itself (the E46 DSC MK60
//   does: B8 29 F1 .. / B8 F1 29 ..), have no status byte there (byte 2 is the ECU's address), so
//   for them only the SGBD's own JOB_STATUS decides;
// - the reply must be as long as earlier good replies to the same request: the SGBD does not check
//   lengths, so a short reply would decode into invented values.
sealed class ReplyGuard(Catalog catalog)
{
    readonly Dictionary<string, int> lengths = new();
    readonly object gate = new();

    // Whether a reply under this EdiabasLib CommParameter carries a DS2-style status in byte 2.
    // Unknown (no parameters yet) counts as DS2, the stricter choice.
    public static bool HasDs2Status(uint[]? commParameter) =>
        commParameter is not { Length: > 0 } || commParameter[0] is 0x0005 or 0x0006;

    // A KWP2000* exchange: format byte with address mode, tester F1 as source, and the reply addressed
    // back to F1 from the ECU the request went to. A DS2 reply would need a length of 0xF1 and a status
    // equal to the request's length byte to look like this.
    public static bool IsKwp2000StarExchange(byte[] request, byte[] response) =>
        request.Length >= 4 && response.Length >= 4 && request[0] >= 0x80 && request[2] == 0xF1
        && response[0] >= 0x80 && response[1] == 0xF1 && response[2] == request[1];

    public void Check(byte[] request, byte[] response, bool ds2Status = true)
    {
        if (IsKwp2000StarExchange(request, response))
        {
            // The frame carries its own length (format, target, source, length, data, checksum), and one
            // request can legitimately get replies of different lengths: FS_LESEN is a single frame whose
            // length grows with the fault count. So check the frame against itself, not earlier replies.
            int framed = response[3] + 5;
            if (response.Length != framed)
            {
                throw new FelkodException("ecu_reply_length",
                    $"reply is {response.Length} bytes, its KWP2000* length byte says {framed}");
            }
            return;
        }
        string text = catalog.StatusText(response[2]);
        if (ds2Status && !Results.IsOkStatus(text))
        {
            throw new FelkodException("ecu_rejected", $"JOB_STATUS={text} (DS2 status {response[2]:X2})");
        }
        lock (gate)
        {
            if (lengths.TryGetValue(Convert.ToHexString(request), out int expected) && expected != response.Length)
            {
                throw new FelkodException("ecu_reply_length",
                    $"reply is {response.Length} bytes, earlier replies to this request were {expected}");
            }
        }
    }

    // Called once a reply has decoded: later replies to this request must match its length.
    public void Learn(byte[] request, byte[] response)
    {
        lock (gate)
        {
            lengths.TryAdd(Convert.ToHexString(request), response.Length);
        }
    }
}

// EdiabasLib's serial interface, recording each telegram exchange of the running job so the
// ReplyGuard can check jobs that go through EdiabasLib too.
sealed class ObservedObd : EdInterfaceObd
{
    public List<(byte[] Request, byte[] Response, bool Ds2Status)> Exchanges { get; } = new();

    public override bool TransmitData(byte[] sendData, out byte[] receiveData)
    {
        var request = Ds2Port.FullTelegram(sendData.ToArray());
        bool ok = base.TransmitData(sendData, out receiveData);
        if (ok && receiveData is { Length: >= 4 })
        {
            Exchanges.Add((request, receiveData.ToArray(), ReplyGuard.HasDs2Status(CommParameter)));
        }
        return ok;
    }
}
