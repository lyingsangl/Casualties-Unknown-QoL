using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace RecipeBan
{
    /// <summary>
    /// 配方屏蔽：在配方表生成完成后，按「成品 id」删掉指定配方。
    /// 当前默认屏蔽 Weight_Metal 的 重力金属 T1~T4
    /// （该模组本身没有配置开关，只能在运行时移除）。
    /// </summary>
    [BepInPlugin("com.local.recipeban", "配方屏蔽", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enable;
        internal static ConfigEntry<string> BanIds;
        internal static ConfigEntry<string> BanNames;

        private const string DefaultNames = "Gravity Metal";

        private const string DefaultIds =
            "gravitymetal_orig_t1,gravitymetal_orig_t2,gravitymetal_orig_t3,gravitymetal_orig_t4";

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Enable = Config.Bind("通用", "Enable", true, "是否启用配方屏蔽。");
            BanIds = Config.Bind("通用", "BanIds", DefaultIds,
                "要屏蔽的成品 id，英文逗号分隔。");
            BanNames = Config.Bind("通用", "BanNames", DefaultNames,
                "要屏蔽的成品名称关键词（英文逗号分隔，命中即删），用于 id 猜不准时兜底。");

            try
            {
                var h = new Harmony("com.local.recipeban.patch");
                h.PatchAll(typeof(RecipeSetupPatch));
                Log.LogInfo("补丁已应用: Recipes.SetUpRecipes 后清理");
            }
            catch (Exception e) { Log.LogWarning("补丁失败: " + e.Message); }

            StartCoroutine(FallbackApply());
        }

        /// <summary>补丁没命中时的兜底：等一会儿直接删一次</summary>
        private IEnumerator FallbackApply()
        {
            for (int i = 0; i < 6; i++)
            {
                yield return new UnityEngine.WaitForSecondsRealtime(6f);
                Cut();
            }
        }

        internal static void Cut()
        {
            if (!Enable.Value) return;
            try
            {
                var t = FindType("Recipes");
                var list = t?.GetField("recipes", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IList;
                if (list == null || list.Count == 0) return;

                var keys = new List<string>();
                foreach (var s in BanIds.Value.Split(','))
                {
                    var k = s.Trim();
                    if (k.Length > 0) keys.Add(k);
                }
                var names = new List<string>();
                foreach (var s in BanNames.Value.Split(','))
                {
                    var k = s.Trim();
                    if (k.Length > 0) names.Add(k);
                }

                int removed = 0;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var r = list[i];
                    if (r == null) continue;
                    var rf = r.GetType().GetField("result", BindingFlags.Public | BindingFlags.Instance);
                    var res = rf?.GetValue(r);
                    if (res == null) continue;
                    var idf = res.GetType().GetField("id", BindingFlags.Public | BindingFlags.Instance);
                    var id = idf?.GetValue(res) as string;
                    if (string.IsNullOrEmpty(id)) continue;

                    bool hit = keys.Exists(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
                    if (!hit && names.Count > 0)
                    {
                        string shown = null;
                        try
                        {
                            var nf = res.GetType().GetField("name", BindingFlags.Public | BindingFlags.Instance);
                            shown = nf?.GetValue(res) as string;
                        }
                        catch { }
                        if (string.IsNullOrEmpty(shown)) shown = SafeName(id);
                        hit = names.Exists(n => shown != null && shown.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    if (hit)
                    {
                        list.RemoveAt(i);
                        removed++;
                    }
                }

                if (removed > 0) Log.LogInfo("已屏蔽 " + removed + " 条配方（重力金属）");
            }
            catch (Exception e) { Log.LogWarning("屏蔽失败: " + e.Message); }
        }

        private static string SafeName(string id)
        {
            try { var n = Locale.GetItem(id); if (!string.IsNullOrEmpty(n)) return n; } catch { }
            try { if (Item.GlobalItems.ContainsKey(id)) return Item.GlobalItems[id]?.fullName; } catch { }
            return id;
        }

        private static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { var t = asm.GetType(name); if (t != null) return t; } catch { }
            }
            return null;
        }
    }

    [HarmonyPatch]
    internal static class RecipeSetupPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("Recipes"); if (t != null) break; } catch { }
            }
            return t?.GetMethod("SetUpRecipes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            try { Plugin.Cut(); } catch { }
        }
    }
}
