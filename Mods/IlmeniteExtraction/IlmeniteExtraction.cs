// 萃取钛铁矿 —— 把金属废料 + 生化流体炼成钛铁矿粉末，再压成钛铁矿块
//
// 配方：
//   [9 级]  1 × 金属废料(scrapmetal) + 10ml 生化流体(biochem) → 1 × 钛铁矿粉末(ilmenitepowder)
//   [10 级] 2 × 钛铁矿粉末 → 1 × 钛铁矿块(ilmenitechunk)
//
// 物品注册走 CUCoreLib 的 ItemRegistry，配方走 RecipeRegistry；
// 新物品的属性从「金属废料」克隆，保证分类/品质/标签与游戏原生物品一致。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using CUCoreLib.Data;
using CUCoreLib.Registries;
using UnityEngine;

namespace IlmeniteExtraction
{
    [BepInPlugin("com.local.ilmeniteextraction", "萃取钛铁矿", "1.0.1")]
    [BepInDependency("net.cucorelib", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new BepInEx.Logging.ManualLogSource Logger;

        private ConfigEntry<string> PowderId;
        private ConfigEntry<bool> BiochemIsLiquid;
        private ConfigEntry<string> OreId;
        private ConfigEntry<bool> BlueSprite;

        private void Awake()
        {
            Logger = base.Logger;

            PowderId = Config.Bind("General", "PowderItemId", "ilmenitepowder", "钛铁矿粉末的物品 ID");
            OreId = Config.Bind("General", "OreItemId", "ilmenitechunk", "第二道配方的产物（游戏已有的钛铁矿块）");
            BiochemIsLiquid = Config.Bind("General", "BiochemIsLiquid", true,
                "生化流体是否按液体处理（true=需要 10ml 液体，false=当作固体物品 1 个）");
            BlueSprite = Config.Bind("Display", "BlueNailSprite", true, "把钉子贴图染蓝作为新物品图标");

            StartCoroutine(Init());
        }

        private IEnumerator Init()
        {
            // 等游戏把原生物品表建好
            // 物品表不在启动时创建，而是进关卡/读档后才填充 —— 一直等到它出现
            float waited = 0f;
            while (!HasVanillaItems())
            {
                waited += Time.unscaledDeltaTime;
                if (waited > 10f && Mathf.Abs(waited % 30f) < 0.05f)
                    Logger.LogInfo("仍在等待物品表… 已等 " + Mathf.RoundToInt(waited) + " 秒");
                yield return new WaitForSecondsRealtime(1f);
            }
            Logger.LogInfo("物品表已就绪（等待 " + Mathf.RoundToInt(waited) + " 秒），开始注册");

            try { RegisterPowder(); }
            catch (Exception e) { Logger.LogError("注册粉末失败: " + e); }

            yield return new WaitForSeconds(1f);

            try { RegisterRecipes(); }
            catch (Exception e) { Logger.LogError("注册配方失败: " + e); }
        }

        private static bool HasVanillaItems()
        {
            var d = Item.GlobalItems;
            if (d == null || d.Count < 100) return false;
            return d.ContainsKey("scrapmetal");
        }

        // ---------- 物品 ----------

        private void RegisterPowder()
        {
            string id = PowderId.Value;
            if (Item.GlobalItems.ContainsKey(id))
            {
                Logger.LogInfo("物品已存在，跳过注册: " + id);
                return;
            }

            ItemInfo src = Item.GlobalItems["scrapmetal"];
            CustomItemInfo info = CloneAs<CustomItemInfo>(src);
            info.fullName = "钛铁矿粉末";
            info.description =
                "从金属废料中萃取出的暗色矿粉，含有可观的钛铁成分。\n" +
                "<color=#ffee58>┃用途：两份可压制成一块钛铁矿</color>\n" +
                "<color=orange>闻起来有一股化学药剂的刺鼻味。</color>";
            try { info.SetTags(); } catch { }

            Sprite icon = BlueSprite.Value ? BlueNail() : NailSprite();

            ItemRegistry.Register(id, info, icon);
            Logger.LogInfo("已注册物品: " + id + "（钛铁矿粉末）");
        }

        private static Sprite NailSprite()
        {
            foreach (var id in new[] { "nails", "cqnails", "scrapmetal" })
            {
                try
                {
                    var go = Resources.Load<GameObject>(id);
                    if (go == null) continue;
                    var sr = go.GetComponent<SpriteRenderer>();
                    if (sr != null && sr.sprite != null) return sr.sprite;
                }
                catch { }
            }
            return null;
        }

        /// <summary>把钉子贴图整体偏蓝，作为新物品的占位图标</summary>
        private static Sprite BlueNail()
        {
            var baseSprite = NailSprite();
            if (baseSprite == null) return null;

            try
            {
                var tex = baseSprite.texture;
                var r = baseSprite.textureRect;
                int w = Mathf.Max(1, (int)r.width), h = Mathf.Max(1, (int)r.height);

                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var px = tex.GetPixels((int)r.x, (int)r.y, w, h);
                for (int i = 0; i < px.Length; i++)
                {
                    // 保留明暗，整体推向蓝紫
                    float lum = px[i].r * 0.3f + px[i].g * 0.59f + px[i].b * 0.11f;
                    px[i] = new Color(lum * 0.35f, lum * 0.62f, Mathf.Min(1f, lum * 1.55f + 0.25f), px[i].a);
                }
                copy.SetPixels(px);
                copy.Apply(false, false);
                copy.filterMode = tex.filterMode;
                copy.wrapMode = tex.wrapMode;

                return Sprite.Create(copy, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f),
                                     baseSprite.pixelsPerUnit, 0, SpriteMeshType.FullRect);
            }
            catch (Exception e)
            {
                Logger.LogWarning("染色失败，改用原贴图: " + e.Message);
                return baseSprite;
            }
        }

        /// <summary>把某个原生 ItemInfo 的字段整体复制到目标类型，避免手写一堆未知语义的字段</summary>
        private static T CloneAs<T>(object src) where T : new()
        {
            var dst = new T();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            for (Type cur = src.GetType(); cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                foreach (var f in cur.GetFields(F | BindingFlags.DeclaredOnly))
                {
                    try
                    {
                        var target = FindField(typeof(T), f.Name);
                        if (target != null && target.FieldType.IsAssignableFrom(f.FieldType))
                            target.SetValue(dst, f.GetValue(src));
                    }
                    catch { }
                }
            }
            return dst;
        }

        private static FieldInfo FindField(Type t, string name)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            for (Type cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var f = cur.GetField(name, F | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        // ---------- 配方 ----------

        private void RegisterRecipes()
        {
            string powder = PowderId.Value;
            string ore = OreId.Value;

            // 第一道：金属废料 + 10ml 生化流体 → 钛铁矿粉末（9 级）
            var r1 = new Recipe
            {
                INT = 9,
                category = Recipes.RecipeCategory.Materials,
                items = new List<RecipeItem>
                {
                    new RecipeItem(0f) { specific = true, specificId = "scrapmetal" },
                },
                result = new RecipeResult { id = powder, amount = 1, isLiquid = false, resultCondition = 1f }
            };

            if (BiochemIsLiquid.Value)
                r1.items.Add(new RecipeItem(10f) { specific = true, specificId = "biochem", isLiquid = true });
            else
                r1.items.Add(new RecipeItem(0f) { specific = true, specificId = "biochem" });

            RecipeRegistry.Register(r1);
            Logger.LogInfo("已注册配方 1：金属废料 + 10ml 生化流体 → 钛铁矿粉末（INT 9，材料类）");

            // 第二道：2 × 钛铁矿粉末 → 钛铁矿块（10 级）
            var r2 = new Recipe
            {
                INT = 10,
                category = Recipes.RecipeCategory.Materials,
                items = new List<RecipeItem>
                {
                    new RecipeItem(0f) { specific = true, specificId = powder },
                    new RecipeItem(0f) { specific = true, specificId = powder },
                },
                result = new RecipeResult { id = ore, amount = 1, isLiquid = false, resultCondition = 1f }
            };

            RecipeRegistry.Register(r2);
            Logger.LogInfo("已注册配方 2：2 × 钛铁矿粉末 → 钛铁矿块（INT 10，材料类）");
        }
    }
}
