using RimWorld;
using UnityEngine;
using Verse;
using Multiplayer.API;

namespace PawnOwnership
{
    public class ITab_Ownership : ITab
    {
        public ITab_Ownership()
        {
            this.labelKey = "所属";
            this.size = new Vector2(300f, 200f);
        }

        public override bool IsVisible
        {
            get
            {
                if (SelThing == null) return false;

                // 殖民者/奴隶
                if (SelThing is Pawn pawn && (pawn.IsColonist || pawn.IsSlave))
                    return true;

                // 物品
                if (SelThing.def.category == ThingCategory.Item)
                    return true;

                // 动物（非殖民者/奴隶的 Pawn）
                if (SelThing is Pawn animal && !animal.IsColonist && !animal.IsSlave)
                    return true;

                return false;
            }
        }

        protected override void CloseTab() { }
        protected override bool StillValid => IsVisible;

        private string GetOwner(Thing thing)
        {
            // Pawn 走世界组件
            if (thing is Pawn pawn)
                return pawn.GetOwner();

            // Thing 走地图组件
            return thing.Map?.GetComponent<MapComponent_PawnOwnership>()?.GetOwner(thing);
        }

        protected override void FillTab()
        {
            Thing thing = SelThing;
            if (thing == null) return;

            Rect rect = new Rect(10f, 10f, this.size.x - 20f, this.size.y - 20f);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);

            // 显示当前所属
            string currentOwner = GetOwner(thing);
            if (string.IsNullOrEmpty(currentOwner))
            {
                listing.Label("当前所属：无");
            }
            else
            {
                listing.Label($"当前所属：{currentOwner}");
            }

            if (listing.ButtonText("更改所属"))
            {
                Find.WindowStack.Add(new Dialog_SetOwner(thing));
            }

            // Debug 模式开关
            listing.Gap(10f);
            listing.Label("--- 调试设置 ---".Colorize(Color.yellow));

            bool gameDebugMode = Prefs.DevMode;
            listing.Label($"游戏开发者模式: {(gameDebugMode ? "开启" : "关闭")}");

            bool currentDebugMode = MapComponent_PawnOwnership.DebugMode;
            bool isManual = MapComponent_PawnOwnership.DebugModeManual;

            string buttonText;
            if (!isManual)
            {
                buttonText = $"模组 Debug: 跟随游戏 ({(currentDebugMode ? "开启" : "关闭")})";
            }
            else
            {
                buttonText = $"模组 Debug: 强制 {(currentDebugMode ? "开启" : "关闭")}";
            }

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

            string statusText = !isManual ? "(跟随游戏开发者模式)" : "(手动设置)";
            listing.Label(statusText.Colorize(Color.gray));

            listing.End();
        }
    }
}
