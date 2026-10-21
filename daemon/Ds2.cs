using System.Diagnostics;
using System.IO.Ports;

namespace Felkod.Daemon;

// Times in ms from the start of writing the request: echo complete, first reply byte (after the echo), reply complete.
sealed record Ds2Exchange(byte[] Request, byte[] Response, double EchoMs, double FirstByteMs, double TotalMs, double WaitedMs, int Retries = 0);

// Native DS2 over the K+DCAN cable: 9600 baud 8E1, telegram = address, total length, payload, XOR checksum.
// The cable echoes every byte sent (single-wire K-line), so the echo is read back and checked first.
// EdiabasLib does the same, but sleeps the SGBD's regeneration time (100 ms for MS43) before every
// request; here the gap between a response and the next request is a parameter.
sealed class Ds2Port : IDisposable
{
    const int EchoTimeoutMs = 200;
    // Per byte at 9600 baud with 11 bits per byte (start, 8 data, even parity, stop).
    const double ByteMs = 11 * 1000.0 / 9600;

    // A reply arrived but was garbled (checksum, framing, echo): worth one immediate retry,
    // unlike silence, which means the ECU or cable is gone.
    public const string Corrupt = "link_corrupt";

    readonly SerialPort port;
    long lastResponseEnd;

    public Ds2Port(string path)
    {
        port = new SerialPort(path, 9600, Parity.Even, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            // K-line mode on the K+DCAN cable: both modem lines low, as EdiabasLib leaves them for DS2.
            DtrEnable = false,
            RtsEnable = false,
            ReadTimeout = 5,
            WriteTimeout = 1000,
        };
        try
        {
            port.Open();
        }
        catch (Exception ex)
        {
            port.Dispose();
            throw new FelkodException("link", $"cannot open {path}: {ex.Message}", isLink: true);
        }
    }

    public static byte Checksum(ReadOnlySpan<byte> data)
    {
        byte x = 0;
        foreach (byte b in data)
        {
            x ^= b;
        }
        return x;
    }

    // Sends a complete telegram (with checksum) and returns the ECU's reply (with checksum).
    // Throws FelkodException with IsLink for no echo, no reply, or a corrupt reply.
    public Ds2Exchange Transact(byte[] request, int gapMs, int firstByteTimeoutMs, int interByteTimeoutMs)
    {
        var gap = Stopwatch.StartNew();
        while (lastResponseEnd != 0 && Stopwatch.GetElapsedTime(lastResponseEnd).TotalMilliseconds < gapMs)
        {
            Thread.Sleep(1);
        }
        double waitedMs = gap.Elapsed.TotalMilliseconds;

        try
        {
            port.DiscardInBuffer();
            var clock = Stopwatch.StartNew();
            port.Write(request, 0, request.Length);
            var echo = ReadExactly(request.Length, EchoTimeoutMs + request.Length * ByteMs);
            if (echo == null || !echo.AsSpan().SequenceEqual(request))
            {
                throw echo == null
                    ? new FelkodException("link", "no echo from the cable (is the switch on K and the cable on the OBD port?)", isLink: true)
                    : new FelkodException(Corrupt, $"echo mismatch: sent {Convert.ToHexString(request)}, got {Convert.ToHexString(echo)}", isLink: true);
            }
            double echoMs = clock.Elapsed.TotalMilliseconds;

            var head = ReadExactly(2, firstByteTimeoutMs);
            if (head == null)
            {
                throw new FelkodException("link", "no response from the ECU", isLink: true);
            }
            double firstByteMs = clock.Elapsed.TotalMilliseconds - echoMs;
            int length = head[1];
            if (length < 4 || head[0] != request[0])
            {
                throw new FelkodException(Corrupt, $"not a DS2 reply: {Convert.ToHexString(head)}", isLink: true);
            }
            var tail = ReadExactly(length - 2, (length - 2) * ByteMs + interByteTimeoutMs);
            if (tail == null)
            {
                throw new FelkodException(Corrupt, $"reply cut short after {Convert.ToHexString(head)}", isLink: true);
            }
            var response = new byte[length];
            head.CopyTo(response, 0);
            tail.CopyTo(response, 2);
            if (Checksum(response.AsSpan(0, length - 1)) != response[^1])
            {
                throw new FelkodException(Corrupt, $"checksum error in reply {Convert.ToHexString(response)}", isLink: true);
            }
            lastResponseEnd = Stopwatch.GetTimestamp();
            return new Ds2Exchange(request, response, Math.Round(echoMs, 2), Math.Round(firstByteMs, 2),
                Math.Round(clock.Elapsed.TotalMilliseconds, 2), Math.Round(waitedMs, 2));
        }
        catch (FelkodException)
        {
            // something may have been on the wire: the gap before the next request counts from now
            lastResponseEnd = Stopwatch.GetTimestamp();
            throw;
        }
        catch (Exception ex)
        {
            // write timeouts, a yanked device, a closed port: all mean the link is gone
            throw new FelkodException("link", $"serial port: {ex.GetType().Name}: {ex.Message}", isLink: true);
        }
    }

    // Reads and drops bytes until the line has been quiet for quietMs (at most maxMs): the rest of a
    // garbled reply must not be taken as the start of the next one.
    public void DrainUntilIdle(int quietMs = 20, int maxMs = 300)
    {
        var buffer = new byte[256];
        var total = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        while (quiet.ElapsedMilliseconds < quietMs && total.ElapsedMilliseconds < maxMs)
        {
            try
            {
                if (port.Read(buffer, 0, buffer.Length) > 0)
                {
                    quiet.Restart();
                }
            }
            catch (TimeoutException)
            {
                // nothing arrived within the read timeout
            }
        }
        lastResponseEnd = Stopwatch.GetTimestamp();
    }

    // Reads count bytes within timeoutMs; null on timeout.
    byte[]? ReadExactly(int count, double timeoutMs)
    {
        var buffer = new byte[count];
        int read = 0;
        var clock = Stopwatch.StartNew();
        while (read < count)
        {
            if (clock.Elapsed.TotalMilliseconds > timeoutMs)
            {
                return null;
            }
            try
            {
                read += port.Read(buffer, read, count - read);
            }
            catch (TimeoutException)
            {
                // poll again until the deadline
            }
        }
        return buffer;
    }

    public void Dispose()
    {
        try
        {
            port.Close();
        }
        catch (IOException)
        {
            // device already gone
        }
        port.Dispose();
    }
}
