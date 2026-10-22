using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WarmSnowTogether.Networking;
using WarmSnowTogether.Networking.Sync;
using WarmSnowTogether.Systems.Logging;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking.Loot
{
  // ============================================================================================
  // [WST Loot Sync v1.2] 逆向接入层 —— 全部基于暖雪真实符号（Assembly-CSharp 反编译核对）。
  //
  // 本文件负责 4 件事（LootSyncManager 只做网络/跟踪/快照）：
  //   1) 安装“掉落物生成”钩子：给各 *PoolControl.Pop（返回 GameObject）打 prefix(捕获实参) +
  //      postfix(拿到结果对象)，把 (生成方法, 实参) 编码成 LootDescriptor.Data；
  //   2) 客户端镜像：解码 (方法, 实参)，在客户端的同一个 Pool 单例上调用同一个 Pop，
  //      得到完全相同的掉落物（Pop 只是从自己的对象池/预制体生成，客户端调用安全）；
  //   3) 客户端副作用拦截（防止镜像拾取乱写本地进度）：拦截
  //      GlobalParameter.AchievementAddProgress、Talent.MachineHeartRuneManager.OnAddSouls、
  //      SaveSystemControl.Save，以及客户端对本地宝箱的开启（宝箱内容由房主掉落镜像提供）；
  //   4) 兼容 LootSyncManager 原有的 LootHookAdapter API 占位。
  //
  // 已核对的真实符号（2026-10 反编译，Assembly-CSharp）：
  //   掉落生成池（Pop 返回 GameObject）：
  //     SoulPoolControl.Pop(DropSoulType, [opt] bool isRealBlueSoul)
  //     RedSoulPoolControl.Pop()
  //     PotionDropPool.Pop(PN potionName, int level, Vector3 pos, [opt] bool isJump, [opt] bool canRefine)
  //     PotionDropPool.Pop(int index, ...)
  //     SkillDropPool.Pop(Vector3 pos, [opt] bool isGolden, [opt] bool isJump, [opt] bool isNightmare)
  //     MagicSwordPool.Pop(int index, int level, List<MagicSwordEntry> entrys, Vector3 pos,
  //                        [opt] bool isJump, [opt] bool canBeSelled)
  //     MagicSwordPoolPool.Pop()
  //     StoryChipPoolControl.Pop(int index, ...)
  //   掉落物组件：SoulControl / SoulRealControl / PotionDropControl / SkillDropControl /
  //     MagicSwordDropControl / StoryChipDropControl / BuddhaDrop / BreakForStoryDrop
  //   条目内容（已由 Pop 实参固定，无需 seed）：MagicSword{magicSwordName,Level,entrys,NmLevel}、
  //     MagicSwordEntry{magicSwordEntryName,level,values,isNightmare}、Potion{PotionName,elementType,Level}
  //   客户端拦截：GlobalParameter.AchievementAddProgress(AchievementType,int)（static）、
  //     Talent.MachineHeartRuneManager.OnAddSouls(int)（static）、SaveSystemControl.Save(...)、
  //     ChestControl.SetOpen/CopperLoot/SilverLoot/GoldLoot（private instance）。
  // ============================================================================================
  internal static class LootHookAdapter
  {
    // 实机逆向已完成：启用物品同步。
    public static readonly bool Ready = true;

    // 来源过滤：只同步“敌人死亡 / 宝箱”窗口内生成的掉落物（排除关卡静态掉落）。
    public static readonly bool FilterToLootSourceContext = true;
    // 敌人死亡真正刷掉落是在 EnemyControl.<IEDrop> 协程里（有延迟），宽限期要覆盖它。
    public const float LootSourceContextGraceSeconds = 2.5f;
    public static readonly ComponentHookSpec[] LootSourceContextTargets =
    {
      new ComponentHookSpec(typeof(EnemyControl), "Death"),
      new ComponentHookSpec(typeof(ChestControl), "SetOpen"),
      new ComponentHookSpec(typeof(ChestControl), "CopperLoot"),
      new ComponentHookSpec(typeof(ChestControl), "SilverLoot"),
      new ComponentHookSpec(typeof(ChestControl), "GoldLoot"),
    };

    // 实际被挂钩的掉落生成方法（类名 + 方法名； adapter 会挂该名字下所有返回 GameObject 的重载）。
    internal static readonly MethodHookSpec[] SpawnHookTargets =
    {
      new MethodHookSpec(typeof(SoulPoolControl), "Pop"),
      new MethodHookSpec(typeof(RedSoulPoolControl), "Pop"),
      new MethodHookSpec(typeof(PotionDropPool), "Pop"),
      new MethodHookSpec(typeof(SkillDropPool), "Pop"),
      new MethodHookSpec(typeof(MagicSwordPool), "Pop"),
      new MethodHookSpec(typeof(MagicSwordPoolPool), "Pop"),
      new MethodHookSpec(typeof(StoryChipPoolControl), "Pop"),
    };

    // 旧框架 API 占位：真实实现走 InstallCreationHooks。
    public static readonly ComponentHookSpec[] ComponentHookTargets = new ComponentHookSpec[0];
    public static readonly MethodHookSpec[] MethodHookTargets = new MethodHookSpec[0];

    private const string HarmonyId = "com.llamerrr.WarmSnowTogether.lootadapter";
    private static Harmony _harmony;
    private static bool _installed;
    private static readonly List<MethodInfo> _spawnMethods = new List<MethodInfo>();
    private static readonly Dictionary<MethodBase, int> _spawnMethodIndex = new Dictionary<MethodBase, int>();
    private static readonly Stack<CapturedCall> _captureStack = new Stack<CapturedCall>();
    private static Action<GameObject, LootDescriptor> _onCreated;
    private static Action<GameObject> _onConsumed;
    private static bool _consumeHooksInstalled;

    private sealed class CapturedCall
    {
      public MethodBase Method;
      public object[] Args;
    }

    // ============================================================================================
    // 1) 安装生成钩子 + 客户端副作用拦截
    // ============================================================================================
    public static bool InstallCreationHooks(Action<GameObject, LootDescriptor> onCreated)
    {
      if (_installed)
      {
        _onCreated = onCreated;
        return _spawnMethods.Count > 0;
      }
      _onCreated = onCreated;
      try
      {
        _harmony = new Harmony(HarmonyId);
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST][Loot] 创建 Harmony(lootadapter) 失败: " + ex.Message);
        return false;
      }

      int patched = 0;
      for (int i = 0; i < SpawnHookTargets.Length; i++)
      {
        MethodHookSpec spec = SpawnHookTargets[i];
        if (spec.TargetType == null || string.IsNullOrEmpty(spec.MethodName)) continue;
        MethodInfo[] methods = spec.TargetType.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int m = 0; m < methods.Length; m++)
        {
          MethodInfo candidate = methods[m];
          if (candidate.Name != spec.MethodName) continue;
          if (!typeof(GameObject).IsAssignableFrom(candidate.ReturnType)) continue;
          try
          {
            MethodInfo prefix = typeof(LootHookAdapter).GetMethod(
                "CaptureSpawnArgs", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo postfix = typeof(LootHookAdapter).GetMethod(
                "SpawnCreatedPostfix", BindingFlags.Static | BindingFlags.NonPublic);
            _harmony.Patch(candidate,
                prefix: new HarmonyMethod(prefix),
                postfix: new HarmonyMethod(postfix));
            _spawnMethodIndex[candidate] = _spawnMethods.Count;
            _spawnMethods.Add(candidate);
            patched++;
          }
          catch (Exception ex)
          {
            Logger.LogWarning("[WST][Loot] 挂钩子失败 " + spec.TargetType.Name + "." + candidate.Name + ": " + ex.Message);
          }
        }
      }

      InstallClientSideEffectSuppression();
      _installed = true;
      Logger.LogInfo("[WST][Loot] 真实掉落钩子已安装，共 " + patched + " 个生成方法。");
      return patched > 0;
    }

    private static void CaptureSpawnArgs(MethodBase __originalMethod, object[] __args)
    {
      try
      {
        if (__originalMethod == null) return;
        if (_captureStack.Count > 32) _captureStack.Clear();   // 异常保护
        CapturedCall call = new CapturedCall();
        call.Method = __originalMethod;
        call.Args = __args;
        _captureStack.Push(call);
      }
      catch (Exception)
      {
      }
    }

    private static void SpawnCreatedPostfix(MethodBase __originalMethod, ref GameObject __result)
    {
      try
      {
        if (_captureStack.Count == 0 || __result == null) return;
        CapturedCall call = _captureStack.Pop();
        if (call.Method != __originalMethod)
        {
          _captureStack.Clear();
          return;
        }
        if (!IsKnownDropObject(__result)) return;

        LootDescriptor descriptor;
        if (!TryEncodeDescriptor(call.Method, call.Args, out descriptor)) return;
        Action<GameObject, LootDescriptor> cb = _onCreated;
        if (cb != null) cb(__result, descriptor);
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST][Loot] 掉落生成 postfix 异常: " + ex.Message);
      }
    }

    // ============================================================================================
    // 2) 已知掉落物过滤 + 描述编码/解码
    // ============================================================================================
    private static bool IsKnownDropObject(GameObject go)
    {
      if (go == null) return false;
      if (go.GetComponent<SoulControl>() != null) return true;
      if (go.GetComponent<SoulRealControl>() != null) return true;
      if (go.GetComponent<PotionDropControl>() != null) return true;
      if (go.GetComponent<SkillDropControl>() != null) return true;
      if (go.GetComponent<MagicSwordDropControl>() != null) return true;
      if (go.GetComponent<StoryChipDropControl>() != null) return true;
      if (go.GetComponent<BuddhaDrop>() != null) return true;
      if (go.GetComponent<BreakForStoryDrop>() != null) return true;
      return false;
    }

    private static bool TryEncodeDescriptor(MethodBase method, object[] args, out LootDescriptor descriptor)
    {
      descriptor = null;
      int methodKey;
      if (!_spawnMethodIndex.TryGetValue(method, out methodKey)) return false;
      try
      {
        ParameterInfo[] parameters = method.GetParameters();
        if (args == null || args.Length != parameters.Length) return false;

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
          writer.Write((byte)2);              // 版本 2：args + 运行期字段快照
          writer.Write(methodKey);
          for (int i = 0; i < parameters.Length; i++)
          {
            if (!WriteArg(writer, parameters[i].ParameterType, args[i])) return false;
          }
          writer.Write(0);                    // 字段快照数量（EnrichDescriptor 稍后回填）
          writer.Flush();
          string data = Convert.ToBase64String(ms.ToArray());
          descriptor = new LootDescriptor((int)LootKind.Custom, data, Vector2.zero, 0f);
          return true;
        }
      }
      catch (Exception)
      {
        return false;
      }
    }

    // 支持的类型：int/float/bool/Vector2/Vector3/任意 enum/List<MagicSwordEntry>
    private static bool WriteArg(BinaryWriter writer, Type type, object value)
    {
      if (value == null)
      {
        writer.Write((byte)0);
        return true;
      }
      if (type.IsEnum)
      {
        writer.Write((byte)1);
        writer.Write(Convert.ToInt32(value));
        return true;
      }
      if (type == typeof(int))
      {
        writer.Write((byte)2);
        writer.Write((int)value);
        return true;
      }
      if (type == typeof(float))
      {
        writer.Write((byte)3);
        writer.Write((float)value);
        return true;
      }
      if (type == typeof(bool))
      {
        writer.Write((byte)4);
        writer.Write((bool)value);
        return true;
      }
      if (type == typeof(Vector3))
      {
        writer.Write((byte)5);
        Vector3 v = (Vector3)value;
        writer.Write(v.x);
        writer.Write(v.y);
        writer.Write(v.z);
        return true;
      }
      if (type == typeof(Vector2))
      {
        writer.Write((byte)6);
        Vector2 v2 = (Vector2)value;
        writer.Write(v2.x);
        writer.Write(v2.y);
        return true;
      }
      if (type == typeof(List<MagicSwordEntry>))
      {
        writer.Write((byte)7);
        List<MagicSwordEntry> entries = value as List<MagicSwordEntry>;
        if (entries == null)
        {
          writer.Write(-1);   // null 与空列表不同：null 表示游戏内部自己随机生成
          return true;
        }
        writer.Write(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
          MagicSwordEntry entry = entries[i];
          writer.Write(Convert.ToInt32(entry.magicSwordEntryName));
          writer.Write(entry.level);
          writer.Write(entry.values);
          writer.Write(entry.isNightmare);
        }
        return true;
      }
      return false;
    }

    private static object ReadArg(BinaryReader reader, Type type)
    {
      byte tag = reader.ReadByte();
      if (tag == 0) return null;
      if (tag == 1)
      {
        int enumValue = reader.ReadInt32();
        return type.IsEnum ? Enum.ToObject(type, enumValue) : (object)enumValue;
      }
      if (tag == 2) return reader.ReadInt32();
      if (tag == 3) return reader.ReadSingle();
      if (tag == 4) return reader.ReadBoolean();
      if (tag == 5) return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
      if (tag == 6) return new Vector2(reader.ReadSingle(), reader.ReadSingle());
      if (tag == 7)
      {
        int count = reader.ReadInt32();
        if (count < 0) return null;
        List<MagicSwordEntry> entries = new List<MagicSwordEntry>(Mathf.Max(0, count));
        for (int i = 0; i < count; i++)
        {
          MagicSwordEntry entry = new MagicSwordEntry();
          entry.magicSwordEntryName = (MagicSwordEntryName)reader.ReadInt32();
          entry.level = reader.ReadInt32();
          entry.values = reader.ReadSingle();
          entry.isNightmare = reader.ReadBoolean();
          entries.Add(entry);
        }
        return entries;
      }
      throw new InvalidDataException("unsupported arg tag " + tag);
    }

    // ------------------------------------------------------------------------------------------
    // 运行期字段快照：Pop 只创建对象，EnemyControl.<IEDrop> 随后还会改字段
    // （例如 SoulControl.price / index / isDirectToPlayer 在 Pop 之后才赋值）。
    // 所以房主在广播前（延迟 2 帧）再抓一次掉落组件的 public 字段，客户端在建镜像后照抄，
    // 保证“灵魂价格/词条/品质/数量”等与房主完全一致。
    // ------------------------------------------------------------------------------------------
    internal static Component FindDropComponent(GameObject go)
    {
      if (go == null) return null;
      Component c;
      c = go.GetComponent<SoulControl>(); if (c != null) return c;
      c = go.GetComponent<SoulRealControl>(); if (c != null) return c;
      c = go.GetComponent<PotionDropControl>(); if (c != null) return c;
      c = go.GetComponent<SkillDropControl>(); if (c != null) return c;
      c = go.GetComponent<MagicSwordDropControl>(); if (c != null) return c;
      c = go.GetComponent<StoryChipDropControl>(); if (c != null) return c;
      c = go.GetComponent<BuddhaDrop>(); if (c != null) return c;
      c = go.GetComponent<BreakForStoryDrop>(); if (c != null) return c;
      return null;
    }

    private sealed class FieldSnapshot
    {
      public string Path;
      public byte[] Payload;
    }

    public static void EnrichDescriptor(GameObject dropObject, LootDescriptor descriptor)
    {
      if (dropObject == null || descriptor == null || string.IsNullOrEmpty(descriptor.Data)) return;
      try
      {
        Component component = FindDropComponent(dropObject);
        if (component == null) return;
        byte[] oldBytes = Convert.FromBase64String(descriptor.Data);
        int methodKey;
        int argsEnd;
        using (MemoryStream readStream = new MemoryStream(oldBytes))
        using (BinaryReader reader = new BinaryReader(readStream))
        {
          reader.ReadByte();                       // version
          methodKey = reader.ReadInt32();
          if (methodKey < 0 || methodKey >= _spawnMethods.Count) return;
          ParameterInfo[] parameters = _spawnMethods[methodKey].GetParameters();
          for (int i = 0; i < parameters.Length; i++)
          {
            ReadArg(reader, parameters[i].ParameterType);
          }
          argsEnd = (int)readStream.Position;
        }

        List<FieldSnapshot> snapshots = new List<FieldSnapshot>();
        CollectFields(component.GetType(), component, string.Empty, snapshots, 0);
        using (MemoryStream writeStream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(writeStream))
        {
          writer.Write((byte)2);                   // version 2: args + field snapshot
          writer.Write(oldBytes, 1, argsEnd - 1);  // methodKey + args（跳过旧 version 字节）
          writer.Write(snapshots.Count);
          for (int i = 0; i < snapshots.Count; i++)
          {
            writer.Write(snapshots[i].Path);
            writer.Write(snapshots[i].Payload.Length);
            writer.Write(snapshots[i].Payload);
          }
          writer.Flush();
          descriptor.Data = Convert.ToBase64String(writeStream.ToArray());
        }
      }
      catch (Exception ex)
      {
        Logger.LogDebug("[WST][Loot] EnrichDescriptor 失败: " + ex.Message);
      }
    }

    private static void CollectFields(Type type, object owner, string prefix,
        List<FieldSnapshot> output, int depth)
    {
      if (type == null || owner == null || depth > 3) return;
      FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
      for (int i = 0; i < fields.Length; i++)
      {
        FieldInfo field = fields[i];
        if (field.IsStatic) continue;
        Type fieldType = field.FieldType;
        if (fieldType.IsEnum || fieldType == typeof(int) || fieldType == typeof(float)
            || fieldType == typeof(bool) || fieldType == typeof(Vector2) || fieldType == typeof(Vector3)
            || fieldType == typeof(List<MagicSwordEntry>))
        {
          object value;
          try
          {
            value = field.GetValue(owner);
          }
          catch (Exception)
          {
            continue;
          }
          byte[] payload = EncodeValue(fieldType, value);
          if (payload == null) continue;
          FieldSnapshot snap = new FieldSnapshot();
          snap.Path = prefix + field.Name;
          snap.Payload = payload;
          output.Add(snap);
        }
        else if (fieldType.IsValueType && !fieldType.IsPrimitive)
        {
          // Potion / MagicSword 这类结构体：展开其 public 字段
          object nested;
          try
          {
            nested = field.GetValue(owner);
          }
          catch (Exception)
          {
            continue;
          }
          CollectFields(fieldType, nested, prefix + field.Name + ".", output, depth + 1);
        }
      }
    }

    private static byte[] EncodeValue(Type type, object value)
    {
      try
      {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(ms))
        {
          if (!WriteArg(writer, type, value)) return null;
          writer.Flush();
          return ms.ToArray();
        }
      }
      catch (Exception)
      {
        return null;
      }
    }

    private static void ApplyFieldSnapshots(BinaryReader reader, GameObject mirror)
    {
      Component component = FindDropComponent(mirror);
      if (component == null) return;
      int count = reader.ReadInt32();
      for (int i = 0; i < count; i++)
      {
        string path = reader.ReadString();
        int length = reader.ReadInt32();
        byte[] payload = reader.ReadBytes(length);
        try
        {
          ApplyOneField(component, path, payload);
        }
        catch (Exception)
        {
        }
      }
    }

    private static void ApplyOneField(Component component, string path, byte[] payload)
    {
      string[] parts = path.Split('.');
      FieldInfo field = component.GetType().GetField(parts[0], BindingFlags.Instance | BindingFlags.Public);
      if (field == null) return;
      if (parts.Length == 1)
      {
        object value = DecodeValue(payload, field.FieldType);
        if (value != null || !field.FieldType.IsValueType) field.SetValue(component, value);
        return;
      }
      if (parts.Length == 2)
      {
        object boxed = field.GetValue(component);
        if (boxed == null) return;
        FieldInfo sub = field.FieldType.GetField(parts[1], BindingFlags.Instance | BindingFlags.Public);
        if (sub == null) return;
        object value = DecodeValue(payload, sub.FieldType);
        if (value != null || !sub.FieldType.IsValueType) sub.SetValue(boxed, value);
        field.SetValue(component, boxed);
      }
    }

    private static object DecodeValue(byte[] payload, Type targetType)
    {
      using (MemoryStream ms = new MemoryStream(payload))
      using (BinaryReader reader = new BinaryReader(ms))
      {
        object value = ReadArg(reader, targetType);
        if (value == null) return null;
        if (targetType.IsEnum && !(value is Enum)) return Enum.ToObject(targetType, Convert.ToInt32(value));
        if (value.GetType() != targetType && !targetType.IsEnum)
        {
          try
          {
            return Convert.ChangeType(value, targetType);
          }
          catch (Exception)
          {
            return null;
          }
        }
        return value;
      }
    }

    // 旧 API：本框架不再通过 GameObject 反射取内容，返回 false 让 manager 保持原 descriptor。
    public static bool TryBuildDescriptor(GameObject dropObject, out LootDescriptor descriptor)
    {
      descriptor = null;
      return false;
    }

    // 旧 API：拾取判定由 LootSyncManager 的“对象池回收检测（父节点/激活状态）”统一处理。
    public static bool IsPickedUp(GameObject dropObject)
    {
      return false;
    }

    // ============================================================================================
    // 3) 客户端镜像：解码 (方法, 实参) 并在客户端同一个 Pool 单例上重放 Pop
    // ============================================================================================
    public static GameObject CreateMirror(LootDescriptor descriptor)
    {
      if (descriptor == null || string.IsNullOrEmpty(descriptor.Data)) return null;
      try
      {
        byte[] bytes = Convert.FromBase64String(descriptor.Data);
        using (MemoryStream ms = new MemoryStream(bytes))
        using (BinaryReader reader = new BinaryReader(ms))
        {
          byte version = reader.ReadByte();
          if (version != 1 && version != 2) return null;
          int methodKey = reader.ReadInt32();
          if (methodKey < 0 || methodKey >= _spawnMethods.Count) return null;
          MethodInfo method = _spawnMethods[methodKey];
          ParameterInfo[] parameters = method.GetParameters();
          object[] args = new object[parameters.Length];
          for (int i = 0; i < parameters.Length; i++)
          {
            args[i] = ReadArg(reader, parameters[i].ParameterType);
          }

          object instance = null;
          if (!method.IsStatic)
          {
            FieldInfo instanceField = method.DeclaringType.GetField("instance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (instanceField != null) instance = instanceField.GetValue(null);
            if (instance == null)
            {
              Logger.LogWarning("[WST][Loot] 客户端缺少池子单例 " + method.DeclaringType.Name + ".instance，无法建镜像。");
              return null;
            }
          }

          object result = method.Invoke(instance, args);
          GameObject mirror = result as GameObject;
          if (mirror != null && !IsKnownDropObject(mirror))
          {
            // 生成出来的不是已知掉落物（可能是纯特效），丢弃。
            UnityEngine.Object.Destroy(mirror);
            return null;
          }
          if (mirror != null && version >= 2)
          {
            try
            {
              ApplyFieldSnapshots(reader, mirror);
            }
            catch (Exception fieldEx)
            {
              Logger.LogDebug("[WST][Loot] 应用字段快照失败: " + fieldEx.Message);
            }
          }
          return mirror;
        }
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST][Loot] CreateMirror 失败: " + ex.GetType().Name + ": " + ex.Message);
        return null;
      }
    }

    // ============================================================================================
    // 4) 客户端副作用拦截 / 本地宝箱屏蔽
    // ============================================================================================
    // 拾取上报：拾取完成时游戏会把掉落物归还对象池（SetActive(false)/重新挂父节点），
    // 掉落组件的 OnDisable 正好在这一刻触发。给它挂 postfix，比轮询更即时、更准确。
    public static bool InstallConsumeHooks(Action<GameObject> onConsumed)
    {
      _onConsumed = onConsumed;
      if (_consumeHooksInstalled) return true;
      _consumeHooksInstalled = true;
      try
      {
        if (_harmony == null) _harmony = new Harmony(HarmonyId);
        MethodInfo postfix = typeof(LootHookAdapter).GetMethod(
            "DropDisabledPostfix", BindingFlags.Static | BindingFlags.NonPublic);
        Type[] dropTypes =
        {
          typeof(SoulControl), typeof(SoulRealControl), typeof(PotionDropControl),
          typeof(SkillDropControl), typeof(MagicSwordDropControl),
          typeof(StoryChipDropControl), typeof(BuddhaDrop),
        };
        int patched = 0;
        for (int i = 0; i < dropTypes.Length; i++)
        {
          try
          {
            MethodInfo onDisable = dropTypes[i].GetMethod("OnDisable",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (onDisable == null) continue;
            _harmony.Patch(onDisable, postfix: new HarmonyMethod(postfix));
            patched++;
          }
          catch (Exception ex)
          {
            Logger.LogWarning("[WST][Loot] 挂拾取钩子失败 " + dropTypes[i].Name + ": " + ex.Message);
          }
        }
        Logger.LogInfo("[WST][Loot] 拾取上报钩子已安装，共 " + patched + " 个掉落类。");
        return true;
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST][Loot] 安装拾取上报钩子失败: " + ex.Message);
        return false;
      }
    }

    private static void DropDisabledPostfix(object __instance)
    {
      try
      {
        Component component = __instance as Component;
        if (component == null) return;
        LootSyncManager loot = LootSyncManager.Instance;
        if (loot == null || !loot.IsRunning) return;
        // 场景切换时旧场景对象会被批量 disable，不能当成“被捡走”。
        WorldSyncManager world = WorldSyncManager.Instance;
        if (world != null && world.IsClientLoading) return;
        Action<GameObject> cb = _onConsumed;
        if (cb != null) cb(component.gameObject);
      }
      catch (Exception)
      {
      }
    }

    private static void InstallClientSideEffectSuppression()
    {
      TryPatchStatic(typeof(GlobalParameter), "AchievementAddProgress", "SuppressAchievementPrefix");
      TryPatchStatic(typeof(Talent.MachineHeartRuneManager), "OnAddSouls", "SuppressTalentSoulPrefix");
      TryPatchInstance(typeof(SaveSystemControl), "Save", "SuppressSavePrefix");
      TryPatchInstance(typeof(ChestControl), "SetOpen", "SuppressChestPrefix");
      TryPatchInstance(typeof(ChestControl), "CopperLoot", "SuppressChestPrefix");
      TryPatchInstance(typeof(ChestControl), "SilverLoot", "SuppressChestPrefix");
      TryPatchInstance(typeof(ChestControl), "GoldLoot", "SuppressChestPrefix");
    }

    private static void TryPatchStatic(Type type, string methodName, string prefixName)
    {
      try
      {
        MethodInfo prefixMethod = typeof(LootHookAdapter).GetMethod(prefixName,
            BindingFlags.Static | BindingFlags.NonPublic);
        if (prefixMethod == null) return;
        MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
          if (methods[i].Name != methodName) continue;
          _harmony.Patch(methods[i], prefix: new HarmonyMethod(prefixMethod));
          Logger.LogDebug("[WST][Loot] 已拦截 " + type.Name + "." + methodName);
        }
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST][Loot] 拦截 " + type.Name + "." + methodName + " 失败: " + ex.Message);
      }
    }

    private static void TryPatchInstance(Type type, string methodName, string prefixName)
    {
      try
      {
        MethodInfo prefixMethod = typeof(LootHookAdapter).GetMethod(prefixName,
            BindingFlags.Static | BindingFlags.NonPublic);
        if (prefixMethod == null) return;
        MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
          if (methods[i].Name != methodName) continue;
          _harmony.Patch(methods[i], prefix: new HarmonyMethod(prefixMethod));
          Logger.LogDebug("[WST][Loot] 已拦截 " + type.Name + "." + methodName);
        }
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST][Loot] 拦截 " + type.Name + "." + methodName + " 失败: " + ex.Message);
      }
    }

    private static bool ShouldSuppressLocalSideEffects()
    {
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient) return false;
        LootSyncManager loot = LootSyncManager.Instance;
        return loot != null && loot.IsRunning;
      }
      catch (Exception)
      {
        return false;
      }
    }

    private static bool SuppressAchievementPrefix()
    {
      return !ShouldSuppressLocalSideEffects();
    }

    private static bool SuppressTalentSoulPrefix()
    {
      return !ShouldSuppressLocalSideEffects();
    }

    private static bool SuppressSavePrefix()
    {
      return !ShouldSuppressLocalSideEffects();
    }

    // 客户端不能开自己本地那份宝箱（否则会本地凭空生成一份掉落），内容由房主掉落镜像提供。
    private static bool SuppressChestPrefix()
    {
      return !ShouldSuppressLocalSideEffects();
    }

    // 保留旧字段供后续扩展（不参与运行）。
    internal static readonly string[] SeedOrModifierHints =
    {
      "MagicSword.magicSwordName/Level/magicSwordEntrys/NmLevel（真实字段，掉落时已由 Pop 实参定死，无需 seed）",
      "MagicSwordEntry.magicSwordEntryName/level/values/isNightmare（已随 MagicSwordPool.Pop 的 entrys 实参同步）",
    };
  }

  // 掉落物组件生命周期钩子描述（LootSyncManager fallback 用）。
  internal struct ComponentHookSpec
  {
    public Type TargetType;
    public string MethodName;

    public ComponentHookSpec(Type targetType, string methodName)
    {
      TargetType = targetType;
      MethodName = methodName;
    }
  }

  // 生成方法钩子描述（LootSyncManager fallback / 来源上下文用）。
  internal struct MethodHookSpec
  {
    public Type TargetType;
    public string MethodName;

    public MethodHookSpec(Type targetType, string methodName)
    {
      TargetType = targetType;
      MethodName = methodName;
    }
  }
}
