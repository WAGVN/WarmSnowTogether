using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using WarmSnowTogether.Networking.Packets;
using WarmSnowTogether.Networking.Steam;
using WarmSnowTogether.Networking.Sync;
using WarmSnowTogether.Systems.Logging;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking.Loot
{
  // [WST Loot Sync v1] 房主权威的掉落物同步。
  //
  // 目标：所有玩家都“以房主的物品为标准”——房主端生成的掉落物在客户端有镜像，能看到、能捡、
  //       捡到的物品由客户端本地游戏逻辑正常入包/使用；任一端捡走之后，其它端的镜像删除
  //       （单份、先到先得，房主裁决）。
  //
  // 流程：
  //   房主：监听掉落物生成（LootHookAdapter.ComponentHookTargets / MethodHookTargets）
  //         -> TryBuildDescriptor 得到 LootDescriptor，分配 NetId，广播 LootSpawnPacket
  //         -> 轮询（0.2s）检测房主自己捡走/销毁的掉落物，广播 LootConsumedPacket
  //         -> 收到客户端 LootConsumedPacket：删除房主原物并向其它客户端广播
  //         -> 新客户端加入/加载完场景后请求快照：SendSnapshotTo 补发当前所有掉落物
  //   客户端：收到 LootSpawnPacket -> LootHookAdapter.CreateMirror 在本地重建同样的掉落物
  //         -> 本地拾取逻辑照常运行（物品进本地背包、本地生效）
  //         -> LootHookAdapter 的拾取回调（或镜像对象消失时）向房主发 LootConsumedPacket
  //         -> 收到 LootConsumedPacket 删除本地镜像
  //
  // 逆向接入层未完成（LootHookAdapter.Ready == false）时，本管理器只打印一次提示，完全不参与运行。
  public class LootSyncManager : MonoBehaviour
  {
    private static LootSyncManager _instance;

    public static LootSyncManager Instance
    {
      get
      {
        if (_instance == null)
        {
          GameObject go = new GameObject("LootSyncManager");
          _instance = go.AddComponent<LootSyncManager>();
        }
        return _instance;
      }
    }

    private const string HarmonyId = "com.llamerrr.WarmSnowTogether.lootsync";
    // 房主轮询掉落物是否被捡走/销毁的间隔（比敌人 5Hz 稍慢，掉落物不需要那么快）。
    private const float PollInterval = 0.2f;
    // 单场景最多跟踪的掉落物数量（防止异常刷物导致无限增长）。
    private const int MaxTrackedLoot = 512;

    // 一条被跟踪的掉落物（房主端是原物，客户端是镜像）。
    private sealed class TrackedLoot
    {
      public int NetId;
      public GameObject GameObject;
      public LootDescriptor Descriptor;
      public int InstanceId;
      // 登记时的父节点/激活状态：WarmSnow 的 *PoolControl 拾取后会 Push 回池子
      // （常见表现 = SetActive(false) 和/或重新挂到池子根节点），而不是 Destroy，
      // 所以框架用“父节点变化 / 取消激活”作为拾取信号之一。
      public Transform InitialParent;
      public bool InitialActive;
      public float RegisteredAt;
    }

    // host: netId -> 房主原物
    private readonly Dictionary<int, TrackedLoot> _hostLoot = new Dictionary<int, TrackedLoot>();
    // client: netId -> 本地镜像
    private readonly Dictionary<int, TrackedLoot> _clientMirrors = new Dictionary<int, TrackedLoot>();
    // 已登记过的 Unity 实例 id，避免同一个掉落物被多个钩子重复登记。
    private readonly HashSet<int> _knownInstances = new HashSet<int>();
    // client: 已经收到“被捡走”的 netId（乐观删除 + 防乱序：先到 Consumed 再到 Spawn 时直接忽略）。
    private readonly HashSet<int> _consumedIds = new HashSet<int>();
    private readonly List<int> _removeBuffer = new List<int>();
    // host: 最近一次场景加载时间（场景卸载时旧掉落物会被批量 disable，避免误报被捡走）。
    private float _lastSceneLoadTime;

    private int _nextNetId = 1;
    private bool _initialised;
    private bool _hooksInstalled;
    private bool _notReadyLogged;
    private bool _capWarned;
    private bool _firstHostLootLogged;
    private bool _firstClientMirrorLogged;
    private bool _mirrorUnavailableLogged;
    private float _nextPollTime;
    private float _nextErrorLogTime;

    private static Harmony _harmony;
    private static MethodInfo _componentSpawnPostfixMethod;
    private static MethodInfo _methodSpawnPostfixMethod;

    // ==== 掉落来源上下文（见 LootHookAdapter.FilterToLootSourceContext）====
    // >0 表示当前正处在“敌人死亡/开宝箱”的调用栈里；宽限期覆盖协程/延迟生成。
    private static int _lootSourceDepth;
    private static float _lootSourceGraceUntil;
    private static bool _hasLootSourceContextTargets;
    private static MethodInfo _lootSourceEnterMethod;
    private static MethodInfo _lootSourceExitMethod;

    private bool SyncActive
    {
      get { return _initialised && LootHookAdapter.Ready; }
    }

    // 供 LootHookAdapter 的客户端副作用拦截判断“是否处于联机物品同步会话中”。
    public bool IsRunning
    {
      get { return SyncActive; }
    }

    private void Awake()
    {
      DontDestroyOnLoad(gameObject);
      SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
      SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // 由 WarmSnowTogetherMod.Start 调用一次。
    public void Initialise()
    {
      if (_initialised) return;
      _initialised = true;

      if (!LootHookAdapter.Ready)
      {
        if (!_notReadyLogged)
        {
          _notReadyLogged = true;
          Logger.LogInfo("[WST][Loot] 物品同步框架已加载，但逆向接入层未完成（LootHookAdapter.Ready=false），功能保持关闭。");
        }
        return;
      }

      try
      {
        InstallHooks();
        Logger.LogInfo("[WST][Loot] 物品同步已启用（房主权威 + 客户端镜像拾取）。");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST][Loot] 初始化钩子失败: " + ex.GetType().Name + ": " + ex.Message);
      }
    }

    // ------------------------------------------------------------------ hook installation

    private void InstallHooks()
    {
      if (_hooksInstalled) return;
      _hooksInstalled = true;

      try
      {
        _harmony = new Harmony(HarmonyId);
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST][Loot] 创建 Harmony 实例失败: " + ex.Message);
        return;
      }

      _componentSpawnPostfixMethod = typeof(LootSyncManager).GetMethod(
          "ComponentSpawnPostfix", BindingFlags.Static | BindingFlags.NonPublic);
      _methodSpawnPostfixMethod = typeof(LootSyncManager).GetMethod(
          "MethodSpawnPostfix", BindingFlags.Static | BindingFlags.NonPublic);

      // 0) 首选：adapter 的真实游戏钩子（各 *PoolControl.Pop，携带生成实参）。
      bool adapterHooks = false;
      try
      {
        adapterHooks = LootHookAdapter.InstallCreationHooks(OnHostDropCreated);
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST][Loot] adapter 创建钩子安装失败: " + ex.Message);
      }

      if (!adapterHooks)
      {
      // 1) 掉落物组件的生命周期方法（推荐入口）
      ComponentHookSpec[] componentSpecs = LootHookAdapter.ComponentHookTargets;
      if (componentSpecs != null)
      {
        for (int i = 0; i < componentSpecs.Length; i++)
        {
          ComponentHookSpec spec = componentSpecs[i];
          if (spec.TargetType == null || string.IsNullOrEmpty(spec.MethodName)) continue;
          try
          {
            MethodInfo target = spec.TargetType.GetMethod(spec.MethodName,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (target == null)
            {
              Logger.LogWarning("[WST][Loot] 找不到钩子方法: " + spec.TargetType.Name + "." + spec.MethodName);
              continue;
            }
            if (_componentSpawnPostfixMethod == null) continue;
            _harmony.Patch(target, postfix: new HarmonyMethod(_componentSpawnPostfixMethod));
            Logger.LogDebug("[WST][Loot] 已挂掉落物生命周期钩子: " + spec.TargetType.Name + "." + spec.MethodName);
          }
          catch (Exception ex)
          {
            Logger.LogWarning("[WST][Loot] 挂钩子失败 " + spec.TargetType.Name + "." + spec.MethodName + ": " + ex.Message);
          }
        }
      }

      // 2) 生成方法返回值（可选入口）
      MethodHookSpec[] methodSpecs = LootHookAdapter.MethodHookTargets;
      if (methodSpecs != null)
      {
        for (int i = 0; i < methodSpecs.Length; i++)
        {
          MethodHookSpec spec = methodSpecs[i];
          if (spec.TargetType == null || string.IsNullOrEmpty(spec.MethodName)) continue;
          try
          {
            MethodInfo[] candidates = spec.TargetType.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            int patched = 0;
            for (int c = 0; c < candidates.Length; c++)
            {
              MethodInfo candidate = candidates[c];
              if (candidate.Name != spec.MethodName) continue;
              if (!typeof(GameObject).IsAssignableFrom(candidate.ReturnType)) continue;
              if (_methodSpawnPostfixMethod == null) continue;
              _harmony.Patch(candidate, postfix: new HarmonyMethod(_methodSpawnPostfixMethod));
              patched++;
            }
            if (patched == 0)
            {
              Logger.LogWarning("[WST][Loot] 没有找到可挂的生成方法: " + spec.TargetType.Name + "." + spec.MethodName);
            }
            else
            {
              Logger.LogDebug("[WST][Loot] 已挂生成方法钩子: " + spec.TargetType.Name + "." + spec.MethodName + " x" + patched);
            }
          }
          catch (Exception ex)
          {
            Logger.LogWarning("[WST][Loot] 挂生成方法钩子失败 " + spec.TargetType.Name + "." + spec.MethodName + ": " + ex.Message);
          }
        }
      }
      }   // end if (!adapterHooks)

      // 3) 掉落来源上下文：只在“敌人死亡 / 开宝箱”窗口内登记掉落物，
      //    关卡预设的静态掉落（不经过这些入口生成）不会被同步。
      InstallLootSourceContextHooks();

      // 4) 拾取完成回调（客户端用它通知房主）
      bool consumeHooks = false;
      try
      {
        consumeHooks = LootHookAdapter.InstallConsumeHooks(OnLocalDropConsumed);
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST][Loot] 安装拾取回调失败: " + ex.Message);
      }
      if (!consumeHooks)
      {
        Logger.LogWarning("[WST][Loot] consume hooks not installed; fallback = pool-return detection (parent change / deactivated).");
      }
    }

    // 给 LootHookAdapter.LootSourceContextTargets 里的入口方法打 prefix/postfix：
    //   prefix: _lootSourceDepth++
    //   postfix: _lootSourceDepth-- 并给一个宽限窗口（覆盖协程/延迟生成的掉落物）
    private void InstallLootSourceContextHooks()
    {
      if (!LootHookAdapter.FilterToLootSourceContext) return;

      ComponentHookSpec[] specs = LootHookAdapter.LootSourceContextTargets;
      if (specs == null || specs.Length == 0)
      {
        Logger.LogWarning("[WST][Loot] 没有配置掉落来源入口，将同步所有来源的掉落物（包括静态掉落）。");
        return;
      }

      _lootSourceEnterMethod = typeof(LootSyncManager).GetMethod(
          "LootSourceContextEnter", BindingFlags.Static | BindingFlags.NonPublic);
      _lootSourceExitMethod = typeof(LootSyncManager).GetMethod(
          "LootSourceContextExit", BindingFlags.Static | BindingFlags.NonPublic);
      if (_lootSourceEnterMethod == null || _lootSourceExitMethod == null) return;

      _hasLootSourceContextTargets = true;
      for (int i = 0; i < specs.Length; i++)
      {
        ComponentHookSpec spec = specs[i];
        if (spec.TargetType == null || string.IsNullOrEmpty(spec.MethodName)) continue;
        try
        {
          MethodInfo target = spec.TargetType.GetMethod(spec.MethodName,
              BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
          if (target == null)
          {
            Logger.LogWarning("[WST][Loot] 找不到掉落来源入口: " + spec.TargetType.Name + "." + spec.MethodName);
            continue;
          }
          _harmony.Patch(target,
              prefix: new HarmonyMethod(_lootSourceEnterMethod),
              postfix: new HarmonyMethod(_lootSourceExitMethod));
          Logger.LogDebug("[WST][Loot] 已挂掉落来源入口: " + spec.TargetType.Name + "." + spec.MethodName);
        }
        catch (Exception ex)
        {
          Logger.LogWarning("[WST][Loot] 挂掉落来源入口失败 " + spec.TargetType.Name + "." + spec.MethodName + ": " + ex.Message);
        }
      }
    }

    private static void LootSourceContextEnter()
    {
      _lootSourceDepth++;
    }

    private static void LootSourceContextExit()
    {
      if (_lootSourceDepth > 0) _lootSourceDepth--;
      _lootSourceGraceUntil = Time.unscaledTime + LootHookAdapter.LootSourceContextGraceSeconds;
    }

    private static bool IsInLootSourceContext()
    {
      if (!_hasLootSourceContextTargets) return true;   // 未配置入口时不过滤（保守）
      return _lootSourceDepth > 0 || Time.unscaledTime <= _lootSourceGraceUntil;
    }

    // 掉落物组件生命周期 postfix（ComponentHookTargets）：
    // 注意参数用 object，Harmony 会绑定到目标实例（各种掉落类都能复用同一个 postfix）。
    private static void ComponentSpawnPostfix(object __instance)
    {
      try
      {
        Component component = __instance as Component;
        if (component == null) return;
        LootSyncManager manager = _instance;
        if (manager == null || !manager.SyncActive) return;
        manager.OnPotentialDropCreated(component.gameObject);
      }
      catch (Exception ex)
      {
        LootSyncManager manager = _instance;
        if (manager != null) manager.LogErrorThrottled("[WST][Loot] 生命周期 postfix 异常: " + ex.Message);
      }
    }

    // 生成方法 postfix（MethodHookTargets）：__result 是生成出来的 GameObject。
    private static void MethodSpawnPostfix(ref GameObject __result)
    {
      try
      {
        LootSyncManager manager = _instance;
        if (manager == null || !manager.SyncActive) return;
        manager.OnPotentialDropCreated(__result);
      }
      catch (Exception ex)
      {
        LootSyncManager manager = _instance;
        if (manager != null) manager.LogErrorThrottled("[WST][Loot] 生成方法 postfix 异常: " + ex.Message);
      }
    }

    // ------------------------------------------------------------------ host side

    // 旧入口（fallback）：从 GameObject 反射构建 descriptor。
    private void OnPotentialDropCreated(GameObject dropObject)
    {
      if (dropObject == null) return;
      LootDescriptor descriptor;
      if (LootHookAdapter.TryBuildDescriptor(dropObject, out descriptor) && descriptor != null)
      {
        RegisterHostLoot(dropObject, descriptor);
      }
    }

    // adapter 的真实创建钩子回调：descriptor 已包含 (生成方法, 实参)，镜像端可原样重放 Pop。
    private void OnHostDropCreated(GameObject dropObject, LootDescriptor descriptor)
    {
      if (dropObject == null || descriptor == null) return;
      RegisterHostLoot(dropObject, descriptor, true);
    }

    // 房主端：登记一个掉落物并广播。来源过滤/去重/上限都在这里统一处理。
    // deferBroadcast=true 时延迟 2 帧再广播：等 EnemyControl.<IEDrop> 把 price/index 等字段改完，
    // 再由 LootHookAdapter.EnrichDescriptor 抓一次字段快照（否则灵魂价格会是 0）。
    private void RegisterHostLoot(GameObject dropObject, LootDescriptor descriptor, bool deferBroadcast = false)
    {
      NetworkManager network = NetworkManager.Instance;
      if (network == null || !network.IsHost) return;   // 客户端只通过 LootSpawnPacket 建镜像

      // 来源过滤：只同步“敌人死亡掉落 / 宝箱掉落”窗口内生成的东西；
      // 关卡预设的静态掉落（GenerateDropPoints 之类）不经过这些入口，因此不会被登记。
      if (!IsInLootSourceContext())
      {
        return;
      }

      int instanceId;
      try
      {
        instanceId = dropObject.GetInstanceID();
      }
      catch (Exception)
      {
        return;
      }
      if (_knownInstances.Contains(instanceId)) return;

      if (_hostLoot.Count >= MaxTrackedLoot)
      {
        if (!_capWarned)
        {
          _capWarned = true;
          Logger.LogWarning("[WST][Loot] 房主掉落物跟踪数量达到上限 " + MaxTrackedLoot + "，后续掉落物不再同步。");
        }
        return;
      }
      _knownInstances.Add(instanceId);

      try
      {
        Transform t = dropObject.transform;
        descriptor.Position = new Vector2(t.position.x, t.position.y);
        descriptor.Rotation = t.eulerAngles.z;
      }
      catch (Exception)
      {
      }

      int netId = _nextNetId++;
      TrackedLoot tracked = new TrackedLoot();
      tracked.NetId = netId;
      tracked.GameObject = dropObject;
      tracked.Descriptor = descriptor;
      tracked.InstanceId = instanceId;
      tracked.RegisteredAt = Time.unscaledTime;
      try
      {
        tracked.InitialParent = dropObject.transform.parent;
        tracked.InitialActive = dropObject.activeSelf;
      }
      catch (Exception)
      {
      }
      _hostLoot[netId] = tracked;

      if (deferBroadcast)
      {
        StartCoroutine(BroadcastHostLootAfterFrames(netId));
      }
      else
      {
        BroadcastHostLoot(netId);
      }
    }

    private System.Collections.IEnumerator BroadcastHostLootAfterFrames(int netId)
    {
      // 等 IEDrop 在这一帧/下一帧把 SoulControl.price/index、MagicSword 等字段写完。
      yield return null;
      yield return null;
      BroadcastHostLoot(netId);
    }

    private void BroadcastHostLoot(int netId)
    {
      TrackedLoot tracked;
      if (!_hostLoot.TryGetValue(netId, out tracked) || tracked == null) return;
      GameObject dropObject = tracked.GameObject;
      if (dropObject == null)
      {
        RemoveHostLoot(netId, true, false);
        return;
      }
      try
      {
        Transform t = dropObject.transform;
        tracked.Descriptor.Position = new Vector2(t.position.x, t.position.y);
        tracked.Descriptor.Rotation = t.eulerAngles.z;
      }
      catch (Exception)
      {
      }
      try
      {
        LootHookAdapter.EnrichDescriptor(dropObject, tracked.Descriptor);
      }
      catch (Exception ex)
      {
        Logger.LogDebug("[WST][Loot] EnrichDescriptor 调用失败: " + ex.Message);
      }

      HostController host = GetHostController();
      if (host == null)
      {
        _hostLoot.Remove(netId);
        _knownInstances.Remove(tracked.InstanceId);
        return;
      }

      try
      {
        host.Broadcast(BuildSpawnPacket(tracked), 0UL, true);
      }
      catch (Exception ex)
      {
        LogErrorThrottled("[WST][Loot] 广播掉落物失败: " + ex.Message);
        _hostLoot.Remove(netId);
        _knownInstances.Remove(tracked.InstanceId);
        return;
      }

      if (!_firstHostLootLogged)
      {
        _firstHostLootLogged = true;
        Logger.LogInfo("[WST][Loot] 首个掉落物已同步: " + tracked.Descriptor.ToString());
      }
      else
      {
        Logger.LogDebug("[WST][Loot] 已同步掉落物 netId=" + netId + " " + tracked.Descriptor.ToString());
      }
    }

    private void HostPoll()
    {
      if (_hostLoot.Count == 0) return;
      _removeBuffer.Clear();
      foreach (KeyValuePair<int, TrackedLoot> entry in _hostLoot)
      {
        TrackedLoot tracked = entry.Value;
        if (tracked == null || tracked.GameObject == null)
        {
          _removeBuffer.Add(entry.Key);
          continue;
        }
        try
        {
          if (IsLootGone(tracked))
          {
            _removeBuffer.Add(entry.Key);
          }
        }
        catch (Exception ex)
        {
          LogErrorThrottled("[WST][Loot] IsPickedUp 异常: " + ex.Message);
        }
      }
      for (int i = 0; i < _removeBuffer.Count; i++)
      {
        RemoveHostLoot(_removeBuffer[i], true, false);
      }
    }

    // 删除房主端的一条掉落物，并（可选）通知所有客户端该 netId 已被捡走。
    // destroyIfAlive：拾取/回收的情况不要 Destroy（游戏自己会把它归还对象池）；
    //                客户端远程捡走的情况才需要销毁房主原物。
    private void RemoveHostLoot(int netId, bool notifyClients, bool destroyIfAlive = true)
    {
      TrackedLoot tracked;
      if (!_hostLoot.TryGetValue(netId, out tracked)) return;
      _hostLoot.Remove(netId);
      if (tracked != null)
      {
        // 池对象会被重复 Pop（同一个 GameObject 复用），必须放开 instance id 去重，
        // 否则这只池对象下一次被生成的新掉落物永远不会同步。
        _knownInstances.Remove(tracked.InstanceId);
      }

      if (destroyIfAlive && tracked != null && tracked.GameObject != null)
      {
        try
        {
          UnityEngine.Object.Destroy(tracked.GameObject);
        }
        catch (Exception)
        {
        }
      }

      if (!notifyClients) return;
      HostController host = GetHostController();
      if (host == null) return;
      try
      {
        LootConsumedPacket packet = new LootConsumedPacket();
        packet.NetId = netId;
        packet.ByPlayerId = GetLocalSteamId();
        host.Broadcast(packet, 0UL, true);
      }
      catch (Exception ex)
      {
        LogErrorThrottled("[WST][Loot] 广播 LootConsumed 失败: " + ex.Message);
      }
    }

    // 新客户端加入 / 客户端加载完场景后：把房主当前所有掉落物补发一遍。
    public void SendSnapshotTo(ulong targetSteamId)
    {
      if (!SyncActive) return;
      NetworkManager network = NetworkManager.Instance;
      if (network == null || !network.IsHost) return;
      if (_hostLoot.Count == 0)
      {
        Logger.LogInfo("[WST][Loot] 掉落物快照: 当前没有掉落物。");
        return;
      }

      int sent = 0;
      foreach (KeyValuePair<int, TrackedLoot> entry in _hostLoot)
      {
        TrackedLoot tracked = entry.Value;
        if (tracked == null || tracked.GameObject == null) continue;
        try
        {
          RefreshDescriptorFromObject(tracked);
          network.SendPacket(targetSteamId, BuildSpawnPacket(tracked), true);
          sent++;
        }
        catch (Exception ex)
        {
          LogErrorThrottled("[WST][Loot] 发送掉落物快照失败: " + ex.Message);
        }
      }
      Logger.LogInfo("[WST][Loot] 掉落物快照已发送给 " + targetSteamId + "，共 " + sent + " 个。");
    }

    private void RefreshDescriptorFromObject(TrackedLoot tracked)
    {
      if (tracked == null || tracked.GameObject == null) return;
      LootDescriptor fresh;
      if (LootHookAdapter.TryBuildDescriptor(tracked.GameObject, out fresh) && fresh != null)
      {
        try
        {
          Transform t = tracked.GameObject.transform;
          fresh.Position = new Vector2(t.position.x, t.position.y);
          fresh.Rotation = t.eulerAngles.z;
        }
        catch (Exception)
        {
        }
        tracked.Descriptor = fresh;
      }
    }

    private static LootSpawnPacket BuildSpawnPacket(TrackedLoot tracked)
    {
      LootSpawnPacket packet = new LootSpawnPacket();
      packet.NetId = tracked.NetId;
      LootDescriptor descriptor = tracked.Descriptor;
      packet.Kind = descriptor != null ? descriptor.Kind : 0;
      packet.Data = descriptor != null ? descriptor.Data : string.Empty;
      packet.Position = descriptor != null ? descriptor.Position : Vector2.zero;
      packet.Rotation = descriptor != null ? descriptor.Rotation : 0f;
      return packet;
    }

    // ------------------------------------------------------------------ client side

    public void HandleLootSpawn(LootSpawnPacket packet)
    {
      try
      {
        if (packet == null || !SyncActive) return;
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient) return;

        WorldSyncManager world = WorldSyncManager.Instance;
        if (world != null && !world.IsSceneReady)
        {
          Logger.LogDebug("[WST][Loot] 客户端场景未就绪，丢弃掉落物 netId=" + packet.NetId + "（之后会由快照补齐）。");
          return;
        }

        if (_clientMirrors.ContainsKey(packet.NetId))
        {
          Logger.LogDebug("[WST][Loot] 重复 LootSpawn netId=" + packet.NetId + "，忽略。");
          return;
        }

        // 先收到 Consumed 再收到 Spawn（乱序/延迟）：直接忽略，避免留下永远删不掉的镜像。
        if (_consumedIds.Contains(packet.NetId))
        {
          Logger.LogDebug("[WST][Loot] LootSpawn for already-consumed netId=" + packet.NetId + " ignored.");
          return;
        }

        if (_clientMirrors.Count >= MaxTrackedLoot)
        {
          if (!_capWarned)
          {
            _capWarned = true;
            Logger.LogWarning("[WST][Loot] 客户端掉落物镜像数量达到上限 " + MaxTrackedLoot + "，后续镜像不再创建。");
          }
          return;
        }

        LootDescriptor descriptor = new LootDescriptor(packet.Kind, packet.Data, packet.Position, packet.Rotation);
        GameObject mirror = LootHookAdapter.CreateMirror(descriptor);
        if (mirror == null)
        {
          if (!_mirrorUnavailableLogged)
          {
            _mirrorUnavailableLogged = true;
            Logger.LogWarning("[WST][Loot] 客户端镜像创建失败（LootHookAdapter.CreateMirror 返回 null），"
              + "可能是该掉落类型尚未实现重建；已跳过 netId=" + packet.NetId + "。");
          }
          return;
        }

        try
        {
          Transform t = mirror.transform;
          t.position = new Vector3(packet.Position.x, packet.Position.y, t.position.z);
          t.rotation = Quaternion.Euler(0f, 0f, packet.Rotation);
        }
        catch (Exception)
        {
        }

        TrackedLoot tracked = new TrackedLoot();
        tracked.NetId = packet.NetId;
        tracked.GameObject = mirror;
        tracked.Descriptor = descriptor;
        tracked.RegisteredAt = Time.unscaledTime;
        try
        {
          tracked.InitialParent = mirror.transform.parent;
          tracked.InitialActive = mirror.activeSelf;
        }
        catch (Exception)
        {
        }
        _clientMirrors[packet.NetId] = tracked;

        if (!_firstClientMirrorLogged)
        {
          _firstClientMirrorLogged = true;
          Logger.LogInfo("[WST][Loot] 首个客户端掉落物镜像已创建: " + descriptor.ToString());
        }
        else
        {
          Logger.LogDebug("[WST][Loot] 客户端镜像 netId=" + packet.NetId + " " + descriptor.ToString());
        }
      }
      catch (Exception ex)
      {
        LogErrorThrottled("[WST][Loot] HandleLootSpawn 失败: " + ex.Message);
      }
    }

    public void HandleLootConsumed(LootConsumedPacket packet)
    {
      try
      {
        if (packet == null || !SyncActive) return;
        NetworkManager network = NetworkManager.Instance;
        if (network == null) return;

        if (network.IsClient)
        {
          _consumedIds.Add(packet.NetId);
          RemoveClientMirror(packet.NetId);
          return;
        }

        if (network.IsHost)
        {
          // 客户端先捡到了：房主删除原物，并把这个 netId 已消失的事实广播给其它客户端。
          if (_hostLoot.ContainsKey(packet.NetId))
          {
            RemoveHostLoot(packet.NetId, true, true);
          }
        }
      }
      catch (Exception ex)
      {
        LogErrorThrottled("[WST][Loot] HandleLootConsumed 失败: " + ex.Message);
      }
    }

    private void RemoveClientMirror(int netId)
    {
      TrackedLoot tracked;
      if (!_clientMirrors.TryGetValue(netId, out tracked)) return;
      _clientMirrors.Remove(netId);
      if (tracked != null && tracked.GameObject != null)
      {
        try
        {
          UnityEngine.Object.Destroy(tracked.GameObject);
        }
        catch (Exception)
        {
        }
      }
    }

    private void ClientPoll()
    {
      if (_clientMirrors.Count == 0) return;

      // 场景加载过程中不轮询：旧场景对象会在加载时被销毁，容易被误判成“被捡走”。
      WorldSyncManager world = WorldSyncManager.Instance;
      if (world != null && world.IsClientLoading) return;

      _removeBuffer.Clear();
      foreach (KeyValuePair<int, TrackedLoot> entry in _clientMirrors)
      {
        TrackedLoot tracked = entry.Value;
        if (tracked == null || IsLootGone(tracked))
        {
          _removeBuffer.Add(entry.Key);
        }
      }
      for (int i = 0; i < _removeBuffer.Count; i++)
      {
        int netId = _removeBuffer[i];
        _clientMirrors.Remove(netId);
        // 没有安装拾取钩子时的兜底：镜像对象消失就认为被捡走，通知房主删除原物。
        SendConsumedToHost(netId);
      }
    }

    // 拾取回调（由 LootHookAdapter.InstallConsumeHooks 安装的补丁调用）。
    private void OnLocalDropConsumed(GameObject dropObject)
    {
      try
      {
        if (dropObject == null || !SyncActive) return;
        NetworkManager network = NetworkManager.Instance;
        if (network == null) return;

        if (network.IsClient)
        {
          int netId = FindNetId(_clientMirrors, dropObject);
          if (netId <= 0) return;
          _clientMirrors.Remove(netId);
          _consumedIds.Add(netId);
          SendConsumedToHost(netId);
          Logger.LogDebug("[WST][Loot] 客户端捡走掉落物 netId=" + netId + "，已通知房主。");
        }
        else if (network.IsHost)
        {
          // 场景卸载会批量 disable 掉落物；刚加载过场景时忽略这类“被捡走”信号。
          if (Time.unscaledTime - _lastSceneLoadTime < 1f) return;
          int netId = FindNetId(_hostLoot, dropObject);
          if (netId <= 0) return;
          // 房主自己捡走的：游戏已把对象归还对象池，不要再 Destroy。
          RemoveHostLoot(netId, true, false);
          Logger.LogDebug("[WST][Loot] 房主捡走掉落物 netId=" + netId + "，已通知客户端。");
        }
      }
      catch (Exception ex)
      {
        LogErrorThrottled("[WST][Loot] 拾取回调异常: " + ex.Message);
      }
    }

    private void SendConsumedToHost(int netId)
    {
      NetworkManager network = NetworkManager.Instance;
      if (network == null || !network.IsClient) return;
      try
      {
        LootConsumedPacket packet = new LootConsumedPacket();
        packet.NetId = netId;
        packet.ByPlayerId = GetLocalSteamId();
        network.SendPacket(0UL, packet, true);
      }
      catch (Exception ex)
      {
        LogErrorThrottled("[WST][Loot] 发送 LootConsumed 失败: " + ex.Message);
      }
    }

    private static int FindNetId(Dictionary<int, TrackedLoot> table, GameObject dropObject)
    {
      if (dropObject == null) return 0;
      foreach (KeyValuePair<int, TrackedLoot> entry in table)
      {
        TrackedLoot tracked = entry.Value;
        if (tracked != null && tracked.GameObject == dropObject) return entry.Key;
      }
      return 0;
    }

    // 掉落物“已经不在世界上了”的通用判定（不依赖 Destroy）：
    //   WarmSnow 的 *PoolControl.Pop 会把对象激活、Push 会把它 SetActive(false) 并挂回池子；
    //   拾取上报主要靠 adapter 给各掉落类 OnDisable 挂的 postfix（见 LootHookAdapter）。
    //   这里只做兜底：对象被销毁 / 登记时是激活的而现在被取消激活。
    private static bool IsLootGone(TrackedLoot tracked)
    {
      if (tracked == null || tracked.GameObject == null) return true;
      try
      {
        GameObject go = tracked.GameObject;
        if (tracked.InitialActive && !go.activeSelf) return true;
      }
      catch (Exception)
      {
        return true;
      }
      try
      {
        return LootHookAdapter.IsPickedUp(tracked.GameObject);
      }
      catch (Exception)
      {
        return false;
      }
    }

    // ------------------------------------------------------------------ lifecycle / polling

    private void Update()
    {
      if (!SyncActive) return;
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null) return;
        if (Time.unscaledTime < _nextPollTime) return;
        _nextPollTime = Time.unscaledTime + PollInterval;

        if (network.IsHost)
        {
          HostPoll();
        }
        else if (network.IsClient)
        {
          ClientPoll();
        }
      }
      catch (Exception ex)
      {
        LogErrorThrottled("[WST][Loot] Update 失败: " + ex.GetType().Name + ": " + ex.Message);
      }
    }

    // 场景切换：旧场景的掉落物随场景销毁，清空跟踪表即可（netId 保持单调递增，避免两端串号）。
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
      _hostLoot.Clear();
      _clientMirrors.Clear();
      _knownInstances.Clear();
      _consumedIds.Clear();
      _removeBuffer.Clear();
      _capWarned = false;
      _lastSceneLoadTime = Time.unscaledTime;
      Logger.LogDebug("[WST][Loot] 场景 '" + scene.name + "' 已加载，掉落物跟踪表已清空。");
    }

    // 网络会话结束时由 NetworkManager 调用：清空所有跟踪（不销毁房主原物/客户端镜像以外的东西）。
    public void ClearAll()
    {
      try
      {
        foreach (KeyValuePair<int, TrackedLoot> entry in _hostLoot)
        {
          TrackedLoot tracked = entry.Value;
          if (tracked != null && tracked.GameObject != null) UnityEngine.Object.Destroy(tracked.GameObject);
        }
        _hostLoot.Clear();

        foreach (KeyValuePair<int, TrackedLoot> entry in _clientMirrors)
        {
          TrackedLoot tracked = entry.Value;
          if (tracked != null && tracked.GameObject != null) UnityEngine.Object.Destroy(tracked.GameObject);
        }
        _clientMirrors.Clear();

        _knownInstances.Clear();
        _consumedIds.Clear();
        _removeBuffer.Clear();
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST][Loot] ClearAll 失败: " + ex.Message);
      }
    }

    // ------------------------------------------------------------------ helpers

    private static HostController GetHostController()
    {
      NetworkManager network = NetworkManager.Instance;
      if (network == null || !network.IsHost) return null;
      return network.Host;
    }

    private static ulong GetLocalSteamId()
    {
      try
      {
        SteamP2PManager p2p = SteamP2PManager.Instance;
        if (p2p != null && p2p.LocalSteamID != 0) return p2p.LocalSteamID;
      }
      catch (Exception)
      {
      }
      try
      {
        return SteamReflectionHelper.GetLocalSteamId();
      }
      catch (Exception)
      {
        return 0UL;
      }
    }

    private void LogErrorThrottled(string message)
    {
      if (Time.unscaledTime < _nextErrorLogTime) return;
      _nextErrorLogTime = Time.unscaledTime + 3f;
      Logger.LogError(message);
    }
  }
}
