using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Hr.Infrastructure.Devices;

/// <summary>
/// One connected conversation with a terminal. Owns the socket, the session id the
/// device hands out on connect, and the reply counter both sides use to pair
/// requests with responses.
/// </summary>
internal sealed class ZkSession : IDisposable
{
    private const int MaxPayload = 4 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ILogger _logger;

    private ushort _sessionId;
    private ushort _replyId;
    private bool _deviceDisabled;

    private ZkSession(TcpClient client, ILogger logger)
    {
        _client = client;
        _stream = client.GetStream();
        _logger = logger;
    }

    public static async Task<ZkSession> OpenAsync(
        string host, int port, int commKey, ILogger logger, CancellationToken ct)
    {
        var client = new TcpClient { ReceiveTimeout = 15_000, SendTimeout = 15_000 };

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(host, port, connectTimeout.Token);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new ZkDeviceException(
                $"Could not reach the terminal at {host}:{port}. " +
                "Check it is powered on, on the same network, and that TCP 4370 is open.", ex);
        }

        var session = new ZkSession(client, logger);

        try
        {
            var reply = await session.ExchangeAsync(ZkCommand.Connect, [], ct);

            // The device answers with the session id it wants used from now on.
            session._sessionId = reply.SessionId;

            if (reply.Command == ZkCommand.AckUnauth)
            {
                // Terminal has a comm key set; answer the challenge.
                var key = MakeCommKey(commKey, session._sessionId);
                var auth = await session.ExchangeAsync(ZkCommand.Auth, key, ct);
                if (auth.Command != ZkCommand.AckOk)
                    throw new ZkDeviceException(
                        "The terminal rejected the comm key. Check the device's " +
                        "Comm Key setting matches the one configured here.");
            }
            else if (reply.Command != ZkCommand.AckOk)
            {
                throw new ZkDeviceException(
                    $"The terminal refused the connection (code {reply.Command}).");
            }

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public async Task DisableDeviceAsync(CancellationToken ct)
    {
        await CommandAsync(ZkCommand.DisableDevice, ct);
        _deviceDisabled = true;
    }

    public async Task EnableDeviceAsync(CancellationToken ct)
    {
        if (!_deviceDisabled) return;
        try
        {
            await CommandAsync(ZkCommand.EnableDevice, ct);
        }
        catch (Exception ex)
        {
            // Worth shouting about: a terminal left disabled won't open the door.
            _logger.LogError(ex, "Failed to re-enable the terminal after a read");
        }
        finally
        {
            _deviceDisabled = false;
        }
    }

    public async Task CommandAsync(ushort command, CancellationToken ct, byte[]? payload = null)
    {
        var reply = await ExchangeAsync(command, payload ?? [], ct);
        if (reply.Command != ZkCommand.AckOk)
            throw new ZkDeviceException(
                $"The terminal rejected command {command} (code {reply.Command}).");
    }

    /// <summary>
    /// Runs a command that returns a bulk payload — the attendance log or user list.
    /// </summary>
    /// <remarks>
    /// Devices answer in one of two shapes. Older firmware replies CMD_DATA with the
    /// whole payload inline. Newer firmware, including the uFace 800, replies
    /// CMD_PREPARE_DATA carrying the total byte count and then streams that many
    /// bytes across follow-up packets, which are accumulated here before the buffer
    /// is released with CMD_FREE_DATA.
    /// </remarks>
    public async Task<byte[]> ReadDataAsync(
        ushort command, CancellationToken ct, byte[]? payload = null)
    {
        var reply = await ExchangeAsync(command, payload ?? [], ct);

        if (reply.Command == ZkCommand.Data)
            return reply.Data;

        if (reply.Command == ZkCommand.AckOk && reply.Data.Length >= 4)
        {
            // Some firmware acknowledges with the size and streams straight after.
            var expected = BinaryPrimitives.ReadInt32LittleEndian(reply.Data);
            return await AccumulateAsync(expected, ct);
        }

        if (reply.Command != ZkCommand.PrepareData)
        {
            if (reply.Command == ZkCommand.AckOk) return [];
            throw new ZkDeviceException(
                $"The terminal answered command {command} with an unexpected code " +
                $"({reply.Command}). It may be running firmware this client doesn't cover.");
        }

        if (reply.Data.Length < 4)
            throw new ZkDeviceException("The terminal announced a data transfer without a size.");

        var total = BinaryPrimitives.ReadInt32LittleEndian(reply.Data);
        if (total is < 0 or > MaxPayload)
            throw new ZkDeviceException($"The terminal announced an implausible payload of {total} bytes.");

        var data = await AccumulateAsync(total, ct);

        try
        {
            await ExchangeAsync(ZkCommand.FreeData, [], ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Terminal did not acknowledge FREE_DATA; payload already read");
        }

        return data;
    }

    private async Task<byte[]> AccumulateAsync(int total, CancellationToken ct)
    {
        var buffer = new byte[total];
        var filled = 0;

        while (filled < total)
        {
            var packet = await ReceiveAsync(ct);

            // The stream ends with an ACK once everything has been sent.
            if (packet.Command is ZkCommand.AckOk or ZkCommand.AckData && packet.Data.Length == 0)
                break;

            var take = Math.Min(packet.Data.Length, total - filled);
            if (take <= 0) break;

            packet.Data.AsSpan(0, take).CopyTo(buffer.AsSpan(filled));
            filled += take;
        }

        if (filled < total)
        {
            _logger.LogWarning(
                "Terminal announced {Total} bytes but sent {Filled}; using what arrived",
                total, filled);
            Array.Resize(ref buffer, filled);
        }

        return buffer;
    }

    // --- device parameters ---

    /// <summary>Reads a named setting, e.g. <c>~SerialNumber</c>, <c>UserCounts</c>.</summary>
    public async Task<string?> ReadStringParamAsync(string name, CancellationToken ct)
    {
        try
        {
            var reply = await ExchangeAsync(
                ZkCommand.DeviceInfo, Encoding.ASCII.GetBytes(name), ct);
            if (reply.Command != ZkCommand.AckOk) return null;

            var text = Encoding.ASCII.GetString(reply.Data).TrimEnd('\0');
            var eq = text.IndexOf('=');
            return eq >= 0 ? text[(eq + 1)..].Trim() : text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Terminal did not return parameter {Name}", name);
            return null;
        }
    }

    public async Task<int?> ReadIntParamAsync(string name, CancellationToken ct) =>
        int.TryParse(await ReadStringParamAsync(name, ct), out var value) ? value : null;

    public async Task<string?> ReadFirmwareAsync(CancellationToken ct)
    {
        try
        {
            var reply = await ExchangeAsync(1100, [], ct); // CMD_GET_VERSION
            return reply.Command == ZkCommand.AckOk
                ? Encoding.ASCII.GetString(reply.Data).TrimEnd('\0').Trim()
                : null;
        }
        catch { return null; }
    }

    public async Task<DateTime?> ReadTimeAsync(CancellationToken ct)
    {
        try
        {
            var reply = await ExchangeAsync(ZkCommand.GetTime, [], ct);
            if (reply.Command != ZkCommand.AckOk || reply.Data.Length < 4) return null;

            var encoded = BinaryPrimitives.ReadUInt32LittleEndian(reply.Data);
            var time = ZkDeviceClient.DecodeTime(encoded);
            return time == default ? null : time;
        }
        catch { return null; }
    }

    // --- transport ---

    private readonly record struct Packet(ushort Command, ushort SessionId, ushort ReplyId, byte[] Data);

    private async Task<Packet> ExchangeAsync(ushort command, byte[] payload, CancellationToken ct)
    {
        await SendAsync(command, payload, ct);
        return await ReceiveAsync(ct);
    }

    private async Task SendAsync(ushort command, byte[] payload, CancellationToken ct)
    {
        _replyId++;

        var body = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), command);
        // bytes 2-3 hold the checksum and stay zero while it is computed
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), _sessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(6), _replyId);
        payload.CopyTo(body, 8);

        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), Checksum(body));

        var frame = new byte[ZkFraming.HeaderSize + body.Length];
        ZkFraming.Magic.CopyTo(frame, 0);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, ZkFraming.HeaderSize);

        await _stream.WriteAsync(frame, ct);
        await _stream.FlushAsync(ct);
    }

    private async Task<Packet> ReceiveAsync(CancellationToken ct)
    {
        var header = new byte[ZkFraming.HeaderSize];
        await ReadExactAsync(header, ct);

        if (!header.AsSpan(0, 4).SequenceEqual(ZkFraming.Magic))
            throw new ZkDeviceException(
                "Unrecognised response from the terminal. Is something else listening on this port?");

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 8 or > MaxPayload)
            throw new ZkDeviceException($"The terminal announced an implausible packet of {length} bytes.");

        var body = new byte[length];
        await ReadExactAsync(body, ct);

        return new Packet(
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(6)),
            body[8..]);
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = await _stream.ReadAsync(buffer.AsMemory(read), ct);
            if (got == 0)
                throw new ZkDeviceException("The terminal closed the connection mid-transfer.");
            read += got;
        }
    }

    /// <summary>
    /// The protocol's 16-bit ones-complement checksum over the packet, with the
    /// checksum field itself treated as zero.
    /// </summary>
    private static ushort Checksum(byte[] packet)
    {
        var sum = 0;

        for (var i = 0; i + 1 < packet.Length; i += 2)
        {
            if (i == 2) continue; // the checksum field
            sum += BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(i));
            if (sum > ushort.MaxValue) sum -= ushort.MaxValue;
        }

        if (packet.Length % 2 == 1) sum += packet[^1];
        while (sum > ushort.MaxValue) sum -= ushort.MaxValue;

        sum = ~sum;
        while (sum < 0) sum += ushort.MaxValue;

        return (ushort)sum;
    }

    /// <summary>
    /// Derives the authentication token from the device's comm key and the session
    /// id: reverse the key's bits, add the session, then XOR through the constant
    /// "ZKSO" and a tick byte.
    /// </summary>
    private static byte[] MakeCommKey(int commKey, ushort sessionId, byte ticks = 50)
    {
        uint k = 0;
        for (var i = 0; i < 32; i++)
            if ((commKey & (1 << i)) != 0)
                k = (k << 1) | 1;
            else
                k <<= 1;

        k += sessionId;

        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, k);

        b[0] ^= (byte)'Z';
        b[1] ^= (byte)'K';
        b[2] ^= (byte)'S';
        b[3] ^= (byte)'O';

        // swap the two 16-bit halves
        (b[0], b[2]) = (b[2], b[0]);
        (b[1], b[3]) = (b[3], b[1]);

        b[0] ^= ticks;
        b[1] ^= ticks;
        b[3] ^= ticks;

        return b;
    }

    public void Dispose()
    {
        try
        {
            if (_client.Connected)
            {
                if (_deviceDisabled)
                    EnableDeviceAsync(CancellationToken.None).GetAwaiter().GetResult();

                SendAsync(ZkCommand.Exit, [], CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Terminal did not acknowledge disconnect");
        }
        finally
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }
}

/// <summary>A problem talking to a terminal, with a message fit to show an administrator.</summary>
public class ZkDeviceException(string message, Exception? inner = null)
    : Exception(message, inner);
