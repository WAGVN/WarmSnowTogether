using System.IO;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;

namespace WarmSnowTogether.Networking.Packets
{
  // [WST Loot Sync v1] 某个掉落物被某个玩家捡走（单份掉落、先到先得）。
  //   client -> host : 客户端捡走了 netId，请房主删除原物并通知其它客户端；
  //   host -> all    : 房主（或某个客户端，经房主转发）捡走了 netId，各端删除镜像。
  public class LootConsumedPacket : INetworkPacket
  {
    public PacketType Type => PacketType.LootConsumed;

    public int NetId;
    // 拾取者的 SteamId（仅用于日志/统计；0 表示未知）。
    public ulong ByPlayerId;

    public void Serialize(BinaryWriter writer)
    {
      writer.Write(NetId);
      writer.Write(ByPlayerId);
    }

    public void Deserialize(BinaryReader reader)
    {
      NetId = reader.ReadInt32();
      ByPlayerId = reader.ReadUInt64();
    }
  }
}
