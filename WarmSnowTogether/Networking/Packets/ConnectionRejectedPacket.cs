using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    public class ConnectionRejectedPacket : INetworkPacket
    {
        public PacketType Type => PacketType.ConnectionRejected;

        public int ProtocolVersion;

        public void Serialize(BinaryWriter writer) => writer.Write(ProtocolVersion);
        public void Deserialize(BinaryReader reader) => ProtocolVersion = reader.ReadInt32();
    }
}