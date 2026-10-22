using System.Collections.Generic;
using UnityEngine;

namespace WarmSnowTogether.Networking.Entities
{
  public class NetworkEntityManager
  {
    private static NetworkEntityManager _instance;
    private static readonly object _lockObj = new object();

    public static NetworkEntityManager Instance
    {
      get
      {
        lock (_lockObj)
        {
          if (_instance == null)
          {
            _instance = new NetworkEntityManager();
          }
          return _instance;
        }
      }
    }

    private int _nextId = 1;
    // host端：实体ID -> 游戏内怪物对象（暖雪原生对象，不能Destroy）
    private readonly Dictionary<int, object> _entities = new Dictionary<int, object>();
    // client端：实体ID -> 客户端生成的远端怪物GameObject（模组生成，需要销毁）
    private Dictionary<int, GameObject> _remoteEntities = new Dictionary<int, GameObject>();

    public int AssignId(object entity)
    {
      int id = _nextId++;
      _entities[id] = entity;
      return id;
    }

    public object GetEntity(int id)
    {
      if (_entities.TryGetValue(id, out var e))
      {
        return e;
      }
      return null;
    }

    public void AddRemote(int id, GameObject go)
    {
      _remoteEntities[id] = go;
    }

    public GameObject GetRemote(int id)
    {
      if (_remoteEntities.TryGetValue(id, out var go))
      {
        return go;
      }
      return null;
    }

    public void RemoveRemote(int id)
    {
      if (_remoteEntities.TryGetValue(id, out var go))
      {
        UnityEngine.Object.Destroy(go);
      }
      _remoteEntities.Remove(id);
    }

    public void Clear()
    {
      // 只销毁客户端生成的远端实体
      foreach (var kv in _remoteEntities)
      {
        UnityEngine.Object.Destroy(kv.Value);
      }
      _remoteEntities.Clear();

      // host端：只清空映射表，【不销毁游戏原生怪物实例】
      _entities.Clear();
      _nextId = 1;
    }
  }
}