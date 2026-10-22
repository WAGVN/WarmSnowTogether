using System;
using System.IO;
using WarmSnowTogether.Networking;
using WarmSnowTogether.Networking.Interfaces;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Systems.Logging;
using Debug = WarmSnowTogether.Systems.Logging.Debug;

namespace WarmSnowTogether.Networking.Serialization
{
  // [WST NetSync v1.1] Packet (de)serialization with hard size limits and exception guards.
  //
  // 旧实现没有长度上限、也不捕获异常：
  //   * 一个畸形包（或某次 Steam 回调给出的异常长度）可以让 BinaryReader 按一个伪造的
  //     string 长度去分配内存，最坏情况直接 OOM，崩在本 MOD 里；
  //   * Deserialize 抛出的 EndOfStreamException 原本会一路冒到 NetworkManager.HandlePacket
  //     的 catch，刷 Error 日志（客户端 P2P 抖动时容易看到）。
  // 现在统一在这里拦下：超长包直接丢弃，解析异常降级为 Warning 并返回 null。
  public static class PacketSerializer
  {
    // 本 MOD 最大的正常包（SceneSync，含场景名字符串）也只有几十字节。
    // 64KB 上限足以覆盖任何正常数据，同时挡住畸形包。
    public const int MaxPacketSize = 64 * 1024;

    public static byte[] Serialize(INetworkPacket packet)
    {
      if (packet == null)
      {
        Debug.LogWarning("[PacketSerializer] Serialize: packet is null");
        return null;
      }

      try
      {
        using (var ms = new MemoryStream())
        using (var writer = new BinaryWriter(ms))
        {
          writer.Write((byte)packet.Type);
          packet.Serialize(writer);
          writer.Flush();
          if (ms.Length > MaxPacketSize)
          {
            Debug.LogError("[PacketSerializer] Serialize: packet " + packet.Type + " is " + ms.Length
              + " bytes, over the " + MaxPacketSize + " byte limit; dropped.");
            return null;
          }
          return ms.ToArray();
        }
      }
      catch (Exception ex)
      {
        Debug.LogError("[PacketSerializer] Serialize failed for " + packet.Type + ": "
          + ex.GetType().Name + ": " + ex.Message);
        return null;
      }
    }

    public static INetworkPacket Deserialize(byte[] data)
    {
      if (data == null || data.Length == 0)
      {
        Debug.LogWarning("[PacketSerializer] Deserialize: empty data");
        return null;
      }
      if (data.Length > MaxPacketSize)
      {
        Debug.LogWarning("[PacketSerializer] Deserialize: " + data.Length
          + " bytes over the " + MaxPacketSize + " byte limit; packet dropped.");
        return null;
      }

      try
      {
        using (var ms = new MemoryStream(data, false))
        using (var reader = new BinaryReader(ms))
        {
          PacketType type = (PacketType)reader.ReadByte();
          INetworkPacket packet = PacketFactory.Create(type);

          if (packet != null)
          {
            packet.Deserialize(reader);
            return packet;
          }

          Debug.LogWarning($"[PacketSerializer] Unknown packet type: {type}");
          return null;
        }
      }
      catch (Exception ex)
      {
        // 截断/损坏的包不能把异常抛到更新循环里：降级成警告，丢包即可。
        Debug.LogWarning("[PacketSerializer] Deserialize failed: " + ex.GetType().Name + ": " + ex.Message);
        return null;
      }
    }
  }
}
