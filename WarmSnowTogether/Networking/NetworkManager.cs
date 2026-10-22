using System;
using UnityEngine;
using WarmSnowTogether.Networking.Entities;
using WarmSnowTogether.Networking.Enums;
using WarmSnowTogether.Networking.Interfaces;
using WarmSnowTogether.Networking.Loot;
using WarmSnowTogether.Networking.Packets;
using WarmSnowTogether.Networking.Serialization;
using WarmSnowTogether.Networking.Steam;
using WarmSnowTogether.Networking.Sync;
using Debug = WarmSnowTogether.Systems.Logging.Debug;

namespace WarmSnowTogether.Networking
{
  // [WST NetSync v1.1] Core network facade (owner: lead).
  // Owns the role/transport lifecycle and routes every incoming packet to the module that
  // understands it. Host Authority design:
  //   host   -> owns scene index, enemy spawn/AI/HP/death, player positions
  //   client -> follows SceneSync, renders host enemies as passive copies, sends DamageRequest
  //
  // v1.1 fixes:
  //   * a host Disconnect packet is now actually handled by the client (v1 ignored it, so a
  //     client whose host quit kept stale avatars/enemy copies until the P2P session failed);
  //   * P2P session loss / connect timeout / lobby leave all funnel through one cleanup that
  //     removes remote avatars + passive enemy copies and resets client scene-sync state;
  //   * IsConnected now reports "really connected" instead of "a role object exists";
  //   * duplicate StartHosting/ConnectTo calls (double LobbyCreated / double LobbyEnter
  //     callbacks, or a reconnect while already connected) are ignored instead of tearing the
  //     live session down.
  public class NetworkManager
  {
    private static NetworkManager _instance;
    public static NetworkManager Instance
    {
      get
      {
        if (_instance == null)
        {
          _instance = new NetworkManager();
        }
        return _instance;
      }
    }

    public bool IsHost { get; private set; }
    public bool IsClient { get; private set; }
    public bool IsConnected => Host != null || (Client != null && Client.IsConnected);
    public INetworkRole CurrentRole { get; private set; }
    public HostController Host { get; private set; }
    public ClientController Client { get; private set; }

    private SteamP2PManager _p2p;
    private SteamLobbyManager _lobby;

    // [WST NetSync v1] bumped from 1 -> 2 because PlayerPositionPacket/Enemy*Packet layout changed.
    // [WST Loot Sync v1] bumped 2 -> 3 because LootSpawn/LootConsumed packet types were added.
    // Both peers must run the same build; this rejects a stale build as a safety net.
    public const int ProtocolVersion = 3;

    public void Initialise()
    {
      try
      {
        Debug.Log("NetworkManager: Initializing P2P Manager...");
        _p2p = SteamP2PManager.Instance;
        _p2p.Initialise();
        _p2p.OnPacketReceived += HandlePacket;
        Debug.Log("NetworkManager: P2P Manager initialized.");

        Debug.Log("NetworkManager: Initializing Lobby Manager...");
        _lobby = SteamLobbyManager.Instance;
        _lobby.Initialise();
        Debug.Log("NetworkManager: Lobby Manager initialized.");

        Debug.Log("NetworkManager Initialised.");
        PlayerManager.Instance.gameObject.SetActive(true);
      }
      catch (System.Exception ex)
      {
        Debug.LogError($"NetworkManager: Exception during initialization: {ex.GetType().Name}: {ex.Message}");
        Debug.LogError($"NetworkManager: Stack trace: {ex.StackTrace}");
        throw;
      }
    }

    public void Update()
    {
      _lobby?.Update();
      _p2p?.Update();
      CurrentRole?.Update();
    }

    public void StartHosting()
    {
      if (IsHost && Host != null)
      {
        Debug.Log("[WST] StartHosting ignored: already hosting.");
        return;
      }
      if (CurrentRole != null) Shutdown();
      IsHost = true;
      IsClient = false;
      Host = new HostController();
      Host.Initialise();
      Host.StartSession();
      CurrentRole = Host;
      Debug.Log("[WST] Started Hosting.");
    }

    public void ConnectTo(ulong hostId)
    {
      if (hostId == 0UL)
      {
        Debug.LogWarning("[WST] ConnectTo ignored: host id is 0.");
        return;
      }
      if (IsClient && Client != null && Client.HostId == hostId && (Client.IsConnected || Client.IsConnecting))
      {
        Debug.Log("[WST] ConnectTo ignored: already connected/connecting to host " + hostId + ".");
        return;
      }
      if (CurrentRole != null) Shutdown();
      IsHost = false;
      IsClient = true;
      Client = new ClientController();
      Client.Initialise();
      Client.Connect(hostId);
      CurrentRole = Client;
      Debug.Log($"[WST] Connecting to host {hostId}...");
    }

    public void Shutdown()
    {
      try
      {
        CurrentRole?.Shutdown();
      }
      catch (Exception ex)
      {
        Debug.LogError("[WST] Error during role shutdown: " + ex.GetType().Name + ": " + ex.Message);
      }
      CurrentRole = null;
      Host = null;
      Client = null;
      IsHost = false;
      IsClient = false;
      CleanUpRuntimeState("network shutdown");
    }

    // The host closed the session on purpose (Disconnect packet). Tear the client side down.
    private void HandleHostDisconnect(ulong senderId)
    {
      if (Client == null) return;
      if (Client.HostId != 0UL && senderId != Client.HostId)
      {
        Debug.LogWarning("[WST] Ignoring Disconnect from non-host peer " + senderId + ".");
        return;
      }
      Debug.LogWarning("[WST] Host " + senderId + " closed the session; cleaning up client state.");
      Client.HandleHostDisconnected();
      Shutdown();
      TryLeaveLobbyAfterDisconnect();
    }

    // Called by ClientController when the P2P session to the host failed / the handshake timed out.
    public void NotifyClientLostHost()
    {
      if (!IsClient) return;
      Debug.LogWarning("[WST] Client lost the host link; cleaning up.");
      Shutdown();
      TryLeaveLobbyAfterDisconnect();
    }

    private void TryLeaveLobbyAfterDisconnect()
    {
      try
      {
        // The Steam lobby is owned by the host, so it is gone/invalid now. Leave it so that the
        // next "联机模式" click creates/joins a fresh lobby instead of opening an invite dialog.
        SteamLobbyManager lobby = SteamLobbyManager.Instance;
        if (lobby != null) lobby.LeaveLobby();
      }
      catch (Exception ex)
      {
        Debug.LogWarning("[WST] Could not leave the stale Steam lobby: " + ex.Message);
      }
    }

    // Removes every piece of runtime state that belongs to a live session. Safe to call twice.
    private void CleanUpRuntimeState(string reason)
    {
      try
      {
        PlayerManager.Instance.ClearAll();
      }
      catch (Exception ex)
      {
        Debug.LogWarning("[WST] Cleanup (players) failed: " + ex.Message);
      }

      try
      {
        EnemySyncManager manager = EnemySyncManager.Instance;
        if (manager != null) manager.ClearAllClientCopies();
      }
      catch (Exception ex)
      {
        Debug.LogWarning("[WST] Cleanup (enemies) failed: " + ex.Message);
      }

      try
      {
        LootSyncManager manager = LootSyncManager.Instance;
        if (manager != null) manager.ClearAll();
      }
      catch (Exception ex)
      {
        Debug.LogWarning("[WST] Cleanup (loot) failed: " + ex.Message);
      }

      try
      {
        WorldSyncManager manager = WorldSyncManager.Instance;
        if (manager != null) manager.ResetForDisconnect();
      }
      catch (Exception ex)
      {
        Debug.LogWarning("[WST] Cleanup (scene sync) failed: " + ex.Message);
      }

      try
      {
        SteamLobbyManager lobby = SteamLobbyManager.Instance;
        if (lobby != null) lobby.SetStatus("SESSION ENDED (" + reason + ")");
      }
      catch (Exception)
      {
      }
    }

    private void HandlePacket(ulong senderId, byte[] data)
    {
      try
      {
        INetworkPacket packet = PacketSerializer.Deserialize(data);
        if (packet == null)
        {
          Debug.LogWarning($"[NetworkManager] Failed to deserialize packet from {senderId}");
          return;
        }
        ProcessPacket(senderId, packet);
      }
      catch (Exception e)
      {
        Debug.LogError($"[NetworkManager] Error handling packet type from {senderId}: {e.GetType().Name}: {e.Message}");
      }
    }

    private void ProcessPacket(ulong senderId, INetworkPacket packet)
    {
      switch (packet.Type)
      {
        // ------------------------------------------------------------------ connection handshake
        case PacketType.ConnectionRequest:
          if (IsHost)
          {
            var req = (ConnectionRequestPacket)packet;
            Debug.Log($"[NetworkManager] Host received ConnectionRequest from {senderId}, version={req.ProtocolVersion}");
            Host?.HandleJoinRequest(senderId, req.ClientId, req.ProtocolVersion);
          }
          break;
        case PacketType.ConnectionAccepted:
          if (IsClient)
          {
            Client?.HandleConnectionAccepted(senderId, (ConnectionAcceptedPacket)packet);
          }
          break;
        case PacketType.ConnectionRejected:
          if (IsClient)
          {
            Client?.HandleConnectionRejected(senderId, (ConnectionRejectedPacket)packet);
          }
          break;
        case PacketType.Disconnect:
          if (IsHost) Host?.HandleClientDisconnect(senderId);
          else if (IsClient) HandleHostDisconnect(senderId);
          break;

        // ------------------------------------------------------------------ player avatars (req 2)
        case PacketType.PlayerPosition:
          var posPacket = (PlayerPositionPacket)packet;
          if (IsClient)
          {
            // Local id is filtered inside PlayerManager (never spawn an avatar for ourselves).
            PlayerManager.Instance.HandlePlayerPosition(posPacket);
          }
          else if (IsHost)
          {
            Host?.HandlePlayerPosition(senderId, posPacket);
          }
          break;
        case PacketType.PlayerJoin:
          var joinPacket = (PlayerJoinPacket)packet;
          if (IsClient)
          {
            PlayerManager.Instance.SpawnRemotePlayer(joinPacket.PlayerId, joinPacket.Position, joinPacket.Rotation);
          }
          break;
        case PacketType.PlayerLeave:
          var leavePacket = (PlayerLeavePacket)packet;
          if (IsClient)
          {
            PlayerManager.Instance.RemoveRemotePlayer(leavePacket.PlayerId);
          }
          else if (IsHost)
          {
            Host?.HandleClientDisconnect(leavePacket.PlayerId);
          }
          break;

        // ------------------------------------------------------------------ scene sync (req 1)
        case PacketType.SceneSync:
          if (IsClient)
          {
            var sceneSync = (SceneSyncPacket)packet;
            WorldSyncManager.Instance?.ApplySceneSync(sceneSync);
          }
          break;

        // ------------------------------------------------------------------ enemy authority (req 3+4)
        case PacketType.SnapshotRequest:
          if (IsHost)
          {
            EnemySyncManager.Instance?.HandleSnapshotRequest(senderId);
            // [WST Loot Sync v1] 同一个 SnapshotRequest 也用来补发房主当前的掉落物快照。
            LootSyncManager.Instance?.SendSnapshotTo(senderId);
          }
          break;
        case PacketType.DamageRequest:
          if (IsHost)
          {
            EnemySyncManager.Instance?.HandleDamageRequest((DamageRequestPacket)packet);
          }
          break;
        case PacketType.EnemySpawn:
          if (IsClient)
          {
            EnemySyncManager.Instance?.HandleEnemySpawn((EnemySpawnPacket)packet);
          }
          break;
        case PacketType.EnemyState:
          if (IsClient)
          {
            EnemySyncManager.Instance?.HandleEnemyState((EnemyStatePacket)packet);
          }
          break;
        case PacketType.EnemyDeath:
          if (IsClient)
          {
            EnemySyncManager.Instance?.HandleEnemyDeath((EnemyDeathPacket)packet);
          }
          break;

        // ------------------------------------------------------------------ loot sync (v1.2)
        case PacketType.LootSpawn:
          if (IsClient)
          {
            LootSyncManager.Instance?.HandleLootSpawn((LootSpawnPacket)packet);
          }
          break;
        case PacketType.LootConsumed:
          // Client: remove the local mirror. Host: a client picked it up -> destroy host original
          // and broadcast the removal to the other clients (role check happens inside the handler).
          LootSyncManager.Instance?.HandleLootConsumed((LootConsumedPacket)packet);
          break;

        // ------------------------------------------------------------------ legacy packets (kept for compat)
        case PacketType.WorldState:
          if (IsClient)
          {
            WorldSyncManager.Instance?.ApplyWorldState((WorldStatePacket)packet);
          }
          break;
        case PacketType.PlayerState:
          // Deprecated in v1: player HP is not mirrored (the old handler corrupted local HP).
          break;
        case PacketType.LoadingState:
          if (IsClient)
          {
            LoadingSyncManager.Instance?.ApplyLoadingState(((LoadingStatePacket)packet).IsLoading);
          }
          break;
        case PacketType.RoomChange:
          // Deprecated in v1: SceneSync replaces room broadcasting.
          break;
        default:
          Debug.LogWarning($"[NetworkManager] Unhandled packet type: {packet.Type}");
          break;
      }
    }

    public void SendPacket(ulong targetId, INetworkPacket packet, bool reliable = true)
    {
      CurrentRole?.SendPacket(targetId, packet, reliable);
    }
  }
}
