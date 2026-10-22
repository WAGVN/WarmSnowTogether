using UnityEngine;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking.Sync
{
  // [WST NetSync v1] deprecated player-sync manager (no-op) - owner: A
  //
  // DEPRECATED / INTENTIONALLY EMPTY.
  //
  // v1 of the mod used this MonoBehaviour to broadcast a PlayerStatePacket (which carried HP,
  // max HP and status flags) roughly twice a second, and on receive it wrote the remote HP into
  // the LOCAL player's playerParameter. That corrupted the local run (the local player's HP bar
  // followed the remote player) and is the bug that forced this rewrite.
  //
  // Position, direction and animation are now owned by PlayerManager (sender side) and
  // RemotePlayerAvatar (receiver side); HP is deliberately NOT synchronised over the network.
  //
  // The class, its singleton and its MonoBehaviour lifecycle are kept only because
  // Core/WarmSnowTogetherMod.cs:50 activates PlayerSyncManager.Instance.gameObject, and keeping
  // the object alive avoids touching that file. It sends no packets, reads no game state and
  // writes nothing into the local player.
  public class PlayerSyncManager : MonoBehaviour
  {
    private static PlayerSyncManager _instance;

    public static PlayerSyncManager Instance
    {
      get
      {
        if (_instance == null)
        {
          GameObject go = new GameObject("PlayerSyncManager");
          _instance = go.AddComponent<PlayerSyncManager>();
          DontDestroyOnLoad(go);
        }
        return _instance;
      }
    }

    private void Awake()
    {
      if (_instance == null) _instance = this;
      Logger.LogInfo("[WST][PlayerSync] deprecated player-sync manager activated (no-op)");
    }

    private void Start()
    {
      // Intentionally empty: all logic was removed in the NetSync v1 rewrite.
    }
  }
}
