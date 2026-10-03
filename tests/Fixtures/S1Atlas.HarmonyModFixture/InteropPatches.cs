// Interop-view stand-in: mirrors the generated Il2Cpp projection shape (Il2Cpp-prefixed
// namespace) so patch targets written against the interop view resolve to game symbols
// through the normalizer. The game index holds Game.Widget only.
using Game;
using HarmonyLib;

namespace Il2CppGame
{
    public class Widget
    {
    }
}

namespace Mod
{
    [HarmonyPatch(typeof(Il2CppGame.Widget), "Run")]
    public class InteropPrefixPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
        }
    }
}
