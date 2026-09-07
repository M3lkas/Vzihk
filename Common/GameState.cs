using System.Collections.Concurrent;
using System.Net;

namespace Vzihk.Common
{
    /// <summary>Состояние одного игрока (клиента) на сервере.</summary>
    public class PlayerState
    {
        public float X { get; set; }
        public float Y { get; set; }
        public ushort Ammo { get; set; } = 10;
        public int ShotsFired { get; set; }
        public int Hits { get; set; }
    }

    /// <summary>
    /// Потокобезопасный реестр игроков, ключом служит IP-адрес и порт клиента.
    /// Так как UDP не устанавливает соединение, сервер идентифицирует игроков
    /// по их сетевому адресу (IPEndPoint) — этого достаточно для учебной задачи.
    /// </summary>
    public class PlayerRegistry
    {
        private readonly ConcurrentDictionary<IPEndPoint, PlayerState> _players = new();

        public PlayerState GetOrCreate(IPEndPoint endpoint)
        {
            return _players.GetOrAdd(endpoint, _ => new PlayerState());
        }

        public int Count => _players.Count;
    }
}
