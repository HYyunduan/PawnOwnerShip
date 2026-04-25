using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Multiplayer.API;

namespace PawnOwnership
{
    public class Dialog_SetOwner : Window
    {
        private Pawn pawn;
        private List<IPlayerInfo> players;
        private Vector2 scrollPosition;
        private float scrollViewHeight;

        public Dialog_SetOwner(Pawn pawn)
        {
            this.pawn = pawn;
            
            // 获取在线玩家列表
            if (MP.enabled && MP.IsInMultiplayer)
            {
                this.players = MP.GetPlayers()?.ToList() ?? new List<IPlayerInfo>();
            }
            else
            {
                // 单人模式，显示默认玩家
                this.players = new List<IPlayerInfo>();
            }

            this.forcePause = true;
            this.closeOnAccept = false;
            this.doCloseButton = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = false;
        }

        public override Vector2 InitialSize => new Vector2(400f, 450f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label($"设置角色 <b>{pawn.LabelShortCap}</b> 的归属玩家：");
            listing.Gap(12f);

            string currentOwner = pawn.GetOwner() ?? "无";
            listing.Label($"当前归属: {currentOwner}");
            listing.Gap(12f);

            // 显示玩家列表
            if (players.Count > 0)
            {
                listing.Label("选择玩家:");
                listing.Gap(6f);

                // 计算滚动区域
                Rect scrollRect = listing.GetRect(inRect.height - listing.CurHeight - 80f);
                Rect viewRect = new Rect(0f, 0f, scrollRect.width - 16f, scrollViewHeight);

                Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);
                {
                    float curY = 0f;
                    foreach (var player in players)
                    {
                        string playerName = player.Username;
                        Rect rowRect = new Rect(0f, curY, viewRect.width, 30f);
                        
                        // 高亮当前归属玩家
                        if (playerName == currentOwner)
                        {
                            Widgets.DrawHighlight(rowRect);
                        }
                        
                        if (Widgets.ButtonText(rowRect, playerName))
                        {
                            SetOwner(playerName);
                        }
                        
                        curY += 32f;
                    }
                    scrollViewHeight = curY;
                }
                Widgets.EndScrollView();
            }
            else
            {
                // 单人模式或无玩家
                if (!MP.enabled || !MP.IsInMultiplayer)
                {
                    listing.Label("单人模式");
                    listing.Gap(6f);
                    
                    if (listing.ButtonText("设置为 Player1"))
                    {
                        SetOwner("Player1");
                    }
                }
                else
                {
                    listing.Label("未检测到在线玩家");
                }
            }

            listing.Gap(12f);

            if (listing.ButtonText("清除归属"))
            {
                SetOwner(null);
            }

            listing.Gap(12f);

            // 测试按钮：验证 Multiplayer 广播
            if (listing.ButtonText("[测试] 广播 SyncSetMineOwner"))
            {
                TestSyncUI.Open();
            }

            listing.End();
        }

        private void SetOwner(string ownerName)
        {
            MapComponent_PawnOwnership.SyncSetPawnOwner(Find.CurrentMap.uniqueID, pawn.thingIDNumber, ownerName);
            this.Close();
        }
    }
}