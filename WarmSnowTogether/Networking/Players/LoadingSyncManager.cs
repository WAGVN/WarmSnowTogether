using UnityEngine;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.Networking.Sync
{
  // [WST NetSync v1] loading-state mirror (store + log only) - owner: A
  //
  // The host broadcasts LoadingStatePacket while it loads a stage; NetworkManager forwards it to
  // ApplyLoadingState(). This class only stores the flag: WorldSyncManager reads IsClientLoading
  // (Networking/Rooms/WorldSyncManager.cs:122,148) to hold the client's stage spawn until the host
  // has actually finished loading. No packet is sent from here.
  public class LoadingSyncManager : MonoBehaviour
  {
    private static LoadingSyncManager _instance;

    public static LoadingSyncManager Instance
    {
      get
      {
        if (_instance == null)
        {
          GameObject go = new GameObject("LoadingSyncManager");
          _instance = go.AddComponent<LoadingSyncManager>();
          DontDestroyOnLoad(go);
        }
        return _instance;
      }
    }

    private bool _isClientLoading;

    // True while the remote (host) side is loading a stage. Defaults to false so the client never
    // waits forever when no loading packet arrives.
    public bool IsClientLoading
    {
      get { return _isClientLoading; }
    }

    private void Awake()
    {
      if (_instance == null) _instance = this;
      Logger.LogInfo("[WST][LoadingSync] loading sync manager activated");
    }

    public void ApplyLoadingState(bool isLoading)
    {
      if (_isClientLoading == isLoading) return;
      _isClientLoading = isLoading;
      Logger.LogInfo("[WST][LoadingSync] host loading state -> " + isLoading);
    }
  }
}
