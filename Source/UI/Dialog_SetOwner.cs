using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Multiplayer.API;

namespace PawnOwnership
{
    /// <summary>
    /// 更改归属对话框。支持单个对象与多选批量设置。
    /// </summary>
    public class Dialog_SetOwner : Window
    {
        private readonly List<Thing> things;
        private List<IPlayerInfo> players;
        private Vector2 scrollPosition;
        private float scrollViewHeight;

        public Dialog_SetOwner(Thing thing) : this(new List<Thing> { thing })
        {
        }

        public Dialog_SetOwner(List<Thing> things)
        {
            this.things = things ?? new List<Thing>();

            // 获取在线玩家列表
            if (MP.enabled && MP.IsInMultiplayer)
            {
                this.players = MP.GetPlayers()?.ToList() ?? new List<IPlayerInfo>();
            }
            else
            {
                this.players = new List<IPlayerInfo>();
            }

            this.forcePause = true;
            this.closeOnAccept = false;
            this.doCloseButton = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = false;
        }

        public override Vector2 InitialSize => new Vector2(400f, 450f);

        private bool IsBatch => things.Count > 1;

        private string GetCurrentOwner()
        {
            string common = OwnershipBulk.GetCommonOwner(things);
            return string.IsNullOrEmpty(common) ? "无" : common;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            if (IsBatch)
            {
                listing.Label($"批量设置 <b>{things.Count}</b> 个对象的归属玩家：");
            }
            else
            {
                string name = things.Count == 1 ? things[0].LabelShortCap : "对象";
                listing.Label($"设置 <b>{name}</b> 的归属玩家：");
            }
            listing.Gap(12f);

            string currentOwner = GetCurrentOwner();
            listing.Label($"当前归属: {currentOwner}");
            listing.Gap(12f);

            // 显示玩家列表
            if (players.Count > 0)
            {
                listing.Label("选择玩家:");
                listing.Gap(6f);

                Rect scrollRect = listing.GetRect(inRect.height - listing.CurHeight - 80f);
                Rect viewRect = new Rect(0f, 0f, scrollRect.width - 16f, scrollViewHeight);

                Widgets.BeginScrollView(scrollRect, ref scrollPosition, viewRect);
                {
                    float curY = 0f;
                    foreach (var player in players)
                    {
                        string playerName = player.Username;
                        Rect rowRect = new Rect(0f, curY, viewRect.width, 30f);

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

            listing.End();
        }

        private void SetOwner(string ownerName)
        {
            int applied = OwnershipBulk.Apply(things, ownerName);
            Log.Message("[PawnOwnership] 设置归属: " + applied + "/" + things.Count
                        + " 个对象 -> " + (ownerName ?? "(清除)"));
            this.Close();
        }
    }
}
