using BepInEx;
using UnityEngine;
using UnityEngine.SceneManagement;
using WarmSnowTogether.Networking;
using WarmSnowTogether.Networking.Entities;
using WarmSnowTogether.Networking.Loot;
using WarmSnowTogether.Networking.Sync;
using WarmSnowTogether.Systems.Logging;
using Logger = WarmSnowTogether.Systems.Logging.Logger;
using WarmSnowTogether.UI;

namespace WarmSnowTogether.Core
{
  // [WST NetSync v1] Plugin entry point (owner: lead).
  // Awake: logger + UI + network facade (Steam P2P / lobby / role).
  // Start: activate the world/player/enemy sync managers.
  [BepInPlugin("com.llamerrr.WarmSnowTogether", "Warm Snow Together", "1.0.0")]
  public class WarmSnowTogetherMod : BaseUnityPlugin
  {
    public static WarmSnowTogetherMod Instance { get; private set; }

    private void Awake()
    {
      Instance = this;
      SceneManager.sceneLoaded += OnSceneLoaded;
      try
      {
        WarmSnowTogether.Systems.Logging.Logger.Initialise(base.Logger);

        UIManager.Initialise();

        Logger.LogInfo("Warm Snow Together starting (Host-Authority NetSync v1)...");
        NetworkManager.Instance.Initialise();
        Logger.LogInfo("Core systems initialized, waiting for game UI initialization.");
      }
      catch (System.Exception ex)
      {
        Logger.LogError($"Exception during Awake initialization: {ex.GetType().Name}: {ex.Message}");
        Logger.LogError($"Stack trace: {ex.StackTrace}");
        throw;
      }
    }

    private void Start()
    {
      try
      {
        // Requirement 1: host tells the client which Unity scene to load.
        WorldSyncManager.Instance.gameObject.SetActive(true);

        // Requirement 2: remote player avatar (visual mirror of the other player).
        PlayerSyncManager.Instance.gameObject.SetActive(true);
        LoadingSyncManager.Instance.gameObject.SetActive(true);

        // Requirements 3 + 4: host-authoritative enemies and damage routing.
        // EnemySyncManager also installs the Harmony patches used by the client
        // (suppress local enemy spawning, redirect player damage to the host, block local death).
        EnemySyncManager.Instance.gameObject.SetActive(true);
        EnemySyncManager.Instance.Initialise();

        // [WST Loot Sync v1] Host-authoritative loot mirroring. The whole framework is active, but it
        // stays 100% inactive until LootHookAdapter.Ready is set to true (after the reverse-engineering
        // TODOs in LootHookAdapter.cs are filled in), so shipping this build cannot change loot behaviour.
        LootSyncManager.Instance.gameObject.SetActive(true);
        LootSyncManager.Instance.Initialise();

        // Troubleshooting overlay: shows role / lobby id / last Steam error on
        // screen in every scene (F9 toggles). Makes lobby failures visible
        // instead of looking like "the button did nothing".
        StatusOverlay.EnsureCreated();

        Logger.LogInfo("WarmSnow Together ready.");
      }
      catch (System.Exception ex)
      {
        Logger.LogError($"Exception during Start sync init: {ex.GetType().Name}: {ex.Message}");
        Logger.LogError($"Stack trace: {ex.StackTrace}");
      }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
      Logger.LogInfo($"[WST] Scene loaded: name={scene.name}, buildIndex={scene.buildIndex}");
      try
      {
        UIManager.OnSceneLoaded(scene, mode);
      }
      catch (System.Exception ex)
      {
        Logger.LogError($"Error handling scene load for UI: {ex.Message}");
      }
    }

    private void OnDestroy()
    {
      SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void Update()
    {
      try
      {
        NetworkManager.Instance.Update();
      }
      catch (System.Exception ex)
      {
        Logger.LogError($"Error in NetworkManager Update: {ex.Message}");
      }
      try
      {
        UIManager.Update();
      }
      catch (System.Exception ex)
      {
        Logger.LogError($"Error in UIManager Update: {ex.Message}");
      }
    }
  }
}
