using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WarmSnowTogether.Systems.Logging;
using HarmonyLib;
namespace WarmSnowTogether.Networking.Steam
{
    public static partial class SteamReflectionHelper
    {
        private static bool initialised = false;
        private static ulong cachedSteamId = 0;
        private static bool steamIdCached = false;
        private static Assembly cachedSteamworksAssembly = null;
        private static int workingSendSignatureIndex = -1;

        public static void InitialiseSteamTypes()
        {
            try
            {
                Assembly steamworksAssembly = null;
                Assembly[] assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    if (string.Equals(assemblies[i].GetName().Name, "Assembly-CSharp-firstpass"))
                    {
                        steamworksAssembly = assemblies[i];
                        break;
                    }
                }
                if (ReferenceEquals(steamworksAssembly, null))
                {
                    return;
                }
                cachedSteamworksAssembly = steamworksAssembly;

                steamUserType = steamworksAssembly.GetType("Steamworks.SteamUser", false);
                steamFriendsType = steamworksAssembly.GetType("Steamworks.SteamFriends", false);
                steamNetworkingType = steamworksAssembly.GetType("Steamworks.SteamNetworking", false);
                steamMatchmakingType = steamworksAssembly.GetType("Steamworks.SteamMatchmaking", false);
                steamUtilsType = steamworksAssembly.GetType("Steamworks.SteamUtils", false);
                steamAppsType = steamworksAssembly.GetType("Steamworks.SteamApps", false);
                gameLobbyJoinRequestedCallbackType = steamworksAssembly.GetType("Steamworks.GameLobbyJoinRequested_t", false);
                p2pSessionConnectFailType = steamworksAssembly.GetType("Steamworks.P2PSessionConnectFail_t", false);
                gameJoinRequestedCallbackType = steamworksAssembly.GetType("Steamworks.GameRichPresenceJoinRequested_t", false);
                lobbyEnterCallbackType = steamworksAssembly.GetType("Steamworks.LobbyEnter_t", false);
                lobbyCreatedCallbackType = steamworksAssembly.GetType("Steamworks.LobbyCreated_t", false);
                lobbyDataUpdateCallbackType = steamworksAssembly.GetType("Steamworks.LobbyDataUpdate_t", false);

                Debug.Log($"[ETGSteamP2P] Found Steamworks types:");
                Debug.Log($"  SteamUser: {steamUserType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  SteamFriends: {steamFriendsType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  SteamNetworking: {steamNetworkingType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  SteamMatchmaking: {steamMatchmakingType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  SteamUtils: {steamUtilsType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  SteamApps: {steamAppsType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  GameJoinRequestedCallback: {gameJoinRequestedCallbackType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  LobbyEnterCallback: {lobbyEnterCallbackType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  LobbyCreatedCallback: {lobbyCreatedCallbackType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  LobbyDataUpdateCallback: {lobbyDataUpdateCallbackType?.FullName ?? "NOT FOUND"}");
                Debug.Log($"  GameLobbyJoinRequestedCallback: {gameLobbyJoinRequestedCallbackType?.FullName ?? "NOT FOUND"}");

                CacheSteamUserMethods();
                CacheSteamNetworkingMethods();
                CacheSteamFriendsMethods();
                CacheSteamMatchmakingMethods();
                initialised = (!ReferenceEquals(steamNetworkingType, null) && !ReferenceEquals(sendP2PPacketMethod, null));
            }
            catch (Exception e)
            {
                if (e is System.TypeLoadException tle)
                {
                    Debug.LogError($"[ETGSteamP2P] TypeLoadException: Type Name: {tle.TypeName}");
                    Debug.LogError($"[ETGSteamP2P] Message: {tle.Message}");
                    Debug.LogError($"[ETGSteamP2P] Failed exception: {tle}");
                }
                else
                {
                    Debug.LogError($"[ETGSteamP2P] {e.GetType().Name}: {e.Message}");
                    Debug.LogError($"[ETGSteamP2P] Stack: {e.StackTrace}");
                }
                initialised = false;
            }
        }
        private static void CacheSteamUserMethods()
        {
            if (!ReferenceEquals(steamUserType, null))
            {
                getSteamIdMethod = steamUserType.GetMethod("GetSteamID", BindingFlags.Public | BindingFlags.Static);
                if (ReferenceEquals(getSteamIdMethod, null))
                {
                    getSteamIdMethod = steamUserType.GetMethod("get_SteamID", BindingFlags.Public | BindingFlags.Static);
                }
            }
        }
        private static void CacheSteamNetworkingMethods()
        {
            if (ReferenceEquals(steamNetworkingType, null)) return;
            try
            {
                var allMethods = steamNetworkingType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                foreach (var method in allMethods)
                {
                    if (method.Name == "SendP2PPacket")
                    {
                        sendP2PPacketMethod = method;
                        break;
                    }
                }
                readP2PPacketMethod = steamNetworkingType.GetMethod("ReadP2PPacket", BindingFlags.Public | BindingFlags.Static);
                foreach (var method in allMethods)
                {
                    if (method.Name != "IsP2PPacketAvailable") continue;
                    var ps = method.GetParameters();
                    if (ps.Length >= 1 && ps[0].IsOut && ps[0].ParameterType == typeof(uint).MakeByRefType())
                    {
                        isP2PPacketAvailableMethod = method;
                        break;
                    }
                }
                if (isP2PPacketAvailableMethod == null)
                {
                    foreach (var method in allMethods)
                    {
                        if (method.Name == "IsP2PPacketAvailable")
                        {
                            isP2PPacketAvailableMethod = method;
                            break;
                        }
                    }
                }
                acceptP2PSessionMethod = steamNetworkingType.GetMethod("AcceptP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static);
                closeP2PSessionMethod = steamNetworkingType.GetMethod("CloseP2PSessionWithUser", BindingFlags.Public | BindingFlags.Static);

                Debug.Log($"[ETGSteamP2P] SendP2PPacket: {(sendP2PPacketMethod != null ? "Found" : "Missing")}");
                Debug.Log($"[ETGSteamP2P] ReadP2PPacket: {(readP2PPacketMethod != null ? "Found" : "Missing")}");
                Debug.Log($"[ETGSteamP2P] IsP2PPacketAvailable: {(isP2PPacketAvailableMethod != null ? "Found" : "Missing")}");
                Debug.Log($"[ETGSteamP2P] AcceptP2PSessionWithUser: {(acceptP2PSessionMethod != null ? "Found" : "Missing")}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ETGSteamP2P] Error in CacheSteamNetworkingMethods: {ex.Message}\n{ex.StackTrace}");
            }
        }
        private static void CacheSteamFriendsMethods()
        {
            if (!ReferenceEquals(steamFriendsType, null))
            {
                setRichPresenceMethod = steamFriendsType.GetMethod("SetRichPresence", BindingFlags.Public | BindingFlags.Static);
                clearRichPresenceMethod = steamFriendsType.GetMethod("ClearRichPresence", BindingFlags.Public | BindingFlags.Static);
                getFriendCountMethod = steamFriendsType.GetMethod("GetFriendCount", BindingFlags.Public | BindingFlags.Static);
                getFriendByIndexMethod = steamFriendsType.GetMethod("GetFriendByIndex", BindingFlags.Public | BindingFlags.Static);
                getFriendPersonaNameMethod = steamFriendsType.GetMethod("GetFriendPersonaName", BindingFlags.Public | BindingFlags.Static);
                getFriendPersonaStateMethod = steamFriendsType.GetMethod("GetFriendPersonaState", BindingFlags.Public | BindingFlags.Static);
                getFriendGamePlayedMethod = steamFriendsType.GetMethod("GetFriendGamePlayed", BindingFlags.Public | BindingFlags.Static);
                getFriendRichPresenceMethod = steamFriendsType.GetMethod("GetFriendRichPresence", BindingFlags.Public | BindingFlags.Static);

                if (!ReferenceEquals(getFriendGamePlayedMethod, null))
                {
                    var parameters = getFriendGamePlayedMethod.GetParameters();
                    var paramStr = "";
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (i > 0) paramStr += ", ";
                        string prefix = parameters[i].IsOut ? "out " : (parameters[i].ParameterType.IsByRef ? "ref " : "");
                        paramStr += $"{prefix}{parameters[i].ParameterType.Name} {parameters[i].Name}";
                    }
                }
            }
        }
        private static void CacheSteamMatchmakingMethods()
        {
            if (!ReferenceEquals(steamMatchmakingType, null))
            {
                createLobbyMethod = steamMatchmakingType.GetMethod("CreateLobby", BindingFlags.Public | BindingFlags.Static);
                joinLobbyMethod = steamMatchmakingType.GetMethod("JoinLobby", BindingFlags.Public | BindingFlags.Static);
                leaveLobbyMethod = steamMatchmakingType.GetMethod("LeaveLobby", BindingFlags.Public | BindingFlags.Static);
                setLobbyDataMethod = steamMatchmakingType.GetMethod("SetLobbyData", BindingFlags.Public | BindingFlags.Static);
                getLobbyDataMethod = steamMatchmakingType.GetMethod("GetLobbyData", BindingFlags.Public | BindingFlags.Static);
                setLobbyJoinableMethod = steamMatchmakingType.GetMethod("SetLobbyJoinable", BindingFlags.Public | BindingFlags.Static);
                inviteUserToLobbyMethod = steamMatchmakingType.GetMethod("InviteUserToLobby", BindingFlags.Public | BindingFlags.Static);
                getLobbyOwnerMethod = steamMatchmakingType.GetMethod("GetLobbyOwner", BindingFlags.Public | BindingFlags.Static);
                getLobbyMemberCountMethod = steamMatchmakingType.GetMethod("GetNumLobbyMembers", BindingFlags.Public | BindingFlags.Static);
                getLobbyMemberByIndexMethod = steamMatchmakingType.GetMethod("GetLobbyMemberByIndex", BindingFlags.Public | BindingFlags.Static);
                _allowP2PPacketRelayMethod = steamNetworkingType.GetMethod("AllowP2PPacketRelay", BindingFlags.Public | BindingFlags.Static);
            }
        }
        public static string GetPlayerName(ulong steamId)
        {
            if (getFriendPersonaNameMethod == null) return steamId.ToString();
            try
            {
                object cSteamId = ConvertToCSteamID(steamId);
                object result = getFriendPersonaNameMethod.Invoke(null, new object[] { cSteamId });
                return result?.ToString() ?? steamId.ToString();
            }
            catch { return steamId.ToString(); }
        }
        public static ulong GetSteamID()
        {
            try
            {
                if (steamIdCached && (cachedSteamId != 0))
                {
                    return cachedSteamId;
                }
                if (!initialised)
                {
                    InitialiseSteamTypes();
                }
                if (!ReferenceEquals(getSteamIdMethod, null))
                {
                    object result = getSteamIdMethod.Invoke(null, null);
                    if (!ReferenceEquals(result, null))
                    {
                        try
                        {
                            if (result is ulong directULong)
                            {
                                cachedSteamId = directULong;
                                steamIdCached = true;
                                return directULong;
                            }
                            ulong steamId = Convert.ToUInt64(result);
                            cachedSteamId = steamId;
                            steamIdCached = true;
                            return steamId;
                        }
                        catch (Exception)
                        {
                            Type resultType = result.GetType();
                            var idField = resultType.GetField("m_SteamID") ??
                                         resultType.GetField("SteamID") ??
                                         resultType.GetField("steamID") ??
                                         resultType.GetField("value") ??
                                         resultType.GetField("Value");
                            if (!ReferenceEquals(idField, null))
                            {
                                object fieldValue = idField.GetValue(result);
                                if (!ReferenceEquals(fieldValue, null))
                                {
                                    ulong fieldSteamId = Convert.ToUInt64(fieldValue);
                                    cachedSteamId = fieldSteamId;
                                    steamIdCached = true;
                                    return fieldSteamId;
                                }
                            }
                            string stringValue = result.ToString();
                            if (ulong.TryParse(stringValue, out ulong parsedId))
                            {
                                cachedSteamId = parsedId;
                                steamIdCached = true;
                                return parsedId;
                            }
                            if (!steamIdCached)
                            {
                                Debug.LogWarning($"[ETGSteamP2P] Could not extract Steam ID from type {resultType.FullName}");
                            }
                        }
                    }
                }
                else
                {
                    if (!steamIdCached)
                    {
                        Debug.LogWarning("[ETGSteamP2P] GetSteamID method returned null");
                    }
                }
                return 0;
            }
            catch (Exception e)
            {
                if (!steamIdCached)
                {
                    Debug.LogError($"[ETGSteamP2P] Error getting Steam ID: {e.Message}");
                }
                return 0;
            }
        }
        public static bool TryDifferentSendSignatures(object steamIdParam, byte[] data, bool reliable)
        {
            try
            {
                if (ReferenceEquals(steamNetworkingType, null))
                    return false;
                var allMethods = steamNetworkingType.GetMethods(BindingFlags.Public | BindingFlags.Static);
                var sendMethods = new List<MethodInfo>();
                foreach (var method in allMethods)
                {
                    if (string.Equals(method.Name, "SendP2PPacket"))
                    {
                        sendMethods.Add(method);
                    }
                }
                if (workingSendSignatureIndex >= 0 && workingSendSignatureIndex < sendMethods.Count)
                {
                    if (TrySendWithSignature(sendMethods[workingSendSignatureIndex], steamIdParam, data, reliable))
                    {
                        return true;
                    }
                }
                for (int i = 0; i < sendMethods.Count; i++)
                {
                    if (i == workingSendSignatureIndex) continue;
                    if (TrySendWithSignature(sendMethods[i], steamIdParam, data, reliable))
                    {
                        workingSendSignatureIndex = i;
                        return true;
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ETGSteamP2P] Error trying different send signatures: {e.Message}");
                return false;
            }
        }
        private static bool TrySendWithSignature(MethodInfo method, object steamIdParam, byte[] data, bool reliable)
        {
            try
            {
                var parameters = method.GetParameters();
                object sendType = CreateP2PSendType(parameters, reliable);
                if (parameters.Length == 5)
                {
                    object result = method.Invoke(null, new object[] { steamIdParam, data, (uint)data.Length, sendType, 0 });
                    return result is bool success && success;
                }
                else if (parameters.Length == 4)
                {
                    object result = method.Invoke(null, new object[] { steamIdParam, data, (uint)data.Length, 0 });
                    return result is bool success && success;
                }
                else if (parameters.Length == 3)
                {
                    object result = method.Invoke(null, new object[] { steamIdParam, data, (uint)data.Length });
                    return result is bool success && success;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        private static object CreateP2PSendType(ParameterInfo[] parameters, bool reliable)
        {
            Type sendType = null;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType.Name == "EP2PSend")
                {
                    sendType = parameters[i].ParameterType;
                    break;
                }
            }
            int value = reliable ? 2 : 0;
            if (sendType != null && sendType.IsEnum)
            {
                return Enum.ToObject(sendType, value);
            }
            return value;
        }
        public static object ConvertToCSteamID(ulong steamId)
        {
            if (!initialised)
                InitialiseSteamTypes();
            if (cachedSteamworksAssembly == null)
                throw new InvalidOperationException("Steamworks assembly not found.");
            var cSteamIDType = cachedSteamworksAssembly.GetType("Steamworks.CSteamID", false);
            if (cSteamIDType == null)
                throw new InvalidOperationException("Steamworks.CSteamID type not found.");
            var constructor = cSteamIDType.GetConstructor(new Type[] { typeof(ulong) });
            if (constructor == null)
                throw new MissingMethodException("Steamworks.CSteamID has no constructor taking a ulong.");
            return constructor.Invoke(new object[] { steamId });
        }
        public static Assembly GetSteamworksAssembly()
        {
            if (ReferenceEquals(cachedSteamworksAssembly, null) && !initialised)
            {
                InitialiseSteamTypes();
            }
            return cachedSteamworksAssembly;
        }
        public static string GetFriendRichPresence(ulong friendSteamId, string key)
        {
            try
            {
                if (ReferenceEquals(getFriendRichPresenceMethod, null))
                {
                    Debug.LogWarning("[ETGSteamP2P] GetFriendRichPresence method not available");
                    return "";
                }
                if (string.IsNullOrEmpty(key))
                {
                    Debug.LogWarning("[ETGSteamP2P] Rich Presence key is null or empty");
                    return "";
                }
                var steamIdParam = ConvertToCSteamID(friendSteamId);
                if (ReferenceEquals(steamIdParam, null))
                {
                    Debug.LogWarning($"[ETGSteamP2P] Could not convert Steam ID {friendSteamId} to CSteamID");
                    return "";
                }
                var result = getFriendRichPresenceMethod.Invoke(null, new object[] { steamIdParam, key });
                return result?.ToString() ?? "";
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ETGSteamP2P] Error getting friend Rich Presence for key '{key}': {e.Message}");
                return "";
            }
        }
        public static object CreateCSteamID(ulong steamId)
        {
            return ConvertToCSteamID(steamId);
        }
        public static ulong GetLocalSteamId()
        {
            return GetCurrentUserSteamId();
        }
        private static Type p2pSessionConnectFailType;
        public static Type P2PSessionConnectFailType => p2pSessionConnectFailType;
        private static ulong GetCurrentUserSteamId()
        {
            if (steamIdCached && cachedSteamId != 0)
            {
                return cachedSteamId;
            }
            try
            {
                if (!ReferenceEquals(getSteamIdMethod, null))
                {
                    object result = getSteamIdMethod.Invoke(null, null);
                    if (!ReferenceEquals(result, null))
                    {
                        try
                        {
                            if (result is ulong directULong)
                            {
                                cachedSteamId = directULong;
                                steamIdCached = true;
                                return directULong;
                            }
                            ulong steamId = Convert.ToUInt64(result);
                            cachedSteamId = steamId;
                            steamIdCached = true;
                            return steamId;
                        }
                        catch (Exception)
                        {
                            Type resultType = result.GetType();
                            var idField = resultType.GetField("m_SteamID") ??
                                         resultType.GetField("SteamID") ??
                                         resultType.GetField("steamID") ??
                                         resultType.GetField("value") ??
                                         resultType.GetField("Value");
                            if (!ReferenceEquals(idField, null))
                            {
                                object fieldValue = idField.GetValue(result);
                                if (!ReferenceEquals(fieldValue, null))
                                {
                                    ulong fieldSteamId = Convert.ToUInt64(fieldValue);
                                    cachedSteamId = fieldSteamId;
                                    steamIdCached = true;
                                    return fieldSteamId;
                                }
                            }
                            string stringValue = result.ToString();
                            if (ulong.TryParse(stringValue, out ulong parsedId))
                            {
                                cachedSteamId = parsedId;
                                steamIdCached = true;
                                return parsedId;
                            }
                        }
                    }
                }
                return 0;
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                if (tie.InnerException != null)
                {
                    if (tie.InnerException is System.InvalidOperationException &&
                        tie.InnerException.Message.Contains("Steamworks is not initialized"))
                    {
                        throw tie.InnerException;
                    }

                    Debug.LogError($"[ETGSteamP2P] Error getting Steam ID: {tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
                }
                else
                {
                    Debug.LogError($"[ETGSteamP2P] Error getting Steam ID: TargetInvocationException with no inner exception");
                }
                return 0;
            }
            catch (System.InvalidOperationException)
            {
                throw;
            }
            catch (Exception e)
            {
                Debug.LogError($"[ETGSteamP2P] Error getting Steam ID: {e.GetType().Name}: {e.Message}");
                return 0;
            }
        }
        private static MethodInfo _allowP2PPacketRelayMethod;
        public static void AllowP2PPacketRelay(bool allow)
        {
            if (_allowP2PPacketRelayMethod != null)
            {
                try
                {
                    object result = _allowP2PPacketRelayMethod.Invoke(null, new object[] { allow });
                    Debug.Log($"[ETGSteamP2P] AllowP2PPacketRelay({allow}) returned {(result is bool b ? b.ToString() : "unknown")}");
                }
                catch (TargetInvocationException e)
                {
                    string innerMessage = e.InnerException != null ? e.InnerException.Message : e.Message;
                    Debug.LogWarning($"[ETGSteamP2P] AllowP2PPacketRelay skipped: {innerMessage}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[ETGSteamP2P] AllowP2PPacketRelay failed: {e.Message}");
                }
            }
            else
            {
                Debug.LogWarning("[ETGSteamP2P] AllowP2PPacketRelay method not found.");
            }
        }
    }
}
