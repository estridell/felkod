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
    public bool Failed { get; private set; }

    public void Capture()
    {
        capturing = true;
        Captured.Clear();
        expectedRequest = null;
        pendingResponse = null;
        Failed = false;
    }

    public void Answer(byte[] request, byte[] response)
    {
        capturing = false;
        expectedRequest = request;
        pendingResponse = response;
        Failed = false;
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
        if (pendingResponse == null || !request.AsSpan().SequenceEqual(expectedRequest))
        {
            Failed = true;
            Ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0009);
            return false;
        }
        receiveData = pendingResponse;
        pendingResponse = null;
        return true;
    }

    public override bool IsValidInterfaceName(string name) => name == "DECODE";
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
