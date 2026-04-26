using HarmonyLib;
using Verse;
using Multiplayer.API;
using PawnOwnership.Patches;

namespace PawnOwnership
{
    public class Mod_PawnOwnership : Mod
    {
        public Mod_PawnOwnership(ModContentPack content) : base(content)
        {
            var harmony = new Harmony("omas.PawnOwnership");
            
            // 先注册静态 patch（通过 attribute 的）
            harmony.PatchAll();
            
            // 再注册动态 patch
            HarmonyDynamicPatches.RegisterDynamicPatches(harmony);
            
            Log.Message("[PawnOwnership] Mod 初始化成功！");
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
