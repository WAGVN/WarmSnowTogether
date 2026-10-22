using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using System.Collections.Concurrent;
using WarmSnowTogether.Core;
using WarmSnowTogether.Systems.Logging;
using Debug = WarmSnowTogether.Systems.Logging.Debug;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking.Steam
{
  public struct IncomingPacket
  {
    public ulong Sender;
    public byte[] Data;
    public IncomingPacket(ulong sender, byte[] data)
    {
      Sender = sender;
      Data = data;
    }
  }

  // [WST NetSync v1.1] Steam P2P transport.
  //
  // v1.1 performance / robustness fixes:
  //   * per-packet SendPacket/ReadPackets logs were Info level: with a 5Hz enemy stream that
  //     meant hundreds of synchronous writes into BepInEx/LogOutput.log per second. They are
  //     LogDebug now (and dropped by default BepInEx log level), failures stay visible;
  //   * at most 256 queued packets are dispatched per frame, so a burst/flood cannot stall the
  //     main thread for one long frame (the rest is processed next frame);
  //   * packet size is sanity-checked (0 < size <= 64KB) and the CSteamID type is cached;
  //   * SteamId lookup retries are limited to once per second instead of every frame;
  //   * CloseSession() lets HostController/ClientController close P2P sessions on disconnect.
  public class SteamP2PManager
  {
    private static readonly object _instanceLock = new object();
    private static SteamP2PManager _instance;

    public static SteamP2PManager Instance
    {
      get
      {
        lock (_instanceLock)
        {
          if (_instance == null)
          {
            try
            {
              _instance = new SteamP2PManager();
            }
            catch (System.TypeLoadException tle)
            {
              Debug.LogError($"SteamP2PManager: TypeLoadException: {tle.Message}");
              throw;
            }
            catch (System.Exception ex)
            {
              Debug.LogError($"SteamP2PManager: {ex.GetType().Name}: {ex.Message}");
              throw;
            }
          }
          return _instance;
        }
      }
    }

    private object _p2pSessionConnectFailCallback;
    private object _p2pSessionRequestCallback;
    private const int CHANNEL_INDEX = 0;
    private const int MAX_PACKETS_PER_READ = 32;
    private const int MAX_PACKETS_DISPATCHED_PER_FRAME = 256;
    private const int MAX_INCOMING_PACKET_BYTES = 64 * 1024;
    private const float READ_PACKETS_INTERVAL_SECONDS = 0.05f;

    public bool IsInitialised { get; private set; }
    public ulong LocalSteamID { get; private set; }

    private bool _steamIdRetrieved = false;
    private float _nextSteamIdAttemptTime;
    private Assembly _steamworksAssembly;
    private Type _csteamIdType;

    private readonly ConcurrentQueue<IncomingPacket> _incomingPacketQueue = new ConcurrentQueue<IncomingPacket>();

    // 延迟关闭的 P2P 会话：房主 At Shutdown 时先发 Disconnect（reliable），立刻关会话会把包丢掉，
    // 所以延迟一小段时间再 CloseP2PSessionWithUser。
    private struct PendingSessionClose
    {
      public ulong RemoteSteamId;
      public float ExecuteTime;
    }
    private readonly List<PendingSessionClose> _pendingSessionCloses = new List<PendingSessionClose>();

    // Events (main thread only)
    public event Action<ulong, byte[]> OnPacketReceived;
    public event Action<ulong> OnP2PSessionRequest;
    public event Action<ulong> OnP2PSessionFailed;

    private float _lastReadPacketsTime;
    private float _nextReadErrorLogTime;
    private float _nextDispatchErrorLogTime;
    private float _nextSendErrorLogTime;
    private float _nextSizeWarnTime;
    private int _readPacketsCallCount;
    private const int READ_PACKETS_LOG_INTERVAL = 120;

    public SteamP2PManager()
    {
      IsInitialised = false;
      LocalSteamID = 0;
      _lastReadPacketsTime = -READ_PACKETS_INTERVAL_SECONDS;
    }

    public void Initialise()
    {
      if (IsInitialised) return;
      try
      {
        Debug.Log("SteamP2PManager: Initializing Steam Reflection Helper...");
        SteamReflectionHelper.InitialiseSteamTypes();

        if (!SteamReflectionHelper.IsInitialised)
        {
          Debug.LogError("Failed to initialise Steam Reflection Helper.");
          return;
        }
        Debug.Log("SteamP2PManager: Steam Reflection Helper initialized.");
        _steamworksAssembly = SteamReflectionHelper.GetSteamworksAssembly();
        TryHookP2PSessionRequestCallback();
        LocalSteamID = 0;
        _steamIdRetrieved = false;
        _nextSteamIdAttemptTime = 0f;
        Debug.Log("Steam P2P Manager Initialised. Local SteamID will be retrieved when Steamworks is ready.");
        _lastReadPacketsTime = -READ_PACKETS_INTERVAL_SECONDS;
        IsInitialised = true;
      }
      catch (System.Exception ex)
      {
        Debug.LogError($"SteamP2PManager: Exception - {ex.GetType().Name}: {ex.Message}");
        Debug.LogError($"SteamP2PManager: Stack: {ex.StackTrace}");
        IsInitialised = false;
        throw;
      }
    }

    public void Shutdown()
    {
      _p2pSessionRequestCallback = null;
      _p2pSessionConnectFailCallback = null;
      while (_incomingPacketQueue.TryDequeue(out _)) { }
      IsInitialised = false;
      Debug.Log("SteamP2PManager shutdown, callbacks cleared");
    }

    public void Update()
    {
      if (!IsInitialised)
        return;

      if (!_steamIdRetrieved && Time.unscaledTime >= _nextSteamIdAttemptTime)
      {
        _nextSteamIdAttemptTime = Time.unscaledTime + 1f;
        TryRetrieveSteamId();
      }

      float currentTime = Time.unscaledTime;
      if (currentTime - _lastReadPacketsTime >= READ_PACKETS_INTERVAL_SECONDS)
      {
        _lastReadPacketsTime = currentTime;
        ReadPackets();
      }

      ProcessPendingSessionCloses();

      // Bounded dispatch: a malformed/flooding peer must not freeze the main thread.
      int dispatched = 0;
      while (dispatched < MAX_PACKETS_DISPATCHED_PER_FRAME && _incomingPacketQueue.TryDequeue(out IncomingPacket pkg))
      {
        dispatched++;
        try
        {
          OnPacketReceived?.Invoke(pkg.Sender, pkg.Data);
        }
        catch (Exception ex)
        {
          if (Time.unscaledTime >= _nextDispatchErrorLogTime)
          {
            _nextDispatchErrorLogTime = Time.unscaledTime + 5f;
            Debug.LogError("[SteamP2P] Packet handler threw: " + ex.GetType().Name + ": " + ex.Message);
          }
        }
      }
    }

    private void TryRetrieveSteamId()
    {
      try
      {
        ulong steamId = SteamReflectionHelper.GetLocalSteamId();
        if (steamId != 0)
        {
          LocalSteamID = steamId;
          _steamIdRetrieved = true;
          Debug.Log($"SteamP2PManager: Successfully retrieved Steam ID: {LocalSteamID}");
        }
      }
      catch (System.InvalidOperationException)
      {
      }
      catch (System.Exception ex)
      {
        Debug.LogWarning($"SteamP2PManager: Unexpected error retrieving Steam ID: {ex.GetType().Name}: {ex.Message}");
      }
    }

    private void TryHookP2PSessionRequestCallback()
    {
      try
      {
        if (_p2pSessionRequestCallback != null)
          return;

        if (_steamworksAssembly == null)
          _steamworksAssembly = SteamReflectionHelper.GetSteamworksAssembly();

        if (_steamworksAssembly == null)
        {
          Debug.LogWarning("SteamP2PManager: Steamworks assembly not available; skipping P2P session callback hookup.");
          return;
        }

        Type callbackType = ResolveP2PSessionRequestCallbackType();
        if (callbackType == null)
        {
          Debug.LogWarning("SteamP2PManager: Could not resolve P2PSessionRequest callback type.");
          return;
        }
        _p2pSessionRequestCallback = SteamCallbackRouter.CreateCallback(_steamworksAssembly, callbackType, HandleP2PSessionRequestCallback);
        if (_p2pSessionRequestCallback != null)
        {
          Debug.Log("SteamP2PManager: Registered P2PSessionRequest callback.");
        }
        else
        {
          Debug.LogWarning("SteamP2PManager: Failed to register P2PSessionRequest callback.");
        }

        if (SteamReflectionHelper.P2PSessionConnectFailType != null)
        {
          _p2pSessionConnectFailCallback = SteamCallbackRouter.CreateCallback(
              _steamworksAssembly,
              SteamReflectionHelper.P2PSessionConnectFailType,
              HandleP2PSessionConnectFail);
        }
      }
      catch (Exception ex)
      {
        Debug.LogWarning($"SteamP2PManager: Failed to hook P2PSessionRequest callback: {ex.Message}");
      }
    }

    private void HandleP2PSessionConnectFail(object callbackData)
    {
      ulong remoteSteamId = ExtractSteamIDFromCallback(callbackData);
      if (remoteSteamId != 0)
      {
        Debug.Log($"P2P session failed with {remoteSteamId}");
        OnP2PSessionFailed?.Invoke(remoteSteamId);
      }
    }

    private Type ResolveP2PSessionRequestCallbackType()
    {
      if (_steamworksAssembly == null) return null;
      string[] candidateTypeNames =
      {
                "Steamworks.P2PSessionRequest_t",
                "Steamworks.P2PSessionRequest"
            };
      foreach (string typeName in candidateTypeNames)
      {
        Type resolvedType = _steamworksAssembly.GetType(typeName, false);
        if (resolvedType != null)
        {
          return resolvedType;
        }
      }
      foreach (Type type in _steamworksAssembly.GetTypes())
      {
        if (type != null && type.Name.Contains("P2PSessionRequest"))
        {
          return type;
        }
      }
      return null;
    }

    private void HandleP2PSessionRequestCallback(object callbackData)
    {
      if (!IsInitialised) return; // stale callback after shutdown
      ulong remoteSteamId = ExtractSteamIDFromCallback(callbackData);
      Debug.Log($"[SteamP2P] Received P2PSessionRequest from {remoteSteamId}");
      if (remoteSteamId == 0)
      {
        Debug.LogWarning("[SteamP2P] P2PSessionRequest with invalid Steam ID");
        return;
      }
      bool accepted = TryAcceptP2PSession(remoteSteamId);
      Debug.Log($"[SteamP2P] AcceptP2PSessionWithUser result: {accepted}");
      OnP2PSessionRequest?.Invoke(remoteSteamId);
    }

    private bool TryAcceptP2PSession(ulong remoteSteamId)
    {
      if (remoteSteamId == 0) return false;
      try
      {
        if (SteamReflectionHelper.AcceptP2PSessionMethod == null)
        {
          Debug.LogError("[SteamP2P] AcceptP2PSessionMethod is null!");
          return false;
        }
        object steamIdObj = SteamReflectionHelper.CreateCSteamID(remoteSteamId);
        if (steamIdObj == null)
        {
          Debug.LogError($"[SteamP2P] Failed to create CSteamID for {remoteSteamId}");
          return false;
        }
        object result = SteamReflectionHelper.AcceptP2PSessionMethod.Invoke(null, new object[] { steamIdObj });
        bool accepted = result is bool b && b;
        Debug.Log($"[SteamP2P] AcceptP2PSessionWithUser returned {accepted}");
        return accepted;
      }
      catch (Exception e)
      {
        Debug.LogError($"[SteamP2P] Exception in TryAcceptP2PSession: {e.Message}");
        return false;
      }
    }

    // 主动关闭与某个远端玩家的 P2P 会话（退出联机/断线清理时调用）。
    public void CloseSession(ulong remoteSteamId)
    {
      if (!IsInitialised || remoteSteamId == 0) return;
      MethodInfo closeMethod = SteamReflectionHelper.CloseP2PSessionMethod;
      if (closeMethod == null) return;
      try
      {
        object steamIdObj = SteamReflectionHelper.CreateCSteamID(remoteSteamId);
        if (steamIdObj == null) return;
        closeMethod.Invoke(null, new object[] { steamIdObj });
        Logger.LogDebug("[SteamP2P] Closed P2P session with " + remoteSteamId);
      }
      catch (Exception e)
      {
        Debug.LogWarning("[SteamP2P] CloseP2PSessionWithUser(" + remoteSteamId + ") failed: " + e.Message);
      }
    }

    // 延迟关闭：先把 Disconnect 包发出去，等一小段时间再关会话，避免 reliable 包被一起丢弃。
    public void CloseSessionDeferred(ulong remoteSteamId, float delaySeconds = 0.25f)
    {
      if (!IsInitialised || remoteSteamId == 0) return;
      if (delaySeconds < 0f) delaySeconds = 0f;
      PendingSessionClose pending = new PendingSessionClose();
      pending.RemoteSteamId = remoteSteamId;
      pending.ExecuteTime = Time.unscaledTime + delaySeconds;
      _pendingSessionCloses.Add(pending);
    }

    private void ProcessPendingSessionCloses()
    {
      if (_pendingSessionCloses.Count == 0) return;
      float now = Time.unscaledTime;
      for (int i = _pendingSessionCloses.Count - 1; i >= 0; i--)
      {
        PendingSessionClose pending = _pendingSessionCloses[i];
        if (pending.ExecuteTime > now) continue;
        _pendingSessionCloses.RemoveAt(i);
        CloseSession(pending.RemoteSteamId);
      }
    }

    private ulong ExtractSteamIDFromCallback(object callbackData)
    {
      if (callbackData == null) return 0;
      Type callbackType = callbackData.GetType();
      string[] fieldNames = { "m_steamIDRemote", "m_SteamIDRemote", "m_ulSteamIDRemote", "m_SteamID", "m_ulSteamIDLobby" };
      foreach (string fieldName in fieldNames)
      {
        var field = callbackType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
          return ExtractSteamID(field.GetValue(callbackData));
        }
      }
      return 0;
    }

    private void ReadPackets()
    {
      _readPacketsCallCount++;
      if (_readPacketsCallCount % READ_PACKETS_LOG_INTERVAL == 0)
        Logger.LogDebug("[SteamP2P] ReadPackets heartbeat, callCount=" + _readPacketsCallCount);
      try
      {
        var isAvailableMethod = SteamReflectionHelper.IsP2PPacketAvailableMethod;
        var readMethod = SteamReflectionHelper.ReadP2PPacketMethod;
        if (isAvailableMethod == null || readMethod == null) return;

        if (_csteamIdType == null && _steamworksAssembly != null)
        {
          _csteamIdType = _steamworksAssembly.GetType("Steamworks.CSteamID");
        }
        if (_csteamIdType == null) return;

        int packetsRead = 0;
        while (packetsRead++ < MAX_PACKETS_PER_READ)
        {
          uint msgSize = 0;
          object[] availArgs = new object[] { msgSize, CHANNEL_INDEX };
          bool isAvailable = (bool)isAvailableMethod.Invoke(null, availArgs);
          if (!isAvailable) break;
          msgSize = (uint)availArgs[0];

          // Always consume the packet: leaving it in Steam's queue would block every later packet.
          byte[] data = new byte[msgSize];
          object senderCSteamID = Activator.CreateInstance(_csteamIdType);
          uint actualMsgSize = 0;
          object[] readArgs = new object[] { data, msgSize, actualMsgSize, senderCSteamID, CHANNEL_INDEX };
          bool readSuccess = (bool)readMethod.Invoke(null, readArgs);
          actualMsgSize = (uint)readArgs[2];
          senderCSteamID = readArgs[3];

          if (readSuccess && actualMsgSize > 0)
          {
            if (actualMsgSize > MAX_INCOMING_PACKET_BYTES)
            {
              if (Time.unscaledTime >= _nextSizeWarnTime)
              {
                _nextSizeWarnTime = Time.unscaledTime + 5f;
                Debug.LogWarning("[SteamP2P] Dropping oversized incoming packet: " + actualMsgSize + " bytes.");
              }
              continue;
            }
            ulong senderId = ExtractSteamID(senderCSteamID);
            if (actualMsgSize != data.Length)
            {
              byte[] trimmedData = new byte[actualMsgSize];
              Array.Copy(data, trimmedData, actualMsgSize);
              data = trimmedData;
            }
            Logger.LogDebug($"[SteamP2P] Received packet from {senderId}, size={actualMsgSize}");
            _incomingPacketQueue.Enqueue(new IncomingPacket(senderId, data));
          }
        }
      }
      catch (Exception ex)
      {
        if (Time.unscaledTime >= _nextReadErrorLogTime)
        {
          _nextReadErrorLogTime = Time.unscaledTime + 5f;
          Debug.LogWarning($"[SteamP2P] ReadPackets exception: {ex.Message}");
        }
      }
    }

    public void SendPacket(ulong targetSteamId, byte[] data, bool reliable = true)
    {
      if (!IsInitialised) return;
      if (targetSteamId == 0 || data == null || data.Length == 0) return;
      try
      {
        object steamIdObj = SteamReflectionHelper.CreateCSteamID(targetSteamId);
        bool success = SteamReflectionHelper.TryDifferentSendSignatures(steamIdObj, data, reliable);
        if (!success)
        {
          if (Time.unscaledTime >= _nextSendErrorLogTime)
          {
            _nextSendErrorLogTime = Time.unscaledTime + 5f;
            Logger.LogError($"[SteamP2P] Failed to send P2P packet to {targetSteamId} (size={data.Length}).");
          }
        }
        else
        {
          Logger.LogDebug($"[SteamP2P] Sent packet to {targetSteamId}, size={data.Length}, reliable={reliable}");
        }
      }
      catch (Exception e)
      {
        if (Time.unscaledTime >= _nextSendErrorLogTime)
        {
          _nextSendErrorLogTime = Time.unscaledTime + 5f;
          Logger.LogError($"Failed to send packet to {targetSteamId}: {e.Message}");
        }
      }
    }

    private ulong ExtractSteamID(object cSteamID)
    {
      if (cSteamID == null) return 0;
      if (cSteamID is ulong ul)
      {
        return ul;
      }
      if (cSteamID is uint ui)
      {
        return ui;
      }
      if (cSteamID is long l)
      {
        return (ulong)l;
      }
      if (cSteamID is string s && ulong.TryParse(s, out ulong parsed))
      {
        return parsed;
      }
      try
      {
        return Convert.ToUInt64(cSteamID);
      }
      catch
      {
      }
      try
      {
        string[] fieldNames = { "m_SteamID", "m_steamID", "SteamID", "steamID", "m_ulSteamID" };
        Type type = cSteamID.GetType();
        foreach (string fieldName in fieldNames)
        {
          var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
          if (field != null)
          {
            object fieldValue = field.GetValue(cSteamID);
            if (fieldValue != null)
            {
              return ExtractSteamID(fieldValue);
            }
          }
        }
      }
      catch
      {
      }
      return 0;
    }
  }
}
