using System.IO;
using UnityEngine;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
  public class WorldStatePacket : INetworkPacket
  {
    public PacketType Type => PacketType.WorldState;
    public string RoomIdentifier; 
    public Vector2 Position;
    public float Rotation;

    public void Serialize(BinaryWriter writer)
    {
      writer.Write(RoomIdentifier ?? "");
      writer.Write(Position.x);
      writer.Write(Position.y);
      writer.Write(Rotation);
    }

    public void Deserialize(BinaryReader reader)
    {
      // 读取顺序必须和Serialize完全保持一致！
      RoomIdentifier = reader.ReadString();
      Position = new Vector2(reader.ReadSingle(), reader.ReadSingle());
      Rotation = reader.ReadSingle();
    }
  }
}
