using HarmonyLib;
using UnityEngine;
using Verse;
using Multiplayer.API;
using PawnOwnership.Patches;
using PawnOwnership.Testing;

namespace PawnOwnership
{
    public class Mod_PawnOwnership : Mod
    {
        /// <summary>
        /// 本地显示设置。写入 Config/ModSettings.xml，不进存档、不参与 Multiplayer 同步（D-001）。
        /// </summary>
        public static PawnOwnershipModSettings Settings { get; private set; }

        public Mod_PawnOwnership(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PawnOwnershipModSettings>();

            var harmony = new Harmony("omas.PawnOwnership");
            
            // 先注册静态 patch（通过 attribute 的）
            harmony.PatchAll();
            
            // 再注册动态 patch
            HarmonyDynamicPatches.RegisterDynamicPatches(harmony);

            // 启动自检（仅在带 -po.selftest-report=<path> 启动时生效）
            SelfTest.RunIfRequested(harmony);

            Log.Message("[PawnOwnership] Mod 初始化成功！");
        }

        /// <summary>
        /// 归属颜色块的显示开关。设置尚未就绪时按默认值 true 处理，保持原有行为。
        /// </summary>
        public static bool ShowMarkers
        {
            get { return Settings == null || Settings.showMarkers; }
            set
            {
                if (Settings != null) Settings.showMarkers = value;
            }
        }

        /// <summary>
        /// 把当前设置落盘。「所属」页签里的开关改完状态后需要调用。
        /// </summary>
        internal static void PersistSettings()
        {
            Mod_PawnOwnership mod = LoadedModManager.GetMod<Mod_PawnOwnership>();
            if (mod != null) mod.WriteSettings();
        }

        public override string SettingsCategory()
        {
            return "归属追踪";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label(
                "归属颜色块的显示开关已移至「所属」页签：\n"
                + "选中物品 / 小人 / 动物后，在检视面板打开「所属」页签，"
                + "开关与模组调试开关放在一起。");
            listing.End();

            base.DoSettingsWindowContents(inRect);
        }
    }

    /// <summary>
    /// Multiplayer API 注册
    /// </summary>
    [StaticConstructorOnStartup]
    public static class PawnOwnershipMPCompat
    {
        static PawnOwnershipMPCompat()
        {
            if (!MP.enabled) return;
            MP.RegisterAll();
            Log.Message("[PawnOwnership] Multiplayer API 已注册！");
        }
    }
}
