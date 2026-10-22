using System.IO;
using WarmSnowTogether.Networking.Enums;

namespace WarmSnowTogether.Networking.Interfaces
{
    public interface INetworkPacket
    {
        PacketType Type { get; }
        void Serialize(BinaryWriter writer);
        void Deserialize(BinaryReader reader);
    }
}
