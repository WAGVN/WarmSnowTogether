using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
    // [WST NetSync v1] Client -> host: "my attack hit enemy <EnemyId>, please apply the damage".
    // The client copy of the enemy never changes its own HP; the host is authoritative.
    // Owner: lead. Requirement 4 (client attacks deal damage, HP stays consistent).
    public class DamageRequestPacket : INetworkPacket
    {
        public PacketType Type => PacketType.DamageRequest;

        public int EnemyId;
        public float Damage;
        public bool IsRepell;
        public float DirX;
        public float DirY;
        public bool IsMeele;
        public bool TrueDamage;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write(Damage);
            writer.Write(IsRepell);
            writer.Write(DirX);
            writer.Write(DirY);
            writer.Write(IsMeele);
            writer.Write(TrueDamage);
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            Damage = reader.ReadSingle();
            IsRepell = reader.ReadBoolean();
            DirX = reader.ReadSingle();
            DirY = reader.ReadSingle();
            IsMeele = reader.ReadBoolean();
            TrueDamage = reader.ReadBoolean();
        }
    }
}
