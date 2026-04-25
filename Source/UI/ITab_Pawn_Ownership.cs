using RimWorld;
using UnityEngine;
using Verse;

namespace PawnOwnership
{
    public class ITab_Pawn_Ownership : ITab
    {
        public ITab_Pawn_Ownership()
        {
            this.labelKey = "所有权";
            this.size = new Vector2(300f, 200f);
        }

        public override bool IsVisible
        {
            get
            {
                if (SelPawn == null) return false;
                return SelPawn.IsColonist || SelPawn.IsSlave;
            }
        }

        protected override void CloseTab() { }
        protected override bool StillValid => IsVisible;

        protected override void FillTab()
        {
            Pawn pawn = SelPawn;
            if (pawn == null) return;

            Rect rect = new Rect(10f, 10f, this.size.x - 20f, this.size.y - 20f);
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);

            // 显示当前角色归属
            string currentOwner = pawn.GetOwner();
            if (string.IsNullOrEmpty(currentOwner))
            {
                listing.Label("当前角色归属：无");
            }
            else
            {
                listing.Label($"当前角色归属：{currentOwner}");
            }

            if (listing.ButtonText("更改角色归属"))
            {
                Find.WindowStack.Add(new Dialog_SetOwner(pawn));
            }

            // 显示当前玩家信息（只读）
            listing.Gap(10f);
            string currentPlayer = MapComponent_PawnOwnership.GetCurrentPlayer();
            listing.Label($"当前玩家：{currentPlayer}".Colorize(Color.cyan));

            // Debug 模式开关
            listing.Gap(10f);
            listing.Label("--- 调试设置 ---".Colorize(Color.yellow));
            
            // 显示游戏 debug 模式状态
            bool gameDebugMode = Prefs.DevMode;
            listing.Label($"游戏开发者模式: {(gameDebugMode ? "开启" : "关闭")}");
            
            // 显示当前 debug 状态
            bool currentDebugMode = MapComponent_PawnOwnership.DebugMode;
            bool isManual = MapComponent_PawnOwnership.DebugModeManual;
            
            // 三态切换按钮
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
                    // 跟随游戏 -> 强制开启
                    MapComponent_PawnOwnership.DebugMode = true;
                }
                else if (currentDebugMode)
                {
                    // 强制开启 -> 强制关闭
                    MapComponent_PawnOwnership.DebugMode = false;
                }
                else
                {
                    // 强制关闭 -> 跟随游戏
                    MapComponent_PawnOwnership.ResetDebugMode();
                }
            }
            
            // 显示状态说明
            string statusText = !isManual ? "(跟随游戏开发者模式)" : "(手动设置)";
            listing.Label(statusText.Colorize(Color.gray));

            listing.End();
        }
    }
}