using System.Buffers.Binary;
using System.Net.Sockets;

namespace Telemetry.Core;

/// <summary>Read-only Modbus TCP function 03 client. One request owns one TCP connection.</summary>
public static class ModbusRegisterClient
{
    public static async Task<ushort> ReadAsync(string host, int port, byte unitId, ushort address,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, deadline.Token);
        await using var stream = client.GetStream();
        var transaction = (ushort)Random.Shared.Next(ushort.MaxValue + 1);
        var request = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(request, transaction);
        request[5] = 6;
        request[6] = unitId;
        request[7] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), address);
        request[11] = 1;
        await stream.WriteAsync(request, deadline.Token);
        var header = new byte[7];
        await stream.ReadExactlyAsync(header, deadline.Token);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != transaction || header[2] != 0 || header[3] != 0 ||
            header[6] != unitId || length is < 3 or > 254) throw new IOException("Invalid Modbus response header.");
        var body = new byte[length - 1];
        await stream.ReadExactlyAsync(body, deadline.Token);
        if (body[0] == 0x83) throw new IOException($"Modbus exception {body[1]}.");
        if (body.Length != 4 || body[0] != 3 || body[1] != 2) throw new IOException("Invalid holding-register response.");
        return BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2));
    }
}
