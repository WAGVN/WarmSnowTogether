using System.IO;
using WarmSnowTogether.Networking.Interfaces;
using WarmSnowTogether.Networking.Enums;

public class PlayerLeavePacket : INetworkPacket
{
    public PacketType Type => PacketType.PlayerLeave;
    public ulong PlayerId;

    public void Serialize(BinaryWriter writer) => writer.Write(PlayerId);
    public void Deserialize(BinaryReader reader) => PlayerId = reader.ReadUInt64();
}