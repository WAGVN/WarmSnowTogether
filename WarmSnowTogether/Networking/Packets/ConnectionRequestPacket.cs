using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    public class ConnectionRequestPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ConnectionRequest;

        public ulong ClientId;
        public int ProtocolVersion;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(ClientId);
            writer.Write(ProtocolVersion);
        }

        public void Deserialize(BinaryReader reader)
        {
            ClientId = reader.ReadUInt64();
            ProtocolVersion = reader.ReadInt32();
        }
    }
}
