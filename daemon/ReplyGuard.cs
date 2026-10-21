using EdiabasLib;

namespace Felkod.Daemon;

// One rule for both transports, applied to jobs that send a single telegram (multi-telegram jobs
// such as FS_LESEN legitimately get replies whose length varies with the fault count):
// - the reply's DS2 status must be one BMW's JOBRESULT table calls OKAY or "data valid": some SGBD
//   jobs report OKAY (with zeros) for a rejected reply;
// - the reply must be as long as earlier good replies to the same request: the SGBD does not check
//   lengths, so a short reply would decode into invented values.
sealed class ReplyGuard(Catalog catalog)
{
    readonly Dictionary<string, int> lengths = new();
    readonly object gate = new();

    public void Check(byte[] request, byte[] response)
    {
        string text = catalog.StatusText(response[2]);
        if (!Results.IsOkStatus(text))
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
    public List<(byte[] Request, byte[] Response)> Exchanges { get; } = new();

    public override bool TransmitData(byte[] sendData, out byte[] receiveData)
    {
        var request = Ds2Port.FullTelegram(sendData.ToArray());
        bool ok = base.TransmitData(sendData, out receiveData);
        if (ok && receiveData is { Length: >= 4 })
        {
            Exchanges.Add((request, receiveData.ToArray()));
        }
        return ok;
    }
}
