// CuGuard —— 给已知会空引用的调用点加护栏
//
// 目前处理：
//   CUCoreUtils.TalkElectronic(string, Item)  —— 物品为 null 时直接跳过，
//   避免 Talker.Talk 里抛 NullReferenceException（EnhancedTools.AutoChargerStatus 每帧触发）
using System;
using System.Collections;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace CuGuard
{
    [BepInPlugin("com.local.cuguard", "CU Guard", "1.0.1")]
    public class Plugin : BaseUnityPlugin
    {
        private void Awake()
        {
            try
            {
                var h = new Harmony("com.local.cuguard");
                int patched = 0;

                var t = AccessTools.TypeByName("CUCoreUtils");
                if (t != null)
                {
                    var m = t.GetMethod("TalkElectronic",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                        null, new[] { typeof(string), typeof(Item) }, null);
                    if (m != null)
                    {
                        h.Patch(m, prefix: new HarmonyMethod(typeof(Guards).GetMethod(nameof(Guards.TalkElectronicPrefix))));
                        patched++;
                        Logger.LogInfo("已加护栏: CUCoreUtils.TalkElectronic（物品为空时跳过）");
                    }
                }

                StartCoroutine(DumpAmmoIds());

                if (patched == 0)
                    Logger.LogWarning("没有找到可加护栏的方法，插件空转");
            }
            catch (Exception e)
            {
                Logger.LogError("加护栏失败: " + e.Message);
            }
        }

        /// <summary>一次性把物品表里的弹药类 ID 打到日志，方便用 spawn 刷</summary>
        private IEnumerator DumpAmmoIds()
        {
            for (int i = 0; i < 20; i++) yield return new WaitForSeconds(1.5f);

            try
            {
                var f = typeof(Item).GetField("GlobalItems",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var dict = f?.GetValue(null) as IDictionary;
                if (dict == null) { Logger.LogWarning("物品表为空，无法列出弹药 ID"); yield break; }

                var hits = new System.Collections.Generic.List<string>();
                foreach (var k in dict.Keys)
                {
                    var id = k as string;
                    if (id == null) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(id, "(?i)ammo|shell|round|bullet|mag|12g|556|9mm|762"))
                        hits.Add(id);
                }
                hits.Sort();
                Logger.LogInfo("[弹药ID] 共 " + hits.Count + " 条: " + string.Join(", ", hits.ToArray()));
            }
            catch (Exception e) { Logger.LogWarning("列出弹药 ID 失败: " + e.Message); }
        }

    }

    internal static class Guards
    {
        /// <summary>item 为 null 时直接跳过，避免下游 Talker.Talk 空引用</summary>
        public static bool TalkElectronicPrefix(Item item)
        {
            return item != null;
        }
    }
}
