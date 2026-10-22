using System;
using UnityEngine;

namespace WarmSnowTogether.Networking.Loot
{
  // [WST Loot Sync v1] 掉落物同步的数据模型（与具体游戏类型解耦）。
  //
  // 设计原则：
  //   房主是唯一的“掉落物权威”：只有房主端生成的掉落物会被登记并广播；
  //   客户端收到描述后，用同一个游戏生成接口在本地重建一个“一模一样的”掉落物（镜像），
  //   客户端自己的拾取逻辑照常运行，所以“能看到、能捡、能正常使用”；
  //   任一端捡走某件东西后，该 netId 的镜像在其它端删除（单份、先到先得，房主裁决）。
  //
  // LootDescriptor 里的 Kind / Data 是给 LootHookAdapter 逆向填写用的自由格式：
  //   Kind：掉落物类别（见 LootKind，仅用于日志/调试/后续扩展，框架不依赖它做逻辑）
  //   Data：由 LootHookAdapter 决定格式的字符串（例如 "weaponId=123;quality=2;affix=..."），
  //         框架只负责原样传输，客户端重建时交回给 LootHookAdapter。
  public enum LootKind
  {
    Unknown = 0,
    Soul = 1,         // 灵魂（含红/蓝魂等，具体由 Data 描述）
    Copper = 2,       // 铜钱/金钱
    Potion = 3,       // 药水
    Skill = 4,        // 技能/秘籍
    MagicSword = 5,   // 魔剑/武器
    Relic = 6,        // 圣物/遗物
    StoryChip = 7,    // 剧情碎片
    Common = 8,       // CommonDropItem 类通用掉落
    Interactive = 9,  // 其它可交互掉落/奖励物
    Custom = 10,      // 逆向时自定义类别
  }

  [Serializable]
  public sealed class LootDescriptor
  {
    public int Kind;
    public string Data;
    public Vector2 Position;
    public float Rotation;

    public LootDescriptor()
    {
      Kind = (int)LootKind.Unknown;
      Data = string.Empty;
      Position = Vector2.zero;
      Rotation = 0f;
    }

    public LootDescriptor(int kind, string data, Vector2 position, float rotation)
    {
      Kind = kind;
      Data = data ?? string.Empty;
      Position = position;
      Rotation = rotation;
    }

    public LootKind KindEnum
    {
      get { return (LootKind)Kind; }
    }

    public override string ToString()
    {
      return "kind=" + Kind + " pos=(" + Position.x.ToString("0.00") + "," + Position.y.ToString("0.00")
        + ") data=" + (Data == null ? string.Empty : Data);
    }
  }
}
