using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using WarmSnowTogether.Networking.Packets;
using WarmSnowTogether.Networking.Steam;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking
{
  // [WST NetSync v1.1] local player sampler + remote avatar registry - owner: A
  //
  // The local player in this game is PlayerAnimControl.instance (there is no PlayerUnit /
  // PlayerController type). This manager only reads that object; it never creates a second player.
  // Remote bodies are RemotePlayerAvatar instances (visual clones) kept in a dictionary keyed by
  // SteamId. The owner (host/client) sends PlayerPositionPacket for the local player and feeds
  // received packets to HandlePlayerPosition(); NetworkManager routes join/leave packets to
  // SpawnRemotePlayer()/RemoveRemotePlayer().
  //
  // v1.1 fixes / hardening:
  //   * RemotePlayerCount: lets WorldSyncManager put 3rd/4th players at different spots
  //     instead of teleporting everyone to exactly host + 1.2 (>=3 players stacked);
  //   * packets with PlayerId == 0 are ignored instead of creating a shared "player 0" avatar;
  //   * a hard cap on the number of mirrored avatars so a malformed/forged position packet
  //     cannot create an unbounded number of GameObjects.
  public class PlayerManager : MonoBehaviour
  {
    private static PlayerManager _instance;

    public static PlayerManager Instance
    {
      get
      {
        if (_instance == null)
        {
          GameObject go = new GameObject("PlayerManager");
          _instance = go.AddComponent<PlayerManager>();
          DontDestroyOnLoad(go);
        }
        return _instance;
      }
    }

    // 4 player co-op + a little headroom; anything above this is treated as bad input.
    private const int MaxRemotePlayers = 8;
    // Offset step used when several clients spawn next to the same host.
    public const float RemoteSpawnOffsetStep = 0.8f;

    private readonly Dictionary<ulong, RemotePlayerAvatar> _remotePlayers = new Dictionary<ulong, RemotePlayerAvatar>();
    private bool _sceneHookInstalled;
    private bool _remoteCapWarned;

    // The 2D physics module is not referenced by the csproj (only UnityEngine.PhysicsModule is), so
    // the player rigidbody is reached through cached reflection instead of the Rigidbody2D type.
    private static FieldInfo _playerRigidbodyField;
    private static PropertyInfo _rbVelocityProperty;

    // The local player is always the PlayerAnimControl singleton.
    public PlayerAnimControl LocalPlayer
    {
      get { return PlayerAnimControl.instance; }
    }

    // Number of remote avatars currently mirrored. Used to spread co-op players on scene entry.
    public int RemotePlayerCount
    {
      get { return _remotePlayers.Count; }
    }

    private void Awake()
    {
      if (_instance == null) _instance = this;
      InstallSceneHook();
    }

    private void OnDestroy()
    {
      if (_sceneHookInstalled)
      {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        _sceneHookInstalled = false;
      }
    }

    private void InstallSceneHook()
    {
      if (_sceneHookInstalled) return;
      SceneManager.sceneLoaded += OnSceneLoaded;
      _sceneHookInstalled = true;
    }

    // Remote avatars live in the scene they were spawned in, so a scene change invalidates them.
    // The registry is cleared here; the next PlayerPositionPacket recreates the avatar from the
    // fresh local player skeletons.
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
      ClearAll();
      Logger.LogInfo("[WST][Players] scene loaded (" + scene.name + "), remote avatars cleared");
    }

    // ---------------------------------------------------------------------------------------
    // Local player -> packet
    // ---------------------------------------------------------------------------------------

    public PlayerPositionPacket CreateLocalPositionPacket(ulong localSteamId)
    {
      PlayerPositionPacket packet = new PlayerPositionPacket();
      packet.PlayerId = localSteamId;
      packet.Position = Vector2.zero;
      packet.Velocity = Vector2.zero;
      packet.Rotation = 0f;
      packet.IsGrounded = false;
      packet.IsDodgeRolling = false;
      packet.AnimationState = 0;
      packet.SpriteId = -1;
      packet.FlipX = false;
      packet.DirIndex = 1; // DIR.DOWN
      packet.AnimName = "";

      PlayerAnimControl player = PlayerAnimControl.instance;
      if (player == null)
      {
        // Still a valid packet (spawn-free zero state) so callers do not need a null check.
        return packet;
      }

      Vector3 position = player.transform.position;
      packet.Position = new Vector2(position.x, position.y);
      packet.Rotation = player.transform.eulerAngles.z;

      object rigidbody = GetLocalRigidbody(player);
      if (rigidbody != null)
      {
        try
        {
          if (_rbVelocityProperty == null) _rbVelocityProperty = rigidbody.GetType().GetProperty("velocity");
          if (_rbVelocityProperty != null)
          {
            object velocity = _rbVelocityProperty.GetValue(rigidbody, null);
            if (velocity is Vector2) packet.Velocity = (Vector2)velocity;
          }
        }
        catch (Exception)
        {
          packet.Velocity = Vector2.zero;
        }
      }

      try
      {
        packet.IsGrounded = player.IsGround;
      }
      catch (Exception)
      {
        packet.IsGrounded = false;
      }

      int dirIndex = (int)player.currentDir;
      if (dirIndex < 0 || dirIndex > 3) dirIndex = 1;
      packet.DirIndex = dirIndex;

      string animName = SkeletonAnimUtil.GetPlayerAnimationName(player.transform, dirIndex);

      // The player root transform is never flipped (PlayerAnimControl.cs has no
      // transform.localScale writes). Only two cases mirror a skeleton:
      //   RIGHT -> leftSkeleton.FaceRight() (ShowRightAnim)
      //   UP    -> upSkeleton.FaceRight() while attacking and moving left
      //            (ShowUpAnim: if (this.isAttack && this.dx < 0f)); dx is private, so the
      //            public lastDx is used as the closest visible equivalent.
      bool attackAnim = animName.IndexOf("attack", StringComparison.OrdinalIgnoreCase) >= 0;
      packet.FlipX = dirIndex == 3 || (dirIndex == 0 && attackAnim && player.lastDx < 0f);

      packet.AnimName = animName;
      packet.IsDodgeRolling = animName.IndexOf("dash", StringComparison.OrdinalIgnoreCase) >= 0;

      return packet;
    }

    // ---------------------------------------------------------------------------------------
    // Remote player lifecycle
    // ---------------------------------------------------------------------------------------

    public void HandlePlayerPosition(PlayerPositionPacket packet)
    {
      if (packet == null) return;
      // A zero id would make every unknown sender share the same avatar; ignore it.
      if (packet.PlayerId == 0UL) return;

      ulong localId = GetLocalSteamId();
      if (localId != 0UL && packet.PlayerId == localId) return; // never mirror ourselves

      RemotePlayerAvatar avatar;
      if (!_remotePlayers.TryGetValue(packet.PlayerId, out avatar) || avatar == null)
      {
        if (_remotePlayers.Count >= MaxRemotePlayers)
        {
          if (!_remoteCapWarned)
          {
            _remoteCapWarned = true;
            Logger.LogWarning("[WST][Players] ignoring extra remote player " + packet.PlayerId
              + ": avatar cap (" + MaxRemotePlayers + ") reached.");
          }
          return;
        }
        avatar = RemotePlayerAvatar.Create(packet.PlayerId);
        if (avatar == null) return;
        _remotePlayers[packet.PlayerId] = avatar;
        Logger.LogInfo("[WST][Players] auto-created avatar for " + packet.PlayerId + " from position packet");
      }
      avatar.Apply(packet);
    }

    public void SpawnRemotePlayer(ulong steamId, Vector2 position, float rotation)
    {
      if (steamId == 0UL) return;
      ulong localId = GetLocalSteamId();
      if (localId != 0UL && steamId == localId) return; // our own join echo

      RemotePlayerAvatar avatar;
      if (_remotePlayers.TryGetValue(steamId, out avatar) && avatar != null)
      {
        avatar.SnapTo(position, rotation);
        return;
      }

      if (_remotePlayers.Count >= MaxRemotePlayers)
      {
        Logger.LogWarning("[WST][Players] SpawnRemotePlayer ignored for " + steamId + ": avatar cap reached.");
        return;
      }

      avatar = RemotePlayerAvatar.Create(steamId);
      if (avatar == null) return;
      _remotePlayers[steamId] = avatar;
      avatar.SnapTo(position, rotation);
      Logger.LogInfo("[WST][Players] spawned remote player " + steamId + " at " + position.x + "," + position.y);
    }

    public void RemoveRemotePlayer(ulong steamId)
    {
      RemotePlayerAvatar avatar;
      if (!_remotePlayers.TryGetValue(steamId, out avatar)) return;
      _remotePlayers.Remove(steamId);
      if (avatar != null)
      {
        Logger.LogInfo("[WST][Players] removing remote player " + steamId);
        Destroy(avatar.gameObject);
      }
    }

    public void ClearAll()
    {
      _remoteCapWarned = false;
      if (_remotePlayers.Count == 0) return;
      foreach (KeyValuePair<ulong, RemotePlayerAvatar> pair in _remotePlayers)
      {
        if (pair.Value != null) Destroy(pair.Value.gameObject);
      }
      _remotePlayers.Clear();
      Logger.LogInfo("[WST][Players] all remote players removed");
    }

    // ---------------------------------------------------------------------------------------
    // Local player teleport (used after a scene transition to place the client next to the host)
    // ---------------------------------------------------------------------------------------

    public void TeleportLocalPlayer(Vector2 position)
    {
      PlayerAnimControl player = PlayerAnimControl.instance;
      if (player == null)
      {
        Logger.LogWarning("[WST][Players] TeleportLocalPlayer ignored: local player not available");
        return;
      }

      Vector3 current = player.transform.position;
      Vector3 target = new Vector3(position.x, position.y, current.z);
      player.transform.position = target;

      object rigidbody = GetLocalRigidbody(player);
      if (rigidbody != null)
      {
        try
        {
          Type rbType = rigidbody.GetType();
          PropertyInfo positionProperty = rbType.GetProperty("position");
          if (positionProperty != null) positionProperty.SetValue(rigidbody, new Vector2(position.x, position.y), null);
          if (_rbVelocityProperty == null) _rbVelocityProperty = rbType.GetProperty("velocity");
          if (_rbVelocityProperty != null) _rbVelocityProperty.SetValue(rigidbody, Vector2.zero, null);
        }
        catch (Exception)
        {
        }
      }
      Logger.LogInfo("[WST][Players] teleported local player to " + position.x + "," + position.y);
    }

    // ---------------------------------------------------------------------------------------

    private static object GetLocalRigidbody(PlayerAnimControl player)
    {
      if (player == null) return null;
      try
      {
        if (_playerRigidbodyField == null)
        {
          _playerRigidbodyField = typeof(PlayerAnimControl).GetField("player_rb", BindingFlags.Public | BindingFlags.Instance);
        }
        if (_playerRigidbodyField == null) return null;
        return _playerRigidbodyField.GetValue(player);
      }
      catch (Exception)
      {
        return null;
      }
    }

    private static ulong GetLocalSteamId()
    {
      try
      {
        return SteamP2PManager.Instance.LocalSteamID;
      }
      catch (Exception)
      {
        // Steamworks not initialised yet (or the type failed to load): treat as "unknown".
        return 0UL;
      }
    }
  }
}
