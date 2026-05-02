using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using Multiplayer.API;
using UnityEngine;
using RimWorld;

namespace PawnOwnership
{
    public static class OwnershipContext
    {
        [System.ThreadStatic]
        public static Verse.Pawn CurrentWorker;
    }

    public class MapComponent_PawnOwnership : MapComponent
    {
        // ==========================================
        // Debug 模式控制
        // ==========================================
        private static bool? _debugModeOverride = null; // null = 跟随游戏, true = 强制开启, false = 强制关闭
        
        public static bool DebugModeManual => _debugModeOverride.HasValue;
        
        public static bool DebugMode
        {
            get => _debugModeOverride ?? Prefs.DevMode;
            set => _debugModeOverride = value;
        }
        
        public static void ResetDebugMode()
        {
            _debugModeOverride = null;
        }
        
        public static void DebugLog(string message)
        {
            if (DebugMode)
            {
                Log.Message(message);
            }
        }
        
        // ==========================================
        // 归属字典（简化版，不区分类型）
        // ==========================================
        
        // Thing 归属：key = ThingID (def.defName + thingIDNumber)
        private Dictionary<string, string> thingOwnership = new Dictionary<string, string>();
        
        // Cell 归属：key = "x_z"
        private Dictionary<string, string> cellOwnership = new Dictionary<string, string>();
        
        // Zone 归属：key = zone.ID
        private Dictionary<int, string> zoneOwnership = new Dictionary<int, string>();
        
        // ==========================================
        // Thing 归属
        // ==========================================
        
        /// <summary>
        /// 判断 Thing 是否应该追踪归属（白名单）
        /// </summary>
        public static bool ShouldTrackOwnership(Thing thing)
        {
            return true;
            // if (thing == null || thing.def == null) return false;
            
            // // Item：物品、资源、装备等
            // if (thing.def.category == ThingCategory.Item) return true;
            
            // // Building：建筑
            // if (thing is Building) return true;
            
            // // Blueprint：蓝图
            // if (thing is Blueprint) return true;
            
            // // Frame：建造框架
            // if (thing is Frame) return true;
            
            // // Corpse：尸体
            // if (thing is Corpse) return true;

            // // Plant：植物
            // if (thing is Plant) return true;
            
            // return false;
        }
        
        public void SetOwner(Thing thing, string playerId)
        {
            if (thing == null) return;
            if (!ShouldTrackOwnership(thing)){
                // DebugLog($"[PawnOwnerShip] SetOwner filtered:{thing.ThingID} -> {playerId}");
                Log.Message($"[PawnOwnerShip] SetOwner filtered:{thing.ThingID} -> {playerId}");

                return;
            }  
            thingOwnership[thing.ThingID] = playerId;
            // DebugLog($"[PawnOwnership] SetOwner: {thing.ThingID} -> {playerId}");
            Log.Message($"[PawnOwnerShip] SetOwner filtered:{thing.ThingID} -> {playerId}");
        }
        
        public void SetOwner(string thingID, string playerId)
        {
            thingOwnership[thingID] = playerId;
            DebugLog($"[PawnOwnership] SetOwner: {thingID} -> {playerId}");
        }
        
        public string GetOwner(Thing thing)
        {
            if (thing == null) return null;
            return thingOwnership.TryGetValue(thing.ThingID, out string owner) ? owner : null;
        }
        
        public string GetOwner(string thingID)
        {
            return thingOwnership.TryGetValue(thingID, out string owner) ? owner : null;
        }
        
        public void RemoveOwner(string thingID)
        {
            thingOwnership.Remove(thingID);
        }
        
        // ==========================================
        // Cell 归属
        // ==========================================
        
        public void SetOwnerCell(int x, int z, string playerId)
        {
            cellOwnership[$"{x}_{z}"] = playerId;
            DebugLog($"[PawnOwnership] SetOwnerCell: Cell_{x}_{z} -> {playerId}");
        }
        
        public string GetOwnerCell(int x, int z)
        {
            return cellOwnership.TryGetValue($"{x}_{z}", out string owner) ? owner : null;
        }
        
        public void RemoveOwnerCell(int x, int z)
        {
            cellOwnership.Remove($"{x}_{z}");
        }
        
        // ==========================================
        // Zone 归属
        // ==========================================
        
        public void SetOwnerZone(int zoneId, string playerId)
        {
            zoneOwnership[zoneId] = playerId;
            DebugLog($"[PawnOwnership] SetOwnerZone: Zone_{zoneId} -> {playerId}");
        }
        
        public string GetOwnerZone(int zoneId)
        {
            return zoneOwnership.TryGetValue(zoneId, out string owner) ? owner : null;
        }
        
        public void RemoveOwnerZone(int zoneId)
        {
            zoneOwnership.Remove(zoneId);
        }
        
        // ==========================================
        // 暂存归属：用于 Blueprint/Frame 过渡
        // ==========================================
        private static Dictionary<int, string> pendingOwnershipMap = new Dictionary<int, string>();
        
        public static void SavePendingOwnership(IntVec3 position, string owner)
        {
            if (string.IsNullOrEmpty(owner)) return;
            pendingOwnershipMap[position.GetHashCode()] = owner;
            DebugLog($"[PawnOwnership] SavePendingOwnership: pos={position}, owner={owner}");
        }
        
        public static string GetAndClearPendingOwnership(IntVec3 position)
        {
            int key = position.GetHashCode();
            if (pendingOwnershipMap.TryGetValue(key, out string owner))
            {
                pendingOwnershipMap.Remove(key);
                DebugLog($"[PawnOwnership] GetAndClearPendingOwnership: pos={position}, owner={owner}");
                return owner;
            }
            return null;
        }
        
        // ==========================================
        // 延迟同步队列
        // =========================================
        private static Queue<DelayedSyncMessage> syncQueue = new Queue<DelayedSyncMessage>();
        
        public struct DelayedSyncMessage
        {
            public string type;      // "Thing", "Cell", "Zone"
            public int mapId;
            public string thingID;   // ThingID 字符串（仅 Thing 类型使用）
            public int zoneId;       // Zone ID（仅 Zone 类型使用）
            public int cellX;        // Cell X 坐标（仅 Cell 类型使用）
            public int cellZ;        // Cell Z 坐标（仅 Cell 类型使用）
            public string playerName;
        }
        
        /// <summary>
        /// 添加延迟同步消息到队列（Thing 版）
        /// </summary>
        public static void QueueSyncMessageThing(int mapId, Thing thing, string playerName)
        {
            syncQueue.Enqueue(new DelayedSyncMessage
            {
                type = "Thing",
                mapId = mapId,
                thingID = thing.ThingID,
                playerName = playerName
            });
            DebugLog($"[PawnOwnership] Queued sync: {thing.ThingID} -> {playerName}");
        }
        
        /// <summary>
        /// 添加延迟同步消息到队列（Cell 版）
        /// </summary>
        public static void QueueSyncMessageCell(int mapId, int cellX, int cellZ, string playerName)
        {
            syncQueue.Enqueue(new DelayedSyncMessage
            {
                type = "Cell",
                mapId = mapId,
                cellX = cellX,
                cellZ = cellZ,
                playerName = playerName
            });
            DebugLog($"[PawnOwnership] Queued sync: Cell_{cellX}_{cellZ} -> {playerName}");
        }
        
        /// <summary>
        /// 添加延迟同步消息到队列（Zone 版）
        /// </summary>
        public static void QueueSyncMessageZone(int mapId, int zoneId, string playerName)
        {
            syncQueue.Enqueue(new DelayedSyncMessage
            {
                type = "Zone",
                mapId = mapId,
                zoneId = zoneId,
                playerName = playerName
            });
            DebugLog($"[PawnOwnership] Queued sync: Zone_{zoneId} -> {playerName}");
        }
        
        /// <summary>
        /// 处理延迟同步队列（由 Tick 调用）
        /// </summary>
        public static void ProcessSyncQueue()
        {
            while (syncQueue.Count > 0)
            {
                var msg = syncQueue.Dequeue();
                
                Map targetMap = Find.Maps.FirstOrDefault(m => m.uniqueID == msg.mapId);
                if (targetMap == null) continue;
                
                var comp = targetMap.GetComponent<MapComponent_PawnOwnership>();
                if (comp == null) continue;
                
                switch (msg.type)
                {
                    case "Thing":
                        comp.SyncSetOwner(msg.thingID, msg.playerName);
                        DebugLog($"[PawnOwnership] Processed sync: {msg.thingID} -> {msg.playerName}");
                        break;
                    case "Cell":
                        comp.SyncSetOwnerCell(msg.cellX, msg.cellZ, msg.playerName);
                        DebugLog($"[PawnOwnership] Processed sync: Cell_{msg.cellX}_{msg.cellZ} -> {msg.playerName}");
                        break;
                    case "Zone":
                        comp.SyncSetOwnerZone(msg.zoneId, msg.playerName);
                        DebugLog($"[PawnOwnership] Processed sync: Zone_{msg.zoneId} -> {msg.playerName}");
                        break;
                }
            }
        }
        
        public MapComponent_PawnOwnership(Map map) : base(map) { }

        public static string GetCurrentPlayer()
        {
            if (MP.enabled && MP.IsInMultiplayer)
            {
                return MP.PlayerName?.ToString() ?? "Host";
            }
            return "Player1";
        }

        public static bool ShouldProcessOwnership()
        {
            if (!MP.enabled || !MP.IsInMultiplayer) return true;
            return MP.IsExecutingSyncCommandIssuedBySelf;
        }

        // ==========================================
        // Multiplayer 同步方法
        // ==========================================
        
        [SyncMethod]
        public void SyncSetOwner(string thingID, string playerName)
        {
            SetOwner(thingID, playerName);
        }
        
        [SyncMethod]
        public void SyncSetOwnerCell(int x, int z, string playerName)
        {
            SetOwnerCell(x, z, playerName);
        }
        
        [SyncMethod]
        public void SyncSetOwnerZone(int zoneId, string playerName)
        {
            SetOwnerZone(zoneId, playerName);
        }

        // ==========================================
        // Pawn 归属同步（用于 UI 设置）
        // ==========================================
        
        public static void SyncSetThingOwner(int mapId, string thingID, string ownerName)
        {
            Map targetMap = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
            if (targetMap == null) return;
            var comp = targetMap.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            comp.SyncSetOwner(thingID, ownerName);
            DebugLog($"[PawnOwnership] SyncSetThingOwner: {thingID} -> {ownerName}");
        }

        [SyncMethod]
        public static void SyncSetPawnOwner(int mapId, string pawnThingID, string ownerName)
        {
            Map targetMap = Find.Maps.FirstOrDefault(m => m.uniqueID == mapId);
            if (targetMap == null) return;
            Pawn pawn = targetMap.mapPawns.AllPawns.FirstOrDefault(p => p.ThingID == pawnThingID);
            if (pawn == null) return;
            pawn.SetOwner(ownerName);
            var comp = targetMap.GetComponent<MapComponent_PawnOwnership>();
            if (comp == null) return;
            comp.SetOwner(pawn, ownerName);
            DebugLog($"[PawnOwnership] SyncSetPawnOwner: {pawnThingID} -> {ownerName}");
        }

        // ==========================================
        // 统一绘制归属标志
        // ==========================================
        
        private static int lastColorRefreshFrame = -1;

        public override void MapComponentDraw()
        {
            DrawOwnershipMarkers();
        }

        public void DrawOwnershipMarkers()
        {
            // 每 60 帧刷新一次玩家颜色缓存
            if (Time.frameCount - lastColorRefreshFrame >= 60)
            {
                lastColorRefreshFrame = Time.frameCount;
                RefreshPlayerColors();
            }
            // 绘制 Blueprint 归属
            foreach (var blueprint in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
            {
                string owner = GetOwner(blueprint);
                if (!string.IsNullOrEmpty(owner))
                {
                    DrawMarkerAt(blueprint.DrawPos, owner);
                }
            }
            
            // 绘制 Frame 归属
            foreach (var frame in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
            {
                string owner = GetOwner(frame);
                if (!string.IsNullOrEmpty(owner))
                {
                    DrawMarkerAt(frame.DrawPos, owner);
                }
            }
            
            // 绘制可拾取物品归属
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                string owner = GetOwner(thing);
                if (!string.IsNullOrEmpty(owner))
                {
                    DrawMarkerAt(thing.DrawPos, owner);
                }
            }
            
            // 绘制 Designation 归属
            var desManager = map.designationManager;
            if (desManager != null)
            {
                foreach (var des in desManager.AllDesignations)
                {
                    if (!des.target.HasThing)
                    {
                        // // Cell target - 从 Cell 找 Thing
                        // IntVec3 cell = des.target.Cell;
                        // Thing mineableThing = null;
                        // foreach (var t in map.thingGrid.ThingsAt(cell))
                        // {
                        //     if (t.def.mineable) { mineableThing = t; break; }
                        // }
                        
                        // if (mineableThing != null)
                        // {
                        //     string owner = GetOwner(mineableThing.thingIDNumber);
                        //     if (!string.IsNullOrEmpty(owner))
                        //     {
                        //         Vector3 pos = des.DrawLoc();
                        //         pos.x += 0.5f;
                        //         DrawMarkerAt(pos, owner);
                        //     }
                        // }
                        IntVec3 cell = des.target.Cell;
                        string owner = GetOwnerCell(cell.x, cell.z);  // 直接用 Cell 查
                        if (!string.IsNullOrEmpty(owner))
                        {
                            DrawMarkerAt(des.DrawLoc(), owner);
                        }
                    }
                    else
                    {
                        // Thing target
                        Thing targetThing = des.target.Thing;
                        if (targetThing == null) continue;
                        
                        string owner = GetOwner(targetThing);
                        if (!string.IsNullOrEmpty(owner))
                        {
                            Vector3 pos = des.DrawLoc();
                            pos.x += 0.5f;
                            DrawMarkerAt(pos, owner);
                        }
                    }
                }
            }
        }
        
        private void DrawMarkerAt(Vector3 pos, string owner)
        {
            Color color = GetPlayerColor(owner);
            pos.y = AltitudeLayer.MetaOverlays.AltitudeFor() + 0.1f;
            
            float size = 0.3f;
            Material mat = SolidColorMaterials.SimpleSolidColorMaterial(new Color(color.r, color.g, color.b, 0.8f));
            
            Graphics.DrawMesh(
                MeshPool.plane10,
                Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(size, 1f, size)),
                mat,
                AltitudeLayer.MetaOverlays.AltitudeFor().GetHashCode()
            );
        }
        
        // ==========================================
        // 玩家颜色缓存（同步 Multiplayer 颜色）
        // ==========================================
        private static Dictionary<string, Color> playerColorCache = new Dictionary<string, Color>();
        private static bool colorCacheInitialized = false;

        /// <summary>
        /// 从 Multiplayer 的 PlayerInfo 实例读取颜色，刷新缓存
        /// </summary>
        public static void RefreshPlayerColors()
        {
            playerColorCache.Clear();

            if (!MP.enabled || !MP.IsInMultiplayer)
            {
                colorCacheInitialized = true;
                return;
            }

            try
            {
                var players = MP.GetPlayers();
                if (players == null) return;

                // 反射获取 PlayerInfo 类的 color 字段
                FieldInfo colorField = null;

                foreach (var player in players)
                {
                    if (player == null) continue;

                    string name = player.Username;
                    if (string.IsNullOrEmpty(name)) continue;

                    // 延迟获取 colorField（只做一次）
                    if (colorField == null)
                    {
                        colorField = player.GetType().GetField("color",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (colorField == null)
                        {
                            DebugLog("[PawnOwnership] PlayerInfo.color 字段不存在，使用 fallback 颜色");
                            break;
                        }
                    }

                    object value = colorField.GetValue(player);
                    if (value is Color c)
                    {
                        playerColorCache[name] = c;
                        DebugLog($"[PawnOwnership] 玩家颜色: {name} -> R:{c.r:F2} G:{c.g:F2} B:{c.b:F2}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.Warning($"[PawnOwnership] RefreshPlayerColors 失败: {ex.Message}");
            }

            colorCacheInitialized = true;
        }

        private Color GetPlayerColor(string playerName)
        {
            // 多人模式：从缓存取 Multiplayer 颜色
            if (colorCacheInitialized && playerColorCache.TryGetValue(playerName, out Color mpColor))
                return mpColor;

            // fallback：hash 生成（单人模式 / 缓存未初始化 / 玩家不在缓存中）
            int hash = playerName.GetHashCode();
            float r = 0.3f + (hash & 0xFF) / 255f * 0.7f;
            float g = 0.3f + ((hash >> 8) & 0xFF) / 255f * 0.7f;
            float b = 0.3f + ((hash >> 16) & 0xFF) / 255f * 0.7f;
            return new Color(r, g, b, 1f);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref thingOwnership, "thingOwnership", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref cellOwnership, "cellOwnership", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref zoneOwnership, "zoneOwnership", LookMode.Value, LookMode.Value);
            
            if (thingOwnership == null) thingOwnership = new Dictionary<string, string>();
            if (cellOwnership == null) cellOwnership = new Dictionary<string, string>();
            if (zoneOwnership == null) zoneOwnership = new Dictionary<int, string>();
        }
    }
}
