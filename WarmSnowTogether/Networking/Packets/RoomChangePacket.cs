using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    public class RoomChangePacket : INetworkPacket
    {
        public PacketType Type => PacketType.RoomChange;
        public string RoomName;
        public void Serialize(BinaryWriter w) => w.Write(RoomName);
        public void Deserialize(BinaryReader r) => RoomName = r.ReadString();
    }
}