using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using CUCoreLib.Registries;
using UnityEngine;

namespace GunPack
{
    /// <summary>
    /// 枪包注册整合：把 New Gun Loader 载入的自定义枪械接入 CUCoreLib 的物品体系。
    ///
    /// 只做四件事：
    ///   1. 用原始 ItemInfo 向 CUCoreLib 注册（补齐图标 → 商人/容器/UI 才能显示）
    ///   2. 追加原版格式的【弹药】【伤害】说明
    ///   3. 设置掉落权重（0 = 世界不刷，1 = 与原版枪械同权重）
    ///   4. 补耐久，避免 condition=0 被 destroyAtZeroCondition 销毁
    /// 枪的实体、贴图、音效、开火逻辑全部仍由 New Gun Loader + 各枪包提供。
    /// </summary>
    [BepInPlugin("com.local.gunpack", "枪包整合", "1.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<int> SpawnFrequency;
        internal static ConfigEntry<string> GunList;

        /// <summary>默认六个枪包；可在配置里改</summary>
        private const string DefaultGuns =
            "combatshotgun,krammer5001,lectertypet,mcx.rabid,mdrx.interceptor";

        private readonly List<string> _guns = new List<string>();
        private bool _done;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            SpawnFrequency = Config.Bind("枪械", "WorldSpawnFrequency", 1,
                "枪械在掉落池里的份数。0 = 世界不刷（只在商人处出售），1 = 与原版枪械同权重。");
            GunList = Config.Bind("枪械", "Guns", DefaultGuns,
                "要接入的枪械 id，用英文逗号分隔（对应 NewGun/*/config.json 里的 itemId）。");

            StartCoroutine(Init());
        }

        private IEnumerator Init()
        {
            float waited = 0f;
            while (!Ready())
            {
                waited += 1f;
                if (waited > 40f) { Log.LogWarning("等待物品表超时，放弃"); yield break; }
                yield return new WaitForSecondsRealtime(1f);
            }

            foreach (var s in GunList.Value.Split(','))
            {
                var id = s.Trim();
                if (id.Length > 0) _guns.Add(id);
            }
            Log.LogInfo("物品表就绪（等待 " + Mathf.RoundToInt(waited) + " 秒），开始接入 " + _guns.Count + " 把枪");

            foreach (var id in _guns) Register(id);
            _done = true;
        }

        private static bool Ready()
        {
            try { return Item.GlobalItems != null && Item.GlobalItems.Count > 0; }
            catch { return false; }
        }

        private void Register(string id)
        {
            try
            {
                if (!Item.GlobalItems.ContainsKey(id))
                {
                    Log.LogWarning("找不到枪械: " + id + "（New Gun Loader 是否加载了该枪包？）");
                    return;
                }

                var info = Item.GlobalItems[id];

                Sprite icon = null;
                string stats = "";
                try
                {
                    var go = Utils.Create(id, Vector3.zero, 0f);
                    if (go != null)
                    {
                        var sr = go.GetComponentInChildren<SpriteRenderer>();
                        if (sr != null) icon = sr.sprite;

                        var gun = go.GetComponent("GunScript");
                        if (gun != null) stats = BuildStats(gun);

                        var it = go.GetComponent<Item>();
                        if (it != null && it.condition <= 0.001f) it.SetCondition(1f);

                        Destroy(go);
                    }
                }
                catch (Exception e) { Log.LogWarning("读取枪械数据失败 " + id + ": " + e.Message); }

                if (!string.IsNullOrEmpty(stats) && !string.IsNullOrEmpty(info.description)
                    && info.description.IndexOf("【弹药】", StringComparison.Ordinal) < 0)
                {
                    info.description += "\n" + stats;
                }

                ItemRegistry.Register(id, info, icon, SpawnFrequency.Value);
                Log.LogInfo("已接入: " + id + " ｜ 图标 " + (icon != null ? icon.name : "无")
                    + " ｜ 权重 " + SpawnFrequency.Value + " ｜ 价值 " + info.value);
            }
            catch (Exception e) { Log.LogError("接入失败 " + id + ": " + e); }
        }

        /// <summary>从 GunScript 读真实数值，生成与原版一致的两行说明</summary>
        private static string BuildStats(object gun)
        {
            try
            {
                var t = gun.GetType();
                object V(string n) => t.GetField(n)?.GetValue(gun);

                float sd = 0f, ad = 0f;
                int mag = 0, shots = 1;
                string ammo = "?", feed = "?";
                try { sd = Convert.ToSingle(V("structureDamage")); } catch { }
                try { ad = Convert.ToSingle(V("animalDamage")); } catch { }
                try { mag = Convert.ToInt32(V("magCapacity")); } catch { }
                try { shots = Convert.ToInt32(V("shotsPerFire")); } catch { }
                try
                {
                    var at = V("ammoType");
                    string an = at != null ? at.ToString() : "?";
                    ammo = an == "Pistol" ? "手枪弹" : an == "Rifle" ? "步枪弹"
                         : an == "Shotgun" ? "霰弹" : an;
                }
                catch { }
                try
                {
                    var ft = V("feedType");
                    string fn = ft != null ? ft.ToString() : "?";
                    feed = fn == "Mag" ? "弹匣供弹" : fn == "Direct" ? "直接装填" : fn;
                }
                catch { }

                return "<color=#a5d6a7>【弹药】" + ammo + " ｜ " + feed
                    + (mag > 0 ? " ｜ 容量 " + mag : "")
                    + (shots > 1 ? " ｜ 每次 " + shots + " 发" : "") + "</color>\n"
                    + "<color=#ffcc80>【伤害】结构 " + sd.ToString("F0") + " ｜ 生物 " + ad.ToString("F0") + "</color>";
            }
            catch { return ""; }
        }
    }
}
