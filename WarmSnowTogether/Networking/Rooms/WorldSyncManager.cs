// [WST NetSync v1.1] Host-driven Unity scene synchronization (requirement 1: both players load the
// same level scene, client appears next to the host). Owner: C.
//
// Authority: the host's active scene index (SceneManager.GetActiveScene().buildIndex) is the truth.
// The client performs exactly what the vanilla game does when it changes room: SceneManager.LoadSceneAsync
// (decompile: LevelLoad.cs:214 "this.operation = SceneManager.LoadSceneAsync(num);" and
// LevelReload.cs:88 which reloads the current buildIndex). A plain LoadSceneAsync is enough because
// StageControl.Awake boots the stage objects of the newly loaded scene
// (decompile: StageControl.cs:26-39: StageControl.instance + Resources.Load("Prefabs/InitObject 1") /
//  "Prefabs/PostProcessCamera 1"), and StageControl exposes public static StageControl.instance
// (StageControl.cs:1146) plus public MapID / sceneLevel / stageMapId fields.
//
// NOTE: the game's player (PlayerAnimControl) is a scene object, not DontDestroyOnLoad, so the local
// player is re-created by the game after each LoadSceneAsync -> the teleport right after a load may have
// to wait a few frames (bounded retry below).
//
// v1.1 fixes / hardening:
//   * Update() is wrapped in try/catch with throttled logging: an exception in scene-sync code used
//     to be re-thrown by Unity every frame (log spam + one stack trace per frame);
//   * before LoadSceneAsync the host-provided buildIndex is validated against this build's scene
//     list (SceneUtility.GetScenePathByBuildIndex); an invalid index would make Unity log its own
//     scene-load error on every SceneSync packet;
//   * IsSceneReady is cached per frame (it is queried for every EnemySpawn/EnemyState packet);
//   * with 3+ players the teleport target is spread sideways instead of putting every client on
//     host + 1.2;
//   * ResetForDisconnect() lets NetworkManager clear client scene-sync state when the host leaves.
//
// Client: SceneManager.LoadSceneAsync(index) -> wait isDone + 2 frames -> teleport next to the host
// -> request the enemy snapshot. Contains load dedupe, concurrent-load protection, bounded retry and
// client-side door drift self-heal (at most 3 corrective loads).

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using WarmSnowTogether.Networking;
using WarmSnowTogether.Networking.Packets;
using WarmSnowTogether.Systems.Logging;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking.Sync
{
  public class WorldSyncManager : MonoBehaviour
  {
    private static WorldSyncManager _instance;

    // Lazy DontDestroyOnLoad singleton (same pattern as the other mod managers).
    public static WorldSyncManager Instance
    {
      get
      {
        if (_instance == null)
        {
          GameObject go = new GameObject("WorldSyncManager");
          _instance = go.AddComponent<WorldSyncManager>();
          DontDestroyOnLoad(go);
        }
        return _instance;
      }
    }

    // Bounded retry constants (no game-state assumption, all safe to wait out).
    private const int MaxTeleportRetryFrames = 600;      // ~10s at 60fps
    private const int MaxCorrectiveSceneLoads = 3;
    private const float SelfHealCooldown = 3f;
    private const float HostTeleportOffsetX = 1.2f;
    private const float ExtraPlayerOffsetX = 0.8f;

    // ---- host state ----
    private bool _wasHost;
    private int _hostSceneIndex = -1;

    // ---- client state ----
    private bool _wasClient;
    private int _desiredSceneIndex = -1;
    private string _desiredSceneName = string.Empty;
    private Vector2 _hostPosition = Vector2.zero;
    private bool _loading;
    private int _loadGeneration;
    private int _loadAttempts;
    private int _lastFinishedIndex = -1;

    // Teleport retry: the game instantiates its InitObject (player, camera) from StageControl.Awake,
    // so PlayerAnimControl.instance can still be null on the frames right after the scene finished.
    private bool _hasPendingTeleport;
    private Vector2 _pendingTeleportTarget;
    private int _teleportRetryFrames;

    // Scene drift detection: the client's own vanilla LevelLoad door transition can silently move it to a
    // different room. The host is authoritative, so pull the client back to the host's scene (bounded).
    private int _correctiveLoads;
    private float _nextSelfHealTime;

    // IsSceneReady is polled for every enemy packet (~5Hz * enemy count); cache it per frame.
    private int _sceneReadyFrame = -1;
    private bool _sceneReadyCache;

    // Update() exception throttling.
    private float _nextUpdateErrorLogTime;

    // Host: true. Client: the desired (host) scene index is known and already active.
    public bool IsSceneReady
    {
      get
      {
        int frame = Time.frameCount;
        if (frame == _sceneReadyFrame) return _sceneReadyCache;
        _sceneReadyFrame = frame;
        _sceneReadyCache = ComputeSceneReady();
        return _sceneReadyCache;
      }
    }

    // 客户端是否正在执行“加载主机场景”的流程。LootSyncManager 的轮询会参考它，
    // 避免在场景卸载/加载期间把“对象被场景销毁”误判成“被玩家捡走”。
    public bool IsClientLoading
    {
      get { return _loading; }
    }

    private bool ComputeSceneReady()
    {
      NetworkManager nm = NetworkManager.Instance;
      if (nm == null || !nm.IsClient)
      {
        return true;      // host and offline mode are always "ready"
      }
      if (_desiredSceneIndex < 0)
      {
        return false;
      }
      return SceneManager.GetActiveScene().buildIndex == _desiredSceneIndex;
    }

    private void InvalidateSceneReadyCache()
    {
      _sceneReadyFrame = -1;
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

    private void Start()
    {
      // Capture the initial scene so the first Update() does not broadcast a spurious change.
      _hostSceneIndex = SceneManager.GetActiveScene().buildIndex;
      NetworkManager nm = NetworkManager.Instance;
      _wasHost = nm != null && nm.IsHost;
      _wasClient = nm != null && nm.IsClient;
      LogSceneContext("WorldSyncManager ready");
    }

    private void Update()
    {
      try
      {
        UpdateCore();
      }
      catch (Exception ex)
      {
        if (Time.unscaledTime >= _nextUpdateErrorLogTime)
        {
          _nextUpdateErrorLogTime = Time.unscaledTime + 2f;
          Logger.LogError("[WST] WorldSyncManager.Update failed: " + ex.GetType().Name + ": " + ex.Message);
        }
      }
    }

    private void UpdateCore()
    {
      NetworkManager nm = NetworkManager.Instance;
      bool isHost = nm != null && nm.IsHost;
      bool isClient = nm != null && nm.IsClient;

      // Reset the client state on role switches / shutdown, and re-anchor the host index when hosting starts.
      if (isHost && !_wasHost)
      {
        _wasHost = true;
        _hostSceneIndex = SceneManager.GetActiveScene().buildIndex;
        ResetClientState("host mode started");
        Logger.LogInfo($"[WST] Host mode started; scene anchor idx={_hostSceneIndex}.");
      }
      else if (!isHost && _wasHost)
      {
        _wasHost = false;
      }

      if (isClient && !_wasClient)
      {
        _wasClient = true;
        ResetClientState("client mode started");
        Logger.LogInfo("[WST] Client mode started; waiting for SceneSync from host.");
      }
      else if (!isClient && _wasClient)
      {
        _wasClient = false;
        ResetClientState("client mode ended");
      }

      if (isHost) UpdateHost();
      else if (isClient) UpdateClient();
    }

    // ---------- host side ----------

    private void UpdateHost()
    {
      int active = SceneManager.GetActiveScene().buildIndex;
      if (_hostSceneIndex < 0)
      {
        _hostSceneIndex = active;
        return;
      }
      if (active == _hostSceneIndex)
      {
        return;
      }
      _hostSceneIndex = active;

      // Index 0 is the boot/main-menu scene: never drag clients into it.
      if (active <= 0)
      {
        Logger.LogInfo($"[WST] Host entered scene idx={active} (boot/menu); SceneSync not broadcast.");
        return;
      }

      NetworkManager nm = NetworkManager.Instance;
      if (nm == null || nm.Host == null)
      {
        Logger.LogInfo($"[WST] Host scene changed to idx={active} but no host session is active; broadcast skipped.");
        return;
      }

      SceneSyncPacket packet = CreateSceneSyncPacket();
      try
      {
        nm.Host.Broadcast(packet, 0UL, true);
        Logger.LogInfo($"[WST] Host scene changed -> idx={packet.SceneIndex} name='{packet.SceneName}'; SceneSync broadcast to clients.");
      }
      catch (Exception ex)
      {
        Logger.LogError($"[WST] Failed to broadcast SceneSync: {ex.GetType().Name}: {ex.Message}");
      }
    }

    // Host: send the current scene + host anchor position to one client (called on accept/join).
    public void SendSceneSyncTo(ulong targetSteamId)
    {
      NetworkManager nm = NetworkManager.Instance;
      if (nm == null || !nm.IsHost)
      {
        Logger.LogWarning("[WST] SendSceneSyncTo called while not hosting; ignored.");
        return;
      }
      int active = SceneManager.GetActiveScene().buildIndex;
      if (active <= 0)
      {
        Logger.LogInfo($"[WST] Not sending SceneSync: host is in boot/menu scene idx={active}.");
        return;
      }
      SceneSyncPacket packet = CreateSceneSyncPacket();
      try
      {
        nm.SendPacket(targetSteamId, packet, true);
        _hostSceneIndex = packet.SceneIndex;
        Logger.LogInfo($"[WST] Sent SceneSync idx={packet.SceneIndex} name='{packet.SceneName}' pos={packet.HostPosition} to {targetSteamId}.");
      }
      catch (Exception ex)
      {
        Logger.LogError($"[WST] Failed to send SceneSync to {targetSteamId}: {ex.GetType().Name}: {ex.Message}");
      }
    }

    private SceneSyncPacket CreateSceneSyncPacket()
    {
      Scene scene = SceneManager.GetActiveScene();
      Vector2 position = Vector2.zero;
      float rotation = 0f;
      PlayerAnimControl player = PlayerAnimControl.instance;
      if (player != null)
      {
        position = new Vector2(player.transform.position.x, player.transform.position.y);
        rotation = player.transform.eulerAngles.z;
      }
      return new SceneSyncPacket
      {
        SceneIndex = scene.buildIndex,
        SceneName = scene.name ?? string.Empty,
        HostPosition = position,
        HostRotation = rotation
      };
    }

    // ---------- client side ----------

    public void ApplySceneSync(SceneSyncPacket packet)
    {
      if (packet == null)
      {
        Logger.LogError("[WST] ApplySceneSync received a null packet; ignored.");
        return;
      }

      NetworkManager nm = NetworkManager.Instance;
      if (nm != null && nm.IsHost)
      {
        Logger.LogWarning("[WST] ApplySceneSync ignored on host (host is scene authority).");
        return;
      }
      if (packet.SceneIndex < 0)
      {
        Logger.LogError($"[WST] ApplySceneSync with invalid scene index {packet.SceneIndex}; ignored.");
        return;
      }
      if (!IsSceneLoadable(packet.SceneIndex))
      {
        Logger.LogError($"[WST] ApplySceneSync: scene idx={packet.SceneIndex} ('{packet.SceneName}') is not in this build's scene list; ignored.");
        return;
      }

      bool indexChanged = _desiredSceneIndex != packet.SceneIndex;

      // Always refresh the anchor: a duplicate broadcast can carry a newer host position.
      _hostPosition = packet.HostPosition;
      _desiredSceneName = packet.SceneName ?? string.Empty;

      if (_loading)
      {
        if (indexChanged)
        {
          Logger.LogInfo($"[WST] Host moved to idx={packet.SceneIndex} while a load is in progress; will reload after the current load.");
          _desiredSceneIndex = packet.SceneIndex;
          _loadAttempts = 0;
          _correctiveLoads = 0;
        }
        else
        {
          Logger.LogInfo($"[WST] Duplicate SceneSync for idx={packet.SceneIndex} while loading; ignored.");
        }
        return;
      }

      int active = SceneManager.GetActiveScene().buildIndex;
      if (packet.SceneIndex == active && _lastFinishedIndex == packet.SceneIndex)
      {
        Logger.LogInfo($"[WST] Duplicate SceneSync for idx={packet.SceneIndex} ignored: client already synced.");
        return;
      }

      if (indexChanged)
      {
        _loadAttempts = 0;
        _correctiveLoads = 0;
      }
      _desiredSceneIndex = packet.SceneIndex;
      InvalidateSceneReadyCache();

      if (active == packet.SceneIndex)
      {
        Logger.LogInfo($"[WST] Client is already in host scene idx={packet.SceneIndex}; finishing sync.");
        FinishSceneSync();
        return;
      }

      Logger.LogInfo($"[WST] Client scene sync: host is in idx={packet.SceneIndex} name='{_desiredSceneName}' pos={packet.HostPosition}; loading scene...");
      StartCoroutine(LoadSceneRoutine(packet.SceneIndex));
    }

    private static bool IsSceneLoadable(int sceneIndex)
    {
      if (sceneIndex < 0) return false;
      try
      {
        string path = SceneUtility.GetScenePathByBuildIndex(sceneIndex);
        return !string.IsNullOrEmpty(path);
      }
      catch (Exception)
      {
        return false;
      }
    }

    private void UpdateClient()
    {
      // Bounded teleport retry until the game has spawned the local player for the new scene.
      if (_hasPendingTeleport)
      {
        if (_teleportRetryFrames-- <= 0)
        {
          _hasPendingTeleport = false;
          Logger.LogWarning($"[WST] Gave up teleporting the local player next to host at {_pendingTeleportTarget} (player never appeared).");
        }
        else
        {
          TryApplyPendingTeleport();
        }
      }

      // Self-heal: pull the client back to the host's scene if it drifted (own door transition / failed load).
      if (_desiredSceneIndex < 0 || _loading || _lastFinishedIndex < 0)
      {
        return;
      }
      int active = SceneManager.GetActiveScene().buildIndex;
      if (active == _desiredSceneIndex)
      {
        _correctiveLoads = 0;
        return;
      }
      if (Time.time < _nextSelfHealTime || _correctiveLoads >= MaxCorrectiveSceneLoads)
      {
        return;
      }

      _correctiveLoads++;
      _nextSelfHealTime = Time.time + SelfHealCooldown;
      Logger.LogWarning($"[WST] Client scene drift: active idx={active} but host idx={_desiredSceneIndex}; reloading host scene (attempt {_correctiveLoads}/{MaxCorrectiveSceneLoads}).");
      StartCoroutine(LoadSceneRoutine(_desiredSceneIndex));
    }

    // Loads a scene async, waits for completion + 2 frames, then teleports next to the host and asks the
    // host for the enemy snapshot.
    private IEnumerator LoadSceneRoutine(int sceneIndex)
    {
      if (_loading)
      {
        Logger.LogWarning($"[WST] Scene load already in progress; request for idx={sceneIndex} is deferred to the post-load check.");
        yield break;
      }
      if (sceneIndex < 0)
      {
        Logger.LogError($"[WST] Refusing to load invalid scene index {sceneIndex}.");
        yield break;
      }
      if (!IsSceneLoadable(sceneIndex))
      {
        Logger.LogError($"[WST] Refusing to load scene idx={sceneIndex}: not present in this build's scene list.");
        yield break;
      }

      _loading = true;
      int generation = ++_loadGeneration;
      Logger.LogInfo($"[WST] Client loading host scene idx={sceneIndex} (async)...");

      AsyncOperation op = null;
      try
      {
        op = SceneManager.LoadSceneAsync(sceneIndex);
      }
      catch (Exception ex)
      {
        Logger.LogError($"[WST] SceneManager.LoadSceneAsync({sceneIndex}) threw {ex.GetType().Name}: {ex.Message}");
      }

      if (op == null)
      {
        _loading = false;
        Logger.LogError($"[WST] SceneManager.LoadSceneAsync({sceneIndex}) returned null; scene sync aborted.");
        yield break;
      }

      while (!op.isDone)
      {
        yield return null;
      }

      // Keep _loading set during the settle frames below so UpdateClient cannot start a competing load.
      // Give StageControl.Awake/Start time to spawn InitObject (local player, camera) before touching it.
      yield return null;
      yield return null;

      if (generation != _loadGeneration)
      {
        // Client state was reset in the meantime; a newer load owns _loading now, so do not touch it.
        Logger.LogInfo("[WST] Scene load finished but client state was reset in the meantime; not finishing sync.");
        yield break;
      }

      _loading = false;
      InvalidateSceneReadyCache();

      int active = SceneManager.GetActiveScene().buildIndex;
      Logger.LogInfo($"[WST] Client scene load complete: active idx={active} name='{SceneManager.GetActiveScene().name}'.");

      if (_desiredSceneIndex < 0)
      {
        Logger.LogWarning("[WST] Desired scene was reset while loading; not finishing scene sync.");
        yield break;
      }

      if (active != _desiredSceneIndex)
      {
        _loadAttempts++;
        if (_loadAttempts > MaxCorrectiveSceneLoads)
        {
          _loadAttempts = 0;
          Logger.LogError($"[WST] Scene stayed at idx={active} after loading idx={sceneIndex} (desired idx={_desiredSceneIndex}); giving up.");
          yield break;
        }
        Logger.LogWarning($"[WST] Host switched scene during the load; loading desired idx={_desiredSceneIndex} (attempt {_loadAttempts}).");
        yield return StartCoroutine(LoadSceneRoutine(_desiredSceneIndex));
        yield break;
      }

      _loadAttempts = 0;
      FinishSceneSync();
    }

    private void FinishSceneSync()
    {
      if (_desiredSceneIndex < 0)
      {
        Logger.LogWarning("[WST] FinishSceneSync without a desired scene; ignoring (state was reset).");
        return;
      }

      int active = SceneManager.GetActiveScene().buildIndex;
      _lastFinishedIndex = active;
      _nextSelfHealTime = Time.time + SelfHealCooldown;

      if (active != _desiredSceneIndex)
      {
        Logger.LogWarning($"[WST] FinishSceneSync while active idx={active} != desired idx={_desiredSceneIndex}; Update() will retry.");
      }

      // Spread co-op players sideways instead of stacking every client on host + 1.2.
      int remoteCount = 0;
      try
      {
        PlayerManager players = PlayerManager.Instance;
        if (players != null) remoteCount = players.RemotePlayerCount;
      }
      catch (Exception)
      {
        remoteCount = 0;
      }
      _pendingTeleportTarget = _hostPosition + new Vector2(
          HostTeleportOffsetX + ExtraPlayerOffsetX * remoteCount, 0f);
      _hasPendingTeleport = true;
      _teleportRetryFrames = MaxTeleportRetryFrames;
      TryApplyPendingTeleport();
      if (_hasPendingTeleport)
      {
        Logger.LogWarning("[WST] Local player is not spawned yet after the scene load; will retry the teleport for a few seconds.");
      }

      SendSnapshotRequest();
    }

    private void TryApplyPendingTeleport()
    {
      if (!_hasPendingTeleport)
      {
        return;
      }

      PlayerAnimControl player = PlayerAnimControl.instance;
      if (player == null)
      {
        return;           // not spawned yet, retried from UpdateClient
      }
      if (player.isDeath)
      {
        return;           // do not teleport a dead body, retried from UpdateClient
      }

      try
      {
        PlayerManager manager = PlayerManager.Instance;
        if (manager == null)
        {
          _hasPendingTeleport = false;
          Logger.LogWarning("[WST] Cannot teleport local player: PlayerManager.Instance is null.");
          return;
        }
        manager.TeleportLocalPlayer(_pendingTeleportTarget);
        _hasPendingTeleport = false;
        Logger.LogInfo($"[WST] Local player teleported next to host at {_pendingTeleportTarget}.");
      }
      catch (Exception ex)
      {
        _hasPendingTeleport = false;
        Logger.LogError($"[WST] TeleportLocalPlayer failed: {ex.GetType().Name}: {ex.Message}");
      }
    }

    // Ask the host for the full enemy snapshot after arriving in the scene (EnemySyncManager is not
    // directly referenced here: its handler owns the SnapshotRequestPacket route on the host).
    private void SendSnapshotRequest()
    {
      NetworkManager nm = NetworkManager.Instance;
      if (nm == null || !nm.IsClient)
      {
        return;
      }
      try
      {
        nm.SendPacket(0UL, new SnapshotRequestPacket(), true);
        Logger.LogInfo("[WST] Requested enemy snapshot from host.");
      }
      catch (Exception ex)
      {
        Logger.LogError($"[WST] Failed to send SnapshotRequest: {ex.GetType().Name}: {ex.Message}");
      }
    }

    // ---------- scene change logging (diagnostics for the 2-player test) ----------

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
      try
      {
        InvalidateSceneReadyCache();
        LogSceneContext($"Scene loaded (mode={mode})");
      }
      catch (Exception ex)
      {
        Logger.LogError($"[WST] Scene loaded handler failed: {ex.GetType().Name}: {ex.Message}");
      }
    }

    private static void LogSceneContext(string prefix)
    {
      Scene scene = SceneManager.GetActiveScene();
      string stageInfo = string.Empty;
      StageControl stage = StageControl.instance;
      if (stage != null)
      {
        stageInfo = $" stage: sceneLevel={stage.sceneLevel} MapID={stage.MapID} stageMapId={stage.stageMapId}";
      }
      Logger.LogInfo($"[WST] {prefix}: scene idx={scene.buildIndex} name='{scene.name}'{stageInfo}");
    }

    // ---------- legacy world-state API (kept for old call sites; scene index is authoritative) ----------

    // Log-only no-op: legacy WorldStatePacket used to teleport by reflection; SceneSyncPacket replaced it.
    public void ApplyWorldState(WorldStatePacket packet)
    {
      if (packet == null)
      {
        return;
      }
      Logger.LogInfo($"[WST] Legacy WorldStatePacket received (room='{packet.RoomIdentifier}', pos={packet.Position}, rot={packet.Rotation:0.0}); ignored, scene sync is authoritative.");
    }

    // Legacy helper still called by HostController.SendCurrentWorldState; SceneSyncPacket is the real packet.
    public WorldStatePacket GetCurrentWorldStatePacket()
    {
      Scene scene = SceneManager.GetActiveScene();
      Vector2 position = Vector2.zero;
      float rotation = 0f;
      PlayerAnimControl player = PlayerAnimControl.instance;
      if (player != null)
      {
        position = new Vector2(player.transform.position.x, player.transform.position.y);
        rotation = player.transform.eulerAngles.z;
      }
      return new WorldStatePacket
      {
        RoomIdentifier = scene.name ?? string.Empty,
        Position = position,
        Rotation = rotation
      };
    }

    // Clears all client scene-sync state (role switch, shutdown, reconnect).
    private void ResetClientState(string reason)
    {
      if (_desiredSceneIndex >= 0 || _loading || _hasPendingTeleport)
      {
        Logger.LogInfo($"[WST] Resetting client scene-sync state ({reason}).");
      }
      _loadGeneration++;
      _desiredSceneIndex = -1;
      _desiredSceneName = string.Empty;
      _hostPosition = Vector2.zero;
      _loading = false;
      _loadAttempts = 0;
      _lastFinishedIndex = -1;
      _hasPendingTeleport = false;
      _teleportRetryFrames = 0;
      _correctiveLoads = 0;
      _nextSelfHealTime = 0f;
      InvalidateSceneReadyCache();
    }

    // Called by NetworkManager when the session ends / the host disconnects.
    public void ResetForDisconnect()
    {
      _wasHost = false;
      _wasClient = false;
      _hostSceneIndex = -1;
      ResetClientState("connection ended");
    }
  }
}
