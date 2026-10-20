using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace PawnOwnership
{
    public class ITab_Ownership : ITab
    {
        private static ITab_Ownership shared;

        /// <summary>
        /// 共享实例。单件选择与多选都复用它：<see cref="ITab.StillValid"/> 用 CurTabs.Contains(this)
        /// 做引用比较，因此必须始终返回同一个对象。
        /// </summary>
        public static ITab_Ownership GetShared()
        {
            if (shared == null) shared = new ITab_Ownership();
            return shared;
        }

        public ITab_Ownership()
        {
            this.labelKey = "所属";
            // 高度容纳：当前所属 / 更改所属 / 显示设置 / 调试设置（多选时多一行汇总）
            this.size = new Vector2(300f, 292f);
        }

        public override bool IsVisible
        {
            get
            {
                int numSelected = Find.Selector.NumSelected;
                if (numSelected > 1)
                {
                    // 多选：只要选中里存在支持设置归属的对象就显示
                    return OwnershipBulk.SupportedCount() > 0;
                }
                if (numSelected != 1) return false;

                return OwnershipBulk.IsSupported(SelThing);
            }
        }

        protected override void CloseTab() { }
        protected override bool StillValid => IsVisible;

        protected override void FillTab()
        {
            List<Thing> things = OwnershipBulk.SupportedThings(Find.Selector.SelectedObjects);
            if (things.Count == 0) return;

            Rect rect = new Rect(10f, 10f, this.size.x - 20f, this.size.y - 20f);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);

            if (things.Count == 1)
            {
                Thing thing = things[0];
                string currentOwner = OwnershipBulk.GetOwner(thing);
                listing.Label(string.IsNullOrEmpty(currentOwner) ? "当前所属：无" : $"当前所属：{currentOwner}");

                if (listing.ButtonText("更改所属"))
                {
                    Find.WindowStack.Add(new Dialog_SetOwner(things));
                }
            }
            else
            {
                // 多选：批量设置
                listing.Label($"已选中 {things.Count} 个可设置归属的对象");

                string common = OwnershipBulk.GetCommonOwner(things);
                listing.Label(string.IsNullOrEmpty(common) ? "当前归属：无" : $"当前归属：{common}");

                if (listing.ButtonText("批量更改所属"))
                {
                    Find.WindowStack.Add(new Dialog_SetOwner(things));
                }
            }

            DrawDisplaySettings(listing);
            DrawDebugSettings(listing);

            listing.End();
        }

        /// <summary>显示设置：归属颜色块开关（D-001 更新后唯一入口）。</summary>
        private void DrawDisplaySettings(Listing_Standard listing)
        {
            listing.Gap(10f);
            listing.Label("--- 显示设置 ---".Colorize(Color.yellow));

            bool showMarkers = Mod_PawnOwnership.ShowMarkers;
            listing.CheckboxLabeled(
                "显示归属颜色块",
                ref showMarkers,
                "关闭后不再在蓝图 / 框架 / 物品上绘制归属颜色块。检查信息里的「所属」与本页签不受影响。");

            if (showMarkers != Mod_PawnOwnership.ShowMarkers)
            {
                Mod_PawnOwnership.ShowMarkers = showMarkers;
                Mod_PawnOwnership.PersistSettings();
            }
        }

        /// <summary>调试设置：模组 Debug 跟随游戏开发者模式，或强制开/关。</summary>
        private void DrawDebugSettings(Listing_Standard listing)
        {
            listing.Gap(10f);
            listing.Label("--- 调试设置 ---".Colorize(Color.yellow));

            bool gameDebugMode = Prefs.DevMode;
            listing.Label($"游戏开发者模式: {(gameDebugMode ? "开启" : "关闭")}");

            bool currentDebugMode = MapComponent_PawnOwnership.DebugMode;
            bool isManual = MapComponent_PawnOwnership.DebugModeManual;

            string buttonText = !isManual
                ? $"模组 Debug: 跟随游戏 ({(currentDebugMode ? "开启" : "关闭")})"
                : $"模组 Debug: 强制 {(currentDebugMode ? "开启" : "关闭")}";

            if (listing.ButtonText(buttonText))
            {
                if (!isManual)
                {
                    MapComponent_PawnOwnership.DebugMode = true;
                }
                else if (currentDebugMode)
                {
                    MapComponent_PawnOwnership.DebugMode = false;
                }
                else
                {
                    MapComponent_PawnOwnership.ResetDebugMode();
                }
            }

            listing.Label((isManual ? "(手动设置)" : "(跟随游戏开发者模式)").Colorize(Color.gray));
        }
    }
}
