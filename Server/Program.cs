using System.Net;
using System.Net.Sockets;
using Vzihk.Common;

namespace Vzihk.Server
{
    internal static class Program
    {
        private const int DefaultPort = 11000;
        private const string LogFileName = "server.log";

        private static readonly PlayerRegistry Players = new();
        private static readonly object LogLock = new();
        private static readonly Random Rng = new();

        private static async Task Main(string[] args)
        {
            var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : DefaultPort;

            using var udpServer = new UdpClient(port);
            Log($"Сервер Vzihk запущен, слушает UDP-порт {port}. Для остановки нажмите Ctrl+C.");

            while (true)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udpServer.ReceiveAsync();
                }
                catch (SocketException ex)
                {
                    Log($"Ошибка сокета при приёме пакета: {ex.Message}");
                    continue;
                }

                // Обрабатываем каждый пакет в отдельной задаче, чтобы медленная
                // обработка одного клиента не блокировала приём от остальных.
                _ = HandlePacketAsync(udpServer, received.RemoteEndPoint, received.Buffer);
            }
        }

        private static async Task HandlePacketAsync(UdpClient socket, IPEndPoint clientEndpoint, byte[] rawData)
        {
            CommandType type;
            uint sequence;
            byte[] payload;

            try
            {
                (type, sequence, payload) = PacketHeader.Parse(rawData);
            }
            catch (FormatException ex)
            {
                Log($"[{clientEndpoint}] Не удалось разобрать пакет: {ex.Message}");
                var errorPacket = PacketHeader.Build(CommandType.Error, 0, null);
                await socket.SendAsync(errorPacket, errorPacket.Length, clientEndpoint);
                return;
            }

            var player = Players.GetOrCreate(clientEndpoint);

            byte[] responsePacket;
            switch (type)
            {
                case CommandType.Movement:
                    responsePacket = HandleMovement(clientEndpoint, sequence, payload, player);
                    break;

                case CommandType.Shoot:
                    responsePacket = HandleShoot(clientEndpoint, sequence, payload, player);
                    break;

                default:
                    Log($"[{clientEndpoint}] seq={sequence}: неизвестный тип команды {type}");
                    responsePacket = PacketHeader.Build(CommandType.Error, sequence, null);
                    break;
            }

            await socket.SendAsync(responsePacket, responsePacket.Length, clientEndpoint);
        }

        private static byte[] HandleMovement(IPEndPoint client, uint sequence, byte[] payload, PlayerState player)
        {
            try
            {
                var move = MovementPayload.FromBytes(payload);
                player.X = move.X;
                player.Y = move.Y;

                Log($"[{client}] seq={sequence} MOVEMENT -> новая позиция ({player.X:F2}, {player.Y:F2})");

                var response = new StateUpdatePayload(player.X, player.Y);
                return PacketHeader.Build(CommandType.StateUpdate, sequence, response.ToBytes());
            }
            catch (FormatException ex)
            {
                Log($"[{client}] seq={sequence} MOVEMENT: ошибка payload — {ex.Message}");
                return PacketHeader.Build(CommandType.Error, sequence, null);
            }
        }

        private static byte[] HandleShoot(IPEndPoint client, uint sequence, byte[] payload, PlayerState player)
        {
            try
            {
                var shoot = ShootPayload.FromBytes(payload);

                if (player.Ammo == 0)
                {
                    Log($"[{client}] seq={sequence} SHOOT (угол {shoot.AngleDegrees:F1}°) -> боезапас пуст, выстрел отклонён");
                    var emptyResult = new ShootResultPayload(false, 0);
                    return PacketHeader.Build(CommandType.ShootResult, sequence, emptyResult.ToBytes());
                }

                player.Ammo--;
                player.ShotsFired++;

                // Простая имитация попадания: 50% шанс, независимо от угла.
                // Для реальной игры здесь была бы проверка пересечения луча с другими игроками.
                var hit = Rng.Next(2) == 0;
                if (hit) player.Hits++;

                Log($"[{client}] seq={sequence} SHOOT (угол {shoot.AngleDegrees:F1}°) -> " +
                    $"{(hit ? "попадание" : "промах")}, осталось патронов: {player.Ammo}");

                var result = new ShootResultPayload(hit, player.Ammo);
                return PacketHeader.Build(CommandType.ShootResult, sequence, result.ToBytes());
            }
            catch (FormatException ex)
            {
                Log($"[{client}] seq={sequence} SHOOT: ошибка payload — {ex.Message}");
                return PacketHeader.Build(CommandType.Error, sequence, null);
            }
        }

        /// <summary>Пишет строку лога одновременно в консоль и в файл server.log.</summary>
        private static void Log(string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}";
            lock (LogLock)
            {
                Console.WriteLine(line);
                File.AppendAllText(LogFileName, line + Environment.NewLine);
            }
        }
    }
}
