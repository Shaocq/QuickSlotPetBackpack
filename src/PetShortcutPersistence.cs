using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Duckov;
using ItemStatsSystem;
using Saves;
using UnityEngine;

namespace QuickSlotPetBackpack
{
    /// <summary>
    /// 扩展快捷栏绑定的持久化（宠物背包 / 主背包容器内的物品）。
    ///
    /// 原版 ItemShortcut.SaveData 只记录主背包顶层的格子序号，宠物背包（含容器）
    /// 和主背包容器内的物品都会被记成 -1，读档时被跳过。
    /// 这里按原版同样的方式补上一份绑定数据：
    ///   - 主存储：游戏存档里的 PetShortcutBindings_Data（SavesSystem.Save / Load）
    ///     与 ItemShortcut_Data 在同一个 Save_N.sav 中，换图、读档、回滚备份存档、
    ///     云同步都天然保持同步，这是原版主背包绑定不会丢失的原因。
    ///     存的值是 JSON 字符串：ES3 只需处理 string，不依赖它对 mod 程序集里
    ///     自定义类型的解析，跨版本/跨程序集都最稳
    ///   - 兜底：旧版本的 mod 目录 JSON 文件，仅在游戏存档里还没有这份数据时读取；
    ///     读到的旧数据会在下一次保存时自动迁移进游戏存档
    /// </summary>
    internal static class PetShortcutPersistence
    {
        private const string FileName = "pet_shortcut_bindings.json";

        /// <summary>游戏存档中的键名（与 ItemShortcut_Data 同文件）。</summary>
        private const string SaveKey = "PetShortcutBindings_Data";

        // 字段反射放在第一次使用时做：静态构造里抛异常会连累整个类初始化失败
        private static FieldInfo _itemsField;
        private static bool _itemsFieldSearched;

        private static string _storagePath;
        private static List<BindingRecord> _currentBindings = new List<BindingRecord>();

        private static FieldInfo GetItemsField()
        {
            if (!_itemsFieldSearched)
            {
                _itemsFieldSearched = true;

                try
                {
                    _itemsField = typeof(ItemShortcut).GetField("items", BindingFlags.Instance | BindingFlags.NonPublic);
                }
                catch (Exception e)
                {
                    SafeGuard.Report("获取快捷栏物品列表字段失败", e);
                }
            }

            return _itemsField;
        }

        // JsonUtility 只序列化 public 字段；类型本身也保持 public，避免嵌套私有类型的兼容性问题
        [Serializable]
        public class BindingRecord
        {
            public int shortcutSlot;
            public string path;
        }

        [Serializable]
        public class SaveRecord
        {
            public int saveSlot;
            public List<BindingRecord> bindings = new List<BindingRecord>();
        }

        [Serializable]
        public class StoreFile
        {
            public List<SaveRecord> saves = new List<SaveRecord>();
        }

        /// <summary>当前使用的数据文件路径（日志与排错用）。</summary>
        public static string StoragePath
        {
            get
            {
                try
                {
                    // 不缓存：OnEnable 早于 OnAfterSetup，缓存可能把 "." 记成最终目录
                    if (!string.IsNullOrEmpty(_storagePath))
                        return _storagePath;

                    string directory = ModBehaviour.ModDirectory;
                    if (string.IsNullOrEmpty(directory))
                        directory = ".";

                    return Path.Combine(directory, FileName);
                }
                catch
                {
                    try
                    {
                        return Path.Combine(Application.persistentDataPath, FileName);
                    }
                    catch
                    {
                        return FileName;
                    }
                }
            }
        }

        /// <summary>当前存档里保存了多少个扩展快捷栏绑定。</summary>
        public static int SavedCount => _currentBindings?.Count ?? 0;

        /// <summary>
        /// 按当前存档读取绑定列表，解析路径并重新绑定到快捷栏。
        /// 必须在宠物背包加载完成后调用（宠物背包路径需要它）；
        /// 返回成功恢复的槽位数量。
        /// </summary>
        public static int Apply()
        {
            int applied = 0;

            try
            {
                LoadCurrentSave();

                Diag.Log($"开始恢复绑定：存档中 {_currentBindings.Count} 条");

                // 原版 ItemShortcut.Load 已经把主背包物品恢复到各自槽位；
                // 下面每一次 Set 都要避开这些槽位，绝不改动原版主背包的绑定
                for (int i = 0; i < _currentBindings.Count; i++)
                {
                    BindingRecord record = _currentBindings[i];
                    try
                    {
                        if (record == null
                            || record.shortcutSlot < 0
                            || record.shortcutSlot > ItemShortcut.MaxIndex)
                        {
                            continue;
                        }

                        // 路径无前缀 = 宠物背包；"main:" 前缀 = 主背包容器内
                        Item item = PetShortcutUtility.ResolveBindingPath(record.path);
                        if (item == null || !PetShortcutUtility.IsExtraBindableItem(item))
                        {
                            Debug.LogWarning(
                                $"[快捷栏可使用宠物背包] 存档中的绑定已失效：槽位 {record.shortcutSlot + 3}，路径 {record.path}");
                            Diag.Log($"恢复跳过：槽位 {record.shortcutSlot + 3} 路径 {record.path} 已失效"
                                + $"（{PetShortcutUtility.Describe(item)}）");
                            continue;
                        }

                        if (PetShortcutUtility.HasMainInventoryConflict(record.shortcutSlot, item))
                        {
                            Debug.LogWarning(
                                $"[快捷栏可使用宠物背包] 槽位 {record.shortcutSlot + 3} 的扩展物品与原版主背包绑定冲突"
                                + "（原版 Set_Local 会清除同型号槽位），跳过该绑定以保留原版主背包设置");
                            Diag.Log($"恢复跳过：槽位 {record.shortcutSlot + 3} 与原版主背包绑定冲突");
                            continue;
                        }

                        if (ItemShortcut.Set(record.shortcutSlot, item))
                        {
                            applied++;
                            Diag.Log($"恢复成功：槽位 {record.shortcutSlot + 3} ← 路径 {record.path}："
                                + PetShortcutUtility.Describe(item));
                        }
                        else
                        {
                            Diag.Log($"恢复失败：槽位 {record.shortcutSlot + 3} 的 Set 返回 false"
                                + $"（{PetShortcutUtility.Describe(item)}）");
                        }
                    }
                    catch (Exception e)
                    {
                        SafeGuard.Report("恢复宠物背包快捷栏绑定失败", e);
                    }
                }
            }
            catch (Exception e)
            {
                // 恢复失败只意味着本关没有绑定，绝不能把异常抛给游戏
                SafeGuard.Report("读取宠物背包快捷栏绑定失败", e);
            }

            return applied;
        }

        /// <summary>
        /// 收集当前快捷栏里位于宠物背包（含容器）或主背包容器内的物品，写入游戏存档。
        /// 主背包顶层物品仍由原版逻辑保存，这里只处理原版存不下的部分。
        /// </summary>
        public static void Save()
        {
            try
            {
                if (ModBehaviour.SaveBlocked)
                {
                    Debug.Log("[快捷栏可使用宠物背包] 本关的绑定尚未恢复，跳过保存以免覆盖旧数据");
                    return;
                }

                // 副本等关卡不加载宠物背包，此时保存会把基地里的绑定清空
                if (!LevelConfig.SavePet)
                    return;

                List<BindingRecord> bindings = CollectCurrent();
                if (bindings == null)
                {
                    // 环境没准备好时无法判断绑定情况，宁可这次不保存，
                    // 也不能把存档里的旧绑定覆盖成空
                    Debug.Log("[快捷栏可使用宠物背包] 快捷栏或宠物背包尚未就绪，跳过本次保存");
                    Diag.Log("保存跳过：快捷栏或宠物背包尚未就绪（本次不写游戏存档）");
                    return;
                }

                Diag.Log($"准备保存绑定：共收集到 {bindings.Count} 条（存档槽 {SavesSystem.CurrentSlot}）");

                // 与原版 ItemShortcut.Save 一样写进当前存档文件；
                // 必须在 SavesSystem.CollectSaveData 期间调用，游戏随后会统一落盘
                bool savedToGame = SaveToGameSave(bindings);

                // 独立文件只作为兜底保留：游戏存档写入失败时至少还有一份数据
                WriteJsonBackup(bindings);

                _currentBindings = bindings;

                Debug.Log(
                    $"[快捷栏可使用宠物背包] 存档 {SavesSystem.CurrentSlot} 已保存 {bindings.Count} 个扩展快捷栏绑定（宠物背包 / 主背包容器）"
                    + (savedToGame ? "（写入游戏存档）" : "（游戏存档写入失败，仅保留独立文件）"));
            }
            catch (Exception e)
            {
                SafeGuard.Report("保存宠物背包快捷栏绑定失败", e);
            }
        }

        /// <summary>
        /// 像原版 ItemShortcut_Data 一样写进游戏存档。
        /// 数据与角色数据在同一个 Save_N.sav 里，读档/回滚时一起变化。
        /// </summary>
        private static bool SaveToGameSave(List<BindingRecord> bindings)
        {
            try
            {
                // 值用 JSON 字符串：ES3 只序列化 string，
                // 不依赖它解析 mod 程序集里的自定义类型
                string json = SerializeBindings(bindings);

                // 列表为空也要写：表示本存档确实没有宠物背包绑定，
                // 避免读取时又回退到以前留下的独立文件
                SavesSystem.Save(SaveKey, json);
                return true;
            }
            catch (Exception e)
            {
                SafeGuard.Report("写入游戏存档失败，本次改用独立数据文件兜底", e);
                return false;
            }
        }

        /// <summary>把绑定列表序列化成存进游戏存档的 JSON 字符串。</summary>
        private static string SerializeBindings(List<BindingRecord> bindings)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\"bindings\":[");

            int written = 0;
            if (bindings != null)
            {
                for (int i = 0; i < bindings.Count; i++)
                {
                    BindingRecord binding = bindings[i];
                    if (binding == null || string.IsNullOrEmpty(binding.path))
                        continue;

                    if (written > 0)
                        builder.Append(',');

                    written++;
                    builder.Append("{\"shortcutSlot\":")
                        .Append(binding.shortcutSlot)
                        .Append(",\"path\":\"");
                    AppendEscaped(builder, binding.path);
                    builder.Append("\"}");
                }
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>
        /// 解析游戏存档里的绑定字符串。
        /// 成功时返回列表（可能为空），格式不认识时返回 null（调用方回退到独立文件）。
        /// </summary>
        private static List<BindingRecord> ParseBindings(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                int position = 0;
                object root = ParseValue(json, ref position);
                if (!(root is Dictionary<string, object> rootObject))
                    return null;

                if (!rootObject.TryGetValue("bindings", out object value)
                    || !(value is List<object> entries))
                {
                    return null;
                }

                List<BindingRecord> bindings = new List<BindingRecord>();
                foreach (object entryValue in entries)
                {
                    if (!(entryValue is Dictionary<string, object> entry))
                        continue;

                    BindingRecord binding = new BindingRecord();
                    if (entry.TryGetValue("shortcutSlot", out object indexValue))
                        binding.shortcutSlot = ToInt(indexValue);

                    if (entry.TryGetValue("path", out object pathValue) && pathValue is string path)
                        binding.path = path;

                    if (!string.IsNullOrEmpty(binding.path))
                        bindings.Add(binding);
                }

                return bindings;
            }
            catch (Exception e)
            {
                SafeGuard.Report("解析游戏存档中的宠物背包绑定失败", e);
                return null;
            }
        }

        /// <summary>把绑定写进 mod 目录的独立 JSON（兜底 / 兼容旧版本）。</summary>
        private static void WriteJsonBackup(List<BindingRecord> bindings)
        {
            try
            {
                StoreFile store = ReadStore();
                int slot = SavesSystem.CurrentSlot;

                store.saves.RemoveAll(record => record != null && record.saveSlot == slot);
                if (bindings.Count > 0)
                {
                    store.saves.Add(new SaveRecord
                    {
                        saveSlot = slot,
                        bindings = bindings
                    });
                }

                WriteStore(store);
            }
            catch (Exception e)
            {
                // 兜底文件失败不影响游戏存档里的正式数据
                SafeGuard.Report("写入独立数据文件失败（游戏存档中的绑定不受影响）", e);
            }
        }

        /// <summary>读取当前存档的绑定，不解析物品。</summary>
        public static void LoadCurrentSave()
        {
            _currentBindings = new List<BindingRecord>();

            // 主存储：游戏存档。只要这个键存在且能解析就完全以它为准（哪怕是空列表），
            // 这样回滚到旧存档时不会误用独立文件里的新数据。
            try
            {
                if (SavesSystem.KeyExisits(SaveKey))
                {
                    string json = SavesSystem.Load<string>(SaveKey);
                    List<BindingRecord> bindings = ParseBindings(json);
                    if (bindings != null)
                    {
                        _currentBindings = bindings;
                        Debug.Log(
                            $"[快捷栏可使用宠物背包] 从游戏存档读取到 {_currentBindings.Count} 个绑定");
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("读取游戏存档中的宠物背包绑定失败，改用独立数据文件", e);
            }

            // 兜底：旧版本的独立文件（首次读到后，下次保存会自动迁移进游戏存档）
            try
            {
                StoreFile store = ReadStore();
                int slot = SavesSystem.CurrentSlot;
                SaveRecord record = store.saves.Find(item => item != null && item.saveSlot == slot);
                if (record == null || record.bindings == null)
                    return;

                _currentBindings = record.bindings;
            }
            catch (Exception e)
            {
                SafeGuard.Report("读取独立数据文件中的宠物背包绑定失败", e);
            }
        }

        /// <summary>
        /// 能否可靠地判断「物品在不在宠物背包里」。
        /// 快捷栏实例、宠物背包实例任一缺失，或宠物背包仍在加载，都返回 false。
        /// </summary>
        private static bool IsCollectContextReady()
        {
            try
            {
                if (ItemShortcut.Instance == null)
                    return false;

                PetProxy proxy = PetProxy.Instance;
                if (proxy == null)
                    return false;

                Inventory inventory = proxy.Inventory;
                return inventory != null && !inventory.Loading;
            }
            catch
            {
                // 判断不了就当作未就绪，跳过本次保存
                return false;
            }
        }

        /// <summary>
        /// 扫描快捷栏，生成当前需要保存的绑定列表。
        /// 返回 null 表示「环境不可用、无法判断」（调用方必须跳过保存），
        /// 与「确实没有任何宠物背包绑定」的空列表区分开。
        /// </summary>
        private static List<BindingRecord> CollectCurrent()
        {
            // 宠物背包尚未加载完成时，所有物品都会被判成「不在宠物背包里」，
            // 如果据此写空列表，会把存档里的有效绑定清掉
            if (!IsCollectContextReady())
                return null;

            List<BindingRecord> bindings = new List<BindingRecord>();

            ItemShortcut shortcut = ItemShortcut.Instance;
            FieldInfo itemsField = GetItemsField();
            if (shortcut == null || itemsField == null)
                return null;

            List<Item> items = itemsField.GetValue(shortcut) as List<Item>;
            if (items == null)
                return null;

            int count = Math.Min(items.Count, ItemShortcut.MaxIndex + 1);
            for (int i = 0; i < count; i++)
            {
                Item item = items[i];
                if (item == null)
                    continue;

                string pathText;
                if (PetShortcutUtility.TryGetBindingPath(item, out string bindingPath))
                {
                    // 宠物背包 "2/0"；主背包容器内 "main:3/1"
                    pathText = bindingPath;
                }
                else
                {
                    // 物品正在使用中（已脱离背包，使用完会放回原格）：
                    // 用跟踪记录 / 槽位绑定里的原路径，避免使用中存档把这条绑定漏掉
                    pathText = PetUseTracker.GetPathFor(item);
                    if (string.IsNullOrEmpty(pathText))
                        pathText = PetSlotTracker.GetPathFor(item);
                }

                bool mainTopLevel = PetShortcutUtility.IsMainTopLevelItem(item);

                // 保存时的完整决策链：排查「存档里绑定为空」时唯一可靠的证据
                Diag.Log(
                    $"收集槽位 {i + 3}：{PetShortcutUtility.Describe(item)}，"
                    + $"路径={pathText ?? "无"}，主背包顶层={mainTopLevel}，"
                    + $"所在背包={DescribeInventory(item.InInventory)}");

                if (string.IsNullOrEmpty(pathText))
                {
                    // 主背包顶层物品由原版 ItemShortcut_Data 保存，这里跳过是正常情况
                    if (!mainTopLevel)
                    {
                        Debug.LogWarning(
                            $"[快捷栏可使用宠物背包] 槽位 {i + 3} 的物品不在宠物背包或主背包容器里，跳过保存：{PetShortcutUtility.Describe(item)}");
                    }

                    continue;
                }

                bindings.Add(new BindingRecord
                {
                    shortcutSlot = i,
                    path = pathText
                });
            }

            return bindings;
        }

        /// <summary>背包实例的简短描述（日志用）：区分宠物背包 / 主背包 / 容器。</summary>
        private static string DescribeInventory(Inventory inventory)
        {
            try
            {
                if (inventory == null)
                    return "null";

                if (inventory == PetShortcutUtility.GetPetInventory())
                    return "宠物背包";

                if (inventory == PetShortcutUtility.GetMainInventory())
                    return "主背包";

                Item attached = inventory.AttachedToItem;
                return attached == null
                    ? "未知背包"
                    : "容器(" + PetShortcutUtility.Describe(attached) + ")";
            }
            catch
            {
                return "未知";
            }
        }

        private static StoreFile ReadStore()
        {
            try
            {
                if (!File.Exists(StoragePath))
                    return new StoreFile();

                string json = File.ReadAllText(StoragePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return new StoreFile();

                StoreFile store = ParseStore(json);
                return store ?? new StoreFile();
            }
            catch (Exception e)
            {
                // 文件损坏时先改名留档，避免后续保存把其他存档的数据一起覆盖
                TryBackupCorruptFile();
                SafeGuard.Report("解析宠物背包快捷栏数据文件失败", e);
                return new StoreFile();
            }
        }

        private static void WriteStore(StoreFile store)
        {
            string json = SerializeStore(store);

            try
            {
                WriteAllText(StoragePath, json);
            }
            catch (Exception e)
            {
                // 游戏目录不可写时退到玩家数据目录，至少保证功能可用
                SafeGuard.Report($"写入 {StoragePath} 失败，改用玩家数据目录", e);

                try
                {
                    string fallback = Path.Combine(Application.persistentDataPath, FileName);
                    _storagePath = fallback;
                    WriteAllText(fallback, json);
                }
                catch (Exception fallbackError)
                {
                    // 两处都写不进去时只能放弃本次保存，不能把异常抛给游戏
                    _storagePath = null;
                    SafeGuard.Report("写入玩家数据目录同样失败，本次绑定未能保存", fallbackError);
                }
            }
        }

        // ---------------------------------------------------------------
        // JSON 读写
        //
        // 游戏自带的 JsonUtility 对「internal 外部类里的 public 嵌套类型」
        // 无法正常序列化（实测写出来的文件只有 {}），所以这里自己生成和解析。
        // 数据格式固定且简单：对象、数组、整数、字符串，解析器只覆盖这些。
        // ---------------------------------------------------------------

        private static string SerializeStore(StoreFile store)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("{\n");
            builder.Append("  \"version\": 2,\n");
            builder.Append("  \"saves\": [");

            if (store != null && store.saves != null)
            {
                int saveWritten = 0;

                for (int i = 0; i < store.saves.Count; i++)
                {
                    SaveRecord save = store.saves[i];
                    if (save == null)
                        continue;

                    if (saveWritten > 0)
                        builder.Append(',');
                    saveWritten++;

                    builder.Append("\n    {\n");
                    builder.Append("      \"saveSlot\": ").Append(save.saveSlot).Append(",\n");
                    builder.Append("      \"bindings\": [");

                    int bindingWritten = 0;
                    if (save.bindings != null)
                    {
                        for (int j = 0; j < save.bindings.Count; j++)
                        {
                            BindingRecord binding = save.bindings[j];
                            if (binding == null)
                                continue;

                            if (bindingWritten > 0)
                                builder.Append(',');
                            bindingWritten++;

                            builder.Append("\n        { \"shortcutSlot\": ")
                                .Append(binding.shortcutSlot)
                                .Append(", \"path\": \"");
                            AppendEscaped(builder, binding.path);
                            builder.Append("\" }");
                        }
                    }

                    if (bindingWritten > 0)
                        builder.Append('\n');

                    builder.Append("      ]\n    }");
                }
            }

            if (store != null && store.saves != null && store.saves.Count > 0)
                builder.Append('\n');

            builder.Append("  ]\n}\n");
            return builder.ToString();
        }

        private static void AppendEscaped(StringBuilder builder, string value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }
        }

        private static StoreFile ParseStore(string json)
        {
            int position = 0;
            object root = ParseValue(json, ref position);
            if (!(root is Dictionary<string, object> rootObject))
                return new StoreFile();

            StoreFile store = new StoreFile();
            if (!rootObject.TryGetValue("saves", out object savesValue)
                || !(savesValue is List<object> saves))
            {
                return store;
            }

            foreach (object saveValue in saves)
            {
                if (!(saveValue is Dictionary<string, object> saveObject))
                    continue;

                SaveRecord save = new SaveRecord();
                if (saveObject.TryGetValue("saveSlot", out object slotValue))
                    save.saveSlot = ToInt(slotValue);

                if (saveObject.TryGetValue("bindings", out object bindingsValue)
                    && bindingsValue is List<object> bindings)
                {
                    foreach (object bindingValue in bindings)
                    {
                        if (!(bindingValue is Dictionary<string, object> bindingObject))
                            continue;

                        BindingRecord binding = new BindingRecord();
                        if (bindingObject.TryGetValue("shortcutSlot", out object indexValue))
                            binding.shortcutSlot = ToInt(indexValue);

                        if (bindingObject.TryGetValue("path", out object pathValue)
                            && pathValue is string path)
                        {
                            binding.path = path;
                        }

                        if (!string.IsNullOrEmpty(binding.path))
                            save.bindings.Add(binding);
                    }
                }

                store.saves.Add(save);
            }

            return store;
        }

        private static int ToInt(object value)
        {
            if (value is double number)
                return (int)number;

            if (value is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                return parsed;

            return 0;
        }

        private static object ParseValue(string text, ref int position)
        {
            SkipWhitespace(text, ref position);

            if (position >= text.Length)
                throw new FormatException("JSON 意外结束");

            switch (text[position])
            {
                case '{':
                    return ParseObject(text, ref position);
                case '[':
                    return ParseArray(text, ref position);
                case '"':
                    return ParseString(text, ref position);
                case 't':
                    Expect(text, ref position, "true");
                    return true;
                case 'f':
                    Expect(text, ref position, "false");
                    return false;
                case 'n':
                    Expect(text, ref position, "null");
                    return null;
                default:
                    return ParseNumber(text, ref position);
            }
        }

        private static Dictionary<string, object> ParseObject(string text, ref int position)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            position++; // '{'

            while (true)
            {
                SkipWhitespace(text, ref position);
                if (position >= text.Length)
                    throw new FormatException("JSON 对象意外结束");
                if (text[position] == '}')
                {
                    position++;
                    return result;
                }

                string key = ParseString(text, ref position);

                SkipWhitespace(text, ref position);
                if (position >= text.Length || text[position] != ':')
                    throw new FormatException("JSON 对象缺少冒号");
                position++;

                result[key] = ParseValue(text, ref position);

                SkipWhitespace(text, ref position);
                if (position < text.Length && text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (position < text.Length && text[position] == '}')
                {
                    position++;
                    return result;
                }

                throw new FormatException("JSON 对象格式错误");
            }
        }

        private static List<object> ParseArray(string text, ref int position)
        {
            List<object> result = new List<object>();
            position++; // '['

            while (true)
            {
                SkipWhitespace(text, ref position);
                if (position >= text.Length)
                    throw new FormatException("JSON 数组意外结束");
                if (text[position] == ']')
                {
                    position++;
                    return result;
                }

                result.Add(ParseValue(text, ref position));

                SkipWhitespace(text, ref position);
                if (position < text.Length && text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (position < text.Length && text[position] == ']')
                {
                    position++;
                    return result;
                }

                throw new FormatException("JSON 数组格式错误");
            }
        }

        private static string ParseString(string text, ref int position)
        {
            SkipWhitespace(text, ref position);
            if (position >= text.Length || text[position] != '"')
                throw new FormatException("JSON 字符串格式错误");

            position++;
            StringBuilder builder = new StringBuilder();

            while (position < text.Length)
            {
                char c = text[position++];

                if (c == '"')
                    return builder.ToString();

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (position >= text.Length)
                    break;

                char escaped = text[position++];
                switch (escaped)
                {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        if (position + 4 > text.Length)
                            throw new FormatException("JSON 转义不完整");

                        string hex = text.Substring(position, 4);
                        position += 4;
                        builder.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            throw new FormatException("JSON 字符串意外结束");
        }

        private static double ParseNumber(string text, ref int position)
        {
            int start = position;

            while (position < text.Length)
            {
                char c = text[position];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                    position++;
                else
                    break;
            }

            if (position == start)
                throw new FormatException("JSON 数字格式错误");

            return double.Parse(
                text.Substring(start, position - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture);
        }

        private static void Expect(string text, ref int position, string expected)
        {
            if (position + expected.Length > text.Length
                || string.CompareOrdinal(text, position, expected, 0, expected.Length) != 0)
            {
                throw new FormatException($"JSON 期望 {expected}");
            }

            position += expected.Length;
        }

        private static void SkipWhitespace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
        }

        private static void WriteAllText(string path, string content)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));

            if (File.Exists(path))
                File.Copy(temp, path, true);
            else
                File.Move(temp, path);

            if (File.Exists(temp))
                File.Delete(temp);
        }

        private static void TryBackupCorruptFile()
        {
            try
            {
                if (!File.Exists(StoragePath))
                    return;

                string backup = StoragePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(StoragePath, backup, true);
                Debug.LogWarning($"[快捷栏可使用宠物背包] 已把损坏的数据文件备份为 {backup}");
            }
            catch
            {
                // 备份失败不影响主流程
            }
        }
    }
}
