using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using WarmSnowTogether.Networking.Packets;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking
{
  // [WST NetSync v1] remote player avatar (visual clone of the local player) - owner: A
  //
  // The game builds its own "ghost" visuals by cloning the three skeleton child objects that live
  // under the player root and driving them with AnimControl (PlayerGhostControl.cs:18-22,
  // PlayerAnimControl.cs:204-206). The remote avatar mirrors that approach exactly:
  //   - clone "upSkeleton" / "downSkeleton" / "leftSkeleton" from the local PlayerAnimControl
  //   - strip every Collider2D / Rigidbody2D and every MonoBehaviour except AnimControl and the
  //     spine SkeletonAnimation component. spine-unity.dll is intentionally not referenced by the
  //     csproj, so spine members are only touched through reflection.
  //   - never Instantiate the player root: PlayerAnimControl.Awake destroys duplicate players.
  //
  // Direction convention copied from PlayerAnimControl.ShowUpAnim/ShowDownAnim/ShowLeftAnim/
  // ShowRightAnim (PlayerAnimControl.cs:8328-8540). Exactly one skeleton is visible at a time:
  //   UP    -> upSkeleton,   flip right when FlipX (mirrors the up-attack flip)
  //   DOWN  -> downSkeleton, never flipped
  //   LEFT  -> leftSkeleton, FaceLeft
  //   RIGHT -> leftSkeleton, FaceRight
  //
  // The avatar is purely cosmetic: no colliders, no rigidbody, layer 0, no game logic, no HP.
  public class RemotePlayerAvatar : MonoBehaviour
  {
    // Position smoothing speed used by the contract (about 18 units per second of catch-up).
    private const float PositionLerp = 18f;
    // Any larger positional error (spawn / teleport) is snapped instead of smoothed.
    private const float SnapDistanceSqr = 36f;

    public ulong SteamId;

    private AnimControl _upScript;
    private AnimControl _downScript;
    private AnimControl _sideScript;
    private Component _upSkeletonAnimation;
    private Component _downSkeletonAnimation;
    private Component _sideSkeletonAnimation;

    private readonly HashSet<string> _failedAnimNames = new HashSet<string>();

    private bool _built;
    private bool _buildFailureLogged;
    private bool _missingPlayerLogged;
    private bool _updateErrorLogged;

    private bool _hasTarget;
    private Vector3 _targetPosition;
    private float _targetRotation;

    // DIR.UP = 0, DIR.DOWN = 1, DIR.LEFT = 2, DIR.RIGHT = 3 (see DIR.cs).
    private int _dirIndex = 1;
    private int _appliedDir = -1;
    private bool _flipX;
    private bool _appliedFlipX;
    private string _animName = "";
    private string _appliedAnimName = "";

    private float _nextSkinCheck;
    private bool _skinApplied;
    private PlayerSkin _appliedSkin = PlayerSkin.Default;
    private int _appliedSkinFlag;

    // ---------------------------------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------------------------------

    public static RemotePlayerAvatar Create(ulong steamId)
    {
      GameObject go = new GameObject("RemotePlayer_" + steamId);
      go.layer = 0;
      RemotePlayerAvatar avatar = go.AddComponent<RemotePlayerAvatar>();
      avatar.SteamId = steamId;
      PlayerAnimControl player = PlayerAnimControl.instance;
      if (player != null)
      {
        // Build right away when possible so the avatar is visible on the spawn frame.
        avatar.TryBuildVisual(player);
      }
      Logger.LogInfo("[WST][Remote] created avatar for steamId " + steamId);
      return avatar;
    }

    // Place the avatar immediately (used for the initial PlayerJoin spawn packet).
    public void SnapTo(Vector2 position, float rotation)
    {
      _targetPosition = new Vector3(position.x, position.y, 0f);
      _targetRotation = rotation;
      _hasTarget = true;
      transform.position = _targetPosition;
      transform.rotation = Quaternion.Euler(0f, 0f, rotation);
    }

    // Consume a position packet. Everything here is stored only; Update() applies it smoothly so
    // packet arrival jitter does not translate into visual jitter.
    public void Apply(PlayerPositionPacket packet)
    {
      if (packet == null) return;

      _targetPosition = new Vector3(packet.Position.x, packet.Position.y, 0f);
      _targetRotation = packet.Rotation;
      _dirIndex = (packet.DirIndex >= 0 && packet.DirIndex <= 3) ? packet.DirIndex : 1;
      _flipX = packet.FlipX;
      _animName = packet.AnimName == null ? "" : packet.AnimName;

      if (!_hasTarget)
      {
        // First packet after creation: never slide in from the origin.
        _hasTarget = true;
        transform.position = _targetPosition;
      }
      else if ((transform.position - _targetPosition).sqrMagnitude > SnapDistanceSqr)
      {
        // Teleport or scene transition: close the gap instantly.
        transform.position = _targetPosition;
      }

      if (Mathf.Abs(Mathf.DeltaAngle(transform.eulerAngles.z, _targetRotation)) > 0.01f)
      {
        transform.rotation = Quaternion.Euler(0f, 0f, _targetRotation);
      }
    }

    // ---------------------------------------------------------------------------------------
    // Frame update: smooth movement + direction/animation state machine
    // ---------------------------------------------------------------------------------------

    private void Update()
    {
      try
      {
        PlayerAnimControl player = PlayerAnimControl.instance;
        if (player == null)
        {
          // Menu / loading screen: keep the avatar alive but frozen, never throw.
          if (!_missingPlayerLogged)
          {
            Logger.LogInfo("[WST][Remote] local player not available, avatar " + SteamId + " frozen");
            _missingPlayerLogged = true;
          }
          return;
        }
        _missingPlayerLogged = false;

        if (!_built && !TryBuildVisual(player)) return;
        if (!_hasTarget) return;

        transform.position = Vector3.Lerp(transform.position, _targetPosition, Time.deltaTime * PositionLerp);
        ApplyDirection();
        ApplyAnimation(false);
        if (Time.time >= _nextSkinCheck)
        {
          _nextSkinCheck = Time.time + 2f;
          ApplySkinIfChanged(player, false);
        }
      }
      catch (Exception ex)
      {
        if (!_updateErrorLogged)
        {
          Logger.LogError("[WST][Remote] avatar update failed for " + SteamId + ": " + ex.GetType().Name + ": " + ex.Message);
          _updateErrorLogged = true;
        }
      }
    }

    private void ApplyDirection()
    {
      if (_dirIndex == _appliedDir && _flipX == _appliedFlipX) return;
      _appliedDir = _dirIndex;
      _appliedFlipX = _flipX;

      bool showUp = _dirIndex == 0;
      bool showDown = _dirIndex == 1;
      bool showSide = _dirIndex == 2 || _dirIndex == 3;

      SetScriptVisible(_upScript, showUp);
      SetScriptVisible(_downScript, showDown);
      SetScriptVisible(_sideScript, showSide);

      // PlayerAnimControl.ShowUpAnim:8328 flips the up skeleton when attacking to the left.
      if (showUp) FlipScript(_upScript, _flipX);
      // PlayerAnimControl.ShowLeftAnim:8423 / ShowRightAnim:8473.
      if (showSide) FlipScript(_sideScript, _dirIndex == 3);

      // Force the newly visible skeleton to receive the current animation again.
      _appliedAnimName = "";
    }

    private void ApplyAnimation(bool force)
    {
      if (!force && _animName == _appliedAnimName) return;

      if (string.IsNullOrEmpty(_animName))
      {
        _appliedAnimName = "";
        return;
      }

      // While the visual is not built yet the animation must be retried, so this does not mark it
      // as applied.
      Component skeleton = ActiveSkeletonAnimation();
      if (skeleton == null) return;

      if (_failedAnimNames.Contains(_animName))
      {
        // Known-bad animation name (skeleton asset lacks it): do not retry every frame.
        _appliedAnimName = _animName;
        return;
      }

      bool loop = SkeletonAnimUtil.IsLoopAnimation(_animName);
      if (SkeletonAnimUtil.Play(skeleton, _animName, loop, _failedAnimNames))
      {
        _appliedAnimName = _animName;
      }
      else if (_failedAnimNames.Contains(_animName))
      {
        _appliedAnimName = _animName;
      }
    }

    private Component ActiveSkeletonAnimation()
    {
      if (_dirIndex == 0) return _upSkeletonAnimation;
      if (_dirIndex == 1) return _downSkeletonAnimation;
      return _sideSkeletonAnimation;
    }

    private static void SetScriptVisible(AnimControl script, bool visible)
    {
      if (script == null) return;
      try
      {
        if (visible) script.VisibleAnim();
        else script.InvisibleAnim();
      }
      catch (Exception)
      {
        // Visual only: never let a broken renderer break the avatar.
      }
    }

    private static void FlipScript(AnimControl script, bool faceRight)
    {
      if (script == null) return;
      try
      {
        if (faceRight) script.FaceRight();
        else script.FaceLeft();
      }
      catch (Exception)
      {
      }
    }

    // ---------------------------------------------------------------------------------------
    // Visual construction (clone of the local player's skeleton children)
    // ---------------------------------------------------------------------------------------

    private bool TryBuildVisual(PlayerAnimControl player)
    {
      Transform sourceRoot = player.transform;
      Transform upSource = sourceRoot.Find("upSkeleton");
      Transform downSource = sourceRoot.Find("downSkeleton");
      Transform sideSource = sourceRoot.Find("leftSkeleton");

      if (upSource == null && downSource == null && sideSource == null)
      {
        if (!_buildFailureLogged)
        {
          Logger.LogError("[WST][Remote] local player has none of upSkeleton/downSkeleton/leftSkeleton, avatar " + SteamId + " has no visual");
          _buildFailureLogged = true;
        }
        return false;
      }

      _upScript = CloneSkeleton(upSource, "remote_upSkeleton", out _upSkeletonAnimation);
      _downScript = CloneSkeleton(downSource, "remote_downSkeleton", out _downSkeletonAnimation);
      _sideScript = CloneSkeleton(sideSource, "remote_leftSkeleton", out _sideSkeletonAnimation);

      if (_upScript == null && _downScript == null && _sideScript == null)
      {
        if (!_buildFailureLogged)
        {
          Logger.LogError("[WST][Remote] cloning the player skeletons failed, avatar " + SteamId + " has no visual");
          _buildFailureLogged = true;
        }
        return false;
      }

      // Keep the world scale of the source hierarchy so the clone is not stretched/shrunk.
      try
      {
        transform.localScale = sourceRoot.lossyScale;
      }
      catch (Exception)
      {
      }

      _built = true;
      _appliedDir = -1;
      _appliedFlipX = !_flipX;
      _appliedAnimName = "";
      ApplyDirection();
      ApplyAnimation(true);
      _nextSkinCheck = 0f;
      Logger.LogInfo("[WST][Remote] avatar visual built for " + SteamId);
      return true;
    }

    private AnimControl CloneSkeleton(Transform source, string cloneName, out Component skeletonAnimation)
    {
      skeletonAnimation = null;
      if (source == null) return null;

      GameObject clone;
      try
      {
        clone = Instantiate(source.gameObject, transform);
      }
      catch (Exception ex)
      {
        Logger.LogError("[WST][Remote] failed to clone skeleton " + source.name + ": " + ex.GetType().Name + ": " + ex.Message);
        return null;
      }
      if (clone == null) return null;

      clone.name = cloneName;
      Transform cloneTransform = clone.transform;
      // Instantiate(parent) keeps world position; force the exact local placement of the source.
      cloneTransform.localPosition = source.localPosition;
      cloneTransform.localRotation = source.localRotation;
      cloneTransform.localScale = source.localScale;

      StripNonVisualComponents(clone);
      SkeletonAnimUtil.SetLayerRecursively(clone, 0);
      // Activate only after the risky components are gone: activating an inactive clone would run
      // their OnEnable (e.g. ShadowPlayer.OnEnable mutates sharedMaterial, ShadowPlayer.cs:10-25).
      if (!clone.activeSelf) clone.SetActive(true);
      AnimControl script = clone.GetComponent<AnimControl>();
      if (script == null)
      {
        // Never leave a half-built clone around: Update would retry and leak a copy per frame.
        Logger.LogError("[WST][Remote] cloned skeleton " + source.name + " has no AnimControl, discarding it");
        DestroyImmediate(clone);
        return null;
      }
      skeletonAnimation = SkeletonAnimUtil.FindSkeletonAnimation(clone);
      return script;
    }

    private static void StripNonVisualComponents(GameObject root)
    {
      // A remote avatar must never participate in physics. The 2D physics module is not referenced
      // by the csproj (only UnityEngine.PhysicsModule is), so the physics types are matched by type
      // name while walking the inheritance chain instead of being written in code.
      Component[] components = root.GetComponentsInChildren<Component>(true);
      for (int i = 0; i < components.Length; i++)
      {
        Component component = components[i];
        if (component == null) continue;
        if (component is Transform) continue;

        if (IsPhysicsComponent(component))
        {
          DestroyImmediate(component);
          continue;
        }

        // Keep only the skeleton renderer/animation components. Everything else belongs to the real
        // player entity: ShadowPlayer mutates sharedMaterial (ShadowPlayer.cs:10-25), BoneFollower /
        // effect controllers would follow the wrong skeleton and mutate the local player.
        MonoBehaviour behaviour = component as MonoBehaviour;
        if (behaviour == null) continue;
        string typeName = behaviour.GetType().Name;
        if (typeName == "AnimControl") continue;
        if (typeName == "SkeletonAnimation") continue;
        if (typeName == "SkeletonGraphic") continue;
        DestroyImmediate(behaviour);
      }
    }

    private static bool IsPhysicsComponent(Component component)
    {
      Type type = component.GetType();
      while (type != null && type != typeof(Component) && type != typeof(object))
      {
        string typeName = type.Name;
        if (typeName == "Collider2D" || typeName == "Rigidbody2D" || typeName == "Joint2D") return true;
        if (typeName == "Collider" || typeName == "Rigidbody" || typeName == "Joint") return true;
        type = type.BaseType;
      }
      return false;
    }

    // ---------------------------------------------------------------------------------------
    // Skin: the avatar must look like the local player (same sect skin).
    // ---------------------------------------------------------------------------------------

    // Skin follows the local player's sect (and its live skin-effect flag). It is re-applied only
    // when it changes; the first application happens on the first Update after the clone is built
    // (SkeletonAnimation.Start may not have run yet on the building frame).
    private void ApplySkinIfChanged(PlayerAnimControl player, bool force)
    {
      if (player == null) return;

      PlayerSkin skin = player.curSkin;
      int skinFlag = 0;
      try
      {
        // GlobalParameter.GetCurrentPlayerSkin() (GlobalParameter.cs:643) reads the local player's
        // sect and can throw when the run has not started yet.
        skinFlag = GlobalParameter.GetCurrentPlayerSkin();
      }
      catch (Exception)
      {
        skinFlag = 0;
      }

      if (!force && _skinApplied && skin == _appliedSkin && skinFlag == _appliedSkinFlag) return;
      _skinApplied = true;
      _appliedSkin = skin;
      _appliedSkinFlag = skinFlag;

      string skinName = SkeletonAnimUtil.GetSkinName(skin, skinFlag);
      SkeletonAnimUtil.ApplySkin(_upSkeletonAnimation, skinName);
      SkeletonAnimUtil.ApplySkin(_downSkeletonAnimation, skinName);
      SkeletonAnimUtil.ApplySkin(_sideSkeletonAnimation, skinName);
      Logger.LogInfo("[WST][Remote] avatar " + SteamId + " skin " + skinName + " (sect " + skin + ", flag " + skinFlag + ")");
    }
  }

  // [WST NetSync v1] spine reflection helper (animation name / playback / skin) - owner: A
  //
  // spine-unity.dll is not referenced by the csproj, so every spine member is reached through
  // cached reflection on the component instance (same members the game uses:
  // skeletonAnimation.AnimationName, skeletonAnimation.state.SetAnimation(0, name, loop),
  // skeletonAnimation.skeleton.SetSkin(name) + SetSlotsToSetupPose()).
  // Every entry point swallows failures: animation sync is best effort and must never throw.
  internal static class SkeletonAnimUtil
  {
    internal sealed class Playback
    {
      public Component SkeletonAnimation;
      public PropertyInfo AnimationNameProperty;
      public PropertyInfo StateProperty;
      public MethodInfo SetAnimationMethod;
      public PropertyInfo SkeletonProperty;
      public MethodInfo SetSkinMethod;
      public MethodInfo SetSlotsToSetupPoseMethod;
    }

    private static readonly Dictionary<int, Playback> Cache = new Dictionary<int, Playback>();

    internal static void SetLayerRecursively(GameObject root, int layer)
    {
      if (root == null) return;
      Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
      for (int i = 0; i < transforms.Length; i++)
      {
        if (transforms[i] != null) transforms[i].gameObject.layer = layer;
      }
    }

    internal static string SkeletonNameForDir(int dirIndex)
    {
      if (dirIndex == 0) return "upSkeleton";
      if (dirIndex == 1) return "downSkeleton";
      return "leftSkeleton";
    }

    internal static Component FindSkeletonAnimation(GameObject root)
    {
      if (root == null) return null;
      MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
      for (int i = 0; i < behaviours.Length; i++)
      {
        MonoBehaviour behaviour = behaviours[i];
        if (behaviour == null) continue;
        if (behaviour.GetType().Name == "SkeletonAnimation") return behaviour;
      }
      return null;
    }

    internal static Playback GetPlayback(Component skeletonAnimation)
    {
      if (skeletonAnimation == null) return null;

      int instanceId;
      try
      {
        instanceId = skeletonAnimation.GetInstanceID();
      }
      catch (Exception)
      {
        return null;
      }

      Playback cached;
      if (Cache.TryGetValue(instanceId, out cached) && cached != null && cached.SkeletonAnimation != null)
      {
        return cached;
      }

      // Do not let the cache grow forever across scene loads / avatar respawns.
      if (Cache.Count > 128) Cache.Clear();

      Playback playback = new Playback();
      playback.SkeletonAnimation = skeletonAnimation;
      Type type = skeletonAnimation.GetType();
      playback.AnimationNameProperty = type.GetProperty("AnimationName", BindingFlags.Public | BindingFlags.Instance);
      playback.StateProperty = type.GetProperty("state", BindingFlags.Public | BindingFlags.Instance);
      if (playback.StateProperty != null)
      {
        playback.SetAnimationMethod = playback.StateProperty.PropertyType.GetMethod(
          "SetAnimation", new Type[] { typeof(int), typeof(string), typeof(bool) });
      }
      playback.SkeletonProperty = type.GetProperty("skeleton", BindingFlags.Public | BindingFlags.Instance);
      if (playback.SkeletonProperty != null)
      {
        Type skeletonType = playback.SkeletonProperty.PropertyType;
        playback.SetSkinMethod = skeletonType.GetMethod("SetSkin", new Type[] { typeof(string) });
        playback.SetSlotsToSetupPoseMethod = skeletonType.GetMethod("SetSlotsToSetupPose", Type.EmptyTypes);
      }
      Cache[instanceId] = playback;
      return playback;
    }

    internal static string GetAnimationName(Component skeletonAnimation)
    {
      if (skeletonAnimation == null) return "";
      try
      {
        Playback playback = GetPlayback(skeletonAnimation);
        if (playback == null || playback.AnimationNameProperty == null) return "";
        object value = playback.AnimationNameProperty.GetValue(playback.SkeletonAnimation, null);
        string name = value as string;
        return name == null ? "" : name;
      }
      catch (Exception)
      {
        return "";
      }
    }

    // Animation name currently playing on the visible skeleton of the local player ("" if unknown).
    internal static string GetPlayerAnimationName(Transform playerTransform, int dirIndex)
    {
      if (playerTransform == null) return "";
      try
      {
        Transform skeletonTransform = playerTransform.Find(SkeletonNameForDir(dirIndex));
        if (skeletonTransform == null) return "";
        Component skeletonAnimation = FindSkeletonAnimation(skeletonTransform.gameObject);
        return GetAnimationName(skeletonAnimation);
      }
      catch (Exception)
      {
        return "";
      }
    }

    // Returns true when the animation is now the active one on track 0 (either already playing or
    // just started); false when the skeleton/playback is not available yet and the caller should
    // retry later. A name the skeleton asset does not contain is added to failedNames.
    internal static bool Play(Component skeletonAnimation, string animationName, bool loop, HashSet<string> failedNames)
    {
      if (skeletonAnimation == null || string.IsNullOrEmpty(animationName)) return false;
      if (failedNames != null && failedNames.Contains(animationName)) return false;
      try
      {
        Playback playback = GetPlayback(skeletonAnimation);
        if (playback == null || playback.SetAnimationMethod == null || playback.StateProperty == null) return false;

        if (playback.AnimationNameProperty != null)
        {
          string current = playback.AnimationNameProperty.GetValue(playback.SkeletonAnimation, null) as string;
          if (current == animationName) return true;
        }

        object state = playback.StateProperty.GetValue(playback.SkeletonAnimation, null);
        if (state == null) return false;
        playback.SetAnimationMethod.Invoke(state, new object[] { 0, animationName, loop });
        return true;
      }
      catch (Exception)
      {
        // The skeleton asset does not contain this animation. Remember it so we do not retry every frame.
        if (failedNames != null) failedNames.Add(animationName);
        return false;
      }
    }

    internal static void ApplySkin(Component skeletonAnimation, string skinName)
    {
      if (skeletonAnimation == null || string.IsNullOrEmpty(skinName)) return;
      if (TrySetSkin(skeletonAnimation, skinName)) return;
      if (skinName == "01") return;
      // Fall back to the default skin so the avatar still renders sensibly.
      TrySetSkin(skeletonAnimation, "01");
    }

    private static bool TrySetSkin(Component skeletonAnimation, string skinName)
    {
      try
      {
        Playback playback = GetPlayback(skeletonAnimation);
        if (playback == null || playback.SkeletonProperty == null || playback.SetSkinMethod == null) return false;
        object skeleton = playback.SkeletonProperty.GetValue(playback.SkeletonAnimation, null);
        if (skeleton == null) return false;
        playback.SetSkinMethod.Invoke(skeleton, new object[] { skinName });
        if (playback.SetSlotsToSetupPoseMethod != null)
        {
          playback.SetSlotsToSetupPoseMethod.Invoke(skeleton, null);
        }
        return true;
      }
      catch (Exception)
      {
        return false;
      }
    }

    // Skin name mapping taken from AnimControl.PlayerSkinChange.
    internal static string GetSkinName(PlayerSkin skin, int skinFlag)
    {
      string baseName;
      switch (skin)
      {
        case PlayerSkin.SwordMaster: baseName = "02"; break;
        case PlayerSkin.DrunkMaster: baseName = "03"; break;
        case PlayerSkin.ThunderGod: baseName = "06"; break;
        case PlayerSkin.Venomancer: baseName = "05"; break;
        case PlayerSkin.FrozenMaster: baseName = "04"; break;
        case PlayerSkin.Assassin: baseName = "07"; break;
        case PlayerSkin.SunMoon: baseName = "08"; break;
        case PlayerSkin.Yama: baseName = "09"; break;
        case PlayerSkin.SpiritBlade: baseName = "10"; break;
        default: baseName = "01"; break;
      }
      if (skinFlag == 1) return baseName + "sp";
      if (skinFlag == 2) return baseName + "b";
      if (skinFlag == 3) return baseName + "c";
      return baseName;
    }

    // Loop classification for the animation names the player controller can play.
    internal static bool IsLoopAnimation(string animationName)
    {
      if (string.IsNullOrEmpty(animationName)) return false;
      switch (animationName)
      {
        case "idle":
        case "run":
        case "go":
        case "go2":
        case "dash":
        case "hit01":
        case "hit02":
        case "7-run":
        case "7-go2":
        case "7-idle":
        case "7-dash":
        case "7-hit01":
        case "7-hit02":
          return true;
        default:
          return false;
      }
    }
  }
}
