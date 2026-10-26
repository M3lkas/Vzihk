using Vzihk.Common.Reliability;

namespace Vzihk.Common;

/// <summary>Per-client application state. Transport sends ACK before invoking Process.</summary>
public sealed class GameCommandProcessor
{
    private readonly SequenceWindow shots = new();
    public PlayerState Player { get; } = new();
    public int DuplicateShots { get; private set; }
    public byte[]? Process(Packet packet)
    {
        switch (packet.Type)
        {
            case CommandType.Movement:
                var move = MovementPayload.FromBytes(packet.Payload);
                Player.X = move.X; Player.Y = move.Y;
                return PacketHeader.Build(CommandType.StateUpdate, packet.Sequence,
                    new StateUpdatePayload(Player.X, Player.Y).ToBytes());
            case CommandType.Shoot:
                var shot = ShootPayload.FromBytes(packet.Payload);
                if (!shots.TryAccept(packet.Sequence)) { DuplicateShots++; return null; }
                var hit = false;
                if (Player.Ammo > 0)
                {
                    Player.Ammo--; Player.ShotsFired++;
                    hit = Random.Shared.Next(2) == 0;
                    if (hit) Player.Hits++;
                }
                return PacketHeader.Build(CommandType.ShootResult, packet.Sequence,
                    new ShootResultPayload(hit, Player.Ammo).ToBytes());
            default: return null;
        }
    }
}
