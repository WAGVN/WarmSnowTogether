using System.IO;
using UnityEngine;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
  // [WST Loot Sync v1] host -> clients: 房主端出现了一个权威掉落物。
  // 客户端用 Kind/Data/Position/Rotation 调用 LootHookAdapter.CreateMirror 重建本地镜像。
  public class LootSpawnPacket : INetworkPacket
  {
    public PacketType Type => PacketType.LootSpawn;

    public int NetId;
    public int Kind;
    public string Data;
    public Vector2 Position;
    public float Rotation;

    public void Serialize(BinaryWriter writer)
    {
      writer.Write(NetId);
      writer.Write(Kind);
      writer.Write(Data ?? string.Empty);
      writer.Write(Position.x);
      writer.Write(Position.y);
      writer.Write(Rotation);
    }

    public void Deserialize(BinaryReader reader)
    {
      NetId = reader.ReadInt32();
      Kind = reader.ReadInt32();
      Data = reader.ReadString();
      Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
      Rotation = reader.ReadSingle();
    }
  }
}
