using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    public class EnemyDeathPacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyDeath;

        public int EnemyId;

        public void Serialize(BinaryWriter writer) => writer.Write(EnemyId);

        public void Deserialize(BinaryReader reader) => EnemyId = reader.ReadInt32();
    }
}