using Verse;

namespace PawnOwnership
{
    /// <summary>
    /// Mod 设置：只保存本地显示偏好。
    /// 不写入存档，也不参与 Multiplayer 同步（见 docs/DECISIONS.md D-001）。
    /// </summary>
    public class PawnOwnershipModSettings : ModSettings
    {
        /// <summary>是否在屏幕上绘制归属颜色块。默认开启，保持原有行为。</summary>
        public bool showMarkers = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref showMarkers, "showMarkers", true);
        }
    }
}
