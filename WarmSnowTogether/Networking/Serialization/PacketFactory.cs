using System;
using System.Collections.Generic;
using WarmSnowTogether.Networking.Packets;
using WarmSnowTogether.Networking.Interfaces;
using WarmSnowTogether.Networking.Enums;

namespace WarmSnowTogether.Networking.Serialization
{
  // [WST NetSync v1] packet type -> class registry.
  // [WST Loot Sync v1] 追加 LootSpawn / LootConsumed。
  public static class PacketFactory
  {
    private static readonly Dictionary<PacketType, Type> _packetTypes = new Dictionary<PacketType, Type>
        {
            { PacketType.PlayerPosition, typeof(PlayerPositionPacket) },
            { PacketType.ConnectionRequest, typeof(ConnectionRequestPacket) },
            { PacketType.ConnectionAccepted, typeof(ConnectionAcceptedPacket) },
            { PacketType.Disconnect, typeof(DisconnectPacket) },
            { PacketType.ConnectionRejected, typeof(ConnectionRejectedPacket) },
            { PacketType.PlayerJoin, typeof(PlayerJoinPacket) },
            { PacketType.PlayerLeave, typeof(PlayerLeavePacket) },
            { PacketType.RoomChange, typeof(RoomChangePacket) },
            { PacketType.EnemySpawn, typeof(EnemySpawnPacket) },
            { PacketType.EnemyState, typeof(EnemyStatePacket) },
            { PacketType.EnemyDeath, typeof(EnemyDeathPacket) },
            { PacketType.WorldState, typeof(WorldStatePacket) },
            { PacketType.PlayerState, typeof(PlayerStatePacket) },
            { PacketType.LoadingState, typeof(LoadingStatePacket) },
            // [WST NetSync v1] host-authority packets
            { PacketType.SceneSync, typeof(SceneSyncPacket) },
            { PacketType.DamageRequest, typeof(DamageRequestPacket) },
            { PacketType.SnapshotRequest, typeof(SnapshotRequestPacket) },
            // [WST Loot Sync v1] host-authoritative loot packets
            { PacketType.LootSpawn, typeof(LootSpawnPacket) },
            { PacketType.LootConsumed, typeof(LootConsumedPacket) },
        };

    public static INetworkPacket Create(PacketType type)
    {
      if (_packetTypes.TryGetValue(type, out Type classType))
      {
        try
        {
          return (INetworkPacket)Activator.CreateInstance(classType);
        }
        catch (Exception ex)
        {
          // 构造失败日志
          UnityEngine.Debug.LogError($"[PacketFactory] 创建数据包失败，类型:{type}，{ex.Message}");
        }
      }
      return null;
    }
  }
}
