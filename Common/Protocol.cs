using System;

namespace Vzihk.Common
{
    /// <summary>
    /// Типы команд, которыми обмениваются клиент и сервер.
    /// Значения 1-99 — команды клиента, 100-199 — ответы сервера, 255 — ошибка.
    /// </summary>
    public enum CommandType : byte
    {
        Movement = 1,   // клиент -> сервер: запрос на перемещение
        Shoot = 2,      // клиент -> сервер: запрос на выстрел

        StateUpdate = 100, // сервер -> клиент: обновлённое состояние после Movement
        ShootResult = 101, // сервер -> клиент: результат выстрела после Shoot

        Error = 255     // сервер -> клиент: пакет не удалось обработать
    }

    /// <summary>
    /// Заголовок пакета: тип команды (1 байт), порядковый номер (4 байта, uint),
    /// размер полезной нагрузки (2 байта, ushort). Далее следует сама полезная нагрузка.
    /// </summary>
    public static class PacketHeader
    {
        public const int HeaderSize = sizeof(byte) + sizeof(uint) + sizeof(ushort); // 1 + 4 + 2 = 7 байт

        /// <summary>
        /// Собирает полный пакет: заголовок + payload.
        /// </summary>
        public static byte[] Build(CommandType type, uint sequence, byte[]? payload)
        {
            payload ??= Array.Empty<byte>();
            if (payload.Length > ushort.MaxValue)
                throw new ArgumentException("Полезная нагрузка слишком большая для данного протокола");

            var buffer = new byte[HeaderSize + payload.Length];
            buffer[0] = (byte)type;
            BitConverter.GetBytes(sequence).CopyTo(buffer, 1);
            BitConverter.GetBytes((ushort)payload.Length).CopyTo(buffer, 5);
            payload.CopyTo(buffer, HeaderSize);
            return buffer;
        }

        /// <summary>
        /// Разбирает полученный набор байт на заголовок и полезную нагрузку.
        /// Бросает FormatException, если пакет повреждён или короче заявленного размера.
        /// </summary>
        public static (CommandType Type, uint Sequence, byte[] Payload) Parse(byte[] data)
        {
            if (data.Length < HeaderSize)
                throw new FormatException($"Пакет короче минимального заголовка ({HeaderSize} байт)");

            var type = (CommandType)data[0];
            var sequence = BitConverter.ToUInt32(data, 1);
            var declaredSize = BitConverter.ToUInt16(data, 5);

            if (data.Length < HeaderSize + declaredSize)
                throw new FormatException("Заявленный размер payload больше фактической длины пакета");

            var payload = new byte[declaredSize];
            Array.Copy(data, HeaderSize, payload, 0, declaredSize);
            return (type, sequence, payload);
        }
    }

    /// <summary>Payload команды MOVEMENT: желаемая позиция игрока (X, Y).</summary>
    public readonly struct MovementPayload
    {
        public float X { get; }
        public float Y { get; }

        public MovementPayload(float x, float y)
        {
            X = x;
            Y = y;
        }

        public byte[] ToBytes()
        {
            var buffer = new byte[8];
            BitConverter.GetBytes(X).CopyTo(buffer, 0);
            BitConverter.GetBytes(Y).CopyTo(buffer, 4);
            return buffer;
        }

        public static MovementPayload FromBytes(byte[] data)
        {
            if (data.Length < 8)
                throw new FormatException("Payload MOVEMENT должен содержать 8 байт (2 float)");
            var x = BitConverter.ToSingle(data, 0);
            var y = BitConverter.ToSingle(data, 4);
            return new MovementPayload(x, y);
        }
    }

    /// <summary>Payload команды SHOOT: угол выстрела в градусах.</summary>
    public readonly struct ShootPayload
    {
        public float AngleDegrees { get; }

        public ShootPayload(float angleDegrees)
        {
            AngleDegrees = angleDegrees;
        }

        public byte[] ToBytes() => BitConverter.GetBytes(AngleDegrees);

        public static ShootPayload FromBytes(byte[] data)
        {
            if (data.Length < 4)
                throw new FormatException("Payload SHOOT должен содержать 4 байта (float)");
            return new ShootPayload(BitConverter.ToSingle(data, 0));
        }
    }

    /// <summary>Payload ответа StateUpdate: актуальная позиция игрока после обработки MOVEMENT.</summary>
    public readonly struct StateUpdatePayload
    {
        public float X { get; }
        public float Y { get; }

        public StateUpdatePayload(float x, float y)
        {
            X = x;
            Y = y;
        }

        public byte[] ToBytes()
        {
            var buffer = new byte[8];
            BitConverter.GetBytes(X).CopyTo(buffer, 0);
            BitConverter.GetBytes(Y).CopyTo(buffer, 4);
            return buffer;
        }

        public static StateUpdatePayload FromBytes(byte[] data)
        {
            var x = BitConverter.ToSingle(data, 0);
            var y = BitConverter.ToSingle(data, 4);
            return new StateUpdatePayload(x, y);
        }
    }

    /// <summary>Payload ответа ShootResult: попадание (0/1) и оставшийся боезапас.</summary>
    public readonly struct ShootResultPayload
    {
        public bool Hit { get; }
        public ushort AmmoLeft { get; }

        public ShootResultPayload(bool hit, ushort ammoLeft)
        {
            Hit = hit;
            AmmoLeft = ammoLeft;
        }

        public byte[] ToBytes()
        {
            var buffer = new byte[3];
            buffer[0] = (byte)(Hit ? 1 : 0);
            BitConverter.GetBytes(AmmoLeft).CopyTo(buffer, 1);
            return buffer;
        }

        public static ShootResultPayload FromBytes(byte[] data)
        {
            var hit = data[0] != 0;
            var ammoLeft = BitConverter.ToUInt16(data, 1);
            return new ShootResultPayload(hit, ammoLeft);
        }
    }
}
