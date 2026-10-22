using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;
using UnityEngine;

namespace WarmSnowTogether.Networking.Packets
{
    // [WST NetSync v1] Host -> client: a new host-authoritative enemy appeared (or the full snapshot).
    // Health/MaxHealth are floats because Parameter.HP is a float property.
    // Owner: lead (fields) / enemy-sync (producer+consumer).
    public class EnemySpawnPacket : INetworkPacket
    {
        public PacketType Type => PacketType.EnemySpawn;

        public int EnemyId;    // host assigned network id
        public int MonsterId;  // EnemyControl.MonsterID (index into MonstersControl.EnemyObject)
        public Vector2 Position;
        public float Rotation;
        public float Health;
        public float MaxHealth;

        public void Serialize(BinaryWriter writer)
        {
            writer.Write(EnemyId);
            writer.Write(MonsterId);
            writer.Write(Position.x);
            writer.Write(Position.y);
            writer.Write(Rotation);
            writer.Write(Health);
            writer.Write(MaxHealth);
        }

        public void Deserialize(BinaryReader reader)
        {
            EnemyId = reader.ReadInt32();
            MonsterId = reader.ReadInt32();
            Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Rotation = reader.ReadSingle();
            Health = reader.ReadSingle();
            MaxHealth = reader.ReadSingle();
        }
    }
}
