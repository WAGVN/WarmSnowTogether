using WarmSnowTogether.Networking.Interfaces;
using WarmSnowTogether.Networking.Steam;
using WarmSnowTogether.Networking.Packets;
using WarmSnowTogether.Systems.Logging;
using UnityEngine;
using Debug = WarmSnowTogether.Systems.Logging.Debug;
using Logger = WarmSnowTogether.Systems.Logging.Logger;
using WarmSnowTogether.Networking.Serialization;

namespace WarmSnowTogether.Networking
{
  // [WST NetSync v1.1] Client role.
  //
  // Changes vs v1:
  //   * bounded connection handshake: retries stop after ConnectTimeoutSeconds instead of
  //     spamming "Sending connection request..." forever when the host is offline;
  //   * per-packet position logging moved to LogDebug (4 Info lines per second used to be
  //     written to BepInEx/LogOutput.log for the whole match);
  //   * HostId / IsConnecting are exposed so NetworkManager can ignore duplicate
  //     LobbyEnter/ConnectTo calls and route Disconnect packets;
  //   * losing the P2P session to the host now stops the client cleanly (NetworkManager
  //     removes remote avatars / enemy copies and resets scene sync state).
  public class ClientController : IClient
  {
    private const float PositionSendInterval = 0.25f;
    private const float ConnectRetryInterval = 1f;
    private const float ConnectTimeoutSeconds = 20f;

    private ulong _hostId;
    private SteamP2PManager _p2p;
    public bool IsConnected { get; private set; }
    private float _nextPositionSendTime;
    private float _connectRetryTime;
    private float _connectStartTime;
    private bool _connectTimeoutLogged;

    private bool _connectPending = false;

    public ulong HostId
    {
      get { return _hostId; }
    }

    public bool IsConnecting
    {
      get { return _connectPending; }
    }

    public void Initialise()
    {
      _p2p = SteamP2PManager.Instance;
      if (_p2p != null)
      {
        _p2p.OnP2PSessionFailed += HandleP2PSessionFailed;
      }
      Debug.Log("ClientController Initialised.");
    }

    public void Connect(ulong hostId)
    {
      _hostId = hostId;
      IsConnected = false;
      _connectPending = hostId != 0UL;
      _connectRetryTime = 0f;
      _connectStartTime = Time.realtimeSinceStartup;
      _connectTimeoutLogged = false;
      if (hostId == 0UL)
      {
        Debug.LogWarning("[Client] Connect called with hostId 0; ignored.");
        return;
      }
      Debug.Log($"[Client] Connect called to {hostId}, sending connection request...");
    }

    public void Update()
    {
      if (_connectPending)
      {
        float now = Time.realtimeSinceStartup;
        if (now - _connectStartTime > ConnectTimeoutSeconds)
        {
          if (!_connectTimeoutLogged)
          {
            _connectTimeoutLogged = true;
            Logger.LogWarning("[Client] Connection to host " + _hostId + " timed out after "
              + ConnectTimeoutSeconds + "s.");
            try
            {
              if (SteamLobbyManager.Instance != null)
              {
                SteamLobbyManager.Instance.SetStatus("CONNECT TIMEOUT: host did not answer (host left?)");
              }
            }
            catch (System.Exception)
            {
            }
          }
          _connectPending = false;
          IsConnected = false;
          NetworkManager network = NetworkManager.Instance;
          if (network != null) network.NotifyClientLostHost();
          return;
        }

        if (now >= _connectRetryTime)
        {
          _connectRetryTime = now + ConnectRetryInterval;
          if (_p2p == null) return;

          Logger.LogDebug($"Sending connection request to {_hostId}...");
          SendPacket(_hostId, new ConnectionRequestPacket
          {
            ClientId = _p2p.LocalSteamID,
            ProtocolVersion = NetworkManager.ProtocolVersion
          }, reliable: true);
        }
      }

      if (!IsConnected) return;
      if (Time.realtimeSinceStartup < _nextPositionSendTime) return;

      _nextPositionSendTime = Time.realtimeSinceStartup + PositionSendInterval;
      ulong localId;
      try
      {
        localId = _p2p != null && _p2p.LocalSteamID != 0
            ? _p2p.LocalSteamID
            : SteamReflectionHelper.GetLocalSteamId();
      }
      catch (System.Exception)
      {
        // Steamworks not ready / temporarily offline: never throw out of Update().
        localId = _p2p != null ? _p2p.LocalSteamID : 0UL;
      }

      if (localId == 0) return;
      var packet = PlayerManager.Instance.CreateLocalPositionPacket(localId);
      if (packet == null) return;
      SendPacket(_hostId, packet, reliable: false);
      Logger.LogDebug($"[Client] Sent position packet: ({packet.Position.x:0.00}, {packet.Position.y:0.00})");
    }

    public void HandleConnectionAccepted(ulong senderId, ConnectionAcceptedPacket packet)
    {
      if (packet == null) return;
      if (senderId != _hostId) return;
      if (packet.ProtocolVersion != NetworkManager.ProtocolVersion)
      {
        Logger.LogWarning($"[Client] Protocol mismatch. Host={packet.ProtocolVersion} Local={NetworkManager.ProtocolVersion}");
        _connectPending = false;
        Disconnect();
        return;
      }
      IsConnected = true;
      _connectPending = false;
      _nextPositionSendTime = 0;
      Logger.LogInfo($"[Client] Connection accepted by host {senderId}.");
    }

    public void HandleConnectionRejected(ulong senderId, ConnectionRejectedPacket packet)
    {
      if (senderId != _hostId) return;
      _connectPending = false;
      IsConnected = false;
      int hostProtocol = packet != null ? packet.ProtocolVersion : -1;
      Logger.LogWarning($"[Client] Connection rejected by host {senderId} (host protocol={hostProtocol}, local={NetworkManager.ProtocolVersion}). Halting connection attempts.");
      try
      {
        if (SteamLobbyManager.Instance != null)
        {
          SteamLobbyManager.Instance.SetStatus("CONNECT REJECTED: host protocol " + hostProtocol
            + " != local " + NetworkManager.ProtocolVersion);
        }
      }
      catch (System.Exception)
      {
      }
    }

    // Called by NetworkManager when the host sends a Disconnect packet (host left / shut down).
    public void HandleHostDisconnected()
    {
      IsConnected = false;
      _connectPending = false;
    }

    private void HandleP2PSessionFailed(ulong remoteId)
    {
      if (remoteId != _hostId) return;
      Logger.LogWarning($"[Client] P2P session to host {remoteId} lost, disconnect.");
      IsConnected = false;
      _connectPending = false;
      NetworkManager network = NetworkManager.Instance;
      if (network != null) network.NotifyClientLostHost();
    }

    public void Shutdown()
    {
      Disconnect();
      if (_p2p != null)
      {
        _p2p.OnP2PSessionFailed -= HandleP2PSessionFailed;
      }
    }

    public void Disconnect()
    {
      bool wasConnected = IsConnected;
      IsConnected = false;
      _connectPending = false;
      if (wasConnected && _p2p != null)
      {
        SendPacket(_hostId, new DisconnectPacket(), reliable: true);
        Debug.Log("Disconnected from host.");
      }
      if (_p2p != null && _hostId != 0UL)
      {
        // Deferred: let the reliable Disconnect packet above leave before the session is closed.
        _p2p.CloseSessionDeferred(_hostId);
      }
    }

    public void SendPacket(ulong targetId, INetworkPacket packet, bool reliable = true)
    {
      if (_p2p == null || packet == null) return;
      // 客户端默认发给主机
      if (targetId == 0) targetId = _hostId;
      if (targetId == 0) return;

      byte[] data = PacketSerializer.Serialize(packet);
      if (data == null || data.Length == 0) return;
      _p2p.SendPacket(targetId, data, reliable);
    }
  }
}
