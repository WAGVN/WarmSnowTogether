using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using WarmSnowTogether.Systems.Logging;
using Debug = WarmSnowTogether.Systems.Logging.Debug;
namespace WarmSnowTogether.Networking.Steam
{
  // [WST NetSync v1.1] Steam lobby layer (create / join / leave / invite).
  //
  // v1.1 fixes:
  //   * LeaveLobby() now also shuts the network role down, so "退出联机" really stops hosting
  //     (v1 left HostController/ClientController running and the host kept broadcasting into a
  //     lobby nobody was in);
  //   * JoinLobby()/OnLobbyEnter dedupe: accepting an invite can fire both
  //     GameLobbyJoinRequested and GameRichPresenceJoinRequested, and a second ConnectTo used
  //     to tear down the just-established session;
  //   * the "RunCallbacks method is NULL" warning was printed every frame -> printed once;
  //   * Update() exception logging is throttled to once per 5s instead of one stack per frame;
  //   * CreateLobby() no longer silently returns when Steamworks failed to initialise
  //     (status overlay now shows "Steam unavailable" instead of a button that does nothing).
  public class SteamLobbyManager
  {
    private static readonly object _instanceLock = new object();
    private static SteamLobbyManager _instance;
    public static SteamLobbyManager Instance
    {
      get
      {
        lock (_instanceLock)
        {
          if (_instance == null)
          {
            try
            {
              _instance = new SteamLobbyManager();
            }
            catch (System.TypeLoadException tle)
            {
              Debug.LogError($"SteamLobbyManager: TypeLoadException: {tle.Message}");
              throw;
            }
            catch (System.Exception ex)
            {
              Debug.LogError($"SteamLobbyManager: {ex.GetType().Name}: {ex.Message}");
              throw;
            }
          }
          return _instance;
        }
      }
    }
    public bool IsInitialised { get; private set; }
    public bool IsInLobby { get; private set; }
    public ulong CurrentLobbyId { get; private set; }
    public int MaxMembers { get; private set; }
    private Assembly _steamworksAssembly;
    private Type _steamApiType;
    private MethodInfo _runCallbacksMethod;
    private object _lobbyCreatedCb;
    private object _lobbyEnterCb;
    private object _richPresenceJoinRequestedCb;
    private object _lobbyJoinRequestedCb;
    // [WST NetSync v1] Lobby creation diagnostics / feedback (owner: lead).
    // SteamLobbyManager is not a MonoBehaviour, so a small overlay polls StatusText.
    private object _lobbyCreatedCallResult;
    private bool _lobbyCreatePending;
    private float _lobbyCreatePendingSince;
    private int _lobbyCreateSubmitCount;
    private bool _runCallbacksWarningLogged;
    private float _nextUpdateErrorLogTime;
    private ulong _lastJoinLobbyId;
    private float _lastJoinLobbyTime;
    public string StatusText { get; private set; }
    public event Action OnPlayerListChanged;
    public SteamLobbyManager()
    {
      IsInitialised = false;
      IsInLobby = false;
      CurrentLobbyId = 0;
      MaxMembers = 4;
    }
    public void Initialise()
    {
      if (IsInitialised) return;
      SteamReflectionHelper.InitialiseSteamTypes();
      if (!SteamReflectionHelper.IsInitialised)
      {
        Debug.LogWarning("[SteamLobby] Steamworks not available (not running Steam build???) [launch though steam]");
        SetStatus("STEAM UNAVAILABLE: Steamworks not initialised (launch the game through Steam)");
        return;
      }
      _steamworksAssembly = SteamReflectionHelper.GetSteamworksAssembly();
      if (_steamworksAssembly == null)
      {
        Debug.LogWarning("[SteamLobby] Steamworks assembly not found...");
        SetStatus("STEAM UNAVAILABLE: Steamworks assembly not found");
        return;
      }
      _steamApiType = _steamworksAssembly.GetType("Steamworks.SteamAPI", false);
      _runCallbacksMethod = _steamApiType != null
          ? _steamApiType.GetMethod("RunCallbacks", BindingFlags.Public | BindingFlags.Static)
          : null;
      HookCallbacks();
      IsInitialised = true;
      Debug.Log("[SteamLobby] Initialised");
    }
    public void Shutdown()
    {
      LeaveLobby();
      SteamCallbackRouter.Clear();
      _lobbyCreatedCb = null;
      _lobbyEnterCb = null;
      _richPresenceJoinRequestedCb = null;
      _lobbyJoinRequestedCb = null;
      IsInitialised = false;
      Debug.Log("[SteamLobby] SteamLobbyManager shutdown, callbacks cleared");
    }
    public void Update()
    {
      if (!IsInitialised) return;
      try
      {
        if (_runCallbacksMethod == null)
        {
          if (!_runCallbacksWarningLogged)
          {
            _runCallbacksWarningLogged = true;
            Debug.LogWarning("[SteamLobby] RunCallbacks method is NULL; lobby callbacks will never fire.");
          }
          return;
        }
        _runCallbacksMethod.Invoke(null, null);
        // [WST NetSync v1] if no LobbyCreated_t ever arrives (Steam backend gone),
        // do not leave the UI stuck on "Creating lobby..." forever.
        if (_lobbyCreatePending && UnityEngine.Time.unscaledTime - _lobbyCreatePendingSince > 10f)
        {
          _lobbyCreatePending = false;
          Debug.LogError("[SteamLobby] Lobby creation timed out (no Steam callback in 10s) => Steam client is probably offline");
          SetStatus("CREATE LOBBY TIMEOUT (no Steam callback) => Steam offline?");
        }
      }
      catch (Exception e)
      {
        if (UnityEngine.Time.unscaledTime >= _nextUpdateErrorLogTime)
        {
          _nextUpdateErrorLogTime = UnityEngine.Time.unscaledTime + 5f;
          Debug.LogError("[SteamLobby] Error in Update/RunCallbacks: " + e.Message);
        }
      }
    }
    // Human readable description for Steamworks EResult values seen on lobby calls.
    private static string DescribeLobbyError(uint result)
    {
      switch (result)
      {
        case 0u: return "None (no result?)";
        case 1u: return "OK";
        case 2u: return "Fail";
        case 3u: return "NoConnection => Steam client is OFFLINE / not logged on";
        case 5u: return "InvalidPassword";
        case 6u: return "LoggedInElsewhere";
        case 15u: return "AccessDenied";
        case 16u: return "Timeout";
        case 20u: return "ServiceUnavailable";
        case 21u: return "NotLoggedOn => Steam client is not logged on";
        case 25u: return "LimitExceeded";
        case 84u: return "RateLimitExceeded (too many lobby requests)";
        default: return "EResult " + result;
      }
    }
    public void CreateLobby(int maxMembers = 4)
    {
      if (!IsInitialised)
      {
        Debug.LogWarning("[SteamLobby] CreateLobby ignored: Steamworks is not initialised.");
        SetStatus("CREATE LOBBY FAILED: Steamworks not initialised");
        return;
      }
      if (SteamReflectionHelper.CreateLobbyMethod == null)
      {
        Debug.LogWarning("[SteamLobby] CreateLobby not available");
        SetStatus("Create lobby unavailable (Steamworks method missing)");
        return;
      }
      // Ignore repeated clicks while a creation request is still in flight: Steam
      // rate-limits lobby creation and the old code queued 29 requests in seconds.
      if (_lobbyCreatePending && UnityEngine.Time.unscaledTime - _lobbyCreatePendingSince < 10f)
      {
        Debug.Log("[SteamLobby] CreateLobby ignored: previous request still pending");
        SetStatus(StatusText ?? "Creating lobby...");
        return;
      }
      if (IsInLobby && CurrentLobbyId != 0)
      {
        Debug.Log("[SteamLobby] CreateLobby ignored: already in a lobby (opening invite dialog instead)");
        OpenInviteDialog();
        return;
      }
      try
      {
        if (maxMembers < 2) maxMembers = 2;
        if (maxMembers > 16) maxMembers = 16;
        MaxMembers = maxMembers;
        bool loggedOn = TryGetSteamLoggedOn(out bool online) ? online : false;
        EnableP2PPacketRelay("creating lobby");
        object lobbyType = GetLobbyTypeEnum("k_ELobbyTypeFriendsOnly") ?? GetLobbyTypeEnum("k_ELobbyTypePublic");
        object apiCall = SteamReflectionHelper.CreateLobbyMethod.Invoke(null, new object[] { lobbyType, maxMembers });
        _lobbyCreateSubmitCount++;
        _lobbyCreatePending = true;
        _lobbyCreatePendingSince = UnityEngine.Time.unscaledTime;
        // Correlate the async result with the returned SteamAPICall_t (the raw
        // Callback<T> alone does not tell us which request the result belongs to).
        bool bound = SteamCallbackRouter.SetCallResult(_lobbyCreatedCallResult, apiCall);
        Debug.Log($"[SteamLobby] CreateLobby submitted #{_lobbyCreateSubmitCount} (apiCall={apiCall}, callResultBound={bound}, steamLoggedOn={loggedOn}, steamOnline={online})");
        SetStatus(loggedOn
            ? "Creating lobby... (#" + _lobbyCreateSubmitCount + ")"
            : "Creating lobby... WARNING: Steam client reports OFFLINE (not logged on)");
        Debug.Log("[SteamLobby] Creating lobby...");
      }
      catch (Exception e)
      {
        _lobbyCreatePending = false;
        Debug.LogError("[SteamLobby] Failed to create lobby: " + e.Message);
        SetStatus("Create lobby threw: " + e.Message);
      }
    }

    public void SetStatus(string text)
    {
      StatusText = text;
      Debug.Log("[SteamLobby][Status] " + text);
    }

    // SteamUser.BLoggedOn(): true when the Steam client is logged on to Steam.
    // When it is false every lobby call fails with k_EResultNoConnection.
    private bool TryGetSteamLoggedOn(out bool loggedOn)
    {
      loggedOn = false;
      try
      {
        if (_steamworksAssembly == null) return false;
        Type steamUserType = _steamworksAssembly.GetType("Steamworks.SteamUser", false);
        if (steamUserType == null) return false;
        MethodInfo m = steamUserType.GetMethod("BLoggedOn", BindingFlags.Public | BindingFlags.Static)
                    ?? steamUserType.GetMethod("BLoogedOn", BindingFlags.Public | BindingFlags.Static);
        if (m == null) return false;
        loggedOn = (bool)m.Invoke(null, null);
        return true;
      }
      catch (Exception e)
      {
        Debug.LogWarning("[SteamLobby] BLoggedOn failed: " + e.Message);
        return false;
      }
    }
    public void JoinLobby(ulong lobbyId)
    {
      if (!IsInitialised) return;
      if (lobbyId == 0) return;
      if (SteamReflectionHelper.JoinLobbyMethod == null)
      {
        Debug.LogWarning("[SteamLobby] JoinLobby not available");
        return;
      }
      // Accepting an invite can fire two callbacks; joining the same lobby twice used to
      // reset NetworkManager and drop the freshly established P2P session.
      if (IsInLobby && CurrentLobbyId == lobbyId)
      {
        Debug.Log("[SteamLobby] JoinLobby ignored: already in lobby " + lobbyId);
        return;
      }
      if (lobbyId == _lastJoinLobbyId && UnityEngine.Time.unscaledTime - _lastJoinLobbyTime < 3f)
      {
        Debug.Log("[SteamLobby] JoinLobby ignored: duplicate request for lobby " + lobbyId);
        return;
      }
      _lastJoinLobbyId = lobbyId;
      _lastJoinLobbyTime = UnityEngine.Time.unscaledTime;
      try
      {
        EnableP2PPacketRelay("joining lobby");
        object lobbySteamId = SteamReflectionHelper.CreateCSteamID(lobbyId);
        SteamReflectionHelper.JoinLobbyMethod.Invoke(null, new object[] { lobbySteamId });
        SetStatus("JOINING LOBBY: " + lobbyId + "...");
        Debug.Log("[SteamLobby] Joining lobby " + lobbyId + "...");
      }
      catch (Exception e)
      {
        Debug.LogError("[SteamLobby] Failed to join lobby: " + e.Message);
        SetStatus("JOIN LOBBY FAILED: " + e.Message);
      }
    }
    public void LeaveLobby()
    {
      // 先断开网络角色：房主会给客户端发 Disconnect 包并清理替身；客户端会断开 P2P。
      // v1 只调用了 Steam 的 LeaveLobby，HostController/ClientController 仍在运行。
      try
      {
        Networking.NetworkManager network = Networking.NetworkManager.Instance;
        if (network != null) network.Shutdown();
      }
      catch (Exception e)
      {
        Debug.LogWarning("[SteamLobby] Network shutdown while leaving lobby failed: " + e.Message);
      }

      if (!IsInitialised)
      {
        IsInLobby = false;
        CurrentLobbyId = 0;
        SetStatus("not in lobby");
        return;
      }
      if (!IsInLobby || CurrentLobbyId == 0)
      {
        IsInLobby = false;
        CurrentLobbyId = 0;
        SetStatus("not in lobby");
        return;
      }
      ulong lobbyId = CurrentLobbyId;
      try
      {
        if (SteamReflectionHelper.LeaveLobbyMethod != null)
        {
          object lobbySteamId = SteamReflectionHelper.CreateCSteamID(lobbyId);
          SteamReflectionHelper.LeaveLobbyMethod.Invoke(null, new object[] { lobbySteamId });
        }
      }
      catch (Exception e)
      {
        Debug.LogWarning("[SteamLobby] Failed to leave lobby cleanly: " + e.Message);
      }
      IsInLobby = false;
      CurrentLobbyId = 0;
      _lobbyCreatePending = false;
      SetStatus("LEFT LOBBY");
      Debug.Log("[SteamLobby] Left lobby.");
    }
    public void OpenInviteDialog()
    {
      if (!IsInitialised) return;
      if (!IsInLobby || CurrentLobbyId == 0)
      {
        Debug.LogWarning("[SteamLobby] Not in a lobby to invite from");
        return;
      }
      try
      {
        if (_steamworksAssembly == null) return;
        Type steamFriendsType = _steamworksAssembly.GetType("Steamworks.SteamFriends", false);
        if (steamFriendsType == null) return;
        MethodInfo method = steamFriendsType.GetMethod("ActivateGameOverlayInviteDialog", BindingFlags.Public | BindingFlags.Static);
        if (method == null)
        {
          Debug.LogWarning("[SteamLobby] Steam overlay invite dialog method not found");
          return;
        }
        object lobbySteamId = SteamReflectionHelper.CreateCSteamID(CurrentLobbyId);
        method.Invoke(null, new object[] { lobbySteamId });
      }
      catch (Exception e)
      {
        Debug.LogWarning("[SteamLobby] Failed to open invite dialog: " + e.Message);
      }
    }
    private void HookCallbacks()
    {
      try
      {
        Type lobbyCreatedType = SteamReflectionHelper.LobbyCreatedCallbackType;
        Type lobbyEnterType = SteamReflectionHelper.LobbyEnterCallbackType;
        Type richPresenceJoinRequestedType = SteamReflectionHelper.GameJoinRequestedCallbackType;
        if (_steamworksAssembly == null)
        {
          Debug.LogWarning("[SteamLobby] Steamworks assembly is null, cannot hook callbacks");
          return;
        }
        Debug.Log("[SteamLobby] Attempting to hook callbacks...");
        if (lobbyCreatedType != null)
        {
          Debug.Log("[SteamLobby] Creating LobbyCreated callback...");
          _lobbyCreatedCb = SteamCallbackRouter.CreateCallback(_steamworksAssembly, lobbyCreatedType, OnLobbyCreated);
          Debug.Log("[SteamLobby] LobbyCreated callback created: " + (_lobbyCreatedCb != null ? "SUCCESS" : "FAILED"));
          // [WST NetSync v1] Additionally bind a CallResult so the SteamAPICall_t
          // returned by CreateLobby is actually correlated with its result.
          _lobbyCreatedCallResult = SteamCallbackRouter.CreateCallResult(_steamworksAssembly, lobbyCreatedType, OnLobbyCreated);
          Debug.Log("[SteamLobby] LobbyCreated CallResult created: " + (_lobbyCreatedCallResult != null ? "SUCCESS" : "FAILED"));
        }
        else
        {
          Debug.LogWarning("[SteamLobby] LobbyCreatedCallbackType is null");
        }
        if (lobbyEnterType != null)
        {
          Debug.Log("[SteamLobby] Creating LobbyEnter callback...");
          _lobbyEnterCb = SteamCallbackRouter.CreateCallback(_steamworksAssembly, lobbyEnterType, OnLobbyEnter);
          Debug.Log("[SteamLobby] LobbyEnter callback created: " + (_lobbyEnterCb != null ? "SUCCESS" : "FAILED"));
        }
        else
        {
          Debug.LogWarning("[SteamLobby] LobbyEnterCallbackType is null");
        }
        if (richPresenceJoinRequestedType != null)
        {
          Debug.Log("[SteamLobby] Creating RichPresenceJoinRequested callback...");
          _richPresenceJoinRequestedCb = SteamCallbackRouter.CreateCallback(_steamworksAssembly, richPresenceJoinRequestedType, OnRichPresenceJoinRequested);
          Debug.Log("[SteamLobby] RichPresenceJoinRequested callback created: " + (_richPresenceJoinRequestedCb != null ? "SUCCESS" : "FAILED"));
        }
        else
        {
          Debug.LogWarning("[SteamLobby] GameJoinRequestedCallbackType is null");
        }
        if (_lobbyCreatedCb == null && _lobbyEnterCb == null && _richPresenceJoinRequestedCb == null)
        {
          Debug.LogError("[SteamLobby] Could not hook ANY callbacks (Steamworks callback API mismatch?)");
        }
        else
        {
          Debug.Log("[SteamLobby] Successfully hooked callbacks");
        }
        Type lobbyJoinRequestedType = SteamReflectionHelper.GameLobbyJoinRequestedCallbackType;
        if (lobbyJoinRequestedType != null)
        {
          Debug.Log("[SteamLobby] Creating GameLobbyJoinRequested callback...");
          _lobbyJoinRequestedCb = SteamCallbackRouter.CreateCallback(_steamworksAssembly, lobbyJoinRequestedType, OnGameLobbyJoinRequested);
          Debug.Log("[SteamLobby] GameLobbyJoinRequested callback created: " + (_lobbyJoinRequestedCb != null ? "SUCCESS" : "FAILED"));
        }
        else
        {
          Debug.LogWarning("[SteamLobby] GameLobbyJoinRequestedCallbackType is null");
        }
      }
      catch (Exception e)
      {
        Debug.LogError("[SteamLobby] Failed to hook callbacks: " + e.Message);
        Debug.LogError("[SteamLobby] Stack trace: " + e.StackTrace);
      }
    }
    private void OnLobbyCreated(object callbackData)
    {
      try
      {
        if (callbackData == null) return;
        var type = callbackData.GetType();
        var sb = new StringBuilder();
        sb.AppendLine($"[SteamLobby] ========== LOBBY CREATED ==========");
        sb.AppendLine($"Type: {type.FullName}");
        sb.AppendLine("Fields:");
        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
          var val = f.GetValue(callbackData);
          sb.AppendLine($"  {f.Name} ({f.FieldType.Name}) = {val}");
        }
        Debug.Log(sb.ToString());
        ulong lobbyId = ReadUlongField(callbackData, "m_ulSteamIDLobby", "m_SteamIDLobby", "m_steamIDLobby");
        uint result = ReadUIntField(callbackData, "m_eResult", "m_EResult");
        // [WST NetSync v1] a result arrived: the request is no longer pending.
        _lobbyCreatePending = false;
        if (lobbyId == 0)
        {
          string reason = DescribeLobbyError(result);
          Debug.LogWarning("[SteamLobby] LobbyCreated callback but lobby id was 0 (result=" + result + " => " + reason + ")");
          Debug.LogError("[SteamLobby] Lobby creation failed: " + reason + " | Steam online=" + (TryGetSteamLoggedOn(out bool on) ? on.ToString() : "unknown"));
          SetStatus("CREATE LOBBY FAILED: " + reason + " [Steam online: " + (TryGetSteamLoggedOn(out bool on2) ? on2.ToString() : "?") + "]");
          return;
        }
        // Dedupe: the same LobbyCreated_t may be dispatched through both the raw
        // Callback<T> and the CallResult<T>; only handle the first one.
        if (IsInLobby && CurrentLobbyId == lobbyId)
        {
          Debug.Log("[SteamLobby] Duplicate LobbyCreated for lobby " + lobbyId + ", ignored");
          return;
        }
        Debug.Log("[SteamLobby] Lobby created: " + lobbyId + " result=" + result);
        SetStatus("LOBBY CREATED: " + lobbyId + " (invite dialog opening...)");
        CurrentLobbyId = lobbyId;
        IsInLobby = true;
        EnableP2PPacketRelay("lobby created");
        try
        {
          if (SteamReflectionHelper.SetLobbyDataMethod != null)
          {
            object lobbySteamId = SteamReflectionHelper.CreateCSteamID(lobbyId);
            SteamReflectionHelper.SetLobbyDataMethod.Invoke(null, new object[] { lobbySteamId, "gt_host", SteamReflectionHelper.GetLocalSteamId().ToString() });
            SteamReflectionHelper.SetLobbyDataMethod.Invoke(null, new object[] { lobbySteamId, "gt_proto", Networking.NetworkManager.ProtocolVersion.ToString() });
            Debug.Log("[SteamLobby] Lobby data set");
          }
          if (SteamReflectionHelper.SetLobbyJoinableMethod != null)
          {
            object lobbySteamId = SteamReflectionHelper.CreateCSteamID(lobbyId);
            SteamReflectionHelper.SetLobbyJoinableMethod.Invoke(null, new object[] { lobbySteamId, true });
            Debug.Log("[SteamLobby] Lobby joinable set");
          }
          if (SteamReflectionHelper.SetRichPresenceMethod != null)
          {
            SteamReflectionHelper.SetRichPresenceMethod.Invoke(null, new object[] { "connect", lobbyId.ToString() });
            Debug.Log("[SteamLobby] Rich presence set to connect=" + lobbyId.ToString());
          }
          else
          {
            Debug.LogWarning("[SteamLobby] SetRichPresenceMethod is NULL!");
          }
        }
        catch (Exception richPresenceEx)
        {
          Debug.LogError("[SteamLobby] Error setting rich presence: " + richPresenceEx.Message);
        }
        Networking.NetworkManager.Instance.StartHosting();
        OpenInviteDialog();
      }
      catch (Exception e)
      {
        Debug.LogWarning("[SteamLobby] OnLobbyCreated error: " + e.Message);
      }
    }
    private void OnGameLobbyJoinRequested(object callbackData)
    {
      try
      {
        var type = callbackData.GetType();
        var sb = new StringBuilder();
        sb.AppendLine($"[SteamLobby] ========== GAME LOBBY JOIN REQUESTED ==========");
        sb.AppendLine($"Type: {type.FullName}");
        sb.AppendLine("Fields:");
        foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
          var val = f.GetValue(callbackData);
          sb.AppendLine($"  {f.Name} ({f.FieldType.Name}) = {val}");
        }
        Debug.Log(sb.ToString());
        ulong lobbyId = ReadUlongField(callbackData, "m_steamIDLobby", "m_SteamIDLobby", "m_ulSteamIDLobby");
        ulong friendId = ReadUlongField(callbackData, "m_steamIDFriend", "m_SteamIDFriend", "m_ulSteamIDFriend");
        Debug.Log($"[SteamLobby] Extracted lobby: {lobbyId}, friend: {friendId}");
        if (lobbyId != 0)
          JoinLobby(lobbyId);
        else
          Debug.LogWarning("[SteamLobby] GameLobbyJoinRequested had null lobby ID");
      }
      catch (Exception e)
      {
        Debug.LogError($"[SteamLobby] OnGameLobbyJoinRequested error: {e.Message}");
        Debug.LogError($"[SteamLobby] Stack trace: {e.StackTrace}");
      }
    }
    private void OnLobbyEnter(object callbackData)
    {
      try
      {
        ulong lobbyId = ReadUlongField(callbackData, "m_ulSteamIDLobby", "m_SteamIDLobby");
        if (lobbyId == 0) return;
        bool alreadyInThisLobby = IsInLobby && CurrentLobbyId == lobbyId;
        CurrentLobbyId = lobbyId;
        IsInLobby = true;
        EnableP2PPacketRelay("entered lobby");
        ulong ownerId = 0;
        try
        {
          if (SteamReflectionHelper.GetLobbyOwnerMethod != null)
          {
            object lobbySteamId = SteamReflectionHelper.CreateCSteamID(lobbyId);
            object ownerCSteamId = SteamReflectionHelper.GetLobbyOwnerMethod.Invoke(null, new object[] { lobbySteamId });
            ownerId = ExtractSteamId(ownerCSteamId);
          }
        }
        catch { }
        ulong local = SteamReflectionHelper.GetLocalSteamId();
        Debug.Log("[SteamLobby] Entered lobby: " + lobbyId + " owner=" + ownerId + " local=" + local);
        SetStatus("JOINED LOBBY: " + lobbyId + (ownerId == local ? " (I am host)" : " (connecting to host " + ownerId + ")"));
        if (ownerId != 0 && ownerId != local)
        {
          Networking.NetworkManager network = Networking.NetworkManager.Instance;
          // Duplicate LobbyEnter (or a re-entry after the session is already connected):
          // never tear down a live session by calling ConnectTo again.
          if (network != null && network.IsClient && network.Client != null
              && (network.Client.IsConnected || network.Client.IsConnecting))
          {
            Debug.Log("[SteamLobby] Duplicate LobbyEnter ignored: client already connected/connecting.");
            return;
          }
          if (network != null) network.ConnectTo(ownerId);
        }
        else if (alreadyInThisLobby)
        {
          Debug.Log("[SteamLobby] Duplicate LobbyEnter for lobby " + lobbyId + " ignored.");
        }
      }
      catch (Exception e)
      {
        Debug.LogWarning("[SteamLobby] OnLobbyEnter error: " + e.Message);
      }
    }
    private void OnRichPresenceJoinRequested(object callbackData)
    {
      Debug.Log("[SteamLobby] ========== OnRichPresenceJoinRequested CALLBACK FIRED ==========");
      try
      {
        Debug.Log("[SteamLobby] Callback data type: " + (callbackData != null ? callbackData.GetType().Name : "NULL"));
        string connect = ReadStringField(callbackData, "m_rgchConnect", "m_connect", "m_rgchConnectString");
        Debug.Log("[SteamLobby] Connect string: " + (string.IsNullOrEmpty(connect) ? "(empty or null)" : connect));
        if (string.IsNullOrEmpty(connect))
        {
          Debug.Log("[SteamLobby] Join requested (empty connect string). Accepting invite might not include lobby id");
          return;
        }
        ulong lobbyId;
        if (TryExtractFirstUlong(connect, out lobbyId) && lobbyId != 0)
        {
          Debug.Log("[SteamLobby] Join requested via rich presence. Extracted lobby id: " + lobbyId);
          JoinLobby(lobbyId);
        }
        else
        {
          Debug.Log("[SteamLobby] Join requested but couldn't parse lobby id from connect string: " + connect);
        }
      }
      catch (Exception e)
      {
        Debug.LogError("[SteamLobby] OnRichPresenceJoinRequested error: " + e.Message);
        Debug.LogError("[SteamLobby] Stack trace: " + e.StackTrace);
      }
      Debug.Log("[SteamLobby] ========== OnRichPresenceJoinRequested END ==========");
    }
    private object GetLobbyTypeEnum(string name)
    {
      try
      {
        if (_steamworksAssembly == null) return null;
        Type t = _steamworksAssembly.GetType("Steamworks.ELobbyType", false);
        if (t == null) return null;
        return Enum.Parse(t, name);
      }
      catch
      {
        return null;
      }
    }
    private void EnableP2PPacketRelay(string reason)
    {
      try
      {
        Debug.Log("[SteamLobby] Enabling P2P packet relay while " + reason + "...");
        SteamReflectionHelper.AllowP2PPacketRelay(true);
      }
      catch (Exception e)
      {
        Debug.LogWarning("[SteamLobby] Failed to enable P2P packet relay while " + reason + ": " + e.Message);
      }
    }
    private static ulong ExtractSteamId(object cSteamId)
    {
      if (cSteamId == null) return 0;
      try
      {
        FieldInfo f = cSteamId.GetType().GetField("m_SteamID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f == null) return 0;
        object val = f.GetValue(cSteamId);
        return val != null ? Convert.ToUInt64(val) : 0;
      }
      catch
      {
        return 0;
      }
    }
    private static ulong ReadUlongField(object obj, params string[] fieldNames)
    {
      if (obj == null) return 0;
      Type t = obj.GetType();
      for (int i = 0; i < fieldNames.Length; i++)
      {
        FieldInfo f = t.GetField(fieldNames[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f == null) continue;
        object v = f.GetValue(obj);
        if (v == null) continue;
        if (v.GetType().Name == "CSteamID")
        {
          var idField = v.GetType().GetField("m_SteamID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
          if (idField != null)
          {
            var idVal = idField.GetValue(v);
            if (idVal != null)
              return Convert.ToUInt64(idVal);
          }
        }
        try { return Convert.ToUInt64(v); } catch { }
      }
      return 0;
    }
    private static uint ReadUIntField(object obj, params string[] fieldNames)
    {
      if (obj == null) return 0;
      Type t = obj.GetType();
      for (int i = 0; i < fieldNames.Length; i++)
      {
        FieldInfo f = t.GetField(fieldNames[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f == null) continue;
        object v = f.GetValue(obj);
        if (v == null) continue;
        try { return Convert.ToUInt32(v); } catch { }
      }
      return 0;
    }
    private static string ReadStringField(object obj, params string[] fieldNames)
    {
      if (obj == null) return null;
      Type t = obj.GetType();
      for (int i = 0; i < fieldNames.Length; i++)
      {
        FieldInfo f = t.GetField(fieldNames[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f == null) continue;
        object v = f.GetValue(obj);
        if (v == null) continue;
        return v.ToString();
      }
      return null;
    }
    private static bool TryExtractFirstUlong(string s, out ulong value)
    {
      value = 0;
      if (string.IsNullOrEmpty(s)) return false;
      int start = -1;
      for (int i = 0; i < s.Length; i++)
      {
        if (char.IsDigit(s[i])) { start = i; break; }
      }
      if (start < 0) return false;
      int end = start;
      while (end < s.Length && char.IsDigit(s[end])) end++;
      string digits = s.Substring(start, end - start);
      return ulong.TryParse(digits, out value);
    }
    public List<ulong> GetLobbyMembers()
    {
      var members = new List<ulong>();
      if (!IsInLobby || CurrentLobbyId == 0) return members;
      try
      {
        var lobbyIdObj = SteamReflectionHelper.CreateCSteamID(CurrentLobbyId);
        int count = (int)(SteamReflectionHelper.GetLobbyMemberCountMethod?.Invoke(null, new object[] { lobbyIdObj }) ?? 0);
        for (int i = 0; i < count; i++)
        {
          object member = SteamReflectionHelper.GetLobbyMemberByIndexMethod?.Invoke(null, new object[] { lobbyIdObj, i });
          if (member != null)
          {
            ulong id = ExtractSteamId(member);
            if (id != 0) members.Add(id);
          }
        }
      }
      catch (Exception e)
      {
        Debug.LogWarning($"[SteamLobby] GetLobbyMembers error: {e.Message}");
      }
      return members;
    }
    private void UpdatePlayerList()
    {
      OnPlayerListChanged?.Invoke();
    }
  }
}
