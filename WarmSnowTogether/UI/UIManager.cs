using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
namespace WarmSnowTogether.UI
{
  public static class UIManager
  {
    private static GameObject _uiRoot;
    private static GameObject _panelObj;
    private static float _sceneCheckTimer;
    private static bool _needCheckScene;
    private static Type _stageControlType;
    private static FieldInfo _mapIdField;
    private static bool _hasLoadedStageType;
    private static Font _defaultFont;
    public static void Initialise()
    {
      WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] UIManager init，读取StageControl.MapID");
      _sceneCheckTimer = 0;
      _needCheckScene = false;
      _hasLoadedStageType = false;
      _defaultFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
    }
    public static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
      _needCheckScene = true;
      _sceneCheckTimer = 0f;
    }
    public static void Update()
    {
      try
      {
        if (_needCheckScene)
        {
          _sceneCheckTimer += Time.unscaledDeltaTime;
          if (_sceneCheckTimer >= 0.4f)
          {
            _needCheckScene = false;
            CheckSceneOnce();
          }
        }
      }
      catch (Exception e)
      {
        WarmSnowTogether.Systems.Logging.Logger.LogError($"[UI] Update异常：{e.Message}");
      }
    }
    private static void CheckSceneOnce()
    {
      if (!_hasLoadedStageType)
      {
        Assembly asm = null;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
          if (a.GetName().Name == "Assembly-CSharp")
          {
            asm = a;
            break;
          }
        }
        if (asm == null)
        {
          WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] 找不到Assembly-CSharp");
          return;
        }
        _stageControlType = asm.GetType("StageControl");
        if (_stageControlType == null)
        {
          WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] Assembly-CSharp找不到StageControl");
          return;
        }
        _mapIdField = _stageControlType.GetField("MapID",
            BindingFlags.Public | BindingFlags.Instance);
        if (_mapIdField == null)
        {
          WarmSnowTogether.Systems.Logging.Logger.LogError("[UI] ❌ StageControl找不到MapID字段！");
          return;
        }
        _hasLoadedStageType = true;
        WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] ✅ StageControl.MapID 缓存成功");
      }
      var stageCtrl = UnityEngine.Object.FindObjectOfType(_stageControlType);
      if (stageCtrl == null)
      {
        WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] 当前场景无StageControl实例，销毁联机UI");
        DestroyUI();
        return;
      }
      int mapId = (int)_mapIdField.GetValue(stageCtrl);
      WarmSnowTogether.Systems.Logging.Logger.LogInfo($"[UI] MapID = {mapId}");
      if (mapId == 5)
      {
        WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] ✅ 识别佛堂(祠堂)，创建联机UI");
        CreateUITest();
      }
      else
      {
        DestroyUI();
      }
    }
    private static void CreateUITest()
    {
      if (_uiRoot != null) return;
      _uiRoot = new GameObject("WST_UI_Canvas");
      Canvas canvas = _uiRoot.AddComponent<Canvas>();
      canvas.renderMode = RenderMode.ScreenSpaceOverlay;
      CanvasScaler scaler = _uiRoot.AddComponent<CanvasScaler>();
      scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
      scaler.referenceResolution = new Vector2(1920, 1080);
      _uiRoot.AddComponent<GraphicRaycaster>();
      _panelObj = new GameObject("WST_Panel");
      _panelObj.transform.SetParent(_uiRoot.transform, false);
      Image panelImg = _panelObj.AddComponent<Image>();
      panelImg.color = new Color(0, 0, 0, 0.6f);
      panelImg.raycastTarget = false;
      RectTransform panelRt = _panelObj.GetComponent<RectTransform>();
      panelRt.sizeDelta = new Vector2(320, 160);
      panelRt.anchoredPosition = new Vector2(-720, -320);
      GameObject btnOnline = CreateButton("联机模式", new Vector2(240, 42), new Vector2(0, 40));
      btnOnline.transform.SetParent(_panelObj.transform, false);
      Button onlineBtn = btnOnline.GetComponent<Button>();
      onlineBtn.onClick.RemoveAllListeners();
      onlineBtn.onClick.AddListener(() =>
      {
        WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] 点击：联机模式");
        try
        {
          var lobbyMgr = WarmSnowTogether.Networking.Steam.SteamLobbyManager.Instance;
          if (lobbyMgr == null)
          {
            WarmSnowTogether.Systems.Logging.Logger.LogError("[UI] SteamLobbyManager实例为空");
            return;
          }
          if (!lobbyMgr.IsInLobby)
          {
            WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] 未在大厅，自动创建主机大厅");
            lobbyMgr.CreateLobby(4);
          }
          else
          {
            WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] 已存在大厅，直接打开Steam邀请弹窗");
            lobbyMgr.OpenInviteDialog();
          }
        }
        catch (Exception ex)
        {
          WarmSnowTogether.Systems.Logging.Logger.LogError($"[UI] 联机模式异常: {ex.Message}");
        }
      });
      GameObject btnLeave = CreateButton("退出联机", new Vector2(240, 42), new Vector2(0, -40));
      btnLeave.transform.SetParent(_panelObj.transform, false);
      Button leaveBtn = btnLeave.GetComponent<Button>();
      leaveBtn.onClick.RemoveAllListeners();
      leaveBtn.onClick.AddListener(() =>
      {
        WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] 点击：退出联机");
        try
        {
          var lobbyMgr = WarmSnowTogether.Networking.Steam.SteamLobbyManager.Instance;
          if (lobbyMgr == null)
          {
            WarmSnowTogether.Systems.Logging.Logger.LogError("[UI] SteamLobbyManager实例为空");
            return;
          }
          lobbyMgr.LeaveLobby();
          WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] 已执行退出大厅");
        }
        catch (Exception ex)
        {
          WarmSnowTogether.Systems.Logging.Logger.LogError($"[UI] 退出联机异常: {ex.Message}");
        }
      });
    }
    private static GameObject CreateButton(string txt, Vector2 size, Vector2 pos)
    {
      GameObject go = new GameObject($"Btn_{txt}");
      Image img = go.AddComponent<Image>();
      img.color = Color.gray;
      img.raycastTarget = true;
      RectTransform rt = go.GetComponent<RectTransform>();
      rt.sizeDelta = size;
      rt.anchoredPosition = pos;
      Button btn = go.AddComponent<Button>();
      ColorBlock cb = btn.colors;
      cb.normalColor = Color.gray;
      cb.highlightedColor = Color.white;
      cb.pressedColor = Color.yellow;
      btn.colors = cb;
      GameObject textGo = new GameObject("Text");
      textGo.transform.SetParent(go.transform, false);
      Text text = textGo.AddComponent<Text>();
      text.text = txt;
      text.fontSize = 22;
      text.color = Color.black;
      text.font = _defaultFont;
      RectTransform textRt = textGo.GetComponent<RectTransform>();
      textRt.anchorMin = Vector2.zero;
      textRt.anchorMax = Vector2.one;
      textRt.sizeDelta = Vector2.zero;
      return go;
    }
    public static void DestroyUI()
    {
      if (_uiRoot != null)
      {
        UnityEngine.Object.Destroy(_uiRoot);
        _uiRoot = null;
      }
      _panelObj = null;
      WarmSnowTogether.Systems.Logging.Logger.LogInfo("[UI] 联机UI销毁完成");
    }
  }
}
