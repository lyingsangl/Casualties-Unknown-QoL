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
using HarmonyLib;
using CUCoreLib.Registries;
using UnityEngine;

namespace DietDrug
{
    [BepInPlugin("com.local.dietdrug", "药物扩展", "21.6.0")]
    [BepInDependency("net.cucorelib", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new BepInEx.Logging.ManualLogSource Logger;
        internal static Plugin Instance;

        internal const string DietId = "dietdrug";
        internal const string FlushId = "fatflusher";
        internal const string TestDiet = "test_weight_diet";
        internal const string TestFlush = "test_weight_flush";
        internal const string TestMeta = "test_weight_meta";

        /// <summary>注册设置面板用的中文语言条目</summary>
        private void RegisterSettingLocale()
        {
            try
            {
                LocaleRegistry.Register("other", "gamesetdietdrug_disable_test", "禁用测试用药");
                LocaleRegistry.Register("other", "gamesetdietdrug_disable_testdsc",
                    "勾选：永久关闭测试药，并清除当前存档里的残留；取消：恢复可用（已清除的不返还），重启生效");
            }
            catch (Exception e) { Logger.LogWarning("设置语言注册失败: " + e.Message); }
        }

        internal const string ShellCollector = "shellcollector";

        internal const string DietBottle = "dietdrug_bottle";
        internal const string FlushBottle = "flushdrug_bottle";
        internal const string MetaBottle = "metadrug_bottle";
        internal static readonly string[] MetaBottles = { "metadrug_bottle_a", "metadrug_bottle_b", "metadrug_bottle_g" };

        // 浓缩版：100mL 原液 → 10mL 浓缩液，效力等同 100mL
        internal const string DietConc = "dietdrug_conc";
        internal const string FlushConc = "fatflusher_conc";
        internal static readonly string[] MetaConcs = { "metadrug_conc_a", "metadrug_conc_b", "metadrug_conc_g" };

        internal const string DietConcBottle = "dietdrug_bottle_conc";
        internal const string FlushConcBottle = "flushdrug_bottle_conc";
        internal static readonly string[] MetaConcBottles = { "metadrug_bottle_conc_a", "metadrug_bottle_conc_b", "metadrug_bottle_conc_g" };

        internal const float ConcFactor = 10f;   // 10mL 浓缩液 = 100mL 普通液

        private static float _lastWaterLog = -1f;

        /// <summary>本模组枪包的物品 id（精确匹配，避免把原版 shotgun 算进来）</summary>
        private static readonly string[] GunIds =
        {
            "myscar.scar", "combatshotgun", "krammer5001",
            "lectertypet", "mcx.rabid", "mdrx.interceptor"
        };

        /// <summary>物品显示名（取不到就退回 id）</summary>
        internal static string NameOf(string id)
        {
            try
            {
                var n = Locale.GetItem(id);
                if (!string.IsNullOrWhiteSpace(n) && n != id) return n;
            }
            catch { }
            try
            {
                // 自定义物品（枪械等）没有语言条目，退回 ItemInfo 里的名字
                if (Item.GlobalItems.ContainsKey(id))
                {
                    var nm = Item.GlobalItems[id]?.fullName;
                    if (!string.IsNullOrWhiteSpace(nm)) return nm;
                }
            }
            catch { }
            return id;
        }

        private static bool IsModGun(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (var g in GunIds) if (id == g) return true;
            return false;
        }

        internal static readonly string[] MetabIds =
        {
            "metabodrug_energy", "metabodrug_sport", "metabodrug_coffee"
        };

        internal static ConfigEntry<float> DietDuration, DietWeightLoss, DietThirst, WeightFloor;
        internal static ConfigEntry<bool> FreezeDrain;
        internal static ConfigEntry<bool> EnableTestDrugs;
        internal static ConfigEntry<bool> AutoLoad;
        internal static bool TraceMuzzle = false;  // 默认关闭（每 0.1 秒遍历枪械，影响性能）
        private static Type _gunT;
        private static Type _ammoT;
        internal static ConfigEntry<float> AutoLoadRadius;
        internal static ConfigEntry<bool> PanelUnits;
        internal static bool FromBottle;   // 由瓶装服用时置位，跳过补水
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
            PanelUnits = Config.Bind("通用", "UsePanelKgUnits", false,
                "false = 保持原有手感（配置值即内部偏移，面板约按 1/3 显示）；"
                + "true = 配置值按面板公斤计算（减重会变成约 2.94 倍）。");
            EnableTestDrugs = Config.Bind("调试", "EnableTestDrugs", false,
                "是否注册测试用药（减肥/排脂/代谢）。关闭后它们不存在于游戏中，也不会刷进容器或商人。");
            AutoLoad = Config.Bind("通用", "AutoFillMagazine", true,
                "自动把附近的散装子弹压进未满的弹匣（仅对 F 键标记收藏的弹匣生效）。");
            AutoLoadRadius = Config.Bind("通用", "AutoLoadRadius", 6f,
                "自动装填的搜索半径（米）。");
            FreezeDrain = Config.Bind("通用", "FreezeVanillaDrain", true,
                "药效期间冻结原版的饱食度/水合流失（只抵消自然下降，吃东西仍然有效）。");
            WeightFloor = Config.Bind("通用", "WeightFloorKg", 45f,
                "减重下限（kg，面板显示的体重）。默认 45：正好是「体重偏轻(<44.9)」的分界，"
                + "低于此值原版状态栏与面板的算法会互相矛盾。设 0 关闭。");
            DietThirst = Config.Bind("减肥药", "ThirstDrainPerSecond", 0.05f, "每秒水合流失（10 水合 = 100mL）");

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
            MetaWeight = Config.Bind("新陈代谢剂", "WeightLossPerSecond", 0.00613f, "每秒体重下降（脂肪燃烧，与出汗失水无关）");
            MetaOverdoseBrain = Config.Bind("新陈代谢剂", "OverdoseBrainLossPerSecond", 0.03f, "过量时每秒脑健康损失");
            MetaSweat = Config.Bind("新陈代谢剂", "SweatPerSecond", 4.0f, "每秒出汗量（湿度上升，需大于 3.2 才能抵消无毛时的衰减），到 25 即停");

            SanitizeConfig();
            gameObject.AddComponent<WeightSync>();
            RegisterSettingLocale();
            // 自动压弹已迁到 AmmoPress 模组

            // 商人日志补丁（必须显式 PatchAll，否则类上的 [HarmonyPatch] 不会生效）
            try
            {
                var h = new Harmony("com.local.dietdrug.traderlog");
                h.PatchAll(typeof(TraderInventoryPatch));
                h.PatchAll(typeof(SettingsEntryPatch));
                h.PatchAll(typeof(MiniBarrelLiquidPatch));
                Logger.LogInfo("商人库存日志补丁已应用");
            }
            catch (Exception e) { Logger.LogWarning("商人日志补丁失败: " + e.Message); }
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
                // l 作分隔符的单 token 形式（推荐）
                ConsoleCommandRegistry.Register("Wldiet", "减肥药 100mL", (string[] a) => GiveLiquid(DietId, 100f));
                ConsoleCommandRegistry.Register("Wlflush", "排脂剂 100mL", (string[] a) => GiveLiquid(FlushId, 100f));
                ConsoleCommandRegistry.Register("Wlmeta_a", "代谢剂·α（能量）100mL", (string[] a) => GiveLiquid(MetabIds[0], 100f));
                ConsoleCommandRegistry.Register("Wlmeta_b", "代谢剂·β（运动）100mL", (string[] a) => GiveLiquid(MetabIds[1], 100f));
                ConsoleCommandRegistry.Register("Wlmeta_w", "代谢剂·ω（咖啡）100mL", (string[] a) => GiveLiquid(MetabIds[2], 100f));
                ConsoleCommandRegistry.Register("Tldiet", "测试药·减肥", (string[] a) => GiveLiquid(TestDiet, 100f));
                ConsoleCommandRegistry.Register("Tlflush", "测试药·排脂", (string[] a) => GiveLiquid(TestFlush, 100f));
                ConsoleCommandRegistry.Register("Tlmeta", "测试药·代谢", (string[] a) => GiveLiquid(TestMeta, 100f));
                ConsoleCommandRegistry.Register("Wldrop", "Wldrop <diet|flush|meta_a|meta_b|meta_w> 在脚下生成药瓶（测试刷出）", (string[] a) => DropBottle(a));
                ConsoleCommandRegistry.Register("traderdump", "打印最近商人的货架内容", (string[] a) => DumpTrader());
                ConsoleCommandRegistry.Register("pooldump", "pooldump [分类] 打印掉落池内容与枪械是否在内", (string[] a) => DumpPool(a));
                ConsoleCommandRegistry.Register("traders", "列出场景中全部商人的货架（含 Mod 枪标记）", (string[] a) => DumpAllTraders());
                ConsoleCommandRegistry.Register("cleanup", "清除全图容器里的测试药液（test_weight_*）", (string[] a) => CleanTestLiquids());
                // 空格形式保留为备用
                ConsoleCommandRegistry.Register("Wl", "药物：Wl diet | Wl flush | Wl meta <a|b|w>", (string[] a) => WlCommand(a));
                ConsoleCommandRegistry.Register("Tl", "测试药：Tl diet | Tl flush | Tl meta", (string[] a) => TlCommand(a));
                ConsoleCommandRegistry.Register("weightdbg", "打印体重相关字段（诊断用）", (string[] a) => DumpWeight());
                ConsoleCommandRegistry.Register("weightscan", "扫描所有 20~60 的浮点字段，找出面板读的是哪个", (string[] a) => ScanWeights());
                ConsoleCommandRegistry.Register("weightdump", "打印体重构成（各肢体质量 + 偏移 + 合计）", (string[] a) => DumpWeightParts());
                ConsoleCommandRegistry.Register("weightfix", "weightfix <kg>：把体重设为指定值（测试用）", (string[] a) => FixWeight(a));
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
            RegisterBottles();
            // 弹壳收集器与自动压弹已迁移到独立模组 AmmoPress
            yield return new WaitForSeconds(1f);
            RegisterRecipes();
        }

        // ---------- 液体（原生 LiquidType） ----------

        private void RegisterDrugs()
        {
            AddLiquid(DietId, "减肥药", new Color(0.86f, 0.93f, 0.52f), DietDesc(), OnDietDrink);
            AddLiquid(FlushId, "排脂剂", new Color(0.78f, 0.72f, 0.30f), FlushDesc(), OnFlushDrink);
            AddLiquid(MetabIds[0], "新陈代谢剂·α（能量）", new Color(0.62f, 0.36f, 0.88f), MetabDesc("能量饮料") + MetabFlavor(0), (ml, b) => OnMetaDrink(ml, b, 0));
            AddLiquid(MetabIds[1], "新陈代谢剂·β（运动）", new Color(0.36f, 0.66f, 0.95f), MetabDesc("运动饮料") + MetabFlavor(1), (ml, b) => OnMetaDrink(ml, b, 1));
            AddLiquid(MetabIds[2], "新陈代谢剂·ω（咖啡）", new Color(0.52f, 0.30f, 0.13f), MetabDesc("咖啡") + MetabFlavor(2), (ml, b) => OnMetaDrink(ml, b, 2));

            // 浓缩液：同样喝 10mL，效果按 10 倍计算（= 100mL 普通液）
            AddLiquid(DietConc, "减肥药·浓缩", new Color(0.55f, 0.72f, 0.25f),
                ConcDesc("减肥药"), (ml, b) => OnDietDrink(DoseEquivalent(ml), b), valuePerLiter: 800f);
            AddLiquid(FlushConc, "排脂剂·浓缩", new Color(0.60f, 0.52f, 0.12f),
                ConcDesc("排脂剂"), (ml, b) => OnFlushDrink(DoseEquivalent(ml), b), valuePerLiter: 800f);
            for (int i = 0; i < 3; i++)
            {
                int k = i;
                AddLiquid(MetaConcs[i], "代谢剂·浓缩·" + "αβω"[i], new Color(0.45f, 0.26f, 0.72f),
                    ConcDesc("代谢剂"), (ml, b) => OnMetaDrink(DoseEquivalent(ml), b, k), valuePerLiter: 800f);
            }

            // 纯测试药：只有减重 + 冻结消耗，不触发任何其他效果
            if (!EnableTestDrugs.Value)
            {
                Logger.LogInfo("测试用药未注册（EnableTestDrugs=false）");
                return;
            }
            AddLiquid(TestDiet, "测试药·减肥", new Color(0.9f, 0.9f, 0.9f),
                "测试用。只减重（3kg/480秒），并冻结饱食度与水合消耗，无其他效果。", OnTestDiet);
            AddLiquid(TestFlush, "测试药·排脂", new Color(0.85f, 0.85f, 0.85f),
                "测试用。瞬时 -1.6kg 后持续减重（共 3.5kg），冻结消耗，不呕吐。", OnTestFlush);
            AddLiquid(TestMeta, "测试药·代谢", new Color(0.95f, 0.95f, 0.95f),
                "测试用。只减重（4kg/720秒），冻结消耗，无体温/出汗/咖啡因效果。", OnTestMeta);
        }

        /// <summary>
        /// 浓缩液的剂量换算：液体本身浓度是普通的 10 倍（写在 LiquidType 上），
        /// 单次摄入等效上限 100mL，防止把 100mL 瓶子灌满后一口气喝出 10 倍药效。
        /// </summary>
        private static float DoseEquivalent(float ml) => Mathf.Min(ml * ConcFactor, 100f);

        private static string ConcDesc(string src) =>
            "经火源浓缩的" + src + "，体积只有十分之一，效力不变。\n" +
            "<color=#ffd54f>┃10mL 即等于 100mL 普通" + src + "的效果</color>\n" +
            "<color=#ffcc80>┃瓶子容量 100mL，每次服用 10mL</color>";

        private void AddLiquid(string id, string name, Color color, string desc, LiquidType.OnDrink drink,
            float valuePerLiter = 80f)
        {
            try
            {
                Liquids.Registry[id] = new LiquidType
                {
                    localeName = id,
                    color = color,
                    valuePerLiter = valuePerLiter,
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
            "<color=#91a7ff>┃饮用(每100mL)：水合 +13，代谢强化 8 分钟\n" +
            "┃期间每秒：体重 -0.006kg ┃ 水合 -0.5</color>\n" +
            "<color=orange>┃药效内重复服用会过量，脑健康下降\n" +
            "┃配方：阿片10 + 水芦莓1 + 生化流体20 + 水70</color>";

        private static string FlushDesc() =>
            "一瓶浑浊的土黄色药水，闻起来像变质油脂。\n" +
            "<color=#91a7ff>┃饮用(每100mL)：水合 +13，强烈催吐排出肠脂\n" +
            "┃立刻：呕吐 ┃ 体重 -1.2kg ┃ 水合 -4\n" +
            "┃之后 5 分钟：体重 -0.010kg/秒 ┃ 水合 -0.45/秒</color>\n" +
            "<color=orange>┃纳曲酮诱发呕吐，这一瓶将它浓缩为主药\n" +
            "┃空腹服用容易吐到脱水，建议备水\n" +
            "┃配方：阿片10 + 纳曲酮20 + 生化流体20 + 水50</color>";

        private static string MetabDesc(string source) =>
            "一杯由" + source + "提炼的代谢药剂，入口发烫。\n" +
            "<color=#91a7ff>┃饮用(每100mL)：水合 +13，代谢强化 12 分钟\n" +
            "┃期间每秒：能量 +0.35 ┃ 体力 +0.25 ┃ 肾上腺素 +0.5\n" +
            "┃　　　　　水合 -0.9 ┃ 饥饿 -0.35 ┃ 体重 -0.004kg</color>\n" +
            "<color=#ffee58>┃配方：" + source + "90 + 生化流体10</color>\n" +
            "<color=orange>┃副作用：心跳加快、血压下降，更易口渴饥饿</color>";

        private static string MetabFlavor(int v)
        {
            switch (v)
            {
                case 0: return "\n<color=#b39ddb>┃α 型：持续更久，体力恢复更强</color>";
                case 1: return "\n<color=#90caf9>┃β 型：显著提升移动速度与跳跃</color>";
                default: return "\n<color=#ffcc80>┃ω 型：持续回复精力，抑制困倦</color>";
            }
        }

        // ---------- 弹壳收集器（原版 12 号霰弹盒的工业警戒色版本） ----------

        private void RegisterShellCollector()
        {
            try
            {
                if (Item.GlobalItems.ContainsKey(ShellCollector)) return;
                if (!Item.GlobalItems.ContainsKey("boxof12gauge"))
                { Logger.LogWarning("找不到 boxof12gauge 模板"); return; }

                var src = Item.GlobalItems["boxof12gauge"];
                var info = new ItemInfo();
                foreach (var f in typeof(ItemInfo).GetFields(BindingFlags.Public |
                             BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try { f.SetValue(info, f.GetValue(src)); } catch { }
                }

                info.fullName = "弹壳收集器";
                info.description =
                    "工业警戒色涂装的弹壳收纳盒，用来集中存放 12 号霰弹弹壳。\n" +
                    "<color=#ffd54f>┃容量 120 发</color>\n" +
                    "<color=#ffcc80>┃用 F 键标记后，附近的散装弹壳会自动装入</color>";
                info.category = "tool";
                info.weight = 0.4f;
                info.value = 22;

                ItemRegistry.Register(ShellCollector, info, HazardSprite());
                Logger.LogInfo("已注册物品: " + ShellCollector + "（弹壳收集器）");

                RecipeRegistry.Register(new Recipe
                {
                    INT = 3,
                    category = Recipes.RecipeCategory.Tools,
                    items = new List<RecipeItem> { Solid("plasticchunk") },
                    result = new RecipeResult { id = ShellCollector, amount = 1, isLiquid = false, resultCondition = 1f }
                });
                Logger.LogInfo("配方：塑料块×1 → 弹壳收集器");
            }
            catch (Exception e) { Logger.LogError("注册弹壳收集器失败: " + e); }
        }

        /// <summary>把霰弹盒贴图染成工业警戒色（黄黑相间感）</summary>
        private static Sprite HazardSprite()
        {
            try
            {
                var go = Utils.Create("boxof12gauge", Vector3.zero, 0f);
                if (go == null) return null;
                var sr = go.GetComponentInChildren<SpriteRenderer>();
                var baseSprite = sr != null ? sr.sprite : null;
                Destroy(go);
                if (baseSprite == null) return null;

                var tex = baseSprite.texture;
                var r = baseSprite.textureRect;
                int w = Mathf.Max(1, (int)r.width), h = Mathf.Max(1, (int)r.height);

                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var px = tex.GetPixels((int)r.x, (int)r.y, w, h);
                for (int i = 0; i < px.Length; i++)
                {
                    int y = i / w;
                    // 斜条纹：每 8 像素交替黄/黑
                    bool yellow = ((i % w) + y) / 8 % 2 == 0;
                    float lum = px[i].r * 0.3f + px[i].g * 0.59f + px[i].b * 0.11f;
                    px[i] = yellow
                        ? new Color(Mathf.Clamp01(lum * 1.4f), Mathf.Clamp01(lum * 1.1f), lum * 0.15f, px[i].a)
                        : new Color(lum * 0.25f, lum * 0.25f, lum * 0.25f, px[i].a);
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
                Logger.LogWarning("弹壳收集器染色失败: " + e.Message);
                return null;
            }
        }

        // ---------- 药瓶（照止痛药结构，10 次用量） ----------

        private void RegisterBottles()
        {
            try
            {
                var src = Item.GlobalItems.ContainsKey("painkillers")
                    ? Item.GlobalItems["painkillers"] as LiquidItemInfo : null;
                if (src == null) { Logger.LogWarning("找不到止痛药模板，跳过药瓶注册"); return; }

                AddBottle(DietBottle, DietId, 10f, "减肥药瓶",
                    "浓缩的减肥药剂，装在标准的药物瓶里。\n" +
                    "<color=#91a7ff>┃每次服用 10mL，共 10 次</color>\n" +
                    "<color=orange>味道像嚼了一口酸涩的树皮。</color>", src, new Color(0.72f, 0.86f, 0.42f));

                AddBottle(FlushBottle, FlushId, 10f, "排脂剂瓶",
                    "浓缩的排脂药剂，土黄色的浑浊液体。\n" +
                    "<color=#91a7ff>┃每次服用 10mL，共 10 次</color>\n" +
                    "<color=orange>喝下去三秒后开始反胃。</color>", src, new Color(0.95f, 0.78f, 0.36f));

                var tints = new[] { new Color(0.62f, 0.36f, 0.88f), new Color(0.36f, 0.66f, 0.95f), new Color(0.60f, 0.36f, 0.18f) };
                string[] names = { "代谢剂瓶·α（能量）", "代谢剂瓶·β（运动）", "代谢剂瓶·ω（咖啡）" };
                string[] srcName = { "能量饮料", "运动饮料", "咖啡" };
                for (int i = 0; i < 3; i++)
                    AddBottle(MetaBottles[i], MetabIds[i], 10f, names[i],
                        "由" + srcName[i] + "浓缩而成的代谢加速药剂。\n" +
                        "<color=#91a7ff>┃每次服用 10mL，共 10 次</color>\n" +
                        "<color=#c9a0ff>入口发烫，心跳会明显加快。</color>", src, tints[i]);

                // —— 浓缩瓶：瓶内只有 10mL，但每次服用 10mL 即等于 100mL 普通液 ——
                AddBottle(DietConcBottle, DietConc, 10f, "减肥药瓶·浓缩",
                    "火源浓缩过的减肥药，只剩十分之一体积，效力不变。\n" +
                    "<color=#ffd54f>┃每次服用 10mL，等于 100mL 普通减肥药</color>\n" +
                    "<color=#ffcc80>┃喝空后瓶子会自动销毁</color>", src,
                    new Color(0.55f, 0.72f, 0.25f), 10f);

                AddBottle(FlushConcBottle, FlushConc, 10f, "排脂剂瓶·浓缩",
                    "火源浓缩过的排脂剂，只剩十分之一体积，效力不变。\n" +
                    "<color=#ffd54f>┃每次服用 10mL，等于 100mL 普通排脂剂</color>\n" +
                    "<color=#ffcc80>┃喝空后瓶子会自动销毁</color>", src,
                    new Color(0.60f, 0.52f, 0.12f), 10f);

                string[] concNames = { "代谢剂瓶·浓缩α", "代谢剂瓶·浓缩β", "代谢剂瓶·浓缩ω" };
                Color[] concTints =
                {
                    new Color(0.62f, 0.36f, 0.88f),   // α 紫
                    new Color(0.36f, 0.66f, 0.95f),   // β 蓝
                    new Color(0.60f, 0.36f, 0.18f)    // ω 棕
                };
                for (int i = 0; i < 3; i++)
                    AddBottle(MetaConcBottles[i], MetaConcs[i], 10f, concNames[i],
                        "火源浓缩过的代谢剂，只剩十分之一体积，效力不变。\n" +
                        "<color=#ffd54f>┃每次服用 10mL，等于 100mL 普通代谢剂</color>\n" +
                        "<color=#ffcc80>┃喝空后瓶子会自动销毁</color>", src,
                        concTints[i], 10f);
            }
            catch (Exception e) { Logger.LogError("注册药瓶失败: " + e); }
        }

        private void AddBottle(string id, string liquidId, float perUse, string name, string desc,
            LiquidItemInfo template, Color tint, float contentsMl = 100f)
        {
            if (Item.GlobalItems.ContainsKey(id)) { Logger.LogInfo("药瓶已存在，跳过: " + id); return; }

            var info = new LiquidItemInfo();
            // 反射复制模板的全部字段（避免遗漏未知语义字段）
            var t = typeof(LiquidItemInfo);
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public |
                                          System.Reflection.BindingFlags.NonPublic |
                                          System.Reflection.BindingFlags.Instance))
            {
                try { f.SetValue(info, f.GetValue(template)); } catch { }
            }

            info.fullName = name;
            info.description = desc;
            info.weight = 0.1f;
            info.combineable = true;
            info.usable = true;
            info.capacity = 100f;
            info.defaultContents = new List<LiquidStack> { new LiquidStack(liquidId, contentsMl) };
            info.useAction = (body, item) =>
            {
                var wc = item.GetComponent<WaterContainerItem>();
                if (wc == null) return;
                FromBottle = true;                 // 瓶装版不提供运动饮料级补水
                try { wc.Drink(body, perUse); }
                finally { FromBottle = false; }
            };

            ItemRegistry.Register(id, info, TintedBottle(tint));
            LocaleRegistry.Register("other", id + "dsc", desc);
            Logger.LogInfo("已注册药瓶: " + id + "（" + name + "，每次 " + perUse + "mL）");
        }

        private static Sprite _baseBottle;

        /// <summary>取止痛药瓶的原始贴图（只取一次）</summary>
        private static Sprite BaseBottle()
        {
            if (_baseBottle != null) return _baseBottle;
            try
            {
                var go = Resources.Load<GameObject>("painkillers");
                if (go != null)
                {
                    var sr = go.GetComponent<SpriteRenderer>();
                    if (sr != null && sr.sprite != null) { _baseBottle = sr.sprite; return _baseBottle; }
                }
            }
            catch { }

            try
            {
                var inst = Utils.Create("painkillers", Vector3.zero, 0f);
                if (inst != null)
                {
                    var sr = inst.GetComponentInChildren<SpriteRenderer>();
                    if (sr != null) _baseBottle = sr.sprite;
                    Destroy(inst);
                }
            }
            catch { }
            return _baseBottle;
        }

        /// <summary>把瓶子贴图整体染色（保留明暗，用于区分三种药）</summary>
        private static Sprite TintedBottle(Color tint)
        {
            var baseSprite = BaseBottle();
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
                    int y = i / w;
                    float lum = px[i].r * 0.3f + px[i].g * 0.59f + px[i].b * 0.11f;
                    float shade = 0.45f + lum * 0.75f;      // 保留色相，只压明暗

                    if (y > h * 0.80f - 1f)   // 比之前高一层像素
                    {
                        // 顶部瓶盖：塑料白
                        float capShade = 0.75f + lum * 0.35f;
                        px[i] = new Color(0.95f * capShade, 0.95f * capShade, 0.93f * capShade, px[i].a);
                    }
                    else
                    {
                        px[i] = new Color(tint.r * shade, tint.g * shade, tint.b * shade, px[i].a);
                    }
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
                Logger.LogWarning("药瓶染色失败，改用原贴图: " + e.Message);
                return baseSprite;
            }
        }

        // ---------- 饮用回调（剂量按 ml/100 缩放） ----------

        /// <summary>饮用补水：与运动饮料一致，每 100mL 提供 +13 水合</summary>
        private static void Hydrate(Body body, float ml)
        {
            if (Plugin.FromBottle) return;   // 瓶装/浓缩版不补水
            try { body.Drink(130f * ml * 0.001f); }
            catch (Exception e) { Plugin.Logger.LogWarning("补水失败: " + e.Message); }
        }

        private static void OnDietDrink(float ml, Body body)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            Plugin.Logger.LogInfo("饮用减肥药 " + ml.ToString("F0") + "mL");
            Hydrate(body, ml);   // 每 100mL +13 水（同运动饮料）
            DrugState.Dose(DietId, Plugin.DietDuration.Value * f, ml, 0.02f);
            EffectRunner.Run(DietId, body, Plugin.DietDuration.Value * f, (b, dt, t) =>
            {
                DrugState.TickOverdose(b, DietId, dt);
                DrugMath.AddWeight(b, -Plugin.DietWeightLoss.Value * dt);
                DrugMath.SyncVisual(b, 0.01f);
                DrugMath.AddThirst(b, -Plugin.DietThirst.Value * dt);   // 原版衰减被冻结，这里只扣药物自身的量
            });
        }

        private static void OnFlushDrink(float ml, Body body)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            Plugin.Logger.LogInfo("饮用排脂剂 " + ml.ToString("F0") + "mL");
            Hydrate(body, ml);
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
            try { DrugMath.AddWeight(body, -1.6f * f); }
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

        private static void OnMetaDrink(float ml, Body body, int variant = 0)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            float dur = Plugin.MetaDuration.Value * f;
            if (variant == 0) dur *= 1.25f;                  // α：持续更久
            if (variant == 0) dur *= 1.25f;                  // α：持续更久
            Plugin.Logger.LogInfo("饮用新陈代谢剂 " + ml.ToString("F0") + "mL");
            Hydrate(body, ml);
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
                // 真实水分收支（每帧）：
                //   脂肪产水 = 面板减重速率 × 脂肪占比 × 1.07（1kg 脂肪≈1.07kg 水，1kg 水=100 水合）
                //   出汗失水 = 湿度 × 0.0072 水合/秒（蒸发降温反推）
                //   净变化恒为负或零：产水被限制在出汗量以内，绝不允许涨水
                float panelKgPerSec = Plugin.MetaWeight.Value * (Plugin.PanelUnits.Value ? 1f : 0.34f);
                float fatPerSec = panelKgPerSec * Mathf.Clamp01(Plugin.FatFraction.Value);
                float fatWater = fatPerSec * 1.07f * 100f;              // 水合/秒

                float wetNow = DrugMath.Get(b, "wetness");
                float sweatWater = float.IsNaN(wetNow) ? 0f : wetNow * 0.0072f;   // 水合/秒

                float netWater = fatWater - sweatWater;                 // 负=净流失
                if (netWater < 0f) DrugMath.AddThirst(b, netWater * dt);

                if (t - _lastWaterLog >= 60f)
                {
                    _lastWaterLog = t;
                    Plugin.Logger.LogInfo(string.Format(
                        "[代谢剂·水 {0:F0}s] 出汗 -{1:F3}/秒 ｜脂肪产水 +{2:F3}/秒 ｜净 {3:F3}/秒 ｜已累计 {4:F1} 水合",
                        t, sweatWater, fatWater, netWater, netWater * t));
                }
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

                switch (variant)
                {
                    case 0:  // α 能量型：额外体力恢复
                        DrugMath.AddStamina(b, 0.12f * dt);
                        break;
                    case 1:  // β 运动型：维持兴奋剂倍率（游戏自带的移速/攻速加成通道）
                        DrugMath.KeepStimulant(b, dt, 0.35f);
                        break;
                    default: // γ 咖啡型：回复精力、抑制困倦
                        DrugMath.AddEnergy(b, 0.10f * dt);
                        break;
                }

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

        // ---------- 纯测试药（只有减重） ----------

        private static void OnTestDiet(float ml, Body body)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            float dur = Plugin.DietDuration.Value * f;
            Plugin.Logger.LogInfo("[测试药·减肥] " + ml.ToString("F0") + "mL，持续 " + Mathf.RoundToInt(dur) + "s，预期减重 "
                + (Plugin.DietWeightLoss.Value * dur).ToString("F2") + "kg");
            EffectRunner.Run(TestDiet, body, dur, (b, dt, t) =>
            {
                DrugMath.AddWeight(b, -Plugin.DietWeightLoss.Value * dt);
            });
        }

        private static void OnTestFlush(float ml, Body body)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            Plugin.Logger.LogInfo("[测试药·排脂] " + ml.ToString("F0") + "mL，瞬时 -1.60kg 后持续减重，不触发呕吐");
            try { DrugMath.AddWeight(body, -1.6f * f); } catch { }
            EffectRunner.Run(TestFlush, body, 300f * f, (b, dt, t) =>
            {
                DrugMath.AddWeight(b, -0.0063f * dt);
            });
        }

        private static void OnTestMeta(float ml, Body body)
        {
            float f = Mathf.Clamp(ml / 100f, 0.05f, 3f);
            float dur = Plugin.MetaDuration.Value * f;
            Plugin.Logger.LogInfo("[测试药·代谢] " + ml.ToString("F0") + "mL，持续 " + Mathf.RoundToInt(dur) + "s，预期减重 "
                + (Plugin.MetaWeight.Value * dur).ToString("F2") + "kg");
            EffectRunner.Run(TestMeta, body, dur, (b, dt, t) =>
            {
                DrugMath.AddWeight(b, -Plugin.MetaWeight.Value * dt);
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

                // 旧的「药液 + 火源 → 药瓶」装填配方已移除，避免与瓶装/浓缩配方重复

                // 浓缩：100mL 普通药液 + 火源 → 浓缩瓶（内含 10mL，效力等同 100mL）
                RegisterConcRecipe(DietConcBottle, DietId, 5);
                RegisterConcRecipe(FlushConcBottle, FlushId, 6);
                RegisterConcRecipe(MetaConcBottles[0], MetabIds[0], 7);
                RegisterConcRecipe(MetaConcBottles[1], MetabIds[1], 7);
                RegisterConcRecipe(MetaConcBottles[2], MetabIds[2], 7);

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

                float real = DrugMath.GetRealWeight(body);
                Logger.LogInfo("[体重调试] " + baseMass + " ｜ " + Fld("weightOffset") + " ｜ "
                    + Fld("weightVisualOffset") + " ｜ " + prop
                    + " ｜ 肢体和+偏移=" + (float.IsNaN(real) ? "?" : real.ToString("F2")));
            }
            catch (Exception e) { Logger.LogWarning("体重诊断失败: " + e.Message); }
        }

        /// <summary>weightfix &lt;kg&gt;：把体重设为指定值（测试用）</summary>
        internal static void FixWeight(string[] args)
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) { Logger.LogWarning("不在游戏中"); return; }
                if (args == null || args.Length == 0 || !float.TryParse(args[0], out float target))
                {
                    Logger.LogWarning("用法: weightfix 45");
                    return;
                }

                var limbs = DrugMath.GetLimbs(body);
                var fm = typeof(Limb).GetField("baseMass",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (limbs == null || fm == null) { Logger.LogWarning("拿不到 limbs"); return; }

                float sum = 0f;
                foreach (var l in limbs) if (l != null) sum += (float)fm.GetValue(l);

                float newOffset = target - sum;
                var fo = body.GetType().GetField("weightOffset",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var fv = body.GetType().GetField("weightVisualOffset",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                fo?.SetValue(body, newOffset);
                fv?.SetValue(body, 0f);            // 面板公式里 visual 必须归零

                Logger.LogInfo("[体重修正] 肢体和=" + sum.ToString("F2") + "，偏移设为 " + newOffset.ToString("F2")
                    + " → 目标体重 " + target.ToString("F1") + "kg");
            }
            catch (Exception e) { Logger.LogWarning("体重修正失败: " + e.Message); }
        }

        /// <summary>打印体重构成：每个肢体的质量、偏移、以及各种算法得到的合计</summary>
        internal static void DumpWeightParts()
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) { Logger.LogWarning("不在游戏中"); return; }
                DumpWeightToLog(body, "手动");
            }
            catch (Exception e) { Logger.LogWarning("体重构成打印失败: " + e.Message); }
        }

        /// <summary>把体重相关的全部数字打进日志</summary>
        internal static void DumpWeightToLog(Body body, string tag)
        {
            try
            {

                var limbs = DrugMath.GetLimbs(body);
                if (limbs == null) { Logger.LogWarning("拿不到 limbs"); return; }

                var fm = typeof(Limb).GetField("baseMass",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                float sum = 0f;
                var parts = new List<string>();
                foreach (var l in limbs)
                {
                    if (l == null || fm == null) continue;
                    float m = (float)fm.GetValue(l);
                    sum += m;
                    parts.Add((l.name ?? "?").Replace("(Clone)", "") + "=" + m.ToString("F2"));
                }

                float off = DrugMath.Get(body, "weightOffset");
                float vis = DrugMath.Get(body, "weightVisualOffset");
                float bm = DrugMath.Get(body, "baseMass");

                Logger.LogInfo("[体重构成] 肢体: " + string.Join(" + ", parts.ToArray()));
                Logger.LogInfo("[体重构成] 肢体合计=" + sum.ToString("F2")
                    + " ｜ baseMass=" + bm.ToString("F2")
                    + " ｜ offset=" + off.ToString("F2")
                    + " ｜ visual=" + vis.ToString("F2"));
                Logger.LogInfo("[" + tag + "·体重] 面板公式(50+off*0.34+vis)=" + (50f + off * 0.34f + vis).ToString("F2")
                    + " ｜ 肢体和+offset=" + (sum + off).ToString("F2")
                    + " ｜ baseMass+offset=" + (bm + off).ToString("F2")
                    + " ｜ baseMass+visual=" + (bm + vis).ToString("F2"));

                // 按原版 MoodleManager 的阈值反推状态档（只看 offset）
                string tier = off <= -50f ? "消瘦(underweight4)"
                    : off < -40f ? "营养不良(underweight3)"
                    : off < -30f ? "瘦身(underweight2)"
                    : off < -15f ? "体重偏轻(underweight1)"
                    : off >= 50f ? "肥胖(overweight4)"
                    : off > 40f ? "超重(overweight3)"
                    : "正常";
                Logger.LogInfo("[" + tag + "·体重] offset=" + off.ToString("F2") + " → 状态档: " + tier
                    + " ｜ 饥饿 " + DrugMath.Get(body, "hunger").ToString("F1")
                    + " ｜ 水合 " + DrugMath.Get(body, "thirst").ToString("F1")
                    + " ｜ 体温 " + DrugMath.Get(body, "temperature").ToString("F2")
                    + " ｜ 反胃 " + DrugMath.Get(body, "sicknessAmount").ToString("F1"));
            }
            catch (Exception e) { Logger.LogWarning("体重打印失败: " + e.Message); }
        }

        /// <summary>暴力扫描：把 Body 上所有落在 20~60 的浮点字段/属性都列出来，与面板数字对照</summary>
        internal static void ScanWeights()
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) { Logger.LogWarning("不在游戏中"); return; }

                const System.Reflection.BindingFlags BF =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;

                var found = new List<string>();
                for (var t = body.GetType(); t != null && t != typeof(object); t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BF | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (f.FieldType != typeof(float) && f.FieldType != typeof(int)) continue;
                        try
                        {
                            double v = Convert.ToDouble(f.GetValue(body));
                            if (v > 20 && v < 60) found.Add(f.Name + "=" + v.ToString("F2"));
                        }
                        catch { }
                    }
                    foreach (var pi in t.GetProperties(BF | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        if (!pi.CanRead) continue;
                        if (pi.PropertyType != typeof(float) && pi.PropertyType != typeof(int)) continue;
                        try
                        {
                            double v = Convert.ToDouble(pi.GetValue(body, null));
                            if (v > 20 && v < 60) found.Add(pi.Name + "(属性)=" + v.ToString("F2"));
                        }
                        catch { }
                    }
                }
                Logger.LogInfo("[体重扫描] 共 " + found.Count + " 个候选: " + string.Join(" ｜ ", found.ToArray()));
            }
            catch (Exception e) { Logger.LogWarning("扫描失败: " + e.Message); }
        }

        /// <summary>装填：药液 100mL + 火源 → 药瓶（可反复制作，相当于加热灌装）</summary>
        /// <summary>药瓶配方：对应液体 100mL → 一瓶（浓缩灌装）</summary>
        /// <summary>浓缩配方：100mL 普通液 + 火源 → 浓缩瓶（10mL）</summary>
        private void RegisterConcRecipe(string concBottleId, string liquidId, int level)
        {
            try
            {
                RecipeRegistry.Register(new Recipe
                {
                    INT = level,
                    category = Recipes.RecipeCategory.Food,     // 与热源相关，统一放食物栏
                    items = new List<RecipeItem>
                    {
                        Liquid(liquidId, 100f),
                        new RecipeItem(0f) { specific = false, quality = new CraftingQuality("heatsource", 1f), destroyItem = false }
                    },
                    // dontDrainResultLiquid：否则产物会被清空并置 condition=0（直接销毁）
                    result = new RecipeResult
                    {
                        id = concBottleId, amount = 1, isLiquid = false,
                        resultCondition = 1f, dontDrainResultLiquid = true
                    }
                });
                Logger.LogInfo("浓缩配方：" + liquidId + " 100mL + 火源 → " + concBottleId);
            }
            catch (Exception e) { Logger.LogWarning("浓缩配方注册失败 " + concBottleId + ": " + e.Message); }
        }

        /// <summary>兼容两种传参：数组含命令名或以命令名开头时，去掉第一项</summary>
        private static string[] StripCommandName(string[] a, string cmd)
        {
            if (a == null || a.Length == 0) return a;
            if (a[0] != null && a[0].TrimStart('/').Equals(cmd, StringComparison.OrdinalIgnoreCase))
            {
                var r = new string[a.Length - 1];
                Array.Copy(a, 1, r, 0, r.Length);
                return r;
            }
            return a;
        }

        /// <summary>列出场景里所有商人的货架，标出含 Mod 枪的</summary>
        private void DumpAllTraders()
        {
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType("TraderScript"); if (t != null) break; } catch { }
                }
                if (t == null) { Logger.LogWarning("找不到 TraderScript"); return; }

                var all = Resources.FindObjectsOfTypeAll(t);
                var fi = t.GetField("items", System.Reflection.BindingFlags.Public |
                                             System.Reflection.BindingFlags.NonPublic |
                                             System.Reflection.BindingFlags.Instance);
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                Vector3 me = body != null ? body.transform.position : Vector3.zero;

                int total = 0, withGun = 0, withBottle = 0;
                foreach (var o in all)
                {
                    var mb = o as MonoBehaviour;
                    if (mb == null) continue;
                    if (!mb.gameObject.scene.IsValid()) continue;      // 排除预制体资源

                    total++;
                    var list = fi?.GetValue(o) as System.Collections.IList;
                    var ids = new List<string>();
                    int guns = 0, bottles = 0;
                    if (list != null)
                    {
                        foreach (var it in list)
                        {
                            if (it == null) continue;
                            var id = it.GetType().GetField("id")?.GetValue(it) as string;
                            if (string.IsNullOrEmpty(id)) continue;
                            ids.Add(NameOf(id) + "(" + id + ")");
                            if (IsModGun(id)) guns++;
                            if (id.Contains("drug_bottle")) bottles++;
                        }
                    }
                    if (guns > 0) withGun++;
                    if (bottles > 0) withBottle++;

                    float d = Vector3.Distance(mb.transform.position, me);
                    Logger.LogInfo("[商人] " + mb.transform.position.x.ToString("F0") + ","
                        + mb.transform.position.y.ToString("F0") + " ｜ 距离 " + d.ToString("F0") + "米"
                        + " ｜ " + ids.Count + " 件"
                        + (guns > 0 ? "  ★Mod枪" + guns : "")
                        + (bottles > 0 ? "  ◆药瓶" + bottles : ""));
                    Logger.LogInfo("      " + string.Join(", ", ids.ToArray()));
                }
                Logger.LogInfo("[商人汇总] 场景内共 " + total + " 个商人，含Mod枪 " + withGun
                    + " 个，含药瓶 " + withBottle + " 个");
                Logger.LogInfo("[提示] 只统计已加载区块内的商人；未加载区域的商人不在场景中");
            }
            catch (Exception e) { Logger.LogWarning("商人列表失败: " + e.Message); }
        }

        /// <summary>打印掉落池：各分类数量，以及 Mod 枪是否在内</summary>
        private void DumpPool(string[] a)
        {
            try
            {
                Type t = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { t = asm.GetType("ItemLootPool"); if (t != null) break; } catch { }
                }
                if (t == null) { Logger.LogWarning("找不到 ItemLootPool"); return; }

                var fp = t.GetField("pool", System.Reflection.BindingFlags.Public |
                                            System.Reflection.BindingFlags.NonPublic |
                                            System.Reflection.BindingFlags.Static);
                var pool = fp?.GetValue(null) as System.Collections.IDictionary;
                if (pool == null) { Logger.LogWarning("pool 为空（尚未初始化）"); return; }

                string only = (a != null && a.Length > 0) ? a[0].ToLowerInvariant() : null;
                var summary = new List<string>();
                int cats = 0, ids = 0;
                var gunHits = new List<string>();

                foreach (DictionaryEntry e in pool)
                {
                    string cat = (e.Key as string) ?? "?";
                    if (only != null && !cat.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;

                    var list = e.Value as System.Collections.IList;
                    int n = list != null ? list.Count : 0;
                    cats++; ids += n;
                    summary.Add(cat + "=" + n);

                    if (list != null)
                    {
                        foreach (var o in list)
                        {
                            var id = o as string;
                            if (id != null && IsModGun(id) && !gunHits.Contains(id)) gunHits.Add(id);
                        }
                    }
                }

                Logger.LogInfo("[掉落池] 分类 " + cats + " 个，共 " + ids + " 项 ｜ "
                    + string.Join("  ", summary.ToArray()));
                Logger.LogInfo("[掉落池] Mod枪: "
                    + (gunHits.Count > 0 ? string.Join("、", gunHits.ToArray()) : "无")
                    + "（共 " + gunHits.Count + "/" + GunIds.Length + " 把在池中）");
            }
            catch (Exception e) { Logger.LogWarning("池子查询失败: " + e.Message); }
        }

        private static readonly List<string> _gunCopies = new List<string>();

        /// <summary>
        /// 用原 id 重新走一遍 CUCoreLib 注册，把图标补上。
        /// 「副本」实验已证明：有图标的物品在商人界面能正常显示，原枪缺图标则整栏空白。
        /// </summary>
        /// <summary>
        /// 控制变量实验：把 mod 枪的 ItemInfo 整体复制，用「药瓶同款」的
        /// ItemRegistry.Register 注册成新物品。若副本能在商人界面显示、
        /// 原枪不能，则问题100%在注册路径。
        /// </summary>
        /// <summary>从 GunScript 实例读数值，生成和原版一致的说明块</summary>
        /// <summary>在脚下生成一把 Mod 枪，验证物品本身能否实例化</summary>
        /// <summary>把最近商人的全部货架换成 Mod 枪，用于确认 UI 到底能不能显示它们</summary>
        private static Type _customInstType;
        private static MethodInfo _customInstM;

        /// <summary>用 CUCoreLib 的实例化（商人同款通道）生成物品，失败再退回 Utils.Create</summary>
        /// <summary>原版步枪 vs Mod 半自动步枪（拦截者）+ 4 个满弹匣 + 智力经验</summary>
        /// <summary>打印原版枪械的 barrel / muzzleParticle 局部坐标，作为 mod 枪配置的参照</summary>
        private static readonly Dictionary<int, int> _lastRoundsMap = new Dictionary<int, int>();

        /// <summary>任意一把枪弹数下降时记录一条，输出该枪的 id 与两组坐标</summary>
        /// <summary>记录弹道起点与火光节点的坐标关系，用于定位偏移</summary>
        /// <summary>扫描并删除所有容器里的测试药液</summary>
        internal static void CleanTestLiquidsPublic() => CleanTestLiquids();

        private static void CleanTestLiquids()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType<WaterContainerItem>();
                int containers = 0, removed = 0;
                float ml = 0f;

                foreach (var w in all)
                {
                    if (w == null || w.stack == null) continue;
                    bool touched = false;

                    for (int i = w.stack.Count - 1; i >= 0; i--)
                    {
                        var st = w.stack[i];
                        if (st == null || string.IsNullOrEmpty(st.liquidId)) continue;
                        if (!st.liquidId.StartsWith("test_weight_", StringComparison.Ordinal)) continue;

                        ml += st.amount;
                        w.stack.RemoveAt(i);
                        removed++;
                        touched = true;
                    }
                    if (touched) containers++;
                }

                Logger.LogInfo("[清理] 扫描 " + all.Length + " 个容器，清除 " + removed + " 处测试药液（"
                    + ml.ToString("F0") + "mL），涉及 " + containers + " 个容器");
                if (removed == 0) Logger.LogInfo("[清理] 没有发现残留");
            }
            catch (Exception e) { Logger.LogWarning("清理失败: " + e.Message); }
        }

        /// <summary>打印 Mod 枪的 ItemInfo 关键字段——决定商人能不能刷出它们</summary>
        /// <summary>打印最近一个商人的货架（验证自定义物品是否进货）</summary>
        private void DumpTrader()
        {
            try
            {
                Type type = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { type = asm.GetType("TraderScript"); if (type != null) break; } catch { }
                }
                if (type == null) { Logger.LogWarning("找不到 TraderScript"); return; }

                var arr = UnityEngine.Object.FindObjectsOfType(type);
                if (arr == null || arr.Length == 0) { Logger.LogWarning("场景里没有商人"); return; }

                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                Vector3 me = body != null ? body.transform.position : Vector3.zero;

                object best = null; float bestD = float.MaxValue;
                foreach (var t in arr)
                {
                    var mb = t as MonoBehaviour;
                    if (mb == null) continue;
                    float d = Vector3.Distance(mb.transform.position, me);
                    if (d < bestD) { bestD = d; best = t; }
                }
                if (best == null) { Logger.LogWarning("没找到可用商人"); return; }

                var fi = type.GetField("items", System.Reflection.BindingFlags.Public |
                                                 System.Reflection.BindingFlags.NonPublic |
                                                 System.Reflection.BindingFlags.Instance);
                var list = fi?.GetValue(best) as System.Collections.IList;
                if (list == null) { Logger.LogWarning("拿不到货架列表"); return; }

                Logger.LogInfo("[商人货架] 距离 " + bestD.ToString("F1") + " 米，共 " + list.Count + " 件：");
                int guns = 0, vanGuns = 0, ours = 0;
                foreach (var it in list)
                {
                    if (it == null) continue;
                    var idf = it.GetType().GetField("id");
                    var vf = it.GetType().GetField("value");
                    string id = idf?.GetValue(it) as string ?? "?";
                    object val = vf?.GetValue(it);
                    Logger.LogInfo("    " + id + "  (价值 " + val + ")");
                    if (IsModGun(id)) guns++;
                    else if (id == "shotgun" || id == "rifle" || id == "pistol") vanGuns++;
                    if (id.Contains("dietdrug_bottle") || id.Contains("flushdrug_bottle") || id.Contains("metadrug_bottle")) ours++;
                }
                Logger.LogInfo("[商人货架] Mod枪 " + guns + " 件，原版枪 " + vanGuns + " 件，本模组药瓶 " + ours + " 件");
            }
            catch (Exception e) { Logger.LogWarning("商人扫描失败: " + e.Message); }
        }

        /// <summary>在玩家脚下生成药瓶，验证自定义物品能否正常实例化</summary>
        private static void DropBottle(string[] a)
        {
            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) { Logger.LogWarning("不在游戏中"); return; }

                string k = (a != null && a.Length > 0) ? a[0].ToLowerInvariant() : "diet";
                string id = k == "flush" ? FlushBottle
                    : k == "meta_b" ? MetaBottles[1]
                    : k == "meta_w" ? MetaBottles[2]
                    : k == "meta_a" ? MetaBottles[0]
                    : DietBottle;

                var pos = body.transform.position + new Vector3(0f, 1.2f, 0f);
                var go = Utils.Create(id, pos, 0f);
                if (go == null) { Logger.LogWarning("生成失败: " + id + "（预制体缺失）"); return; }
                Logger.LogInfo("已在脚下生成: " + id);
            }
            catch (Exception e) { Logger.LogWarning("生成异常: " + e.Message); }
        }

        /// <summary>Wl <diet|flush|meta> [a|b|w]</summary>
        private static void WlCommand(string[] a)
        {
            a = StripCommandName(a, "wl");
            string sub = (a != null && a.Length > 0) ? a[0].ToLowerInvariant() : "diet";
            switch (sub)
            {
                case "diet": GiveLiquid(DietId, 100f); break;
                case "flush": GiveLiquid(FlushId, 100f); break;
                case "meta":
                case "metab":
                    string v = (a.Length > 1) ? a[1].ToLowerInvariant() : "a";
                    int idx = v == "b" ? 1 : v == "w" ? 2 : 0;
                    GiveLiquid(MetabIds[idx], 100f);
                    break;
                default:
                    Plugin.Logger.LogWarning("用法: Wl diet | Wl flush | Wl meta <a|b|w>");
                    break;
            }
        }

        /// <summary>Tl <diet|flush|meta></summary>
        private static void TlCommand(string[] a)
        {
            a = StripCommandName(a, "tl");
            string sub = (a != null && a.Length > 0) ? a[0].ToLowerInvariant() : "diet";
            switch (sub)
            {
                case "diet": GiveTest(TestDiet); break;
                case "flush": GiveTest(TestFlush); break;
                case "meta": GiveTest(TestMeta); break;
                default: Plugin.Logger.LogWarning("用法: Tl diet | Tl flush | Tl meta"); break;
            }
        }

        private static void GiveTest(string id)
        {
            if (!Plugin.EnableTestDrugs.Value)
            {
                Plugin.Logger.LogWarning("测试用药未启用，请先在配置文件中把 EnableTestDrugs 设为 true 并重启");
                return;
            }
            GiveLiquid(id, 100f);
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

    /// <summary>
    /// 常驻体重同步：把界面用的 weightVisualOffset 持续对齐到真实值 weightOffset。
    /// 之前只在药效协程里同步，药效一结束就没人管了，面板会长期漂移
    /// （实测出现过真实 44.8kg 而面板显示 29.6kg 的情况）。
    /// </summary>
    /// <summary>
    /// 自动装填：手持弹匣供弹的枪、弹匣已空时，把附近匹配的弹匣装进去。
    /// 走的是原版 GunScript.LoadMag，不伪造数据。
    /// </summary>
    /// <summary>
    /// 自动压弹：把附近散装子弹填进未装满的弹匣。
    /// 走原版 AmmoScript.LoadRound —— 每颗散弹消耗 +1 发，不伪造数值。
    /// （弹匣打空后自动换新弹匣是物品栏 mod 的功能，这里不做）
    /// </summary>
    internal class AutoMagLoader : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (!Plugin.AutoLoad.Value) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;

            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) return;

                // 弹壳收集器容量修正（原版盒子上限 25，这里拉到 120）
                foreach (var am in UnityEngine.Object.FindObjectsOfType<AmmoScript>())
                {
                    if (am == null) continue;
                    var ai = am.GetComponent<Item>();
                    if (ai == null || ai.id != Plugin.ShellCollector) continue;
                    if (am.maxRounds != 120) { am.maxRounds = 120; am.rounds = Mathf.Min(am.rounds, 120); }
                }

                float r = Plugin.AutoLoadRadius.Value;
                var all = UnityEngine.Object.FindObjectsOfType<AmmoScript>();
                if (all == null || all.Length == 0) return;

                var mags = new List<AmmoScript>();
                var rounds = new List<AmmoScript>();
                foreach (var a in all)
                {
                    if (a == null) continue;
                    if (Vector3.Distance(a.transform.position, body.transform.position) > r) continue;
                    if (a.itemType == AmmoScript.AmmoItemType.Magazine)
                    {
                        if (a.rounds >= a.maxRounds) continue;        // 已满
                        var it = a.GetComponent<Item>();
                        if (it == null || !it.favourited) continue;   // 只处理 F 键标记的弹匣
                        mags.Add(a);
                    }
                    else rounds.Add(a);
                }
                if (mags.Count == 0 || rounds.Count == 0) return;   // 无标记弹匣或无散弹，直接跳过

                int loaded = 0;
                foreach (var mag in mags)
                {
                    foreach (var rd in rounds)
                    {
                        if (rd == null) continue;                  // 已被消耗
                        if (mag == null || mag.rounds >= mag.maxRounds) break;
                        if (rd.ammoType != mag.ammoType) continue;

                        int before = mag.rounds;
                        mag.LoadRound(rd);                         // 原版：消耗一颗散弹，弹匣 +1
                        if (mag.rounds > before) loaded++;
                        if (loaded >= 30) break;                   // 每次最多压 30 发，避免刷屏
                    }
                    if (loaded >= 30) break;
                }

                if (loaded > 0)
                    Plugin.Logger.LogInfo("[自动压弹] 本次填入 " + loaded + " 发（仅标记弹匣，半径 "
                        + r.ToString("F0") + " 米，弹匣 " + mags.Count + " 个待装）");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("自动压弹异常: " + e.Message); }
        }
    }

    internal class WeightSync : MonoBehaviour
    {
        private float _next;
        private float _watchNext;
        private float _lastOffset = float.NaN;
        private Body _watched;

        private bool _dumpedOnce;
        private static bool _gunsFixed;
        private float _autoNext;

        private float _bottleNext;
        private static readonly HashSet<string> BottleIds = new HashSet<string>
        {
            Plugin.DietBottle, Plugin.FlushBottle,
            Plugin.MetaBottles[0], Plugin.MetaBottles[1], Plugin.MetaBottles[2],
            Plugin.DietConcBottle, Plugin.FlushConcBottle,
            Plugin.MetaConcBottles[0], Plugin.MetaConcBottles[1], Plugin.MetaConcBottles[2]
        };

        /// <summary>药瓶喝空后自动销毁（每 2 秒检查一次）</summary>
        private void CheckEmptyBottles()
        {
            if (Time.unscaledTime < _bottleNext) return;
            _bottleNext = Time.unscaledTime + 2f;

            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) return;

                List<Item> dead = null;
                foreach (var item in body.GetAllItemsThorough())
                {
                    if (item == null || !BottleIds.Contains(item.id)) continue;
                    var wc = item.GetComponent<WaterContainerItem>();
                    if (wc == null || wc.stack == null) continue;

                    float total = 0f;
                    foreach (var st in wc.stack) if (st != null) total += st.amount;
                    if (total > 0.05f) continue;

                    (dead ??= new List<Item>()).Add(item);
                }

                if (dead == null) return;
                foreach (var it in dead)
                {
                    Plugin.Logger.LogInfo("[药瓶] 已空，销毁: " + it.id);
                    Destroy(it.gameObject);
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("空瓶检查失败: " + e.Message); }
        }

        private void AutoDump()
        {
            if (_dumpedOnce) return;              // 只做一次，避免周期性反射开销
            if (Time.unscaledTime < _autoNext) return;
            _autoNext = Time.unscaledTime + 1f;

            var b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (b == null) return;

            Plugin.DumpWeightToLog(b, _dumpedOnce ? "自动" : "初始");
            _dumpedOnce = true;

            // 枪械注册已移交独立模组 GunPack（com.local.gunpack）
        }

        private void Update()
        {
            AutoDump();
            CheckEmptyBottles();

            // ---- 看门狗已停用（周期性反射开销） ----
            if (false)
            {
                _watchNext = Time.unscaledTime + 2f;
                try
                {
                    var b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                    if (b != null)
                    {
                        if (_watched != b) { _watched = b; _lastOffset = float.NaN; }
                        float off = DrugMath.Get(b, "weightOffset");
                        if (!float.IsNaN(off))
                        {
                            if (!float.IsNaN(_lastOffset))
                            {
                                float d = off - _lastOffset;
                                if (d < -0.15f && !DrugState.AnyActive())
                                    Plugin.Logger.LogWarning("[体重看门狗] 无药效却在掉重: " + d.ToString("F3")
                                        + "kg/2s（" + (d / 2f).ToString("F3") + "kg/s）当前偏移 " + off.ToString("F2"));
                            }
                            _lastOffset = off;
                        }
                    }
                }
                catch { }
            }

            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.2f;

            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) return;

                var fv = body.GetType().GetField("weightVisualOffset",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var fo = body.GetType().GetField("weightOffset",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (fv == null || fo == null) return;

                // 只读不写：visual 必须保持原版值，否则面板会重复计算偏移
                _ = fv; _ = fo;
            }
            catch { }
        }
    }

    /// <summary>按时长跑一段效果，自动处理实体销毁</summary>
    internal class EffectRunner : MonoBehaviour
    {
        // 记录冻结前的值：只抵消自然下降，玩家吃东西仍能提升
        private float _hungerRef = float.NaN;
        private float _thirstRef = float.NaN;
        private float _lastSample = -1f;

        /// <summary>tick 执行后刷新基准，使药物自身的扣减得以保留</summary>
        private void RefreshRefs(Body b)
        {
            if (!Plugin.FreezeDrain.Value) return;
            try
            {
                float h = DrugMath.Get(b, "hunger");
                float t = DrugMath.Get(b, "thirst");
                if (!float.IsNaN(h)) _hungerRef = h;
                if (!float.IsNaN(t)) _thirstRef = t;
            }
            catch { }
        }

        private void Hold(Body b)
        {
            if (!Plugin.FreezeDrain.Value) return;
            try
            {
                float h = DrugMath.Get(b, "hunger");
                float t = DrugMath.Get(b, "thirst");

                if (float.IsNaN(_hungerRef)) _hungerRef = h; else if (h > _hungerRef) _hungerRef = h;
                if (float.IsNaN(_thirstRef)) _thirstRef = t; else if (t > _thirstRef) _thirstRef = t;

                if (h < _hungerRef) DrugMath.Add(b, "hunger", _hungerRef - h);
                if (t < _thirstRef) DrugMath.Add(b, "thirst", _thirstRef - t);
            }
            catch { }
        }

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
                Hold(body);

                // 服药期间的专用采样：每 5 秒一条，便于与服药前数据对比
                if (t - _lastSample >= 5f || _lastSample < 0f)
                {
                    _lastSample = t;
                    Plugin.DumpWeightToLog(body, "服药" + Mathf.RoundToInt(t) + "s");
                }
                t += dt;
                try { tick(body, dt, t); } catch { }
                RefreshRefs(body);   // 基准跟到当前值：只抵消原版衰减，不抵消药物自身扣减
                yield return null;
            }
            // 药效结束播报
            try
            {
                if (body != null) DrugTalk.Say(body, "药物结束了。");
            }
            catch { }

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

        /// <summary>当前是否有药物效果在运行（用于区分掉重来源）</summary>
        internal static bool AnyActive()
        {
            float now = Time.time;
            foreach (var kv in ActiveUntil) if (now < kv.Value) return true;
            return false;
        }
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

    /// <summary>
    /// 迷你桶会用 Liquids.Registry 随机抽液体（WorldGeneration.DistributeMiniBarrels）。
    /// 我们的浓缩液和测试药不应该出现在世界容器里，所以在抽取期间临时摘除，
    /// 抽完立刻放回——不影响配方、瓶子、饮用等任何正常使用。
    /// </summary>
    [HarmonyPatch]
    internal static class MiniBarrelLiquidPatch
    {
        private static readonly List<string> Excluded = new List<string>
        {
            Plugin.DietConc, Plugin.FlushConc,
            Plugin.MetaConcs[0], Plugin.MetaConcs[1], Plugin.MetaConcs[2],
            Plugin.TestDiet, Plugin.TestFlush, Plugin.TestMeta
        };

        private static readonly Dictionary<string, LiquidType> Saved = new Dictionary<string, LiquidType>();
        private static bool _active;

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("WorldGeneration"); if (t != null) break; } catch { }
            }
            return t?.GetMethod("DistributeMiniBarrels", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            if (_active) return;
            try
            {
                Saved.Clear();
                foreach (var id in Excluded)
                {
                    if (Liquids.Registry.TryGetValue(id, out var lt))
                    {
                        Saved[id] = lt;
                        Liquids.Registry.Remove(id);
                    }
                }
                _active = true;
            }
            catch { }
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!_active) return;
            try
            {
                foreach (var kv in Saved) Liquids.Registry[kv.Key] = kv.Value;
                Saved.Clear();
                _active = false;
            }
            catch { }
        }
    }

    /// <summary>把「启用测试用药」加进游戏自带的设置面板（游戏 分类）</summary>
    [HarmonyPatch]
    internal static class SettingsEntryPatch
    {
        private const string Key = "dietdrug_test_drugs";

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("SettingsMenu"); if (t != null) break; } catch { }
            }
            return t?.GetMethod("OpenMenu", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            try
            {
                Type st = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { st = asm.GetType("Settings"); if (st != null) break; } catch { }
                }
                if (st == null) return;

                var getAll = st.GetMethod("GetAllSettings", BindingFlags.Public | BindingFlags.Static);
                var list = getAll != null
                    ? getAll.Invoke(null, null) as System.Collections.IList
                    : st.GetField("settings", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.IList;
                if (list == null) return;

                bool exists = false;
                foreach (var o in list)
                {
                    var nf = o?.GetType().GetField("name");
                    if ((nf?.GetValue(o) as string) == "dietdrug_disable_test") exists = true;
                }
                if (exists) return;

                var sbType = Type.GetType("SettingBool, Assembly-CSharp");
                if (sbType == null) { Plugin.Logger.LogWarning("找不到 SettingBool"); return; }

                var sb = Activator.CreateInstance(sbType);
                sbType.GetField("name").SetValue(sb, "dietdrug_disable_test");
                sbType.GetField("value").SetValue(sb, !Plugin.EnableTestDrugs.Value);   // 勾选 = 禁用

                var catF = sbType.BaseType?.GetField("category");
                if (catF != null) catF.SetValue(sb, Enum.Parse(catF.FieldType, "Game"));

                var applyF = sbType.BaseType?.GetField("apply");
                if (applyF != null)
                {
                    Action act = () =>
                    {
                        try
                        {
                            bool disable = (bool)sbType.GetField("value").GetValue(sb);
                            Plugin.EnableTestDrugs.Value = !disable;     // 勾选=禁用
                            if (disable) Plugin.CleanTestLiquidsPublic(); // 同时清掉存档残留
                            Plugin.Logger.LogInfo("[设置] 禁用测试用药 = " + disable
                                + (disable ? "（已清理存档残留）" : "（下次启动恢复）"));
                        }
                        catch (Exception ex) { Plugin.Logger.LogWarning("应用设置失败: " + ex.Message); }
                    };
                    applyF.SetValue(sb, act);
                }

                list.Add(sb);
                Plugin.Logger.LogInfo("已在设置面板注册：禁用测试用药");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("注册设置项失败: " + e.Message); }
        }
    }

    /// <summary>商人生成库存时自动打印货架，用于全图统计（玩家跑到哪记到哪）</summary>
    [HarmonyPatch]
    internal static class TraderInventoryPatch
    {
        private static int _count, _withGun, _withBottle;

        [HarmonyPrepare]
        private static bool Prepare() => true;

        [HarmonyTargetMethod]
        private static System.Reflection.MethodBase TargetMethod()
        {
            Type t = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType("TraderScript"); if (t != null) break; } catch { }
            }
            return t?.GetMethod("GenerateInventory",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                var fi = __instance.GetType().GetField("items",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                var list = fi?.GetValue(__instance) as System.Collections.IList;
                if (list == null) return;

                _count++;
                var ids = new List<string>();
                int guns = 0, bottles = 0;
                foreach (var it in list)
                {
                    if (it == null) continue;
                    var id = it.GetType().GetField("id")?.GetValue(it) as string;
                    if (string.IsNullOrEmpty(id)) continue;
                    ids.Add(Plugin.NameOf(id));
                    if (id == "myscar.scar" || id == "combatshotgun" || id == "krammer5001"
                        || id == "lectertypet" || id == "mcx.rabid" || id == "mdrx.interceptor") guns++;
                    if (id.Contains("drug_bottle")) bottles++;
                }
                if (guns > 0) _withGun++;
                if (bottles > 0) _withBottle++;

                Plugin.Logger.LogInfo("[商人#" + _count + "] " + ids.Count + " 件"
                    + (guns > 0 ? "  ★Mod枪" + guns : "  ✗无Mod枪")
                    + (bottles > 0 ? "  ◆药瓶" + bottles : ""));
                Plugin.Logger.LogInfo("      " + string.Join(", ", ids.ToArray()));
                Plugin.Logger.LogInfo("[商人累计] 已记录 " + _count + " 个商人，含Mod枪 " + _withGun + " 个");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("商人日志失败: " + e.Message); }
        }
    }

    /// <summary>让角色说一句话（Talker.Talk 接受原始文本，不走本地化表）</summary>
    internal static class DrugTalk
    {
        internal static void Say(Body body, string text)
        {
            if (body == null) return;
            try
            {
                var talker = body.GetType()
                    .GetField("talker", System.Reflection.BindingFlags.Public |
                                         System.Reflection.BindingFlags.NonPublic |
                                         System.Reflection.BindingFlags.Instance)?.GetValue(body);
                if (talker == null) { Plugin.Logger.LogWarning("拿不到 talker，跳过台词"); return; }

                // Talk(string text, Limb limb = null, bool force = false, bool resetTalkTimer = false)
                System.Reflection.MethodInfo m = null;
                foreach (var mi in talker.GetType().GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance))
                {
                    if (mi.Name != "Talk") continue;
                    var ps = mi.GetParameters();
                    if (ps.Length >= 1 && ps[0].ParameterType == typeof(string)) { m = mi; break; }
                }
                if (m == null) { Plugin.Logger.LogWarning("Talker.Talk 未找到"); return; }

                var args = new object[m.GetParameters().Length];
                args[0] = text;
                for (int i = 1; i < args.Length; i++)
                    args[i] = m.GetParameters()[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(m.GetParameters()[i].ParameterType)
                        : null;
                m.Invoke(talker, args);
                Plugin.Logger.LogInfo("台词已播报: " + text);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("台词触发失败: " + e.Message); }
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

        /// <summary>面板公式里 1 点偏移 = 0.34kg，故 1kg = 1/0.34 点</summary>
        private const float KgToOffset = 1f / 0.34f;

        internal static void AddWeight(Body b, float d)
        {
            // d 以「面板公斤」为单位；内部偏移需除以 0.34
            if (Plugin.PanelUnits.Value) d *= KgToOffset;
            if (d >= 0f) { Add(b, "weightOffset", d); return; }

            float floor = Plugin.WeightFloor.Value;
            if (floor > 0f)
            {
                float cur = GetRealWeight(b);
                if (!float.IsNaN(cur))
                {
                    float allowed = cur - floor;            // 还能减多少
                    if (allowed <= 0f)
                    {
                        WarnFloorOnce();
                        return;                              // 到线，停止减重
                    }
                    if (-d > allowed) d = -allowed;          // 只减到线为止
                }
            }
            Add(b, "weightOffset", d);
        }

        private static float _lastFloorWarn = -999f;

        private static void WarnFloorOnce()
        {
            if (Time.time - _lastFloorWarn < 60f) return;
            _lastFloorWarn = Time.time;
            Plugin.Logger.LogWarning("体重已达下限 " + Plugin.WeightFloor.Value + "kg，药物停止减重（防止骨瘦如柴）");
        }

        /// <summary>
        /// 真实体重 = 各肢体质量之和 + weightOffset。
        /// 不能用 Body.baseMass：它是缓存值，饥饿消耗肌肉后不会及时重算（实测差 5kg），
        /// 而原版面板与肥胖/偏轻判定用的都是「肢体和 + 偏移」。
        /// </summary>
        internal static Limb[] GetLimbs(Body b)
        {
            try
            {
                var f = F(b, "limbs");
                return f?.GetValue(b) as Limb[];
            }
            catch { return null; }
        }

        internal static float GetRealWeight(Body b)
        {
            try
            {
                // 与面板完全一致的公式（见 Assembly-CSharp：体重文本 = offset*0.34 + 50 + visual）
                float off = Get(b, "weightOffset");
                float vis = Get(b, "weightVisualOffset");
                if (float.IsNaN(off)) return float.NaN;
                if (float.IsNaN(vis)) vis = 0f;
                return 50f + off * 0.34f + vis;
            }
            catch { return float.NaN; }
        }

        /// <summary>
        /// 让界面用的 weightVisualOffset 跟随真实值，但不能逐帧硬写：
        /// 原版面板/肥胖判定依赖 lastWeight 与 weightChangeTime 的缓存刷新，
        /// 逐帧写极小增量会让面板停在旧值，而判定用实时值，两边差出好几公斤。
        /// 这里只在偏差超过阈值时兜底对齐。
        /// </summary>
        internal static void SyncVisual(Body b, float threshold)
        {
            // 面板公式为 offset*0.34 + 50 + visual，
            // 强行把 visual 写成 offset 会让偏移被计算两次、显示值掉得过快。故不再写入。
            if (true) return;
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
        /// <summary>
        /// 维持 stimulantMultiplier（游戏自带的兴奋剂通道）：
        /// 速度 ×(1 + 倍率)，攻击冷却 ÷(1 + 倍率×0.66)，每帧自然衰减 0.02，故需持续补。
        /// </summary>
        internal static void KeepStimulant(Body b, float dt, float target)
        {
            try
            {
                float cur = Get(b, "stimulantMultiplier");
                if (float.IsNaN(cur)) return;
                if (cur < target) Add(b, "stimulantMultiplier", (target - cur) * Mathf.Min(1f, dt * 2f));
            }
            catch { }
        }

        /// <summary>移速加成（bonusMovement 会每帧归零，仅保留作参考）</summary>
        internal static void AddBonusMovement(Body b, float delta, float cap)
        {
            try
            {
                var f = F(b, "bonusMovement");
                if (f == null) return;
                float cur = (float)f.GetValue(b);
                if (cur < cap) f.SetValue(b, Mathf.Min(cap, cur + delta));
            }
            catch { }
        }

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
