using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    public class LoadingStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.LoadingState;
        public bool IsLoading;

        public void Serialize(BinaryWriter writer) => writer.Write(IsLoading);
        public void Deserialize(BinaryReader reader) => IsLoading = reader.ReadBoolean();
    }
}