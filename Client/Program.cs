using System.Net;
using System.Net.Sockets;
using Vzihk.Common;

namespace Vzihk.Client
{
    internal static class Program
    {
        private const string DefaultHost = "127.0.0.1";
        private const int DefaultPort = 11000;
        private const int TickIntervalMs = 1000; // периодичность отправки команд
        private const int ReceiveTimeoutMs = 2000;

        private static async Task Main(string[] args)
        {
            var host = args.Length > 0 ? args[0] : DefaultHost;
            var port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : DefaultPort;

            using var udpClient = new UdpClient();
            udpClient.Connect(host, port);
            udpClient.Client.ReceiveTimeout = ReceiveTimeoutMs;

            Console.WriteLine($"Клиент Vzihk запущен. Сервер: {host}:{port}. Команды отправляются каждые {TickIntervalMs} мс.");
            Console.WriteLine("Нажмите Ctrl+C для выхода.\n");

            uint sequence = 0;
            var rng = new Random();
            float posX = 0f, posY = 0f;

            while (true)
            {
                sequence++;

                // Чередуем команды: на нечётных тиках — движение, на чётных — выстрел.
                var isMovementTick = sequence % 2 == 1;

                byte[] packet;
                string description;

                if (isMovementTick)
                {
                    // Симулируем небольшое смещение игрока в случайном направлении.
                    posX += (float)(rng.NextDouble() * 2 - 1);
                    posY += (float)(rng.NextDouble() * 2 - 1);

                    var payload = new MovementPayload(posX, posY);
                    packet = PacketHeader.Build(CommandType.Movement, sequence, payload.ToBytes());
                    description = $"MOVEMENT -> запрошенная позиция ({posX:F2}, {posY:F2})";
                }
                else
                {
                    var angle = (float)(rng.NextDouble() * 360);
                    var payload = new ShootPayload(angle);
                    packet = PacketHeader.Build(CommandType.Shoot, sequence, payload.ToBytes());
                    description = $"SHOOT -> угол {angle:F1}°";
                }

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] seq={sequence} Отправка: {description}");

                try
                {
                    await udpClient.SendAsync(packet, packet.Length);
                    await ReceiveAndPrintResponseAsync(udpClient);
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"  Ошибка сети: {ex.Message}");
                }

                await Task.Delay(TickIntervalMs);
            }
        }

        private static async Task ReceiveAndPrintResponseAsync(UdpClient udpClient)
        {
            using var cts = new CancellationTokenSource(ReceiveTimeoutMs);
            UdpReceiveResult response;
            try
            {
                response = await udpClient.ReceiveAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("  Ответ от сервера не получен (таймаут) — пакет мог быть потерян.");
                return;
            }

            try
            {
                var (type, sequence, payload) = PacketHeader.Parse(response.Buffer);
                switch (type)
                {
                    case CommandType.StateUpdate:
                        var state = StateUpdatePayload.FromBytes(payload);
                        Console.WriteLine($"  Ответ seq={sequence}: STATE_UPDATE -> позиция сервера ({state.X:F2}, {state.Y:F2})");
                        break;

                    case CommandType.ShootResult:
                        var result = ShootResultPayload.FromBytes(payload);
                        Console.WriteLine($"  Ответ seq={sequence}: SHOOT_RESULT -> " +
                            $"{(result.Hit ? "попадание!" : "промах")}, осталось патронов: {result.AmmoLeft}");
                        break;

                    case CommandType.Error:
                        Console.WriteLine($"  Ответ seq={sequence}: сервер сообщил об ошибке обработки пакета");
                        break;

                    default:
                        Console.WriteLine($"  Ответ seq={sequence}: неизвестный тип ответа {type}");
                        break;
                }
            }
            catch (FormatException ex)
            {
                Console.WriteLine($"  Получен повреждённый пакет от сервера: {ex.Message}");
            }
        }
    }
}
