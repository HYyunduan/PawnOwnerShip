using System;
using System.Collections.Generic;
using Multiplayer.API;
using Verse;

namespace PawnOwnership
{
    /// <summary>
    /// 批量设置归属的共用逻辑。
    /// UI（「所属」页签 / 更改归属对话框）与自动化测试都走这里，避免两套实现漂移。
    /// </summary>
    public static class OwnershipBulk
    {
        /// <summary>
        /// 该对象是否支持查看/设置归属：
        /// Pawn（殖民者、动物等）与物品（<see cref="ThingCategory.Item"/>）。
        /// 与「所属」页签的可见性规则保持一致。
        /// </summary>
        public static bool IsSupported(object obj)
        {
            if (!(obj is Thing thing) || thing.def == null) return false;
            if (thing is Pawn) return true;
            return thing.def.category == ThingCategory.Item;
        }

        /// <summary>从任意对象集合中筛出支持设置归属的 Thing，保持原顺序。</summary>
        public static List<Thing> SupportedThings(IEnumerable<object> objects)
        {
            List<Thing> result = new List<Thing>();
            if (objects == null) return result;

            foreach (object o in objects)
            {
                if (IsSupported(o)) result.Add((Thing)o);
            }
            return result;
        }

        /// <summary>当前选择中支持设置归属的对象数量。</summary>
        public static int SupportedCount()
        {
            return SupportedThings(Find.Selector.SelectedObjects).Count;
        }

        /// <summary>读取单个对象的归属（Pawn 走世界组件，其余走地图组件）。</summary>
        public static string GetOwner(Thing thing)
        {
            if (thing == null) return null;
            if (thing is Pawn pawn) return pawn.GetOwner();

            Map map = thing.Map;
            if (map == null) return null;
            return map.GetComponent<MapComponent_PawnOwnership>()?.GetOwner(thing);
        }

        /// <summary>
        /// 多个对象的共同归属：全部一致时返回该值（可能为 null），否则返回 "多个"。
        /// </summary>
        public static string GetCommonOwner(IList<Thing> things)
        {
            if (things == null || things.Count == 0) return null;

            string first = GetOwner(things[0]);
            for (int i = 1; i < things.Count; i++)
            {
                if (!string.Equals(first, GetOwner(things[i]), StringComparison.Ordinal)) return "多个";
            }
            return first;
        }

        /// <summary>
        /// 批量设置归属；<paramref name="ownerName"/> 为 null 表示清除归属。
        /// 返回成功处理的个数。
        ///
        /// 联机时走 SyncMethod（保证同步到其他客户端）；单机时直接写本地数据，
        /// 避免依赖 Multiplayer 的同步包装在非联机会话下的行为。
        /// </summary>
        public static int Apply(IList<Thing> things, string ownerName)
        {
            if (things == null || things.Count == 0) return 0;

            bool inMp = MP.enabled && MP.IsInMultiplayer;
            int ok = 0;

            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing == null) continue;

                try
                {
                    Map map = thing.Map ?? Find.CurrentMap;
                    if (map == null) continue;

                    if (inMp)
                    {
                        if (thing is Pawn)
                        {
                            MapComponent_PawnOwnership.SyncSetPawnOwner(map.uniqueID, thing.ThingID, ownerName);
                        }
                        else
                        {
                            MapComponent_PawnOwnership.SyncSetThingOwner(map.uniqueID, thing.ThingID, ownerName);
                        }
                    }
                    else
                    {
                        MapComponent_PawnOwnership comp = map.GetComponent<MapComponent_PawnOwnership>();
                        if (comp == null) continue;

                        if (thing is Pawn pawn)
                        {
                            pawn.SetOwner(ownerName);
                            comp.SetOwner(pawn, ownerName);
                        }
                        else
                        {
                            comp.SetOwner(thing, ownerName);
                        }
                    }
                    ok++;
                }
                catch (Exception ex)
                {
                    Log.Warning("[PawnOwnership] 批量设置归属失败: " + thing.ThingID + " - " + ex.Message);
                }
            }

            MapComponent_PawnOwnership.DebugLog("[PawnOwnership] 批量设置归属: " + ok + "/" + things.Count
                                                + " -> " + (ownerName ?? "(清除)"));
            return ok;
        }
    }
}
