using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;
using UnityEngine;

namespace WarmSnowTogether.Networking.Packets
{
    // [WST NetSync v1] Host -> client: periodic enemy transform + HP update (sent unreliable).
    // Owner: lead (fields) / enemy-sync (producer+consumer).
    public class EnemyStatePacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemyState;

        public int EnemyId;
        public Vector2 Position;
        public float Rotation;
        public float Health;
        public float MaxHealth;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Rotation);
            writer.Write(Health);
            writer.Write(MaxHealth);
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            Health = reader.ReadSingle();
            MaxHealth = reader.ReadSingle();
        }
    }
}
