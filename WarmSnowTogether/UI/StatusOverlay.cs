// [WST NetSync v1] owner: lead
// Small always-on-top status overlay for multiplayer troubleshooting.
// The UIManager only builds its button canvas in the hub (MapID == 5) and shows
// nothing when Steam fails, which made "the button does nothing" impossible to
// diagnose in-game. This overlay is independent of the hub UI and stays visible
// in every scene. Press F9 to hide/show it.
using UnityEngine;
using WarmSnowTogether.Networking;
using WarmSnowTogether.Networking.Steam;
using Logger = WarmSnowTogether.Systems.Logging.Logger;

namespace WarmSnowTogether.UI
{
  public class StatusOverlay : MonoBehaviour
  {
    public static StatusOverlay Instance { get; private set; }

    private bool _visible = true;
    private GUIStyle _style;
    private GUIStyle _titleStyle;

    public static void EnsureCreated()
    {
      if (Instance != null) return;
      var go = new GameObject("WST_StatusOverlay");
      DontDestroyOnLoad(go);
      Instance = go.AddComponent<StatusOverlay>();
      Logger.LogInfo("[StatusOverlay] created (F9 toggles it)");
    }

    private void OnGUI()
    {
      // Toggle with F9 using the IMGUI event itself, so this overlay does not
      // need the UnityEngine.InputLegacyModule assembly reference.
      Event guiEvent = Event.current;
      if (guiEvent != null && guiEvent.type == EventType.KeyDown && guiEvent.keyCode == KeyCode.F9)
      {
        _visible = !_visible;
        guiEvent.Use();
      }
      if (!_visible) return;
      try
      {
        var lm = SteamLobbyManager.Instance;
        var nm = NetworkManager.Instance;
        string steamId = "?";
        try
        {
          if (SteamP2PManager.Instance != null && SteamP2PManager.Instance.IsInitialised)
            steamId = SteamP2PManager.Instance.LocalSteamID.ToString();
        }
        catch { }

        string role = "IDLE";
        if (nm != null)
        {
          if (nm.IsHost) role = "HOST";
          else if (nm.IsClient) role = nm.IsConnected ? "CLIENT (connected)" : "CLIENT (connecting)";
        }

        string lobby = "not in lobby";
        string status = "no status";
        if (lm != null)
        {
          lobby = lm.IsInLobby ? lm.CurrentLobbyId.ToString() : "not in lobby";
          status = string.IsNullOrEmpty(lm.StatusText) ? "(idle, click 联机模式 to host)" : lm.StatusText;
        }
        else
        {
          status = "SteamLobbyManager not initialised (Steam layer unavailable)";
        }

        if (_style == null)
        {
          _style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = false };
          _style.normal.textColor = Color.white;
          _titleStyle = new GUIStyle(_style) { fontSize = 14, fontStyle = FontStyle.Bold };
          _titleStyle.normal.textColor = new Color(0.55f, 1f, 0.65f);
        }

        const float w = 620f;
        GUI.Box(new Rect(8f, 8f, w, 84f), GUIContent.none);
        GUI.Label(new Rect(16f, 12f, w - 16f, 20f),
            "WarmSnowTogether | role: " + role + " | SteamID: " + steamId, _titleStyle);
        GUI.Label(new Rect(16f, 32f, w - 16f, 20f), "Lobby: " + lobby, _style);
        GUI.Label(new Rect(16f, 50f, w - 16f, 20f), "Status: " + status, _style);
        GUI.Label(new Rect(16f, 68f, w - 16f, 18f),
            "[F9] hide/show  |  full log: BepInEx/LogOutput.log", _style);
      }
      catch (System.Exception e)
      {
        Logger.LogWarning("[StatusOverlay] OnGUI error: " + e.Message);
        _visible = false;
      }
    }
  }
}
