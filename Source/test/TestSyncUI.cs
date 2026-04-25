using Verse;
using UnityEngine;
using Multiplayer.API;

namespace PawnOwnership
{
    /// <summary>
    /// 测试 UI：验证 Multiplayer SyncMethod 广播是否生效
    /// 打开方式：按 F8 键
    /// </summary>
    public class TestSyncUI : Window
    {
        public override Vector2 InitialSize => new Vector2(400f, 200f);

        public TestSyncUI()
        {
            this.forcePause = false;
            this.doCloseX = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            
            float y = 0f;
            Widgets.Label(new Rect(0, y, inRect.width, 30f), $"当前玩家: {MapComponent_PawnOwnership.GetCurrentPlayer()}");
            y += 30f;
            Widgets.Label(new Rect(0, y, inRect.width, 30f), $"IsInMultiplayer: {MP.IsInMultiplayer}");
            y += 30f;
            Widgets.Label(new Rect(0, y, inRect.width, 30f), $"IsExecutingSyncCommandIssuedBySelf: {MP.IsExecutingSyncCommandIssuedBySelf}");
            y += 40f;
            
            if (Widgets.ButtonText(new Rect(0, y, inRect.width, 40f), "测试广播 SyncSetOwner"))
            {
                TestBroadcast();
            }
        }

        private void TestBroadcast()
        {
            if (MP.enabled && MP.IsInMultiplayer)
            {
                Log.Message($"[TestSyncUI] 点击按钮，当前玩家: {MapComponent_PawnOwnership.GetCurrentPlayer()}");
                
                Map map = Find.CurrentMap;
                var comp = map.GetComponent<MapComponent_PawnOwnership>();
                
                string playerName = MapComponent_PawnOwnership.GetCurrentPlayer();
                int testThingId = 99999;
                
                comp.SyncSetOwner(testThingId, playerName);
                
                Log.Message($"[TestSyncUI] 已调用 SyncSetOwner({testThingId}, {playerName})");
            }
            else
            {
                Log.Message("[TestSyncUI] 不在 Multiplayer 模式，无法测试广播");
            }
            
            this.Close();
        }

        public static void Open()
        {
            Find.WindowStack.Add(new TestSyncUI());
        }
    }
}
