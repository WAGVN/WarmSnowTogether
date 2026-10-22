using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    // [WST NetSync v1] Client -> host: "I finished loading the scene (or just connected), send me the
    // full enemy snapshot". Owner: lead. Requirement 3 (client sees the host-authoritative enemies).
    public class SnapshotRequestPacket : INetworkPacket
    {
        public PacketType Type => PacketType.SnapshotRequest;

        public void Serialize(BinaryWriter writer) { }

        public void Deserialize(BinaryReader reader) { }
    }
}
