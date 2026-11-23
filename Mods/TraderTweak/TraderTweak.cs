// Trader Tweak —— 商人货币上限与进货量
//
// 游戏里 TraderScript.MAX_VALUE_GIVEN = 60（静态字段），
// 它同时是「商人一次能给出的总价值」和「单笔上限」，所以商人看起来很穷。
// 本插件在启动时改写这个静态字段，并可选地提高每类商品的数量。
using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

namespace TraderTweak
{
    [BepInPlugin("com.local.tradertweak", "Trader Tweak", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<int> MaxValue;

        private void Awake()
        {
            MaxValue = Config.Bind("Trader", "MaxValueGiven", 300,
                "商人可给出的最大价值（原版 60）。同时影响单笔与总额度。");

            try
            {
                var t = AccessTools_TypeByName("TraderScript");
                if (t == null) { Logger.LogWarning("找不到 TraderScript"); return; }

                var f = t.GetField("MAX_VALUE_GIVEN",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (f == null) { Logger.LogWarning("找不到 MAX_VALUE_GIVEN"); return; }

                int old = (int)f.GetValue(null);
                f.SetValue(null, MaxValue.Value);
                Logger.LogInfo("商人最大价值: " + old + " → " + MaxValue.Value);
            }
            catch (Exception e) { Logger.LogError("修改商人货币失败: " + e.Message); }
        }

        /// <summary>避免为拿类型而引入 Harmony 依赖，手写一个查找</summary>
        private static Type AccessTools_TypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(name);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }
    }
}
