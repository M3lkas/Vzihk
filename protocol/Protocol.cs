using System.Buffers.Binary;

namespace Vzihk.Common;

public enum CommandType : byte
{
    Movement = 1, Shoot = 2, Ping = 3, Pong = 4, Ack = 5,
    StateUpdate = 100, ShootResult = 101, Error = 255
}

public sealed record Packet(CommandType Type, ushort Sequence, bool RequiresAck, byte[] Payload);

/// <summary>Version 3: explicit, big-endian wire format; see docs/Protocol_Specification.md.</summary>
public static class PacketHeader
{
    public const int HeaderSize = 8;
    public const ushort ProtocolVersion = 3;

    public static byte[] Build(CommandType type, ushort sequence, byte[]? payload, bool requiresAck = false)
    {
        payload ??= [];
        Validate(type, payload.Length, requiresAck);
        var data = new byte[HeaderSize + payload.Length];
        data[0] = (byte)type;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3), (ushort)payload.Length);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(5), ProtocolVersion);
        data[7] = requiresAck ? (byte)1 : (byte)0;
        payload.CopyTo(data, HeaderSize);
        return data;
    }

    public static Packet Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize) throw new FormatException("Пакет короче заголовка");
        var version = BinaryPrimitives.ReadUInt16BigEndian(data[5..]);
        if (version != ProtocolVersion) throw new FormatException("Неподдерживаемая версия протокола");
        if (data[7] > 1) throw new FormatException("requiresAck должен быть 0 или 1");
        var type = (CommandType)data[0];
        var size = BinaryPrimitives.ReadUInt16BigEndian(data[3..]);
        if (data.Length != HeaderSize + size) throw new FormatException("Длина пакета не совпадает с payloadSize");
        Validate(type, size, data[7] == 1);
        var packet = new Packet(type, BinaryPrimitives.ReadUInt16BigEndian(data[1..]), data[7] == 1, data[HeaderSize..].ToArray());
        // Validate payload values before acknowledging or applying a command.
        switch (type)
        {
            case CommandType.Movement: MovementPayload.FromBytes(packet.Payload); break;
            case CommandType.Shoot: ShootPayload.FromBytes(packet.Payload); break;
            case CommandType.StateUpdate: StateUpdatePayload.FromBytes(packet.Payload); break;
            case CommandType.ShootResult: ShootResultPayload.FromBytes(packet.Payload); break;
        }
        return packet;
    }

    private static void Validate(CommandType type, int size, bool requiresAck)
    {
        var expected = type switch
        {
            CommandType.Movement or CommandType.StateUpdate => 8,
            CommandType.Shoot => 4,
            CommandType.Ping or CommandType.Pong or CommandType.Error => 0,
            CommandType.Ack => 2,
            CommandType.ShootResult => 3,
            _ => throw new FormatException("Неизвестный тип пакета")
        };
        if (size != expected) throw new FormatException($"Неверный размер payload для {type}: {size}, ожидается {expected}");
        if (requiresAck && type != CommandType.Shoot)
            throw new FormatException("В этой версии только SHOOT требует ACK");
        if (type == CommandType.Shoot && !requiresAck)
            throw new FormatException("SHOOT должен требовать ACK");
    }
}

internal static class PayloadCodec
{
    public static void Size(ReadOnlySpan<byte> data, int expected)
    {
        if (data.Length != expected) throw new FormatException($"Ожидается {expected} байт payload");
    }
    public static float ReadFloat(ReadOnlySpan<byte> data)
    {
        var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data));
        if (!float.IsFinite(value)) throw new FormatException("Координата/угол должны быть конечными");
        return value;
    }
    public static void WriteFloat(Span<byte> data, float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        BinaryPrimitives.WriteInt32BigEndian(data, BitConverter.SingleToInt32Bits(value));
    }
    public static byte[] Position(float x, float y)
    {
        var data = new byte[8]; WriteFloat(data, x); WriteFloat(data.AsSpan(4), y); return data;
    }
}

public readonly record struct MovementPayload(float X, float Y)
{
    public byte[] ToBytes() => PayloadCodec.Position(X, Y);
    public static MovementPayload FromBytes(ReadOnlySpan<byte> data)
    {
        PayloadCodec.Size(data, 8); return new(PayloadCodec.ReadFloat(data), PayloadCodec.ReadFloat(data[4..]));
    }
}
public readonly record struct ShootPayload(float AngleDegrees)
{
    public byte[] ToBytes() { var data = new byte[4]; PayloadCodec.WriteFloat(data, AngleDegrees); return data; }
    public static ShootPayload FromBytes(ReadOnlySpan<byte> data)
    {
        PayloadCodec.Size(data, 4); return new(PayloadCodec.ReadFloat(data));
    }
}
public readonly record struct StateUpdatePayload(float X, float Y)
{
    public byte[] ToBytes() => PayloadCodec.Position(X, Y);
    public static StateUpdatePayload FromBytes(ReadOnlySpan<byte> data)
    {
        var p = MovementPayload.FromBytes(data); return new(p.X, p.Y);
    }
}
public readonly record struct ShootResultPayload(bool Hit, ushort AmmoLeft)
{
    public byte[] ToBytes()
    {
        var data = new byte[3]; data[0] = Hit ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(1), AmmoLeft); return data;
    }
    public static ShootResultPayload FromBytes(ReadOnlySpan<byte> data)
    {
        PayloadCodec.Size(data, 3);
        if (data[0] > 1) throw new FormatException("Hit должен быть 0 или 1");
        return new(data[0] == 1, BinaryPrimitives.ReadUInt16BigEndian(data[1..]));
    }
}
public readonly record struct AckPayload(ushort AcknowledgedSequence)
{
    public byte[] ToBytes()
    {
        var data = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(data, AcknowledgedSequence); return data;
    }
    public static AckPayload FromBytes(ReadOnlySpan<byte> data)
    {
        PayloadCodec.Size(data, 2); return new(BinaryPrimitives.ReadUInt16BigEndian(data));
    }
}
