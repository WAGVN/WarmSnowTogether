using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    public class DisconnectPacket : INetworkPacket
    {
        public PacketType Type => PacketType.Disconnect;

        public void Serialize(BinaryWriter writer) { } // empty
        public void Deserialize(BinaryReader reader) { }
    }
}