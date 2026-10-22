// [WST NetSync v1.1] Host-authoritative enemy sync (requirements 3 + 4) - owner: enemy-sync (module B).
//
// Host:
//   * polls MonstersControl.instance.Enemys at 5 Hz, assigns network ids on first sight;
//   * broadcasts EnemySpawnPacket (reliable) once per enemy and EnemyStatePacket (unreliable) per tick;
//   * detects death (isDeath / destroyed / permanently disabled) and broadcasts EnemyDeathPacket (reliable);
//   * answers SnapshotRequestPacket with a full spawn+state snapshot;
//   * applies client DamageRequestPacket through the real EnemyControl.DealDamage path so the normal
//     HP / death / loot flow of the host simulation runs unchanged.
//
// Client:
//   * never simulates enemies: all four MonstersControl spawn methods are blocked, the long
//     EnemyControl.DealDamage overload is forwarded to the host as DamageRequestPacket, and
//     EnemyControl.Death() is blocked (no local death, loot, achievements);
//   * re-creates host enemies as passive copies from EnemySpawnPacket (BehaviourTree disabled, HP bar
//     kept because EnemyControl itself stays enabled), and drives position / rotation / HP / MAX_HP from
//     EnemyStatePacket with local interpolation.
//
// v1.1 fixes (found in the v1 audit):
//   (1) CLIENT NullReferenceException spam: MonsterDynamicInstantiate()/MonsterTestInstantiate() return
//       a GameObject and several vanilla call sites (MonsterSpawner.SpawnByIdStr, MonstersDynamicControl,
//       NightmareModeMonsterSpawner) dereference that return value. Returning null suppressed-spawn
//       made every one of those calls throw NRE on the client. The suppression prefix now returns a
//       small inert stand-in GameObject with an EnemyControl component instead of null.
//   (2) DamageRequest validation: NaN/Infinity/negative damage or direction used to be applied to the
//       host's real EnemyControl (NaN HP corrupts the enemy permanently). Invalid requests are now
//       dropped before touching the simulation.
//   (3) _hostIds held destroyed Unity objects forever (a killed enemy whose GameObject was already
//       destroyed was never removed from the reverse map -> unbounded growth over a long run).
//       It is keyed by instance id (int) now.
//   (4) ClearAllClientCopies() for network shutdown, per-tick error-log throttling, and a hard cap on
//       the number of mirrored copies / stand-in spawn objects.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using WarmSnowTogether.Networking.Packets;
using WarmSnowTogether.Networking.Sync;
using WarmSnowTogether.Systems.Logging;
// Disambiguate against UnityEngine.Logger (both namespaces are imported; Logger would be CS0104).
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking.Entities
{
  public class EnemySyncManager : MonoBehaviour
  {
    // Host snapshot / state broadcast rate (5 Hz).
    private const float SyncInterval = 0.2f;
    // Client interpolation factor for copies (same order of magnitude as the old mod code).
    private const float LerpFactor = 15f;
    // Copies that are further away than this snap instead of interpolating (spawn / teleport).
    private const float SnapDistance = 8f;
    // A host enemy that is only deactivated (not destroyed, not flagged dead) is treated as dead after
    // this many seconds. Boss phase transitions sometimes deactivate an enemy for a frame or two.
    private const float InactiveDeathDelay = 2f;
    // A client copy that received no EnemyStatePacket for this long is destroyed so a lost/never-sent
    // packet cannot leave MonstersControl.Enemys non-empty and keep doors/rooms locked.
    private const float StaleCopyTimeout = 10f;
    // Minimum interval between recovery SnapshotRequestPacket attempts triggered by the stale timeout.
    private const float StaleSnapshotRequestInterval = 5f;
    // Hard caps (bad input / endless-spawner protection).
    private const int MaxClientCopies = 512;
    private const int MaxBlockedSpawnDummies = 128;
    // Harmony instance id (one per module, so the patches are easy to identify in HarmonyX logs).
    private const string HarmonyId = "com.llamerrr.WarmSnowTogether.enemysync";
    // Reflection name of the BehaviorDesigner tree component (assembly is not referenced by the csproj).
    private const string BehaviorTreeTypeName = "BehaviorDesigner.Runtime.BehaviorTree, BehaviorDesigner.Runtime";
    // Reflection name of the 2D body component: UnityEngine.Physics2DModule is not referenced by the
    // csproj (only UnityEngine.PhysicsModule is), so Rigidbody2D must be reached by type name as well.
    private const string Physics2DBodyTypeName = "UnityEngine.Rigidbody2D, UnityEngine.Physics2DModule";
    // Verifier-confirmed: the long EnemyControl.DealDamage overload (EnemyControl.cs:1938) has 30 params.
    private const int DealDamageParameterCount = 30;
    // Max accepted damage per DamageRequestPacket. Far above any legit late-game hit; only protects
    // the host simulation from corrupted/hostile values.
    private const float MaxAcceptedDamage = 100000000f;

    private static EnemySyncManager _instance;

    public static EnemySyncManager Instance
    {
      get
      {
        if (_instance == null)
        {
          GameObject managerObject = new GameObject("EnemySyncManager");
          _instance = managerObject.AddComponent<EnemySyncManager>();
        }
        return _instance;
      }
    }

    // Host side bookkeeping for one host-authoritative enemy.
    private sealed class HostEnemy
    {
      public GameObject GameObject;
      public EnemyControl Control;
      public int InstanceId;
      public int SpawnedMonsterId;
      public float InactiveSince;
    }

    // Client side passive copy of one host enemy.
    private sealed class ClientCopy
    {
      public GameObject GameObject;
      public EnemyControl Control;
      public Vector3 TargetPosition;
      public float TargetRotation;
      public float TargetHealth;
      public float TargetMaxHealth;
      public bool HasState;
      // Time.unscaledTime of the last EnemyStatePacket applied to this copy (stale-copy watchdog).
      public float LastStateTime;
    }

    // host: enemy id -> tracked host enemy.
    private readonly Dictionary<int, HostEnemy> _hostEnemies = new Dictionary<int, HostEnemy>();
    // host: Unity instance id -> enemy id (reverse lookup; int keys keep destroyed objects out of the
    // dictionary so entries can always be removed even after the GameObject is gone).
    private readonly Dictionary<int, int> _hostIds = new Dictionary<int, int>();
    // client: enemy id -> passive copy.
    private readonly Dictionary<int, ClientCopy> _clientCopies = new Dictionary<int, ClientCopy>();
    // client: inert stand-ins returned to vanilla spawn call sites whose spawn we suppressed.
    private readonly List<GameObject> _blockedSpawnDummies = new List<GameObject>();
    // Scratch list reused by the update loops (no per-tick allocations, no LINQ in Update).
    private readonly List<int> _removeBuffer = new List<int>();

    private int _nextEnemyId = 1;
    private float _nextSyncTime;
    private bool _initialised;
    private bool _clientCopyCapWarned;
    private float _nextUpdateErrorLogTime;
    private float _nextBroadcastErrorLogTime;
    // Cached private field of BuffAction (wired on the blocked-spawn stand-in so AddBuff early-exits).
    private static FieldInfo _buffActionEnemyControlField;

    private static bool _applyingRemote;

    // True while EnemySyncManager itself instantiates / mutates an enemy copy. All Harmony prefixes
    // return "run as normal" while this is set, so the manager can bypass its own suppression.
    public static bool IsApplyingRemote
    {
      get { return _applyingRemote; }
    }

    private void Awake()
    {
      DontDestroyOnLoad(gameObject);
      SceneManager.sceneLoaded += OnSceneLoaded;
      Logger.LogInfo("[WST] EnemySyncManager created (host authority / client passive copies).");
    }

    private void OnDestroy()
    {
      SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    // ------------------------------------------------------------------ lifecycle

    // Called once by Mod.Start. Installs the Harmony patches used by client passive mode.
    public void Initialise()
    {
      if (_initialised)
      {
        Logger.LogWarning("[WST] EnemySyncManager.Initialise called twice; ignored.");
        return;
      }
      _initialised = true;
      try
      {
        InstallHarmonyPatches();
        Logger.LogInfo("[WST] EnemySyncManager initialised: client spawn suppression, damage routing and death blocking patched.");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySyncManager.Initialise failed: " + ex);
      }
    }

    private void Update()
    {
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null) return;
        if (network.IsHost)
        {
          HostTick();
        }
        else if (network.IsClient)
        {
          ClientTick();
        }
      }
      catch (Exception ex)
      {
        if (Time.unscaledTime >= _nextUpdateErrorLogTime)
        {
          _nextUpdateErrorLogTime = Time.unscaledTime + 2f;
          Logger.LogError("[WST] EnemySyncManager.Update failed: " + ex);
        }
      }
    }

    // ------------------------------------------------------------------ host side

    private void HostTick()
    {
      if (Time.unscaledTime < _nextSyncTime) return;
      _nextSyncTime = Time.unscaledTime + SyncInterval;

      MonstersControl monsters = MonstersControl.instance;
      if (monsters == null || monsters.Enemys == null) return;

      // 1) Drop ids for enemies that are dead, destroyed, re-indexed or stuck disabled.
      _removeBuffer.Clear();
      foreach (KeyValuePair<int, HostEnemy> entry in _hostEnemies)
      {
        HostEnemy tracked = entry.Value;
        if (tracked == null || tracked.GameObject == null || tracked.Control == null)
        {
          _removeBuffer.Add(entry.Key);
          continue;
        }
        if (tracked.Control.isDeath)
        {
          _removeBuffer.Add(entry.Key);
          continue;
        }
        // MonsterID changing (boss phase / beacon transform) gets a fresh copy on the client side.
        if (tracked.Control.MonsterID != tracked.SpawnedMonsterId)
        {
          _removeBuffer.Add(entry.Key);
          continue;
        }
        if (tracked.GameObject.activeInHierarchy)
        {
          tracked.InactiveSince = 0f;
        }
        else if (tracked.InactiveSince <= 0f)
        {
          tracked.InactiveSince = Time.unscaledTime;
        }
        else if (Time.unscaledTime - tracked.InactiveSince > InactiveDeathDelay)
        {
          _removeBuffer.Add(entry.Key);
        }
      }
      for (int i = 0; i < _removeBuffer.Count; i++)
      {
        RemoveHostEnemy(_removeBuffer[i], true);
      }

      // 2) Register new enemies (spawn packet) and push a state packet for every live enemy.
      for (int i = 0; i < monsters.Enemys.Count; i++)
      {
        GameObject enemyObject = monsters.Enemys[i];
        if (enemyObject == null) continue;
        EnemyControl control = enemyObject.GetComponent<EnemyControl>();
        if (control == null || control.isDeath) continue;

        int instanceId = enemyObject.GetInstanceID();
        bool isNew = !_hostIds.ContainsKey(instanceId);
        int enemyId = EnsureHostEnemyRegistered(enemyObject, control);
        if (isNew)
        {
          BroadCastSpawn(enemyId);
        }
        if (enemyObject.activeInHierarchy)
        {
          BroadCastState(enemyId);
        }
      }
    }

    private int EnsureHostEnemyRegistered(GameObject enemyObject, EnemyControl control)
    {
      int instanceId = enemyObject.GetInstanceID();
      int enemyId;
      if (_hostIds.TryGetValue(instanceId, out enemyId) && _hostEnemies.ContainsKey(enemyId))
      {
        return enemyId;
      }

      enemyId = _nextEnemyId++;
      HostEnemy tracked = new HostEnemy();
      tracked.GameObject = enemyObject;
      tracked.Control = control;
      tracked.InstanceId = instanceId;
      tracked.SpawnedMonsterId = control != null ? control.MonsterID : 0;
      tracked.InactiveSince = 0f;
      _hostEnemies[enemyId] = tracked;
      _hostIds[instanceId] = enemyId;
      return enemyId;
    }

    private void RemoveHostEnemy(int enemyId, bool notifyClients)
    {
      HostEnemy tracked;
      if (!_hostEnemies.TryGetValue(enemyId, out tracked)) return;
      _hostEnemies.Remove(enemyId);
      if (tracked != null)
      {
        // instance-id key: removal works even if the GameObject was already destroyed.
        _hostIds.Remove(tracked.InstanceId);
      }

      if (!notifyClients) return;
      HostController host = GetHostController();
      if (host == null) return;
      try
      {
        EnemyDeathPacket packet = new EnemyDeathPacket();
        packet.EnemyId = enemyId;
        host.Broadcast(packet, 0UL, true);
        Logger.LogDebug("[WST] EnemySync: enemy " + enemyId + " died; EnemyDeath broadcast.");
      }
      catch (Exception ex)
      {
        LogBroadcastError("[WST] EnemySync: failed to broadcast EnemyDeath for " + enemyId, ex);
      }
    }

    private static HostController GetHostController()
    {
      NetworkManager network = NetworkManager.Instance;
      if (network == null || !network.IsHost) return null;
      return network.Host;
    }

    private void LogBroadcastError(string message, Exception ex)
    {
      if (Time.unscaledTime < _nextBroadcastErrorLogTime) return;
      _nextBroadcastErrorLogTime = Time.unscaledTime + 2f;
      Logger.LogError(message + ": " + ex.Message);
    }

    private void BroadCastSpawn(int enemyId)
    {
      HostController host = GetHostController();
      if (host == null) return;
      HostEnemy tracked;
      if (!_hostEnemies.TryGetValue(enemyId, out tracked)) return;
      try
      {
        host.Broadcast(BuildSpawnPacket(enemyId, tracked), 0UL, true);
        Logger.LogDebug("[WST] EnemySync: spawn broadcast id=" + enemyId + " monsterId=" + tracked.SpawnedMonsterId);
      }
      catch (Exception ex)
      {
        LogBroadcastError("[WST] EnemySync: failed to broadcast EnemySpawn for " + enemyId, ex);
      }
    }

    private void BroadCastState(int enemyId)
    {
      HostController host = GetHostController();
      if (host == null) return;
      HostEnemy tracked;
      if (!_hostEnemies.TryGetValue(enemyId, out tracked)) return;
      try
      {
        host.Broadcast(BuildStatePacket(enemyId, tracked), 0UL, false);
      }
      catch (Exception ex)
      {
        LogBroadcastError("[WST] EnemySync: failed to broadcast EnemyState for " + enemyId, ex);
      }
    }

    private static EnemySpawnPacket BuildSpawnPacket(int enemyId, HostEnemy tracked)
    {
      EnemySpawnPacket packet = new EnemySpawnPacket();
      packet.EnemyId = enemyId;
      packet.MonsterId = tracked.Control != null ? tracked.Control.MonsterID : 0;
      if (tracked.GameObject != null)
      {
        Transform enemyTransform = tracked.GameObject.transform;
        packet.Position = new Vector2(enemyTransform.position.x, enemyTransform.position.y);
        packet.Rotation = enemyTransform.eulerAngles.z;
      }
      Parameter parameter = tracked.Control != null ? tracked.Control.enemyParameter : null;
      if (parameter != null)
      {
        packet.Health = parameter.HP;
        packet.MaxHealth = parameter.MAX_HP > 0.5f ? parameter.MAX_HP : 1f;
      }
      else
      {
        packet.Health = 1f;
        packet.MaxHealth = 1f;
      }
      return packet;
    }

    private static EnemyStatePacket BuildStatePacket(int enemyId, HostEnemy tracked)
    {
      EnemyStatePacket packet = new EnemyStatePacket();
      packet.EnemyId = enemyId;
      if (tracked.GameObject != null)
      {
        Transform enemyTransform = tracked.GameObject.transform;
        packet.Position = new Vector2(enemyTransform.position.x, enemyTransform.position.y);
        packet.Rotation = enemyTransform.eulerAngles.z;
      }
      Parameter parameter = tracked.Control != null ? tracked.Control.enemyParameter : null;
      if (parameter != null)
      {
        packet.Health = parameter.HP;
        packet.MaxHealth = parameter.MAX_HP > 0.5f ? parameter.MAX_HP : 1f;
      }
      return packet;
    }

    // Full snapshot: spawn + state for every live host enemy. Called by HandleSnapshotRequest (and by
    // the join flow in HostController).
    public void SendSnapshotTo(ulong targetSteamId)
    {
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsHost)
        {
          Logger.LogWarning("[WST] EnemySync.SendSnapshotTo called while not hosting; ignored.");
          return;
        }

        MonstersControl monsters = MonstersControl.instance;
        if (monsters == null || monsters.Enemys == null)
        {
          Logger.LogWarning("[WST] EnemySync.SendSnapshotTo: MonstersControl missing; snapshot not sent.");
          return;
        }

        int sent = 0;
        for (int i = 0; i < monsters.Enemys.Count; i++)
        {
          GameObject enemyObject = monsters.Enemys[i];
          if (enemyObject == null) continue;
          EnemyControl control = enemyObject.GetComponent<EnemyControl>();
          if (control == null || control.isDeath) continue;

          int enemyId = EnsureHostEnemyRegistered(enemyObject, control);
          HostEnemy tracked;
          if (!_hostEnemies.TryGetValue(enemyId, out tracked)) continue;

          network.SendPacket(targetSteamId, BuildSpawnPacket(enemyId, tracked), true);
          network.SendPacket(targetSteamId, BuildStatePacket(enemyId, tracked), false);
          sent++;
        }
        Logger.LogInfo("[WST] EnemySync: snapshot sent to " + targetSteamId + " with " + sent + " enemies.");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync.SendSnapshotTo failed: " + ex);
      }
    }

    public void HandleSnapshotRequest(ulong requesterSteamId)
    {
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsHost) return;
        Logger.LogInfo("[WST] EnemySync: SnapshotRequest received from " + requesterSteamId + ".");
        SendSnapshotTo(requesterSteamId);
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync.HandleSnapshotRequest failed: " + ex);
      }
    }

    // Client damage report: apply it through the real host damage path (HP / death / loot flow stays
    // unchanged), then push an immediate state packet so the client sees the result without waiting
    // for the next 5 Hz tick.
    public void HandleDamageRequest(DamageRequestPacket packet)
    {
      try
      {
        if (packet == null) return;
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsHost) return;

        HostEnemy tracked;
        if (!_hostEnemies.TryGetValue(packet.EnemyId, out tracked)) return;
        if (tracked == null || tracked.GameObject == null || tracked.Control == null) return;
        EnemyControl control = tracked.Control;
        if (control.isDeath) return;

        // v1.1: never feed NaN/Infinity/negative values into the host simulation.
        // (damage == 0 is accepted: some hits only apply status effects, the real DealDamage needs to
        // run for those.)
        float damage = packet.Damage;
        if (!IsFinite(damage) || damage < 0f)
        {
          Logger.LogDebug("[WST] EnemySync: rejected DamageRequest with invalid damage (" + damage + ") id=" + packet.EnemyId);
          return;
        }
        if (damage > MaxAcceptedDamage) damage = MaxAcceptedDamage;

        Vector2 direction = new Vector2(packet.DirX, packet.DirY);
        if (!IsFinite(direction.x) || !IsFinite(direction.y))
        {
          direction = Vector2.zero;
        }
        if (direction.sqrMagnitude > 0.0001f)
        {
          direction = direction.normalized;
        }

        try
        {
          control.DealDamage(damage, packet.IsRepell, direction,
            isMeele: packet.IsMeele, trueDamage: packet.TrueDamage);
        }
        catch (Exception damageException)
        {
          Logger.LogError("[WST] EnemySync: host DealDamage for enemy " + packet.EnemyId + " threw: " + damageException);
          return;
        }

        if (!control.isDeath)
        {
          BroadCastState(packet.EnemyId);
        }
        else
        {
          // The host death flow removed the enemy from MonstersControl.Enemys already; the next tick
          // would detect it, but reporting it now keeps the client hit feedback immediate.
          RemoveHostEnemy(packet.EnemyId, true);
        }
        Logger.LogDebug("[WST] EnemySync: applied DamageRequest id=" + packet.EnemyId + " damage=" + damage + " meele=" + packet.IsMeele);
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync.HandleDamageRequest failed: " + ex);
      }
    }

    private static bool IsFinite(float value)
    {
      return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // ------------------------------------------------------------------ client side

    private void ClientTick()
    {
      if (_clientCopies.Count == 0) return;

      float deltaTime = Time.deltaTime;
      float lerp = Mathf.Clamp01(deltaTime * LerpFactor);
      _removeBuffer.Clear();
      bool requestSnapshot = false;

      foreach (KeyValuePair<int, ClientCopy> entry in _clientCopies)
      {
        ClientCopy copy = entry.Value;
        if (copy == null || copy.GameObject == null)
        {
          _removeBuffer.Add(entry.Key);
          continue;
        }
        if (!copy.HasState) continue;

        // Stale-copy watchdog: if the host stopped streaming this id, drop the copy so it cannot keep
        // MonstersControl.Enemys non-empty (which gates doors / level clear on the client).
        if (Time.unscaledTime - copy.LastStateTime > StaleCopyTimeout)
        {
          Logger.LogWarning("[WST] EnemySync: client copy id=" + entry.Key + " got no EnemyState for " + StaleCopyTimeout + "s; destroying stale copy.");
          _removeBuffer.Add(entry.Key);
          requestSnapshot = true;
          continue;
        }

        try
        {
          Transform copyTransform = copy.GameObject.transform;
          Vector3 current = copyTransform.position;
          Vector3 offset = copy.TargetPosition - current;
          if (offset.sqrMagnitude > SnapDistance * SnapDistance)
          {
            copyTransform.position = copy.TargetPosition;
          }
          else
          {
            copyTransform.position = Vector3.Lerp(current, copy.TargetPosition, lerp);
          }
          copyTransform.rotation = Quaternion.Euler(0f, 0f, copy.TargetRotation);

          // The copy's Rigidbody2D has simulated=false (see DisableClientCopyPhysics), so the transform is
          // the only thing that needs to move; the physics engine never drives or teleports the copy.
          EnemyControl control = copy.Control;
          if (control != null && control.enemyParameter != null)
          {
            if (copy.TargetMaxHealth > 0f && Mathf.Abs(control.enemyParameter.MAX_HP - copy.TargetMaxHealth) > 0.01f)
            {
              control.enemyParameter.MAX_HP = copy.TargetMaxHealth;
            }
            if (copy.TargetHealth > 0f && Mathf.Abs(control.enemyParameter.HP - copy.TargetHealth) > 0.01f)
            {
              control.enemyParameter.HP = Mathf.Clamp(copy.TargetHealth, 0f, control.enemyParameter.MAX_HP);
            }
          }
        }
        catch (Exception ex)
        {
          Logger.LogError("[WST] EnemySync: client copy update failed: " + ex.Message);
          _removeBuffer.Add(entry.Key);
        }
      }

      for (int i = 0; i < _removeBuffer.Count; i++)
      {
        RemoveClientCopy(_removeBuffer[i], true);
      }

      if (requestSnapshot)
      {
        RequestRecoverySnapshot();
      }
    }

    // The client normally receives a full snapshot right after the scene is ready; this is a bounded
    // recovery path for copies whose state stream died (e.g. a lost EnemyDeath / EnemySpawn packet).
    private float _nextStaleSnapshotRequest;

    private void RequestRecoverySnapshot()
    {
      if (Time.unscaledTime < _nextStaleSnapshotRequest) return;
      _nextStaleSnapshotRequest = Time.unscaledTime + StaleSnapshotRequestInterval;
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient) return;
        network.SendPacket(0UL, new SnapshotRequestPacket(), true);
        Logger.LogInfo("[WST] EnemySync: stale copy detected; SnapshotRequest re-sent to host.");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: recovery SnapshotRequest failed: " + ex);
      }
    }

    public void HandleEnemySpawn(EnemySpawnPacket packet)
    {
      bool applying = false;
      try
      {
        if (packet == null) return;
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient) return;
        if (!WorldSyncManager.Instance.IsSceneReady)
        {
          Logger.LogDebug("[WST] EnemySync: EnemySpawn dropped, client scene not ready yet (id=" + packet.EnemyId + ").");
          return;
        }
        if (_clientCopies.ContainsKey(packet.EnemyId))
        {
          Logger.LogDebug("[WST] EnemySync: duplicate EnemySpawn for id " + packet.EnemyId + "; ignored.");
          return;
        }
        if (_clientCopies.Count >= MaxClientCopies)
        {
          if (!_clientCopyCapWarned)
          {
            _clientCopyCapWarned = true;
            Logger.LogWarning("[WST] EnemySync: client copy cap (" + MaxClientCopies + ") reached; ignoring further spawns.");
          }
          return;
        }

        MonstersControl monsters = MonstersControl.instance;
        if (monsters == null || monsters.EnemyObject == null) return;
        if (packet.MonsterId < 0 || packet.MonsterId >= monsters.EnemyObject.Length || monsters.EnemyObject[packet.MonsterId] == null)
        {
          Logger.LogWarning("[WST] EnemySync: EnemySpawn with unknown MonsterId " + packet.MonsterId + "; ignored.");
          return;
        }

        Vector3 spawnPosition = new Vector3(packet.Position.x, packet.Position.y, 0f);
        GameObject enemyObject = null;
        applying = true;
        _applyingRemote = true;
        try
        {
          // Should not be blocked by our own MonstersControl spawn prefix while IsApplyingRemote is set.
          enemyObject = monsters.MonsterDynamicInstantiate(packet.MonsterId, spawnPosition, true);
        }
        finally
        {
          _applyingRemote = false;
        }

        if (enemyObject == null)
        {
          Logger.LogWarning("[WST] EnemySync: failed to instantiate enemy copy for MonsterId " + packet.MonsterId + ".");
          return;
        }

        enemyObject.transform.position = spawnPosition;
        enemyObject.transform.rotation = Quaternion.Euler(0f, 0f, packet.Rotation);

        ClientCopy copy = new ClientCopy();
        copy.GameObject = enemyObject;
        copy.Control = enemyObject.GetComponent<EnemyControl>();
        copy.TargetPosition = spawnPosition;
        copy.TargetRotation = packet.Rotation;
        copy.TargetHealth = packet.Health;
        copy.TargetMaxHealth = packet.MaxHealth;
        copy.HasState = true;
        copy.LastStateTime = Time.unscaledTime;
        _clientCopies[packet.EnemyId] = copy;

        DisableEnemyAi(enemyObject);
        // The copy is positioned by EnemySyncManager, never by the physics engine.
        // Colliders stay enabled (the player should still bump into enemies), but a non-simulated body
        // cannot be pushed around by physics and cannot push the local player either.
        DisableClientCopyPhysics(enemyObject);
        // Keep copies out of the vision/fight-state path (EnemyControl.cs:876 uses
        // CanEffectFightState, field at EnemyControl.cs:5251) so they cannot trigger BGM/IsFight twice.
        if (copy.Control != null)
        {
          copy.Control.CanEffectFightState = false;
        }
        StartCoroutine(ApplySpawnHealthNextFrames(packet.EnemyId, copy));
        Logger.LogDebug("[WST] EnemySync: spawned passive copy id=" + packet.EnemyId + " monsterId=" + packet.MonsterId + " at " + spawnPosition);
      }
      catch (Exception ex)
      {
        _applyingRemote = false;
        Logger.LogError("[WST] EnemySync.HandleEnemySpawn failed: " + ex + (applying ? " (spawn applying)" : ""));
      }
    }

    public void HandleEnemyState(EnemyStatePacket packet)
    {
      try
      {
        if (packet == null) return;
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient) return;
        if (!WorldSyncManager.Instance.IsSceneReady) return;

        ClientCopy copy;
        if (!_clientCopies.TryGetValue(packet.EnemyId, out copy) || copy == null) return;

        copy.LastStateTime = Time.unscaledTime;
        if (!IsFinite(packet.Position.x) || !IsFinite(packet.Position.y) || !IsFinite(packet.Rotation))
        {
          return;
        }
        copy.TargetPosition = new Vector3(packet.Position.x, packet.Position.y, 0f);
        copy.TargetRotation = packet.Rotation;
        if (IsFinite(packet.MaxHealth) && packet.MaxHealth > 0f)
        {
          copy.TargetMaxHealth = packet.MaxHealth;
        }
        // Never take a copy to 0 HP locally: the host's EnemyDeathPacket is what removes it.
        if (IsFinite(packet.Health) && packet.Health > 0f)
        {
          copy.TargetHealth = packet.Health;
        }
        copy.HasState = true;
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync.HandleEnemyState failed: " + ex);
      }
    }

    public void HandleEnemyDeath(EnemyDeathPacket packet)
    {
      try
      {
        if (packet == null) return;
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient) return;
        RemoveClientCopy(packet.EnemyId, true);
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync.HandleEnemyDeath failed: " + ex);
      }
    }

    private void RemoveClientCopy(int enemyId, bool destroyCopy)
    {
      ClientCopy copy;
      if (!_clientCopies.TryGetValue(enemyId, out copy))
      {
        Logger.LogDebug("[WST] EnemySync: EnemyDeath for unknown copy id " + enemyId + "; ignored.");
        return;
      }
      _clientCopies.Remove(enemyId);
      if (!destroyCopy || copy == null || copy.GameObject == null) return;

      // Keep the client room counter in sync: EnemyControl.OnDisable removes the object from
      // MonstersControl.Enemys and decrements EnemyControl.EnemyCount, but nothing calls
      // MonstersControl.ReduceEnemyCount for copies, so do it here (only if it was actually listed).
      try
      {
        MonstersControl monsters = MonstersControl.instance;
        if (monsters != null && monsters.Enemys != null && monsters.Enemys.Remove(copy.GameObject))
        {
          monsters.ReduceEnemyCount();
        }
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: counter cleanup failed for id " + enemyId + ": " + ex.Message);
      }

      try
      {
        UnityEngine.Object.Destroy(copy.GameObject);
        Logger.LogDebug("[WST] EnemySync: destroyed copy id=" + enemyId + ".");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: failed to destroy copy id " + enemyId + ": " + ex.Message);
      }
    }

    // Called by NetworkManager when the session ends: destroys every passive copy without destroying
    // anything that belongs to the local simulation.
    public void ClearAllClientCopies()
    {
      try
      {
        if (_clientCopies.Count == 0 && _blockedSpawnDummies.Count == 0) return;
        _removeBuffer.Clear();
        foreach (KeyValuePair<int, ClientCopy> entry in _clientCopies)
        {
          _removeBuffer.Add(entry.Key);
        }
        for (int i = 0; i < _removeBuffer.Count; i++)
        {
          RemoveClientCopy(_removeBuffer[i], true);
        }
        _removeBuffer.Clear();
        DestroyBlockedSpawnDummies();
        Logger.LogInfo("[WST] EnemySync: all client copies cleared on session end.");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync.ClearAllClientCopies failed: " + ex);
      }
    }

    // HP / MAX_HP can only be written after EnemyControl.Start (ParameterParseXML) ran, so wait two
    // frames and then copy the host values. The state packets keep correcting HP afterwards.
    private IEnumerator ApplySpawnHealthNextFrames(int enemyId, ClientCopy copy)
    {
      yield return null;
      yield return null;
      if (copy == null || copy.GameObject == null || copy.Control == null) yield break;
      ClientCopy current;
      if (!_clientCopies.TryGetValue(enemyId, out current) || current != copy) yield break;
      EnemyControl control = copy.Control;
      if (control.enemyParameter == null) yield break;
      // Re-apply after EnemyControl.Start (ParameterParseXML may reset serialized flags).
      control.CanEffectFightState = false;
      DisableEnemyAi(copy.GameObject);
      DisableClientCopyPhysics(copy.GameObject);
      if (copy.TargetMaxHealth > 0f)
      {
        control.enemyParameter.MAX_HP = Mathf.Max(1f, copy.TargetMaxHealth);
      }
      if (copy.TargetHealth > 0f)
      {
        control.enemyParameter.HP = Mathf.Clamp(copy.TargetHealth, 0f, control.enemyParameter.MAX_HP);
      }
      Logger.LogDebug("[WST] EnemySync: applied spawn HP for id " + enemyId + " (" + control.enemyParameter.HP + "/" + control.enemyParameter.MAX_HP + ").");
    }

    // ------------------------------------------------------------------ blocked spawn stand-ins

    // v1.1: vanilla call sites dereference the GameObject returned by MonsterDynamicInstantiate /
    // MonsterTestInstantiate. Verified in the IL of Assembly-CSharp: the upstream callers (stage
    // generation, task completion, event triggers, main loop, AI logic, nightmare-mode spawning,
    // MonsterSpawner rooms) do NOT null-check and immediately use result.transform.position /
    // result.GetComponent<EnemyControl>(). Returning null therefore threw NullReferenceException
    // inside the game code for every spawn the client suppressed.
    //
    // This returns a stand-in object with just enough of the real EnemyControl data model for those
    // call sites to run without errors:
    //   * a GameObject with tag "Enemy" and a Rigidbody2D (both common accesses on spawn results),
    //   * a DISABLED EnemyControl (no Awake/OnEnable/LateUpdate -> no AI, counters, HP bar, BGM, loot),
    //   * a non-null Parameter + enhanceType list + isDeath = true,
    //   * a DISABLED BuffAction wired to this EnemyControl, so MonstersDynamicControl's isBuff path
    //     (enemyControl.buffAction.AddBuff(...)) hits the "enemy is already dead -> return null" early
    //     exit instead of throwing on a null buffAction list.
    // The object stays inactive so absolutely nothing runs on it; the JSON-shaped data is enough for
    // the call sites that only walk transform/GetComponent/fields.
    internal GameObject CreateBlockedSpawnDummy()
    {
      try
      {
        if (_blockedSpawnDummies.Count >= MaxBlockedSpawnDummies)
        {
          for (int i = 0; i < _blockedSpawnDummies.Count; i++)
          {
            if (_blockedSpawnDummies[i] != null) return _blockedSpawnDummies[i];
          }
          _blockedSpawnDummies.Clear();
        }

        GameObject dummy = new GameObject("WST_BlockedSpawn");
        // Inactive before adding the components: Awake/OnEnable never run, so the stand-in cannot
        // touch MonstersControl counters, HP bars, BGM or the death flow.
        dummy.SetActive(false);

        // Real enemy prefabs are tagged "Enemy"; some vanilla spawn consumers compare tags.
        try
        {
          dummy.tag = "Enemy";
        }
        catch (Exception)
        {
          // Tag not defined in this build: not fatal for the stand-in.
        }

        // Rigidbody2D is reached by type name (Physics2DModule is not a csproj reference).
        try
        {
          if (_body2DType == null)
          {
            _body2DType = Type.GetType(Physics2DBodyTypeName, false);
          }
          if (_body2DType != null && dummy.GetComponent(_body2DType) == null)
          {
            dummy.AddComponent(_body2DType);
          }
        }
        catch (Exception)
        {
        }

        EnemyControl control = dummy.AddComponent<EnemyControl>();
        control.enabled = false;
        // Fields the vanilla result-using call sites touch (transform / GetComponent are already
        // covered by the GameObject+component); a non-null Parameter and enhanceType list keep the
        // remaining code paths from hitting a null reference.
        control.enemyParameter = new Parameter();
        control.enhanceType = new List<EnhanceType>();
        control.isDeath = true;
        control.CanEffectFightState = false;

        // isBuff spawners (MonstersDynamicControl) call enemyControl.buffAction.AddBuff(...).
        // A disabled real BuffAction + isDeath=true makes AddBuff take its "dead enemy" early return.
        try
        {
          BuffAction buff = dummy.AddComponent<BuffAction>();
          buff.enabled = false;
          control.buffAction = buff;
          if (_buffActionEnemyControlField == null)
          {
            _buffActionEnemyControlField = typeof(BuffAction).GetField(
                "enemyControl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
          }
          if (_buffActionEnemyControlField != null)
          {
            _buffActionEnemyControlField.SetValue(buff, control);
          }
        }
        catch (Exception buffException)
        {
          Logger.LogWarning("[WST] EnemySync: blocked-spawn stand-in without BuffAction: " + buffException.Message);
        }

        _blockedSpawnDummies.Add(dummy);
        return dummy;
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: failed to create blocked-spawn stand-in: " + ex);
        return null;
      }
    }

    private void DestroyBlockedSpawnDummies()
    {
      for (int i = 0; i < _blockedSpawnDummies.Count; i++)
      {
        GameObject dummy = _blockedSpawnDummies[i];
        if (dummy != null)
        {
          try
          {
            UnityEngine.Object.Destroy(dummy);
          }
          catch (Exception)
          {
          }
        }
      }
      _blockedSpawnDummies.Clear();
    }

    // BehaviorDesigner is not referenced by the csproj, so reach the BehaviorTree component by type name
    // and disable it: passive copies must not run AI, patrol, attacks or summons of their own.
    private static Type _behaviorTreeType;
    private static bool _behaviorTreeTypeResolved;

    private static Type ResolveBehaviorTreeType()
    {
      if (_behaviorTreeTypeResolved) return _behaviorTreeType;
      _behaviorTreeTypeResolved = true;
      try
      {
        _behaviorTreeType = Type.GetType(BehaviorTreeTypeName, false);
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: BehaviorTree type resolution failed: " + ex.Message);
      }
      if (_behaviorTreeType == null)
      {
        Logger.LogWarning("[WST] EnemySync: BehaviorDesigner.Runtime.BehaviorTree not found; client enemy copies may keep running AI.");
      }
      return _behaviorTreeType;
    }

    private static void DisableEnemyAi(GameObject enemyObject)
    {
      if (enemyObject == null) return;
      Type behaviorTreeType = ResolveBehaviorTreeType();
      if (behaviorTreeType == null) return;
      try
      {
        Component[] components = enemyObject.GetComponentsInChildren(behaviorTreeType, true);
        if (components == null) return;
        for (int i = 0; i < components.Length; i++)
        {
          Behaviour behaviour = components[i] as Behaviour;
          if (behaviour != null)
          {
            behaviour.enabled = false;
          }
        }
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: failed to disable BehaviourTree on copy: " + ex);
      }
    }

    // ------------------------------------------------------------------ shared helpers

    // The copy's Rigidbody2D must stop simulating: the manager writes transform.position/rotation every
    // frame, so a dynamic body would fight the interpolation, drift on its own and shove the local player.
    // Reached through Type.GetType because UnityEngine.Physics2DModule is not a csproj reference; only
    // the "simulated" property is written (once), so no per-frame reflection cost.
    private static Type _body2DType;
    private static bool _body2DTypeResolved;
    private static PropertyInfo _body2DSimulatedProperty;

    private static void DisableClientCopyPhysics(GameObject enemyObject)
    {
      if (enemyObject == null) return;
      try
      {
        if (!_body2DTypeResolved)
        {
          _body2DTypeResolved = true;
          _body2DType = Type.GetType(Physics2DBodyTypeName, false);
          if (_body2DType != null)
          {
            _body2DSimulatedProperty = _body2DType.GetProperty("simulated");
          }
        }
        if (_body2DType == null)
        {
          Logger.LogWarning("[WST] EnemySync: UnityEngine.Rigidbody2D type not found; client copies keep simulating.");
          return;
        }
        Component body = enemyObject.GetComponent(_body2DType);
        if (body == null) return;
        if (_body2DSimulatedProperty != null && _body2DSimulatedProperty.PropertyType == typeof(bool))
        {
          _body2DSimulatedProperty.SetValue(body, false, null);
        }
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: failed to disable copy physics: " + ex);
      }
    }

    // Client damage prefix entry point (called by EnemySyncPatches).
    internal void ForwardDamageToHost(EnemyControl enemy, float damage, bool isRepell, Vector2 dir, bool isMeele, bool trueDamage)
    {
      if (enemy == null) return;
      if (!IsFinite(damage) || damage < 0f) return;
      int enemyId = ResolveNetId(enemy.gameObject);
      if (enemyId <= 0)
      {
        Logger.LogDebug("[WST] EnemySync: client hit on enemy without host id ignored.");
        return;
      }

      Vector2 direction = dir;
      if (!IsFinite(direction.x) || !IsFinite(direction.y))
      {
        direction = Vector2.zero;
      }
      if (direction.sqrMagnitude > 0.0001f)
      {
        direction = direction.normalized;
      }

      DamageRequestPacket packet = new DamageRequestPacket();
      packet.EnemyId = enemyId;
      packet.Damage = damage;
      packet.IsRepell = isRepell;
      packet.DirX = direction.x;
      packet.DirY = direction.y;
      packet.IsMeele = isMeele;
      packet.TrueDamage = trueDamage;

      NetworkManager network = NetworkManager.Instance;
      if (network == null || !network.IsClient) return;
      network.SendPacket(0UL, packet, true);
      Logger.LogDebug("[WST] EnemySync: DamageRequest sent id=" + enemyId + " damage=" + damage + " meele=" + isMeele);
    }

    internal int ResolveNetId(GameObject enemyObject)
    {
      if (enemyObject == null) return -1;
      // Host side: id assignment table. Client side: copy table keyed by id (reverse scan is bounded).
      int instanceId = enemyObject.GetInstanceID();
      int enemyId;
      if (_hostIds.TryGetValue(instanceId, out enemyId)) return enemyId;
      foreach (KeyValuePair<int, ClientCopy> entry in _clientCopies)
      {
        if (entry.Value != null && entry.Value.GameObject == enemyObject) return entry.Key;
      }
      return -1;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
      try
      {
        // Host: the old scene objects are gone, ids are re-assigned from the new scene.
        // Client: copies died with the old scene (do not destroy them again); the snapshot is requested
        // by WorldSyncManager.ApplySceneSync after the scene is ready.
        _hostEnemies.Clear();
        _hostIds.Clear();
        _clientCopies.Clear();
        _removeBuffer.Clear();
        _clientCopyCapWarned = false;
        _blockedSpawnDummies.Clear();
        NetworkManager network = NetworkManager.Instance;
        bool client = network != null && network.IsClient;
        Logger.LogInfo("[WST] EnemySync: scene '" + scene.name + "' loaded; enemy tables cleared (" + (client ? "client copies re-spawned from next snapshot" : "host ids re-assigned") + ").");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync.OnSceneLoaded failed: " + ex);
      }
    }

    // ------------------------------------------------------------------ Harmony patches

    private static Harmony _harmony;
    private static bool _harmonyInstalled;

    private static void InstallHarmonyPatches()
    {
      if (_harmonyInstalled) return;
      _harmonyInstalled = true;

      MethodInfo spawnPrefix = typeof(EnemySyncPatches).GetMethod("SpawnSuppressionPrefix", BindingFlags.Static | BindingFlags.Public);
      MethodInfo spawnResultPrefix = typeof(EnemySyncPatches).GetMethod("SpawnSuppressionPrefixWithResult", BindingFlags.Static | BindingFlags.Public);
      MethodInfo damagePrefix = typeof(EnemySyncPatches).GetMethod("DealDamagePrefix", BindingFlags.Static | BindingFlags.Public);
      MethodInfo deathPrefix = typeof(EnemySyncPatches).GetMethod("DeathPrefix", BindingFlags.Static | BindingFlags.Public);

      if (spawnPrefix == null || spawnResultPrefix == null || damagePrefix == null || deathPrefix == null)
      {
        Logger.LogError("[WST] EnemySync: prefix method lookup failed; Harmony patches not installed.");
        return;
      }

      try
      {
        _harmony = new Harmony(HarmonyId);
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: could not create Harmony instance: " + ex);
        return;
      }

      // 1) Suppress every locally simulated enemy spawn on the client.
      //    void MonsterInstantiate -> plain prefix; the GameObject-returning overloads get a stand-in
      //    result (v1 returned null and vanilla call sites threw NullReferenceException).
      PatchMonstersSpawn(spawnPrefix, "MonsterInstantiate", typeof(Transform));
      PatchMonstersSpawn(spawnResultPrefix, "MonsterDynamicInstantiate", typeof(Transform));
      PatchMonstersSpawn(spawnResultPrefix, "MonsterDynamicInstantiate", typeof(Vector3));
      PatchMonstersSpawn(spawnResultPrefix, "MonsterTestInstantiate", typeof(Vector3));

      // 2) Redirect client-side damage on copies to the host.
      PatchDealDamage(damagePrefix);

      // 3) Block local death / loot / achievements on client copies.
      PatchDeath(deathPrefix);
    }

    private static MethodInfo FindMonstersControlSpawnMethod(string methodName, Type secondParameterType)
    {
      MethodInfo[] methods = typeof(MonstersControl).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
      for (int i = 0; i < methods.Length; i++)
      {
        MethodInfo candidate = methods[i];
        if (candidate.Name != methodName) continue;
        ParameterInfo[] parameters = candidate.GetParameters();
        if (parameters.Length != 3) continue;
        if (parameters[0].ParameterType != typeof(int)) continue;
        if (parameters[1].ParameterType != secondParameterType) continue;
        return candidate;
      }
      return null;
    }

    private static void PatchMonstersSpawn(MethodInfo prefix, string methodName, Type secondParameterType)
    {
      try
      {
        MethodInfo target = FindMonstersControlSpawnMethod(methodName, secondParameterType);
        if (target == null)
        {
          Logger.LogError("[WST] EnemySync: MonstersControl." + methodName + "(" + secondParameterType.Name + ") not found; client spawn suppression disabled for it.");
          return;
        }
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        Logger.LogDebug("[WST] EnemySync: patched MonstersControl." + methodName + "(" + secondParameterType.Name + ").");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: patching MonstersControl." + methodName + "(" + secondParameterType.Name + ") failed: " + ex);
      }
    }

    private static void PatchDealDamage(MethodInfo prefix)
    {
      try
      {
        // The long overload is identified by its first parameter type (float): the short
        // DealDamage(EnemyControl.Damage) funnels into it as well, so patching it catches every damage.
        // Verifier-confirmed signature: EnemyControl.cs:1938 with exactly 30 parameters (damage ...
        // massage). The prefix only injects the by-name subset, so the length is only used to pick the
        // right overload and to fail loudly if the game changes.
        MethodInfo target = null;
        MethodInfo fallback = null;
        MethodInfo[] methods = typeof(EnemyControl).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < methods.Length; i++)
        {
          MethodInfo candidate = methods[i];
          if (candidate.Name != "DealDamage") continue;
          ParameterInfo[] parameters = candidate.GetParameters();
          if (parameters.Length == 0 || parameters[0].ParameterType != typeof(float)) continue;
          if (parameters.Length == DealDamageParameterCount)
          {
            target = candidate;
            break;
          }
          if (fallback == null)
          {
            fallback = candidate;
          }
        }
        if (target == null)
        {
          target = fallback;
          if (target != null)
          {
            Logger.LogWarning("[WST] EnemySync: EnemyControl.DealDamage overload has " + target.GetParameters().Length
              + " parameters (expected " + DealDamageParameterCount + "); patching it anyway.");
          }
        }
        if (target == null)
        {
          Logger.LogError("[WST] EnemySync: EnemyControl.DealDamage(float, ...) not found; client damage routing disabled.");
          return;
        }
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        Logger.LogDebug("[WST] EnemySync: patched EnemyControl.DealDamage(float, ...) [" + target.GetParameters().Length + " parameters].");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: patching EnemyControl.DealDamage failed: " + ex);
      }
    }

    private static void PatchDeath(MethodInfo prefix)
    {
      try
      {
        MethodInfo target = null;
        MethodInfo[] methods = typeof(EnemyControl).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        for (int i = 0; i < methods.Length; i++)
        {
          MethodInfo candidate = methods[i];
          if (candidate.Name == "Death" && candidate.GetParameters().Length == 0)
          {
            target = candidate;
            break;
          }
        }
        if (target == null)
        {
          Logger.LogError("[WST] EnemySync: EnemyControl.Death() not found; client local death blocking disabled.");
          return;
        }
        _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        Logger.LogDebug("[WST] EnemySync: patched EnemyControl.Death().");
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: patching EnemyControl.Death failed: " + ex);
      }
    }
  }

  // Harmony prefixes used by EnemySyncManager (keep the manager class focused on state, patches are
  // stateless so they can live in one tiny static class).
  internal static class EnemySyncPatches
  {
    // Block all locally simulated enemy spawns on the client (void overload).
    public static bool SpawnSuppressionPrefix()
    {
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient) return true;
        return EnemySyncManager.IsApplyingRemote;
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: spawn suppression prefix failed: " + ex);
        return true;
      }
    }

    // Block all locally simulated enemy spawns on the client (GameObject-returning overloads).
    // Returning null here made vanilla call sites throw NullReferenceException, so the suppressed
    // spawn is replaced by an inert stand-in object (see CreateBlockedSpawnDummy).
    public static bool SpawnSuppressionPrefixWithResult(ref GameObject __result)
    {
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient) return true;
        if (EnemySyncManager.IsApplyingRemote) return true;
        EnemySyncManager manager = EnemySyncManager.Instance;
        if (manager != null)
        {
          __result = manager.CreateBlockedSpawnDummy();
        }
        return false;
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: spawn suppression (result) prefix failed: " + ex);
        return true;
      }
    }

    // Client-side damage on a host-authoritative enemy copy: forward it to the host and never touch
    // the local HP (the host is the only authority for HP / death / loot).
    public static bool DealDamagePrefix(EnemyControl __instance, float damage, bool isRepell, Vector2 dir, bool isMeele, bool trueDamage)
    {
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient || EnemySyncManager.IsApplyingRemote) return true;
        if (__instance == null) return true;
        EnemySyncManager.Instance.ForwardDamageToHost(__instance, damage, isRepell, dir, isMeele, trueDamage);
        return false;
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: DealDamage prefix failed: " + ex);
        return true;
      }
    }

    // Client copies must never run the local death flow (loot, achievements, boss outro, trial data).
    // The host EnemyDeathPacket is what destroys the copy.
    // isDeath must be set explicitly: blocking Death() without it would leave EnemyControl.LateUpdate
    // retrying Death() every frame because HP <= 0 and isDeath is still false.
    public static bool DeathPrefix(EnemyControl __instance)
    {
      try
      {
        NetworkManager network = NetworkManager.Instance;
        if (network == null || !network.IsClient || EnemySyncManager.IsApplyingRemote) return true;
        if (__instance != null)
        {
          __instance.isDeath = true;
        }
        return false;
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST] EnemySync: Death prefix failed: " + ex);
        return true;
      }
    }
  }
}
