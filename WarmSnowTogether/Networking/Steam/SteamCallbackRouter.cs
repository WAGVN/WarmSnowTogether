using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
namespace WarmSnowTogether.Networking.Steam
{
  internal static class SteamCallbackRouter
  {
    private static readonly object _lock = new object();
    private static readonly List<Action<object>> _handlers = new List<Action<object>>();
    private static readonly List<object> _callbackObjects = new List<object>(); // Prevent GC
    public static void Clear()
    {
      lock (_lock)
      {
        WarmSnowTogether.Systems.Logging.Debug.Log("[SteamCallback] Clearing " + _handlers.Count + " handler(s) and " + _callbackObjects.Count + " callback object(s)");
        _handlers.Clear();
        _callbackObjects.Clear();
      }
    }
    public static void Invoke(int handlerIndex, object callbackData)
    {
      if (callbackData != null)
        WarmSnowTogether.Systems.Logging.Debug.Log($"[SteamCallback] Invoke called with handlerIndex={handlerIndex}, dataType={callbackData.GetType().Name}");

      lock (_lock)
      {
        if (handlerIndex < 0 || handlerIndex >= _handlers.Count)
        {
          WarmSnowTogether.Systems.Logging.Debug.LogWarning($"[SteamCallback] Invalid handler index {handlerIndex} (count={_handlers.Count})");
          return;
        }
        _handlers[handlerIndex]?.Invoke(callbackData);
      }
    }
    // [WST NetSync v1] Reusable handle for a Steamworks.CallResult<T> instance.
    // The game ships an integrated Steamworks.NET build whose API is:
    //   static CallResult<T> Create(CallResult<T>.APIDispatchDelegate func)
    //   void Set(SteamAPICall_t hAPICall, CallResult<T>.APIDispatchDelegate func)
    // so the dispatch delegate must be kept around and passed again to Set().
    public sealed class CallResultHandle
    {
      public object CallResultObject;
      public Delegate DispatchDelegate;
    }

    // Create a Steamworks.CallResult<T>: the correct wrapper for async API calls
    // such as SteamMatchmaking.CreateLobby. A raw Callback<T> only broadcasts the
    // result struct; a CallResult<T> correlates it with the SteamAPICall_t that the
    // API call returned (see SetCallResult below).
    // Defensive wrapper: a reflection failure here must never break plugin startup
    // (lobby creation simply falls back to the raw Callback<T> path).
    public static CallResultHandle CreateCallResult(Assembly steamworksAssembly, Type resultStructType, Action<object> handler)
    {
      try
      {
        return CreateCallResultCore(steamworksAssembly, resultStructType, handler);
      }
      catch (Exception e)
      {
        WarmSnowTogether.Systems.Logging.Debug.LogWarning("[SteamCallback] CreateCallResult threw (" + resultStructType.Name + "): " + e.GetType().Name + ": " + e.Message);
        return null;
      }
    }

    private static CallResultHandle CreateCallResultCore(Assembly steamworksAssembly, Type resultStructType, Action<object> handler)
    {
      if (steamworksAssembly == null || resultStructType == null || handler == null)
        return null;
      Type callResultGenericDef = steamworksAssembly.GetType("Steamworks.CallResult`1", false);
      if (callResultGenericDef == null)
      {
        WarmSnowTogether.Systems.Logging.Debug.LogWarning("[SteamCallback] CallResult`1 type not found");
        return null;
      }
      Type callResultClosedType = callResultGenericDef.MakeGenericType(resultStructType);
      MethodInfo createMethod = null;
      foreach (var m in callResultClosedType.GetMethods(BindingFlags.Public | BindingFlags.Static))
      {
        if (m.Name != "Create") continue;
        var ps = m.GetParameters();
        if (ps.Length == 1 && ps[0].ParameterType.IsSubclassOf(typeof(Delegate)))
        {
          createMethod = m;
          break;
        }
      }
      if (createMethod == null)
      {
        WarmSnowTogether.Systems.Logging.Debug.LogWarning($"[SteamCallback] No CallResult Create method found for {resultStructType.Name}");
        return null;
      }
      Type delegateType = createMethod.GetParameters()[0].ParameterType;

      int handlerIndex;
      lock (_lock)
      {
        handlerIndex = _handlers.Count;
        _handlers.Add(handler);
      }

      // The game's CallResult<T>.APIDispatchDelegate has the shape
      // void (T result, bool bIOFailure); build a dynamic method with the exact
      // same signature (the bool is ignored: on I/O failure the struct is zeroed,
      // which our handlers already treat as a failure).
      DynamicMethod dm = new DynamicMethod(
          "GT_CallResult_" + resultStructType.Name,
          typeof(void),
          new Type[] { resultStructType, typeof(bool) },
          typeof(SteamCallbackRouter).Module,
          skipVisibility: true);
      ILGenerator il = dm.GetILGenerator();
      il.Emit(OpCodes.Ldc_I4, handlerIndex);
      il.Emit(OpCodes.Ldarg_0);
      if (resultStructType.IsValueType)
        il.Emit(OpCodes.Box, resultStructType);
      il.Emit(OpCodes.Call, typeof(SteamCallbackRouter).GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static));
      il.Emit(OpCodes.Ret);
      Delegate del = dm.CreateDelegate(delegateType);

      object callResultObj;
      try
      {
        callResultObj = createMethod.Invoke(null, new object[] { del });
      }
      catch (Exception e)
      {
        WarmSnowTogether.Systems.Logging.Debug.LogWarning("[SteamCallback] CallResult.Create threw: " + e.Message);
        return null;
      }
      if (callResultObj == null)
      {
        WarmSnowTogether.Systems.Logging.Debug.LogWarning($"[SteamCallback] CallResult creation returned null for {resultStructType.Name}");
        return null;
      }
      lock (_lock)
      {
        _callbackObjects.Add(callResultObj);
        WarmSnowTogether.Systems.Logging.Debug.Log($"[SteamCallback] Successfully created CallResult for {resultStructType.Name} (stored, total {_callbackObjects.Count})");
      }
      return new CallResultHandle { CallResultObject = callResultObj, DispatchDelegate = del };
    }

    // Bind a pending SteamAPICall_t to a CallResult created above, so the result
    // is delivered to the handler registered for it. Supports both Steamworks.NET
    // shapes: Set(SteamAPICall_t) and Set(SteamAPICall_t, DispatchDelegate).
    public static bool SetCallResult(object handleOrObject, object apiCall)
    {
      if (handleOrObject == null || apiCall == null)
        return false;
      CallResultHandle handle = handleOrObject as CallResultHandle;
      object target = handle != null ? handle.CallResultObject : handleOrObject;
      if (target == null)
        return false;
      try
      {
        foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
          if (m.Name != "Set") continue;
          var ps = m.GetParameters();
          if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(apiCall))
          {
            m.Invoke(target, new object[] { apiCall });
            return true;
          }
          if (ps.Length == 2 && ps[0].ParameterType.IsInstanceOfType(apiCall)
              && handle != null && handle.DispatchDelegate != null
              && ps[1].ParameterType.IsInstanceOfType(handle.DispatchDelegate))
          {
            m.Invoke(target, new object[] { apiCall, handle.DispatchDelegate });
            return true;
          }
        }
        WarmSnowTogether.Systems.Logging.Debug.LogWarning("[SteamCallback] CallResult.Set(...) not found on " + target.GetType().Name);
        return false;
      }
      catch (Exception e)
      {
        WarmSnowTogether.Systems.Logging.Debug.LogWarning("[SteamCallback] CallResult.Set failed: " + e.Message);
        return false;
      }
    }

    public static object CreateCallback(Assembly steamworksAssembly, Type callbackStructType, Action<object> handler)
    {
      if (steamworksAssembly == null || callbackStructType == null || handler == null)
        return null;
      Type callbackGenericDef = steamworksAssembly.GetType("Steamworks.Callback`1", false);
      if (callbackGenericDef == null)
      {
        WarmSnowTogether.Systems.Logging.Debug.LogWarning("[SteamCallback] Callback`1 type not found");
        return null;
      }
      Type callbackClosedType = callbackGenericDef.MakeGenericType(callbackStructType);
      MethodInfo createMethod = null;
      foreach (var m in callbackClosedType.GetMethods(BindingFlags.Public | BindingFlags.Static))
      {
        if (m.Name != "Create") continue;
        var ps = m.GetParameters();
        if (ps.Length == 1 && ps[0].ParameterType.IsSubclassOf(typeof(Delegate)))
        {
          createMethod = m;
          break;
        }
      }
      if (createMethod == null)
      {
        foreach (var m in callbackClosedType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
          if (m.Name != "Create") continue;
          var ps = m.GetParameters();
          if (ps.Length == 2 && ps[0].ParameterType.IsSubclassOf(typeof(Delegate)) && ps[1].ParameterType == typeof(bool))
          {
            createMethod = m;
            break;
          }
        }
      }
      if (createMethod == null)
      {
        WarmSnowTogether.Systems.Logging.Debug.LogWarning($"[SteamCallback] No Create method found for {callbackStructType.Name}");
        return null;
      }
      Type delegateType = createMethod.GetParameters()[0].ParameterType;

      int handlerIndex;
      lock (_lock)
      {
        handlerIndex = _handlers.Count;
        _handlers.Add(handler);
      }

      // Create a dynamic method that matches the delegate signature
      DynamicMethod dm = new DynamicMethod(
          "GT_Callback_" + callbackStructType.Name,
          typeof(void),
          new Type[] { callbackStructType },
          typeof(SteamCallbackRouter).Module,
          skipVisibility: true);
      ILGenerator il = dm.GetILGenerator();
      il.Emit(OpCodes.Ldc_I4, handlerIndex);
      il.Emit(OpCodes.Ldarg_0);
      if (callbackStructType.IsValueType)
        il.Emit(OpCodes.Box, callbackStructType);
      il.Emit(OpCodes.Call, typeof(SteamCallbackRouter).GetMethod("Invoke", BindingFlags.Public | BindingFlags.Static));
      il.Emit(OpCodes.Ret);
      Delegate del = dm.CreateDelegate(delegateType);
      object callbackObj;
      if (createMethod.GetParameters().Length == 1)
        callbackObj = createMethod.Invoke(null, new object[] { del });
      else
        callbackObj = createMethod.Invoke(null, new object[] { del, true });

      lock (_lock)
      {
        if (callbackObj != null)
        {
          _callbackObjects.Add(callbackObj);
          WarmSnowTogether.Systems.Logging.Debug.Log($"[SteamCallback] Successfully created callback for {callbackStructType.Name} (stored, total {_callbackObjects.Count})");
        }
        else
        {
          WarmSnowTogether.Systems.Logging.Debug.LogWarning($"[SteamCallback] Callback creation returned null for {callbackStructType.Name}");
        }
      }
      return callbackObj;
    }
  }
}