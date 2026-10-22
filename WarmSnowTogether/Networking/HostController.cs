using System;
using System.Collections.Generic;
using UnityEngine;
using WarmSnowTogether.Networking.Entities;
using WarmSnowTogether.Networking.Interfaces;
using WarmSnowTogether.Networking.Loot;
using WarmSnowTogether.Networking.Packets;
using WarmSnowTogether.Networking.Serialization;
using WarmSnowTogether.Networking.Steam;
using WarmSnowTogether.Networking.Sync;
using WarmSnowTogether.Systems.Logging;
using Debug = WarmSnowTogether.Systems.Logging.Debug;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking
{
  // [WST NetSync v1.1] Host role (owner: lead).
  // Responsibilities: accept the client, push the initial world state (scene + enemies),
  // relay player positions. Enemy simulation itself lives in EnemySyncManager.
  //
  // v1.1 fixes:
  //   * a repeated ConnectionRequest from an already-registered client (i.e. the first
  //     ConnectionAccepted got lost) now re-sends accept + scene + snapshot instead of being
  //     dropped, so a client can always recover without restarting the game;
  //   * per-broadcast Info logging removed (it wrote one line per enemy per 0.2s tick into
  //     LogOutput.log, which was a measurable frame-time cost with many enemies);
  //   * a position packet with PlayerId == 0 is rewritten to the transport SteamId before it
  //     is shown locally and relayed to other clients (3+ players: otherwise every unknown-id
  //     packet would share one avatar);
  //   * new remote players spawn offset beside the host instead of exactly on top of it
  //     (again a 3+ player fix), and P2P sessions are closed on disconnect/shutdown.
  public class HostController : IHost
  {
    private List<ulong> _connectedClients = new List<ulong>();
    private Dictionary<ulong, ulong> _clientPlayerIds = new Dictionary<ulong, ulong>();
    private SteamP2PManager _p2p;
    private float _nextPositionSendTime;
    private const float PositionSendInterval = 0.25f;
    private const float BaseSpawnOffsetX = 1.2f;
    // Lobby is 4 players max; this is only a safety net against forged ConnectionRequests.
    private const int MaxConnectedClients = 8;
    // Throttle for "position packet from an unknown peer" warnings (4 packets/s per client).
    private float _nextUnknownSenderLogTime;

    public void Initialise()
    {
      _p2p = SteamP2PManager.Instance;
      if (_p2p != null)
      {
        _p2p.OnP2PSessionFailed += HandleP2PSessionFailed;
      }
      Debug.Log("[WST] HostController Initialised.");
    }

    public void StartSession()
    {
      Debug.Log("[WST] Host session started.");
    }

    public void Update()
    {
      if (_connectedClients.Count == 0) return;
      if (Time.realtimeSinceStartup < _nextPositionSendTime) return;

      _nextPositionSendTime = Time.realtimeSinceStartup + PositionSendInterval;
      ulong localId;
      try
      {
        localId = _p2p != null && _p2p.LocalSteamID != 0
            ? _p2p.LocalSteamID
            : SteamReflectionHelper.GetLocalSteamId();
      }
      catch (Exception)
      {
        // Steamworks not ready / temporarily offline: keep the previous behaviour without throwing
        // out of Update() (that would log one error per frame from the plugin Update loop).
        localId = _p2p != null ? _p2p.LocalSteamID : 0UL;
      }

      var packet = PlayerManager.Instance.CreateLocalPositionPacket(localId);
      if (packet != null)
      {
        Broadcast(packet, reliable: false);
      }
    }

    public void Shutdown()
    {
      // Tell clients we are going away before dropping the P2P sessions.
      var disconnectPacket = new DisconnectPacket();
      Broadcast(disconnectPacket, reliable: true);

      if (_p2p != null)
      {
        _p2p.OnP2PSessionFailed -= HandleP2PSessionFailed;
        for (int i = 0; i < _connectedClients.Count; i++)
        {
          try
          {
            // Delayed: the reliable Disconnect above must reach the client before the session dies.
            _p2p.CloseSessionDeferred(_connectedClients[i]);
          }
          catch (Exception ex)
          {
            Logger.LogWarning("[WST] CloseSession(" + _connectedClients[i] + ") failed: " + ex.Message);
          }
        }
      }
      _connectedClients.Clear();
      _clientPlayerIds.Clear();
      PlayerManager.Instance.ClearAll();
      Debug.Log("[WST] Host shutdown complete.");
    }

    private void HandleP2PSessionFailed(ulong clientId)
    {
      HandleClientDisconnect(clientId);
    }

    private void AcceptP2PSession(ulong playerId)
    {
      try
      {
        if (SteamReflectionHelper.AcceptP2PSessionMethod != null)
        {
          object steamIdObj = SteamReflectionHelper.CreateCSteamID(playerId);
          SteamReflectionHelper.AcceptP2PSessionMethod.Invoke(null, new object[] { steamIdObj });
          Debug.Log($"[WST] Accepted P2P session with {playerId}.");
        }
      }
      catch (Exception e)
      {
        Debug.LogWarning($"[WST] Failed to accept P2P session with {playerId}: {e.Message}");
      }
    }

    public void HandleJoinRequest(ulong playerId)
    {
      HandleJoinRequest(playerId, playerId, NetworkManager.ProtocolVersion);
    }

    public void HandleJoinRequest(ulong transportId, ulong playerId, int protocolVersion)
    {
      Debug.Log($"[WST] HandleJoinRequest from {playerId}, version={protocolVersion}, expected={NetworkManager.ProtocolVersion}");
      if (protocolVersion != NetworkManager.ProtocolVersion)
      {
        var reject = new ConnectionRejectedPacket { ProtocolVersion = NetworkManager.ProtocolVersion };
        SendPacket(transportId, reject, reliable: true);
        Debug.Log($"[WST] Rejected {playerId} - protocol mismatch");
        return;
      }

      ulong registeredPlayerId = playerId != 0 ? playerId : transportId;

      if (_connectedClients.Contains(transportId))
      {
        // The client retries ConnectionRequest once a second until it sees ConnectionAccepted.
        // If that packet was lost, re-sending the (idempotent) handshake is the only way the
        // client can ever finish connecting - dropping the request used to leave it stuck.
        Debug.Log($"[WST] Player {playerId} already connected; re-sending accept + world state (idempotent).");
        AcceptP2PSession(transportId);
        SendConnectionAccepted(transportId);
        WorldSyncManager.Instance?.SendSceneSyncTo(transportId);
        EnemySyncManager.Instance?.SendSnapshotTo(transportId);
        LootSyncManager.Instance?.SendSnapshotTo(transportId);
        return;
      }

      if (_connectedClients.Count >= MaxConnectedClients)
      {
        Debug.LogWarning($"[WST] Rejected {playerId}: host client limit ({MaxConnectedClients}) reached.");
        SendPacket(transportId, new ConnectionRejectedPacket { ProtocolVersion = NetworkManager.ProtocolVersion }, reliable: true);
        return;
      }

      _connectedClients.Add(transportId);
      _clientPlayerIds[transportId] = registeredPlayerId;
      Debug.Log($"[WST] Player {playerId} added to connected clients (total {_connectedClients.Count})");

      AcceptP2PSession(transportId);
      SendConnectionAccepted(transportId);

      // ---- [WST NetSync v1] initial world state push, in this order: scene -> enemies -> avatar ----
      Vector2 hostPosition = Vector2.zero;
      var localPlayer = PlayerManager.Instance.LocalPlayer;
      if (localPlayer != null)
      {
        hostPosition = localPlayer.transform.position;
      }

      // Requirement 1: the client loads the host's current Unity scene.
      WorldSyncManager.Instance?.SendSceneSyncTo(transportId);

      // Requirement 3: the client receives every enemy the host currently has (it may drop them
      // while its scene is still loading and request a fresh snapshot afterwards).
      EnemySyncManager.Instance?.SendSnapshotTo(transportId);

      // [WST Loot Sync v1] host-authoritative drops the client can see / pick up (same single copy).
      LootSyncManager.Instance?.SendSnapshotTo(transportId);

      // Requirement 2: the host renders the client's avatar right away, before the first position
      // packet. Offset it sideways so 3+ players do not all stand on the same pixel.
      Vector2 avatarPosition = hostPosition + new Vector2(
          BaseSpawnOffsetX + PlayerManager.RemoteSpawnOffsetStep * PlayerManager.Instance.RemotePlayerCount, 0f);
      PlayerManager.Instance.SpawnRemotePlayer(registeredPlayerId, avatarPosition, 0f);

      // Notify any other clients that somebody joined (keeps 3+ players in sync).
      var joinPacket = new PlayerJoinPacket
      {
        PlayerId = registeredPlayerId,
        Position = avatarPosition,
        Rotation = 0f
      };
      Broadcast(joinPacket, transportId, true);
    }

    private void SendConnectionAccepted(ulong transportId)
    {
      try
      {
        var accept = new ConnectionAcceptedPacket
        {
          HostId = SteamReflectionHelper.GetLocalSteamId(),
          ProtocolVersion = NetworkManager.ProtocolVersion
        };
        SendPacket(transportId, accept, reliable: true);
      }
      catch (Exception ex)
      {
        Logger.LogWarning("[WST] Failed to send ConnectionAccepted to " + transportId + ": " + ex.Message);
      }
    }

    public void HandlePlayerPosition(ulong senderId, PlayerPositionPacket packet)
    {
      if (packet == null) return;
      ulong playerId = packet.PlayerId != 0 ? packet.PlayerId : senderId;
      if (_clientPlayerIds.TryGetValue(senderId, out ulong registeredPlayerId) && registeredPlayerId != 0)
      {
        playerId = registeredPlayerId;
      }

      if (!_connectedClients.Contains(senderId) && !_connectedClients.Contains(playerId))
      {
        if (Time.unscaledTime >= _nextUnknownSenderLogTime)
        {
          _nextUnknownSenderLogTime = Time.unscaledTime + 5f;
          Debug.LogWarning($"[WST] Ignored position packet from unrecognised sender {senderId} / player {playerId}.");
        }
        return;
      }

      // Normalise the id once: the local avatar registry and the relayed packet must use the
      // resolved SteamId (3+ players / LocalSteamID not fetched yet on the client side).
      packet.PlayerId = playerId;

      // Render the client avatar on the host side (requirement 2).
      PlayerManager.Instance.HandlePlayerPosition(packet);

      // Relay to other clients (keeps 3+ players visible to each other).
      Broadcast(packet, senderId, reliable: false);
    }

    public void SendPacket(ulong targetId, INetworkPacket packet, bool reliable = true)
    {
      if (_p2p == null || packet == null) return;
      byte[] data = PacketSerializer.Serialize(packet);
      if (data == null || data.Length == 0) return;
      _p2p.SendPacket(targetId, data, reliable);
    }

    public void Broadcast(INetworkPacket packet, ulong excludeId = 0, bool reliable = true)
    {
      if (_p2p == null || packet == null || _connectedClients.Count == 0) return;
      byte[] data = PacketSerializer.Serialize(packet);
      if (data == null || data.Length == 0) return;
      bool sentAny = false;
      for (int i = 0; i < _connectedClients.Count; i++)
      {
        ulong client = _connectedClients[i];
        if (client != excludeId)
        {
          _p2p.SendPacket(client, data, reliable);
          sentAny = true;
        }
      }
      if (sentAny)
      {
        // Per-packet logs used to be Info level (one line per enemy per 5Hz tick).
        Logger.LogDebug($"[WST] Broadcast {packet.Type} to {_connectedClients.Count} client(s).");
      }
    }

    public void HandleClientDisconnect(ulong clientId)
    {
      if (!_connectedClients.Remove(clientId)) return;

      if (_p2p != null)
      {
        try
        {
          _p2p.CloseSession(clientId);
        }
        catch (Exception)
        {
        }
      }

      if (_clientPlayerIds.TryGetValue(clientId, out ulong playerId))
      {
        _clientPlayerIds.Remove(clientId);
        Debug.Log($"[WST] Client {clientId} disconnected, playerId:{playerId}");

        // Remove the remote avatar on the host side.
        PlayerManager.Instance.RemoveRemotePlayer(playerId);

        var leavePacket = new PlayerLeavePacket
        {
          PlayerId = playerId
        };
        Broadcast(leavePacket, 0, true);
      }
    }
  }
}
