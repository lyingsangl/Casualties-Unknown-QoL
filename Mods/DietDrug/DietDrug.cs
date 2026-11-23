// 药物扩展 v4：完全按游戏原生的 LiquidType 写法注册
//
// 关键改动（v3 的错误做法 → v4 的正确做法）：
//   × 挂钩 DrinkLiquid + 逐帧改 Body 字段
//   √ 直接写 Liquids.Registry，用 onDrink(ml, body) 回调，剂量按毫升缩放
//   × 反射调 DoVomit()（那是私有协程，有前置状态）
//   √ body.vomiter.Vomit()（游戏自己的调用方式）
//
// 配方：
//   减肥药       阿片10mL + 水芦莓1 + 生化流体20 + 水70 → 100mL
//   排脂剂       阿片10mL + 纳曲酮20mL + 生化流体20 + 水50 → 100mL
//   新陈代谢剂×3 能量饮料/运动饮料/咖啡 30mL + 水60 + 生化流体10 → 100mL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using CUCoreLib.Registries;
using UnityEngine;

namespace DietDrug
{
    [BepInPlugin("com.local.dietdrug", "药物扩展", "7.5.0")]
    [BepInDependency("net.cucorelib", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new BepInEx.Logging.ManualLogSource Logger;
        internal static Plugin Instance;

        internal const string DietId = "dietdrug";
        internal const string FlushId = "fatflusher";
        internal static readonly string[] MetabIds =
        {
            "metabodrug_energy", "metabodrug_sport", "metabodrug_coffee"
        };

        internal static ConfigEntry<float> DietDuration, DietWeightLoss, DietThirst;
        internal static ConfigEntry<float> MetaDuration, MetaEnergy, MetaStamina, MetaAdrenaline;
        internal static ConfigEntry<float> MetaThirst, MetaHunger, MetaWeight, MetaOverdoseBrain, MetaSweat, MetaFatWater;
        internal static ConfigEntry<bool> UsePhysicalSweat;
        internal static ConfigEntry<float> FatRecovery;
        internal static ConfigEntry<float> SweatCap;
        internal static ConfigEntry<float> FatFraction;
        private static float _lastWetness = -1f;

        private void Awake()
        {
            Logger = base.Logger;
            Instance = this;

            DietDuration = Config.Bind("减肥药", "DurationSeconds", 480f, "100mL 剂量的持续秒数（按毫升等比缩放）");
            DietWeightLoss = Config.Bind("减肥药", "WeightLossPerSecond", 0.00625f, "每秒体重下降（kg）");
            DietThirst = Config.Bind("减肥药", "ThirstDrainPerSecond", 0.075f, "每秒水合流失（10 水合 = 100mL）");

            MetaDuration = Config.Bind("新陈代谢剂", "DurationSeconds", 720f, "100mL 剂量的持续秒数");
            MetaEnergy = Config.Bind("新陈代谢剂", "EnergyPerSecond", 0.35f, "每秒能量");
            MetaStamina = Config.Bind("新陈代谢剂", "StaminaPerSecond", 0.25f, "每秒体力");
            MetaAdrenaline = Config.Bind("新陈代谢剂", "AdrenalinePerSecond", 0.5f, "每秒肾上腺素");
            MetaThirst = Config.Bind("新陈代谢剂", "ThirstDrainPerSecond", 0.35f, "每秒水合流失（出汗，10 水合 = 100mL）");
            UsePhysicalSweat = Config.Bind("新陈代谢剂", "UsePhysicalSweatModel", true,
                "true=按蒸发潜热计算出汗耗水（推荐）；false=按原版口渴公式反推（几乎不耗水）");
            SweatCap = Config.Bind("新陈代谢剂", "WetnessCap", 24f,
                "出汗目标湿度（5=略微潮湿 20=明显潮湿）。每 1 点湿度≈0.0072 水合/秒。");
            FatRecovery = Config.Bind("新陈代谢剂", "FatWaterRecoveryPercent", 100f,
                "脂肪产水可抵扣出汗失水的百分比（0-100）。100=正好抵消出汗，绝不溢出。");
            FatFraction = Config.Bind("新陈代谢剂", "WeightLossFatFraction", 0.25f,
                "减重中属于纯脂肪的比例（兽人体质：毛发/爪/骨骼占比高，脂肪偏少）。1kg 脂肪≈1.07kg 水。");
            MetaFatWater = Config.Bind("新陈代谢剂", "FatOxidationWaterPerSecond", 0.35f, "脂肪氧化产水（抵扣口渴，最多为出汗量的 80%）");
            MetaHunger = Config.Bind("新陈代谢剂", "HungerDrainPerSecond", 0.04f, "每秒饥饿流失");
            MetaWeight = Config.Bind("新陈代谢剂", "WeightLossPerSecond", 0.00556f, "每秒体重下降（脂肪燃烧，与出汗失水无关）");
            MetaOverdoseBrain = Config.Bind("新陈代谢剂", "OverdoseBrainLossPerSecond", 0.03f, "过量时每秒脑健康损失");
            MetaSweat = Config.Bind("新陈代谢剂", "SweatPerSecond", 4.0f, "每秒出汗量（湿度上升，需大于 3.2 才能抵消无毛时的衰减），到 25 即停");

            SanitizeConfig();
            RegisterConsoleCommands();
            StartCoroutine(Init());
        }

        /// <summary>
        /// 校正配置文件里的过时值。
        /// BepInEx 会锁定首次生成时的默认值，玩家拿着旧 cfg 就会跑出旧行为
        /// （曾因此出现「出汗 0.25 压不过衰减 → 不出汗 + 脂肪产水 0.35 → 水中毒」）。
        /// </summary>
        private void SanitizeConfig()
        {
            try
            {
                float drain = MetaThirst.Value, fat = MetaFatWater.Value, sweat = MetaSweat.Value;

                if (drain <= 0.01f)
                {
                    Logger.LogWarning("出汗流失为 0 会导致只补水不排水（旧版水中毒 bug），已修正为 0.35");
                    MetaThirst.Value = 0.35f; drain = 0.35f;
                }
                float maxFat = Mathf.Max(0.05f, drain) * 0.85f;
                if (fat > maxFat)
                {
                    Logger.LogWarning("脂肪产水过高（" + fat + "），已限制为 " + maxFat.ToString("F2"));
                    MetaFatWater.Value = maxFat;
                }
                if (sweat > 0f && sweat < 3.5f)
                {
                    Logger.LogWarning("出汗量过低（" + sweat + "），无法抵消每秒 3.2 的衰减，已修正为 4.0");
                    MetaSweat.Value = 4.0f;
                }
            }
            catch (Exception e) { Logger.LogWarning("配置校正失败: " + e.Message); }
        }

        private void RegisterConsoleCommands()
        {
            try
            {
                ConsoleCommandRegistry.Register("diet", "获得 100mL 减肥药", (string[] a) => GiveLiquid(DietId, 100f));
                ConsoleCommandRegistry.Register("fat", "获得 100mL 排脂剂", (string[] a) => GiveLiquid(FlushId, 100f));
                ConsoleCommandRegistry.Register("metab", "获得 100mL 新陈代谢剂（能量系）", (string[] a) => GiveLiquid(MetabIds[0], 100f));
                ConsoleCommandRegistry.Register("weightdbg", "打印体重相关字段（诊断用）", (string[] a) => DumpWeight());
                Logger.LogInfo("已注册控制台命令: diet / fat / metab");
            }
            catch (Exception e) { Logger.LogWarning("注册控制台命令失败: " + e.Message); }
        }

        private IEnumerator Init()
        {
            float waited = 0f;
            while (Liquids.Registry == null || Item.GlobalItems == null || Item.GlobalItems.Count < 100)
            {
                waited += Time.unscaledDeltaTime;
                yield return new WaitForSecondsRealtime(1f);
            }
            Logger.LogInfo("世界数据已就绪（等待 " + Mathf.RoundToInt(waited) + " 秒），开始注册");

            RegisterDrugs();
            yield return new WaitForSeconds(1f);
            RegisterRecipes();
        }

        // ---------- 液体（原生 LiquidType） ----------

        private void RegisterDrugs()
        {
            AddLiquid(DietId, "减肥药", new Color(0.86f, 0.93f, 0.52f), DietDesc(), OnDietDrink);
            AddLiquid(FlushId, "排脂剂", new Color(0.78f, 0.72f, 0.30f), FlushDesc(), OnFlushDrink);
            AddLiquid(MetabIds[0], "新陈代谢剂（能量）", new Color(0.62f, 0.36f, 0.88f), MetabDesc("能量饮料"), OnMetaDrink);
            AddLiquid(MetabIds[1], "新陈代谢剂（运动）", new Color(0.36f, 0.66f, 0.95f), MetabDesc("运动饮料"), OnMetaDrink);
            AddLiquid(MetabIds[2], "新陈代谢剂（咖啡）", new Color(0.52f, 0.30f, 0.13f), MetabDesc("咖啡"), OnMetaDrink);
        }

        private void AddLiquid(string id, string name, Color color, string desc, LiquidType.OnDrink drink)
        {
            try
            {
                Liquids.Registry[id] = new LiquidType
                {
                    localeName = id,
                    color = color,
                    valuePerLiter = 80f,
                    healthUsable = true,
                    injectable = true,
                    injectionSickness = 2f,
                    onDrink = drink
                };

                LocaleRegistry.Register("other", id, name);
                LocaleRegistry.Register("other", id + "dsc", desc);

                Logger.LogInfo("已注册液体: " + id + "（" + name + "）");
            }
            catch (Exception e) { Logger.LogError("注册液体失败 " + id + ": " + e); }
        }

        private static string DietDesc() =>
            "一瓶淡黄绿色的药水，气味刺鼻。\n" +
            "<color=#91a7ff>┃饮用(每100mL)：代谢强化 8 分钟\n" +
            "┃期间每秒：体重 -0.006kg ┃ 水合 -0.5</color>\n" +
            "<color=orange>┃药效内重复服用会过量，脑健康下降\n" +
            "┃配方：阿片10 + 水芦莓1 + 生化流体20 + 水70</color>";

        private static string FlushDesc() =>
            "一瓶浑浊的土黄色药水，闻起来像变质油脂。\n" +
            "<color=#91a7ff>┃饮用(每100mL)：强烈催吐，排出肠脂\n" +
            "┃立刻：呕吐 ┃ 体重 -1.2kg ┃ 水合 -4\n" +
            "┃之后 5 分钟：体重 -0.010kg/秒 ┃ 水合 -0.45/秒</color>\n" +
            "<color=orange>┃纳曲酮诱发呕吐，这一瓶将它浓缩为主药\n" +
            "┃空腹服用容易吐到脱水，建议备水\n" +
            "┃配方：阿片10 + 纳曲酮20 + 生化流体20 + 水50</color>";

        private static string MetabDesc(string source) =>
            "一杯由" + source + "提炼的代谢药剂，入口发烫。\n" +
            "<color=#91a7ff>┃饮用(每100mL)：新陈代谢强化 12 分钟\n" +
            "┃期间每秒：能量 +0.35 ┃ 体力 +0.25 ┃ 肾上腺素 +0.5\n" +
            "┃　　　　　水合 -0.9 ┃ 饥饿 -0.35 ┃ 体重 -0.004kg</color>\n" +
            "<color=#ffee58>┃配方：" + source + "90 + 生化流体10</color>\n" +
            "<color=orange>┃副作用：心跳加快、血压下降，更易口渴饥饿</color>";

        // ---------- 饮用回调（剂量按 ml/100 缩放） ----------

        private static void OnDietDrink(float ml, Body body)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            Plugin.Logger.LogInfo("饮用减肥药 " + ml.ToString("F0") + "mL");
            DrugState.Dose(DietId, Plugin.DietDuration.Value * f, ml, 0.02f);
            EffectRunner.Run(DietId, body, Plugin.DietDuration.Value * f, (b, dt, t) =>
            {
                DrugState.TickOverdose(b, DietId, dt);
                DrugMath.AddWeight(b, -Plugin.DietWeightLoss.Value * dt);
                DrugMath.SyncVisual(b, 0.01f);
                DrugMath.AddThirst(b, -Plugin.DietThirst.Value * dt);
            });
        }

        private static void OnFlushDrink(float ml, Body body)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            Plugin.Logger.LogInfo("饮用排脂剂 " + ml.ToString("F0") + "mL");
            DrugState.Dose(FlushId, 300f * f, ml, 0.02f);

            // 立即催吐（原生调用）
            try { body.vomiter.Vomit(); Plugin.Logger.LogInfo("已触发原生呕吐"); }
            catch (Exception e) { Plugin.Logger.LogWarning("呕吐调用失败: " + e.Message); }

            // 1.2 秒后再喷一次血雾：本作血液是黄色的，视觉上像被排出的油脂
            try
            {
                if (Plugin.Instance != null)
                    Plugin.Instance.StartCoroutine(BloodSpray(body, 1.2f));
            }
            catch (Exception e) { Plugin.Logger.LogWarning("血雾触发失败: " + e.Message); }

            // 减重集中在呕吐瞬间（原生呕吐本身不减重），其余消耗压低，
            // 避免和 Vomit() 自带的 happiness-2.5 / hunger-17 / thirst-10 叠得太狠
            // 走游戏自己的 Eat()，与水芦莓、汉堡增减重完全同一条路径
            float before = DrugMath.GetWeightOffset(body);
            try { body.Eat(0f, -1.6f * f); }
            catch (Exception e) { Plugin.Logger.LogWarning("Eat 调用失败: " + e.Message); }
            DrugMath.SyncVisual(body, 0.01f);             // 立刻对齐界面值
            float after = DrugMath.GetWeightOffset(body);
            Plugin.Logger.LogInfo("体重偏移 " + before.ToString("F2") + " → " + after.ToString("F2") + "（目标 -" + (1.2f * f).ToString("F2") + "）");

            DrugMath.AddThirst(body, -4f * f);
            DrugMath.AddHappiness(body, 1.5f * f);

            EffectRunner.Run(FlushId, body, 300f * f, (b, dt, t) =>
            {
                DrugState.TickOverdose(b, FlushId, dt);
                DrugMath.AddWeight(b, -0.0063f * dt);
                DrugMath.SyncVisual(b, 0.01f);
                DrugMath.AddThirst(b, -0.45f * dt);
            });
        }

        private static System.Collections.IEnumerator BloodSpray(Body body, float delay)
        {
            yield return new WaitForSeconds(delay);
            try
            {
                if (body != null && body.vomiter != null)
                {
                    body.vomiter.VomitBlood();
                    Plugin.Logger.LogInfo("已触发血雾（视觉上为油脂）");
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("VomitBlood 失败: " + e.Message); }
        }

        private static void OnMetaDrink(float ml, Body body)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            float dur = Plugin.MetaDuration.Value * f;
            Plugin.Logger.LogInfo("饮用新陈代谢剂 " + ml.ToString("F0") + "mL");
            DrugState.Dose("metabo", dur, ml, Plugin.MetaOverdoseBrain.Value);

            // 能量/体力是每帧重算的派生值，直接加会被覆盖；
            // 改走游戏的 caffeinated 机制：能量消耗×0.55、体力恢复×2、体力消耗×0.25
            try
            {
                float e0 = DrugMath.Get(body, "energy"), s0 = DrugMath.Get(body, "stamina");
                float c0 = DrugMath.Get(body, "caffeinated");

                body.caffeinated = Mathf.Max(body.caffeinated, dur);
                body.stamina = Mathf.Min(100f, body.stamina + 40f * f);
                body.energy  = Mathf.Min(100f, body.energy + 30f * f);
                try { body.Drink(90f * 0.1f * f); } catch { }
                body.happiness += 2f * f;

                Plugin.Logger.LogInfo(string.Format(
                    "代谢剂生效: 能量 {0:F0}→{1:F0}｜体力 {2:F0}→{3:F0}｜咖啡因 {4:F0}→{5:F0}｜持续 {6:F0}s",
                    e0, DrugMath.Get(body, "energy"), s0, DrugMath.Get(body, "stamina"),
                    c0, DrugMath.Get(body, "caffeinated"), dur));
            }
            catch (Exception e) { Plugin.Logger.LogWarning("代谢剂即时效果失败: " + e.Message); }

            // 持续部分：口渴、饥饿、体重（体重两个字段都要改）
            EffectRunner.Run("metabo", body, dur, (b, dt, t) =>
            {
                DrugState.TickOverdose(b, "metabo", dt);
                // 出汗失水（物理模型）：出的汗 = 蒸发掉的水 + 皮肤上新增的水膜。
                //   蒸发降温 = 湿度 × 0.001 °C/秒
                //   耗热 = 降温 × 人体热容 175 kJ/K
                //   汗量 = 耗热 ÷ 汽化潜热 2.43 kJ/g，1g 汗 ≈ 1mL，10 水合 = 100mL
                //   → 每 1 点湿度蒸发 = 0.0072 水合/秒
                //   另加湿膜累积：湿度每上涨 1 点，额外补 0.02 水合
                float wetNow = DrugMath.Get(b, "wetness");
                float tempNow = DrugMath.Get(b, "temperature");
                float sweatWater = 0f;
                if (!float.IsNaN(wetNow) && wetNow > 0.5f)
                {
                    if (Plugin.UsePhysicalSweat.Value)
                    {
                        sweatWater = wetNow * 0.0072f;                       // 蒸发部分
                        if (_lastWetness >= 0f)                              // 湿膜累积部分
                            sweatWater += Mathf.Max(0f, wetNow - _lastWetness) * 0.02f;
                    }
                    else if (!float.IsNaN(tempNow))
                    {
                        // 原版公式：湿度反推等效体温，再算口渴差值（几乎不耗水）
                        float eqT = 37.5f + wetNow / 20f;
                        float rateEq = 1f / (17f - 0.5f * (eqT - 37f));
                        float rateNow = 1f / (17f - 0.5f * (tempNow - 37f));
                        sweatWater = Mathf.Max(0f, rateEq - rateNow);
                    }
                    _lastWetness = wetNow;
                    if (sweatWater > 0f) DrugMath.AddThirst(b, -sweatWater * dt);
                }

                // 脂肪氧化产水：1g 脂肪约产 1.07mL 水，抵扣一部分出汗失水。
                // 硬上限 140：再往上就接近水中毒阈值 175，绝不允许药剂把水合推高
                // 产水不得超过当前出汗失水的 85%，保证净水永远为负（防水中毒）
                // 物理产水：减重 × 脂肪占比 × 1.07（1kg 脂肪≈1.07kg 水，1kg 水=100 水合）
                float fatPerSec = Plugin.MetaWeight.Value * Mathf.Clamp01(Plugin.FatFraction.Value);
                float physicalFatWater = fatPerSec * 1.07f * 100f;      // 水合/秒

                // 硬上限：不得超过出汗量（否则净水为正 → 水中毒）
                float pct = Mathf.Clamp(Plugin.FatRecovery.Value, 0f, 100f) * 0.01f;
                float fatWater = Mathf.Min(physicalFatWater, sweatWater * pct);
                if (DrugMath.Get(b, "thirst") < 140f)
                    DrugMath.AddThirst(b, fatWater * dt);
                DrugMath.AddHunger(b, -Plugin.MetaHunger.Value * dt);
                DrugMath.AddWeight(b, -Plugin.MetaWeight.Value * dt);
                DrugMath.SyncVisual(b, 0.01f);
                DrugMath.KeepCaffeinated(b);

                // 出汗：湿度缓慢上升（游戏里「湿的」状态就是出汗表现），到 25 稳定住
                DrugMath.AddWetness(b, Plugin.MetaSweat.Value * dt, Plugin.SweatCap.Value);

                // 心情：代谢兴奋感，抵消一部分口渴/饥饿带来的负面
                DrugMath.AddHappiness(b, 0.03f * dt);

                // 体温：蒸发冷却 —— 出汗量越大，带走的热量越多。
                // 不限制环境温度（现实中在火山里出汗照样降温，只是抵不过外部加热），
                // 但核心温度降到 35.5 就不再多扣，避免把人冻死。
                float t0 = DrugMath.Get(b, "temperature");
                float wet = DrugMath.Get(b, "wetness");
                if (!float.IsNaN(t0) && !float.IsNaN(wet) && wet > 0.5f && t0 > 35.5f)
                {
                    float cooling = wet * 0.0030f * dt;          // 湿度 25 时约 0.075°C/秒
                    DrugMath.AddTemperature(b, -Mathf.Min(cooling, t0 - 35.5f));
                }

                // 肾上腺素维持在较高水平 → 心率上升（心率高本身就会让伤口流得更快）
                DrugMath.KeepAdrenaline(b, dt, 45f, 5f);

                // 采样日志：第 5 / 15 / 30 / 60 秒各打一次（缩短检测间隔，便于快速核对）
                bool hit = (t >= 5f && t - dt < 5f) || (t >= 15f && t - dt < 15f)
                        || (t >= 30f && t - dt < 30f) || (t >= 60f && t - dt < 60f);
                if (hit)
                    Plugin.Logger.LogInfo(string.Format(
                        "[代谢剂采样 {0:F0}s] 体温 {1:F2} ｜湿度 {9:F0} ｜肾上腺素 {2:F0} ｜心率 {3:F0} ｜水合 {4:F1} ｜饥饿 {5:F1} ｜体重偏移 {6:F2} ｜心情 {7:F1} ｜咖啡因 {8:F0}",
                        t, DrugMath.Get(b, "temperature"), DrugMath.Get(b, "adrenaline"), DrugMath.Get(b, "heartRate"),
                        DrugMath.Get(b, "thirst"), DrugMath.Get(b, "hunger"), DrugMath.GetWeightOffset(b),
                        DrugMath.Get(b, "happiness"), DrugMath.Get(b, "caffeinated"), DrugMath.Get(b, "wetness")));
            });
        }

        // ---------- 配方 ----------

        private static RecipeItem Opiate() => new RecipeItem(0f)
        {
            specific = false, isLiquid = true,
            quality = new CraftingQuality("opiate", 10f)
        };

        /// <summary>任意液体 N 毫升（品质 water / 液体），与原版药方写法一致</summary>
        private static RecipeItem AnyLiquid(float ml) => new RecipeItem(0f)
        {
            specific = false,
            isLiquid = true,
            quality = new CraftingQuality("water", ml)
        };

        private static RecipeItem Liquid(string id, float ml) =>
            new RecipeItem(ml) { specific = true, specificId = id, isLiquid = true };

        private static RecipeItem Solid(string id) =>
            new RecipeItem(0f) { specific = true, specificId = id };

        private void RegisterRecipes()
        {
            try
            {
                RecipeRegistry.Register(new Recipe
                {
                    INT = 10,
                    category = Recipes.RecipeCategory.Medicine,
                    // 水芦莓 ×1（稀有，一棵树掉一个）+ 水芦果 ×2（同树伴生的蓝色透明果实）+ 生化流体 10mL
                    items = new List<RecipeItem>
                    {
                        Solid("mushpear"),
                        Solid("aquapple"),
                        Solid("aquapple"),
                        Liquid("biochem", 10f)
                    },
                    result = new RecipeResult { id = DietId, amount = 1, isLiquid = true, resultCondition = 100f }
                });
                Logger.LogInfo("配方：水芦莓×1 + 水芦果×2 + 生化流体10 → 减肥药 100mL");

                RecipeRegistry.Register(new Recipe
                {
                    INT = 10,
                    category = Recipes.RecipeCategory.Medicine,
                    items = new List<RecipeItem> { Opiate(), Liquid("naltrexone", 20f), Liquid("biochem", 20f), AnyLiquid(50f) },
                    result = new RecipeResult { id = FlushId, amount = 1, isLiquid = true, resultCondition = 100f }
                });
                Logger.LogInfo("配方：阿片10 + 纳曲酮20 + 生化流体20 + 液体50 → 排脂剂 100mL");

                var srcs = new[]
                {
                    new[] { "energydrink", "能量饮料", MetabIds[0] },
                    new[] { "sportsdrink", "运动饮料", MetabIds[1] },
                    new[] { "coffee",      "咖啡",     MetabIds[2] }
                };
                foreach (var s in srcs)
                {
                    RecipeRegistry.Register(new Recipe
                    {
                        INT = 10,
                        category = Recipes.RecipeCategory.Medicine,
                        items = new List<RecipeItem> { Liquid(s[0], 90f), Liquid("biochem", 10f) },
                        result = new RecipeResult { id = s[2], amount = 1, isLiquid = true, resultCondition = 100f }
                    });
                    Logger.LogInfo("配方：" + s[1] + "90 + 生化流体10 → " + s[2] + " 100mL");
                }
            }
            catch (Exception e) { Logger.LogError("注册配方失败: " + e); }
        }

        /// <summary>诊断：把原版面板与肥胖判定可能用到的体重字段全部打印出来</summary>
        internal static void DumpWeight()
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) { Logger.LogWarning("不在游戏中"); return; }

                var t = body.GetType();
                const System.Reflection.BindingFlags F =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

                string Fld(string name)
                {
                    var f = t.GetField(name, F);
                    if (f == null) return name + "=<无此字段>";
                    var v = f.GetValue(body);
                    return name + "=" + (v is float fl ? fl.ToString("F2") : (v == null ? "null" : v.ToString()));
                }

                string baseMass = "baseMass=?";
                for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
                {
                    var f = cur.GetField("baseMass", F | System.Reflection.BindingFlags.DeclaredOnly);
                    if (f != null) { baseMass = "baseMass=" + ((float)f.GetValue(body)).ToString("F2"); break; }
                }

                // 体重属性（若存在）
                string prop = "weight=?";
                var pi = t.GetProperty("weight", F);
                if (pi != null && pi.CanRead)
                {
                    try { prop = "weight(属性)=" + ((float)pi.GetValue(body)).ToString("F2"); } catch { }
                }

                Logger.LogInfo("[体重调试] " + baseMass + " ｜ " + Fld("weightOffset") + " ｜ "
                    + Fld("weightVisualOffset") + " ｜ " + Fld("lastWeight") + " ｜ " + prop);
            }
            catch (Exception e) { Logger.LogWarning("体重诊断失败: " + e.Message); }
        }

        internal static void GiveLiquid(string liquidId, float ml)
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) { Logger.LogWarning("不在游戏中，无法给药"); return; }

                var go = Utils.Create("craftingbottle", body.transform.position, 0f);
                if (go == null) { Logger.LogWarning("临时瓶创建失败"); return; }

                var wc = go.GetComponent<WaterContainerItem>();
                if (wc != null) { wc.stack.Clear(); wc.AddLiquid(liquidId, ml); }

                var item = go.GetComponent<Item>();
                if (item != null) body.AutoPickUpItem(item);

                Logger.LogInfo("已给予 " + liquidId + " " + Mathf.RoundToInt(ml) + "mL");
            }
            catch (Exception e) { Logger.LogWarning("给药失败: " + e.Message); }
        }
    }

    /// <summary>按时长跑一段效果，自动处理实体销毁</summary>
    internal class EffectRunner : MonoBehaviour
    {
        private static readonly Dictionary<string, Coroutine> Running = new Dictionary<string, Coroutine>();

        internal static void Run(string key, Body body, float duration, Action<Body, float, float> tick)
        {
            if (body == null || Plugin.Instance == null) return;

            // 同一药物重复服用时，先停掉上一个协程，避免效果叠加（曾导致补水翻倍）
            if (Running.TryGetValue(key, out var old) && old != null)
            {
                try { Plugin.Instance.StopCoroutine(old); } catch { }
                Plugin.Logger.LogInfo("药物重复生效，已重置计时（" + key + "）");
            }

            var r = Plugin.Instance.gameObject.AddComponent<EffectRunner>();
            Running[key] = r.StartCoroutine(r.Loop(body, duration, tick));
        }

        private IEnumerator Loop(Body body, float duration, Action<Body, float, float> tick)
        {
            float t = 0f;
            while (t < duration && body != null)
            {
                float dt = Time.deltaTime;
                t += dt;
                try { tick(body, dt, t); } catch { }
                yield return null;
            }
            Destroy(this);
        }
    }

    /// <summary>
    /// 过量管理：药效未结束又服一次（或单次剂量超过 150mL）即判定过量，
    /// 过量持续 120 秒，期间按设定速率扣脑健康。
    /// </summary>
    internal static class DrugState
    {
        private const float OverdoseDuration = 120f;
        private const float SingleDoseLimit = 150f;

        private static readonly Dictionary<string, float> ActiveUntil = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> OverdoseUntil = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> BrainRate = new Dictionary<string, float>();

        internal static void Dose(string drug, float duration, float ml, float brainLossPerSec)
        {
            float now = Time.time;
            BrainRate[drug] = brainLossPerSec;

            bool repeat = ActiveUntil.TryGetValue(drug, out var u) && now < u;
            bool bigDose = ml > SingleDoseLimit;

            if (repeat || bigDose)
            {
                OverdoseUntil[drug] = now + OverdoseDuration;
                Plugin.Logger.LogWarning(string.Format(
                    "过量判定：{0}（{1}）— 脑健康 -{2:F2}/秒，持续 {3:F0} 秒",
                    drug, repeat ? "药效内重复服用" : "单次剂量过大",
                    brainLossPerSec, OverdoseDuration));
            }

            ActiveUntil[drug] = now + duration;
        }

        internal static void TickOverdose(Body b, string drug, float dt)
        {
            if (!OverdoseUntil.TryGetValue(drug, out var until) || Time.time >= until) return;
            if (!BrainRate.TryGetValue(drug, out var rate)) return;
            DrugMath.Add(b, "brainHealth", -rate * dt);
        }
    }

    /// <summary>Body 字段读写（反射，避免字段可见性变化导致编译失败）</summary>
    internal static class DrugMath
    {
        private static FieldInfo F(Body b, string name) =>
            b.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        internal static void Add(Body b, string field, float delta)
        {
            if (b == null) return;
            try
            {
                var f = F(b, field);
                if (f != null) f.SetValue(b, (float)f.GetValue(b) + delta);
            }
            catch { }
        }

        internal static void AddWeight(Body b, float d) => Add(b, "weightOffset", d);

        /// <summary>
        /// 让界面用的 weightVisualOffset 跟随真实值，但不能逐帧硬写：
        /// 原版面板/肥胖判定依赖 lastWeight 与 weightChangeTime 的缓存刷新，
        /// 逐帧写极小增量会让面板停在旧值，而判定用实时值，两边差出好几公斤。
        /// 这里只在偏差超过阈值时兜底对齐。
        /// </summary>
        internal static void SyncVisual(Body b, float threshold)
        {
            try
            {
                var fv = F(b, "weightVisualOffset");
                var fo = F(b, "weightOffset");
                if (fv == null || fo == null) return;
                float v = (float)fv.GetValue(b), o = (float)fo.GetValue(b);
                if (Mathf.Abs(v - o) > threshold) fv.SetValue(b, o);
            }
            catch { }
        }

        internal static float Get(Body b, string field)
        {
            try
            {
                var f = F(b, field);
                return f == null ? float.NaN : (float)f.GetValue(b);
            }
            catch { return float.NaN; }
        }

        internal static float GetWeightOffset(Body b)
        {
            try
            {
                var f = F(b, "weightOffset");
                return f == null ? float.NaN : (float)f.GetValue(b);
            }
            catch { return float.NaN; }
        }
        internal static void AddThirst(Body b, float d) => Add(b, "thirst", d);
        internal static void AddHunger(Body b, float d) => Add(b, "hunger", d);
        internal static void AddEnergy(Body b, float d) => Add(b, "energy", d);
        internal static void AddStamina(Body b, float d) => Add(b, "stamina", d);
        internal static void AddAdrenaline(Body b, float d) => Add(b, "adrenaline", d);
        internal static void AddHappiness(Body b, float d) => Add(b, "happiness", d);

        /// <summary>出汗：湿度上升，达到上限后停止</summary>
        internal static void AddWetness(Body b, float delta, float cap)
        {
            try
            {
                var f = F(b, "wetness");
                if (f == null) return;
                float cur = (float)f.GetValue(b);
                if (cur < cap) f.SetValue(b, Mathf.Min(cap, cur + delta));
            }
            catch { }
        }
        internal static void AddTemperature(Body b, float d) => Add(b, "temperature", d);

        /// <summary>当前环境温度（WorldGeneration.world.ambientTemperature）</summary>
        internal static float GetAmbient()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("WorldGeneration");
                    if (t == null) continue;
                    var fw = t.GetField("world", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var world = fw?.GetValue(null);
                    if (world == null) return 20f;
                    var fa = world.GetType().GetField("ambientTemperature",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    return fa != null ? (float)fa.GetValue(world) : 20f;
                }
            }
            catch { }
            return 20f;
        }

        /// <summary>把肾上腺素往上推，直到达到目标值（心率随之升高）</summary>
        internal static void KeepAdrenaline(Body b, float dt, float target, float rate)
        {
            try
            {
                var f = F(b, "adrenaline");
                if (f == null) return;
                float cur = (float)f.GetValue(b);
                if (cur < target) f.SetValue(b, Mathf.Min(target, cur + rate * dt));
            }
            catch { }
        }

        /// <summary>药效期间保持咖啡因不为零（游戏按每秒 -1 衰减）</summary>
        internal static void KeepCaffeinated(Body b)
        {
            try
            {
                var f = F(b, "caffeinated");
                if (f != null && (float)f.GetValue(b) < 5f) f.SetValue(b, 30f);
            }
            catch { }
        }
    }
}
