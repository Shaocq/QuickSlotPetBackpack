using System;
using System.Collections.Generic;
using System.Reflection;
using Duckov;
using Duckov.UI;
using ItemStatsSystem;
using UnityEngine;

namespace QuickSlotPetBackpack
{
    /// <summary>
    /// 宠物背包路径工具。
    ///
    /// 原版快捷栏（<see cref="Duckov.ItemShortcut"/>）只保存「主背包里的格子序号」，
    /// 因此宠物背包（<see cref="PetProxy.PetInventory"/>）里的物品保存时序号是 -1，
    /// 换地图 / 读档后就丢了。这里用「从宠物背包根节点往下的序号路径」来定位物品，
    /// 例如：
    ///   直接放在宠物背包第 3 格          → "3"
    ///   宠物背包第 2 格的容器内第 0 格    → "2/0"
    /// 存档时记录路径，读档后按路径重新取回物品。
    /// </summary>
    internal static class PetShortcutUtility
    {
        /// <summary>路径最大深度，防止数据异常时死循环。</summary>
        private const int MaxDepth = 8;

        /// <summary>
        /// 物品是否位于宠物背包（或宠物背包里的容器）中。
        /// 快捷栏允许这类物品，但仍然禁止武器（沿用原版规则）。
        /// </summary>
        public static bool IsPetItem(Item item)
        {
            try
            {
                if (item == null)
                    return false;

                // 主角色不存在时不做额外放宽，保持原版行为
                if (CharacterMainControl.Main == null)
                    return false;

                if (item.Tags != null && item.Tags.Contains("Weapon"))
                    return false;

                return TryGetPath(item, out _);
            }
            catch
            {
                // 校验函数会被游戏频繁调用，任何异常都只能吞掉
                return false;
            }
        }

        /// <summary>存档路径前缀：主背包容器内的物品。没有前缀表示宠物背包。</summary>
        public const string MainPathPrefix = "main:";

        /// <summary>宠物背包实例；取不到返回 null。</summary>
        public static Inventory GetPetInventory()
        {
            try
            {
                return PetProxy.PetInventory;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>主角色主背包实例；取不到返回 null。</summary>
        public static Inventory GetMainInventory()
        {
            try
            {
                CharacterMainControl master = CharacterMainControl.Main;
                if (master == null || master.CharacterItem == null)
                    return null;

                return master.CharacterItem.Inventory;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 计算物品相对宠物背包的序号路径。物品不在宠物背包里时返回 false。
        /// </summary>
        public static bool TryGetPath(Item item, out List<int> path)
        {
            return TryGetPathTo(item, GetPetInventory(), out path);
        }

        /// <summary>
        /// 计算物品相对主背包的序号路径。顶层物品的路径长度为 1，容器内物品为 2 以上。
        /// </summary>
        public static bool TryGetMainPath(Item item, out List<int> path)
        {
            return TryGetPathTo(item, GetMainInventory(), out path);
        }

        /// <summary>
        /// 从物品往上回溯容器所在的格子，直到走到 <paramref name="root"/> 背包。
        /// 路径顺序为「从根到物品」，例如容器在主背包第 3 格、物品在容器第 1 格 → [3, 1]。
        /// </summary>
        private static bool TryGetPathTo(Item item, Inventory root, out List<int> path)
        {
            path = null;

            try
            {
                if (item == null || root == null)
                    return false;

                List<int> reversed = new List<int>();
                Item current = item;

                for (int depth = 0; depth < MaxDepth && current != null; depth++)
                {
                    Inventory inventory = current.InInventory;
                    if (inventory == null)
                        return false;

                    int index = inventory.GetIndex(current);
                    if (index < 0)
                        return false;

                    reversed.Add(index);

                    // 走到根背包：路径收集完成
                    if (inventory == root)
                    {
                        reversed.Reverse();
                        path = reversed;
                        return true;
                    }

                    // 当前物品是容器时，继续往上一层找该容器所在的物品
                    current = inventory.AttachedToItem;
                }

                return false;
            }
            catch
            {
                // 高频调用路径，任何异常都只当作「不在目标背包里」
                path = null;
                return false;
            }
        }

        /// <summary>
        /// 物品是否正被主角色拿着（原版 UseItem 把物品从背包 Detach 出来后会
        /// <c>transform.SetParent(transform)</c>）。用来识别「正在使用」的瞬态，
        /// 避免把使用中的物品当成失效而换绑到别的同型号物品。
        /// </summary>
        public static bool IsHeldByMainCharacter(Item item)
        {
            try
            {
                CharacterMainControl master = CharacterMainControl.Main;
                if (item == null || master == null || master.transform == null)
                    return false;

                Transform parent = item.transform;
                return parent != null && parent.IsChildOf(master.transform);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>物品是否直接放在主背包顶层（这部分由原版 ItemShortcut_Data 负责保存）。</summary>
        public static bool IsMainTopLevelItem(Item item)
        {
            try
            {
                Inventory mainInventory = GetMainInventory();
                return item != null && mainInventory != null && item.InInventory == mainInventory;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 快捷栏额外接管的物品（原版不支持、由本 mod 补上的部分）：
        ///   - 宠物背包（含容器）里的非武器物品；
        ///   - 主背包容器内部的非武器物品（原版只认主背包顶层）。
        /// 主背包顶层的物品原版本来就支持，不算「额外」。
        /// </summary>
        public static bool IsExtraBindableItem(Item item)
        {
            try
            {
                if (item == null || CharacterMainControl.Main == null)
                    return false;

                // 沿用原版规则：武器不能绑定快捷栏
                if (item.Tags != null && item.Tags.Contains("Weapon"))
                    return false;

                if (TryGetPath(item, out _))
                    return true;

                Inventory mainInventory = GetMainInventory();
                if (mainInventory == null || item.InInventory == mainInventory)
                    return false;

                return TryGetPathTo(item, mainInventory, out List<int> mainPath)
                    && mainPath.Count >= 2;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>按保存的路径从宠物背包里重新取回物品；取不到返回 null。</summary>
        public static Item ResolvePath(string path)
        {
            try
            {
                return ResolvePathIn(GetPetInventory(), path);
            }
            catch (Exception e)
            {
                SafeGuard.Report("解析宠物背包位置失败", e);
                return null;
            }
        }

        /// <summary>按路径从指定背包里取回物品（中间层级必须是容器）；取不到返回 null。</summary>
        private static Item ResolvePathIn(Inventory inventory, string path)
        {
            if (inventory == null || string.IsNullOrEmpty(path))
                return null;

            string[] parts = path.Split('/');
            Item item = null;

            for (int i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out int index) || index < 0)
                    return null;

                item = inventory.GetItemAt(index);
                if (item == null)
                    return null;

                // 还有下一层时，当前物品必须是一个带存储空间的容器
                if (i < parts.Length - 1)
                {
                    inventory = item.Inventory;
                    if (inventory == null)
                        return null;
                }
            }

            return item;
        }

        /// <summary>
        /// 生成可存档的绑定路径：
        ///   宠物背包（含容器）      → "2/0"（与旧版本格式完全一致）
        ///   主背包容器内的物品      → "main:3/1"
        /// 主背包顶层的物品由原版 ItemShortcut_Data 保存，这里返回 false。
        /// </summary>
        public static bool TryGetBindingPath(Item item, out string path)
        {
            path = null;

            try
            {
                if (TryGetPath(item, out List<int> petPath))
                {
                    path = FormatPath(petPath);
                    return !string.IsNullOrEmpty(path);
                }

                if (TryGetMainPath(item, out List<int> mainPath) && mainPath.Count >= 2)
                {
                    path = MainPathPrefix + FormatPath(mainPath);
                    return true;
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("生成物品绑定路径失败", e);
            }

            return false;
        }

        /// <summary>按存档路径取回物品："main:" 前缀表示主背包容器内，其余为宠物背包。</summary>
        public static Item ResolveBindingPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return null;

                if (path.StartsWith(MainPathPrefix, StringComparison.Ordinal))
                    return ResolvePathIn(GetMainInventory(), path.Substring(MainPathPrefix.Length));

                return ResolvePath(path);
            }
            catch (Exception e)
            {
                SafeGuard.Report("解析物品绑定路径失败", e);
                return null;
            }
        }

        /// <summary>把路径列表序列化为存档字符串，例如 "2/0"。</summary>
        public static string FormatPath(List<int> path)
        {
            try
            {
                if (path == null || path.Count == 0)
                    return string.Empty;

                return string.Join("/", path);
            }
            catch (Exception e)
            {
                SafeGuard.Report("格式化宠物背包路径失败", e);
                return string.Empty;
            }
        }

        /// <summary>宠物背包是否已经加载完毕，可以安全地解析物品路径。</summary>
        public static bool IsEnvironmentReady()
        {
            try
            {
                if (ItemShortcut.Instance == null)
                    return false;

                CharacterMainControl master = CharacterMainControl.Main;
                if (master == null || master.CharacterItem == null)
                    return false;

                if (!LevelManager.LevelInited)
                    return false;

                // 副本等关卡不会加载/保存宠物背包（PetProxy.Start 里有同样的条件），
                // 此时背包是空的，不能据此判断「物品已不存在」
                if (!LevelConfig.SavePet)
                    return false;

                PetProxy proxy = PetProxy.Instance;
                if (proxy == null)
                    return false;

                Inventory petInventory = proxy.Inventory;
                return petInventory != null && !petInventory.Loading;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------
        // 物品用完后的自动重绑
        // ---------------------------------------------------------------

        // 反射字段放在第一次使用时做：静态构造里抛异常会连累整个类初始化失败
        private static FieldInfo _itemsField;
        private static bool _itemsFieldSearched;
        private static FieldInfo _itemTypesField;
        private static bool _itemTypesFieldSearched;

        /// <summary>读取快捷栏当前的物品列表；读不到返回 null。</summary>
        public static List<Item> GetShortcutItems()
        {
            try
            {
                if (!_itemsFieldSearched)
                {
                    _itemsFieldSearched = true;
                    _itemsField = typeof(ItemShortcut).GetField(
                        "items", BindingFlags.Instance | BindingFlags.NonPublic);
                }

                ItemShortcut shortcut = ItemShortcut.Instance;
                if (shortcut == null || _itemsField == null)
                    return null;

                return _itemsField.GetValue(shortcut) as List<Item>;
            }
            catch (Exception e)
            {
                SafeGuard.Report("读取快捷栏物品列表失败", e);
                return null;
            }
        }

        /// <summary>读取快捷栏的类型 ID 列表；读不到返回 null。</summary>
        public static List<int> GetShortcutItemTypes()
        {
            try
            {
                if (!_itemTypesFieldSearched)
                {
                    _itemTypesFieldSearched = true;
                    _itemTypesField = typeof(ItemShortcut).GetField(
                        "itemTypes", BindingFlags.Instance | BindingFlags.NonPublic);
                }

                ItemShortcut shortcut = ItemShortcut.Instance;
                if (shortcut == null || _itemTypesField == null)
                    return null;

                return _itemTypesField.GetValue(shortcut) as List<int>;
            }
            catch (Exception e)
            {
                SafeGuard.Report("读取快捷栏类型列表失败", e);
                return null;
            }
        }

        /// <summary>
        /// 尝试把一个「本 mod 尚未接管」的槽位重新接管。
        ///
        /// 背景：物品先绑定在主背包顶层时，槽位属于原版；之后玩家把物品挪进
        /// 宠物背包或主背包容器，原版会判定无效并清空槽位（HUD 图标消失、
        /// 存档写成 -1）。只要槽位里现在还拿着这件物品，就立刻重新接管。
        ///
        /// 只读取原版 items 列表，不修改槽位内容；物品仍在主背包顶层时
        /// <see cref="IsExtraBindableItem"/> 返回 false，本方法不会碰原版槽位。
        /// </summary>
        public static bool TryAdoptSlot(int index)
        {
            try
            {
                List<Item> items = GetShortcutItems();
                if (items == null || index < 0 || index >= items.Count)
                    return false;

                Item item = items[index];
                if (item == null || item.IsBeingDestroyed)
                    return false;

                if (!IsExtraBindableItem(item))
                    return false;

                PetSlotTracker.Remember(index, item);

                if (!PetSlotTracker.TryGetType(index, out _))
                    return false;

                Diag.Log($"槽位 {index + 3} 的物品位于扩展背包，已重新接管：{Describe(item)}");
                return true;
            }
            catch (Exception e)
            {
                SafeGuard.Report("重新接管快捷栏槽位失败", e);
                return false;
            }
        }

        /// <summary>快捷栏补位时可搜索的四个位置。</summary>
        internal enum ItemLocation
        {
            /// <summary>位置未知（绑定记录里没有路径）。</summary>
            Unknown = -1,

            /// <summary>主背包顶层格子。</summary>
            MainTopLevel = 0,

            /// <summary>主背包里的容器内部。</summary>
            MainContainer = 1,

            /// <summary>宠物背包顶层格子。</summary>
            PetTopLevel = 2,

            /// <summary>宠物背包里的容器内部。</summary>
            PetContainer = 3
        }

        /// <summary>
        /// 按「本身优先」的规则查找同型号替代品：
        ///   1. 先搜索绑定物品原来所在的位置；
        ///   2. 再按 主背包顶层 → 主背包容器 → 宠物背包格子 → 宠物背包容器 的顺序
        ///      搜索其余三个位置。
        /// 原位置未知时直接按固定顺序搜索。
        /// </summary>
        public static Item FindReplacementByOrigin(int typeID, int slotIndex, Item exclude)
        {
            try
            {
                if (typeID < 0)
                    return null;

                PetSlotTracker.TryGetLocationKind(slotIndex, out ItemLocation origin);

                // 固定优先级：主背包顶层 → 主背包容器 → 宠物背包格子 → 宠物背包容器
                //（与用户约定：主背包容器先于宠物背包，原版已经先搜过主背包顶层）
                ItemLocation[] order =
                {
                    ItemLocation.MainTopLevel,
                    ItemLocation.MainContainer,
                    ItemLocation.PetTopLevel,
                    ItemLocation.PetContainer
                };

                // 1) 物品原来所在的位置（「本身」）优先
                Item found = FindAt(origin, typeID, exclude);
                if (found != null)
                    return found;

                // 2) 再按固定顺序搜索其余三个位置
                foreach (ItemLocation location in order)
                {
                    if (location == origin)
                        continue;

                    found = FindAt(location, typeID, exclude);
                    if (found != null)
                        return found;
                }

                return null;
            }
            catch (Exception e)
            {
                SafeGuard.Report("按原位置查找同型号替代品失败", e);
                return null;
            }
        }

        private static Item FindAt(ItemLocation location, int typeID, Item exclude)
        {
            switch (location)
            {
                case ItemLocation.MainTopLevel:
                    return FindTopLevelItem(GetMainInventory(), typeID, exclude);

                case ItemLocation.MainContainer:
                    return FindContainerItem(GetMainInventory(), typeID, exclude, 0);

                case ItemLocation.PetTopLevel:
                    return FindTopLevelItem(GetPetInventory(), typeID, exclude);

                case ItemLocation.PetContainer:
                    return FindContainerItem(GetPetInventory(), typeID, exclude, 0);

                default:
                    return null;
            }
        }

        /// <summary>只搜索背包顶层格子里直接放着的物品。</summary>
        private static Item FindTopLevelItem(Inventory inventory, int typeID, Item exclude)
        {
            if (inventory == null)
                return null;

            int capacity = inventory.Capacity;
            for (int i = 0; i < capacity; i++)
            {
                Item item = inventory.GetItemAt(i);
                if (item == null || item == exclude || item.IsBeingDestroyed)
                    continue;

                if (item.TypeID == typeID)
                    return item;
            }

            return null;
        }

        /// <summary>
        /// 只搜索背包里**容器内部**的物品（不含容器本身、不含顶层格子），并递归到嵌套容器。
        /// </summary>
        private static Item FindContainerItem(Inventory inventory, int typeID, Item exclude, int depth)
        {
            if (inventory == null || depth > MaxDepth)
                return null;

            int capacity = inventory.Capacity;
            for (int i = 0; i < capacity; i++)
            {
                Item item = inventory.GetItemAt(i);
                if (item == null || item == exclude || item.IsBeingDestroyed)
                    continue;

                Inventory inner = item.Inventory;
                if (inner == null)
                    continue;

                // 容器内部的第一层优先，再递归更深的容器
                Item found = FindTopLevelItem(inner, typeID, exclude);
                if (found != null)
                    return found;

                found = FindContainerItem(inner, typeID, exclude, depth + 1);
                if (found != null)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// 在宠物背包里找同型号物品：宠物背包格子优先，其次容器内部。
        /// <paramref name="exclude"/> 用于排除当前槽位里的废旧引用。
        /// </summary>
        public static Item FindPetItemByType(int typeID, Item exclude)
        {
            try
            {
                if (typeID < 0)
                    return null;

                Inventory petInventory = GetPetInventory();
                return FindTopLevelItem(petInventory, typeID, exclude)
                    ?? FindContainerItem(petInventory, typeID, exclude, 0);
            }
            catch (Exception e)
            {
                SafeGuard.Report("在宠物背包里查找同型号物品失败", e);
                return null;
            }
        }

        /// <summary>
        /// 在主背包里找同型号物品：顶层格子优先（与原版 <c>MainInventory.Find</c> 一致），
        /// 其次容器内部。
        /// </summary>
        public static Item FindMainItemByType(int typeID)
        {
            try
            {
                if (typeID < 0)
                    return null;

                Inventory mainInventory = GetMainInventory();
                return FindTopLevelItem(mainInventory, typeID, null)
                    ?? FindContainerItem(mainInventory, typeID, null, 0);
            }
            catch (Exception e)
            {
                SafeGuard.Report("在主背包里查找同型号物品失败", e);
                return null;
            }
        }

        /// <summary>只在主背包的容器内部找同型号物品（原版已经搜过顶层）。</summary>
        public static Item FindMainContainerItemByType(int typeID)
        {
            try
            {
                if (typeID < 0)
                    return null;

                return FindContainerItem(GetMainInventory(), typeID, null, 0);
            }
            catch (Exception e)
            {
                SafeGuard.Report("在主背包容器里查找同型号物品失败", e);
                return null;
            }
        }

        /// <summary>
        /// 把宠物物品绑定到槽位是否会波及原版主背包绑定：
        ///   - 目标槽位已被主背包物品占用；
        ///   - 其他槽位有同 TypeID 的主背包物品（原版 Set_Local 会连坐清除）。
        /// 判断不了时按「有冲突」处理，优先保护原版。
        /// </summary>
        public static bool HasMainInventoryConflict(int slot, Item petItem)
        {
            try
            {
                List<Item> items = GetShortcutItems();
                if (items == null)
                    return true;

                int count = Math.Min(items.Count, ItemShortcut.MaxIndex + 1);

                if (slot >= 0 && slot < count)
                {
                    Item target = items[slot];

                    // 只有「仍然有效的主背包物品」才算占用；
                    // 已用完/已销毁的废旧引用不能误判成主背包物品
                    if (target != null
                        && !IsPetItem(target)
                        && ItemShortcut.IsItemValid(target))
                    {
                        return true;
                    }
                }

                for (int i = 0; i < count; i++)
                {
                    if (i == slot)
                        continue;

                    Item existing = items[i];
                    if (existing == null)
                        continue;

                    // 宠物物品之间的同型号冲突是原版限制，不属于「改动主背包」
                    if (IsPetItem(existing))
                        continue;

                    // 已销毁 / 已不在原位置的其他槽位物品同样会体现在原版
                    // itemTypes 里，但并不会被 Set_Local 连坐清除，不算冲突
                    if (!ItemShortcut.IsItemValid(existing))
                        continue;

                    if (existing.TypeID == petItem.TypeID)
                        return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        // 静态事件 ItemShortcut.OnSetItem（public static event）的委托字段
        private static FieldInfo _onSetItemField;
        private static bool _onSetItemFieldSearched;

        // ItemShortcutButton.Refresh 是私有方法，直接调用它作为事件之外的兜底刷新
        private static MethodInfo _buttonRefreshMethod;
        private static bool _buttonRefreshMethodSearched;

        /// <summary>缓存的快捷栏按钮：FindObjectsOfType 不便宜，只在场景重建后重新收集。</summary>
        private static ItemShortcutButton[] _buttonCache;

        /// <summary>
        /// 定时兜底对账的间隔（秒）。只处理本 mod 接管的槽位（最多 6 个），
        /// 每次算一次绑定路径，开销很小；0.2 秒让同类型容器互换等瞬态的恢复更快。
        /// </summary>
        private const float ReconcileInterval = 0.2f;

        private static float _lastReconcileTime = -999f;

        /// <summary>防止刷新过程中再次触发刷新（Refresh → Get → 本 mod 逻辑 → 再通知）。</summary>
        private static bool _notifying;

        /// <summary>刷新过程中又发生了绑定变化（如自动换绑），当前这轮结束后需要补刷一轮。</summary>
        private static bool _refreshRequested;

        /// <summary>
        /// 通知快捷栏 UI 刷新指定槽位，并直接刷新全部快捷栏按钮。
        ///
        /// 为什么要两种方式一起用：
        ///   1. 原版 HUD 只订阅 ItemShortcut.OnSetItem，而游戏里有些状态变化
        ///      （物品使用中脱离背包、用完放回容器）不会产生原版事件；
        ///   2. OnSetItem 是多播委托，任何一个订阅者抛异常都会中断后面的订阅者，
        ///      所以这里逐个调用并各自 try/catch，保证其余按钮仍然刷新；
        ///   3. 最后直接调用每个 ItemShortcutButton.Refresh，事件没订阅上也能同步。
        /// </summary>
        public static void NotifySlotChanged(int index)
        {
            if (_notifying)
            {
                // 当前刷新过程中又发生了绑定变化（典型场景：按钮 Refresh → Get →
                // 检测到物品耗尽并自动换绑）。这时绝不能直接返回，否则触发换绑的
                // 那一个按钮已经停在刷新前的空状态，而之后不会再有人刷新它。
                // 记下请求，等当前这一轮刷新结束后补刷一轮。
                _refreshRequested = true;
                return;
            }

            _notifying = true;

            try
            {
                int rounds = 0;
                do
                {
                    _refreshRequested = false;

                    InvokeOnSetItem(index);
                    RefreshShortcutButtons();
                }
                while (_refreshRequested && ++rounds < 4);
            }
            catch (Exception e)
            {
                SafeGuard.Report("刷新快捷栏槽位显示失败", e);
                Diag.LogException("刷新快捷栏槽位显示失败", e);
            }
            finally
            {
                _notifying = false;
                _refreshRequested = false;
            }
        }

        private static void InvokeOnSetItem(int index)
        {
            try
            {
                if (!_onSetItemFieldSearched)
                {
                    _onSetItemFieldSearched = true;

                    // OnSetItem 在反编译源码里是 public static event，
                    // 只查 NonPublic 会拿不到字段，导致刷新静默失效
                    _onSetItemField = typeof(ItemShortcut).GetField(
                        "OnSetItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }

                if (!(_onSetItemField?.GetValue(null) is Delegate del))
                    return;

                // 逐个调用：一个订阅者异常不能连累其它按钮
                foreach (Delegate handler in del.GetInvocationList())
                {
                    try
                    {
                        ((Action<int>)handler)(index);
                    }
                    catch (Exception e)
                    {
                        SafeGuard.Report("快捷栏 OnSetItem 订阅者执行失败", e);
                        Diag.LogException(
                            $"快捷栏 OnSetItem 订阅者 {handler.Method.DeclaringType?.Name}.{handler.Method.Name} 执行失败", e);
                    }
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("触发快捷栏刷新事件失败", e);
                Diag.LogException("触发快捷栏刷新事件失败", e);
            }
        }

        /// <summary>换关卡 / 换存档后快捷栏按钮会重建，清掉缓存让下一次刷新重新收集。</summary>
        public static void InvalidateShortcutButtonCache()
        {
            _buttonCache = null;
        }

        /// <summary>直接调用所有快捷栏按钮的私有 Refresh，作为事件之外的强制同步。</summary>
        public static void RefreshShortcutButtons()
        {
            try
            {
                ItemShortcutButton[] buttons = GetShortcutButtons();
                if (buttons == null || buttons.Length == 0)
                {
                    // HUD 还没建好时找不到按钮，记一次便于排查（SafeGuard 会限流）
                    SafeGuard.Report("快捷栏 UI 暂时找不到 ItemShortcutButton，本次跳过强制刷新");
                    return;
                }

                foreach (ItemShortcutButton button in buttons)
                {
                    if (button == null)
                        continue;

                    InvokeButtonRefresh(button);
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("强制刷新快捷栏按钮失败", e);
                Diag.LogException("强制刷新快捷栏按钮失败", e);
            }
        }

        /// <summary>
        /// 兜底对账：定时比较本 mod 接管槽位的「按钮缓存显示」与实时
        /// <see cref="ItemShortcut.Get"/>，不一致就调用按钮自己的 Refresh。
        ///
        /// 原版 ItemShortcutButton 只在 OnSetItem / 主背包内容变化 / 堆叠数变化时刷新，
        /// 宠物背包和主背包容器里的变化它收不到；一旦错过，图标就会一直停在
        /// 空白或旧物品上（背包编辑格每次打开都重建，所以看起来是正常的）。
        ///
        /// 这里**只处理 PetSlotTracker 记录过的槽位**：原版主背包槽位一次都不读，
        /// 避免在原版物品使用中（暂时脱离背包）时误判无效而清空原版绑定。
        /// </summary>
        public static void ReconcileShortcutButtons()
        {
            try
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastReconcileTime < ReconcileInterval)
                    return;

                _lastReconcileTime = now;

                ItemShortcutButton[] buttons = GetShortcutButtons();
                if (buttons == null || buttons.Length == 0)
                    return;

                foreach (ItemShortcutButton button in buttons)
                {
                    if (button == null)
                        continue;

                    int index = button.Index;

                    if (!PetSlotTracker.IsTracked(index))
                    {
                        // 兜底补接管：原版槽位里的物品如果已经挪进宠物背包 / 主背包容器，
                        // 原版会把它清空。这里不读原版主背包槽位的内容做任何修改，
                        // 只在物品确实位于扩展背包时接管并刷新。
                        if (!TryAdoptSlot(index))
                            continue;

                        InvokeButtonRefresh(button);
                        continue;
                    }

                    // 必须在 ItemShortcut.Get 之前判断：Get 会顺带调用 Observe 更新位置记录，
                    // 之后就看不出变化了。同类型容器之间移动时物品实例和 TypeID 都没变，
                    // 按钮显示本来就对，但物品已经换到另一个容器里，必须重新刷新一次，
                    // 否则接下来的使用 / 放回流程会停在旧容器的状态上。
                    bool locationChanged = PetSlotTracker.HasLocationChanged(index);

                    Item current = ItemShortcut.Get(index);

                    if (!locationChanged && current == button.TargetItem)
                        continue;

                    if (locationChanged)
                    {
                        Diag.Log(
                            $"槽位 {index + 3} 的物品在容器之间移动（含同类型容器），刷新快捷栏 UI："
                            + Describe(current));
                    }
                    else
                    {
                        Diag.Log(
                            $"UI 兜底同步槽位 {index + 3}：按钮显示 {Describe(button.TargetItem)}，"
                            + $"实时取值 {Describe(current)}");
                    }

                    InvokeButtonRefresh(button);
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("定时同步快捷栏 UI 失败", e);
                Diag.LogException("定时同步快捷栏 UI 失败", e);
            }
        }

        /// <summary>缓存的快捷栏按钮列表；有按钮被销毁（换关/换存档）时自动重新收集。</summary>
        private static ItemShortcutButton[] GetShortcutButtons()
        {
            ItemShortcutButton[] cached = _buttonCache;
            if (cached != null && cached.Length > 0)
            {
                bool alive = true;
                foreach (ItemShortcutButton button in cached)
                {
                    if (button == null)
                    {
                        alive = false;
                        break;
                    }
                }

                if (alive)
                    return cached;
            }

            _buttonCache = UnityEngine.Object.FindObjectsOfType<ItemShortcutButton>(true);
            return _buttonCache;
        }

        /// <summary>调用单个按钮的私有 Refresh，异常只记录不抛出。</summary>
        private static void InvokeButtonRefresh(ItemShortcutButton button)
        {
            try
            {
                if (!_buttonRefreshMethodSearched)
                {
                    _buttonRefreshMethodSearched = true;
                    _buttonRefreshMethod = typeof(ItemShortcutButton).GetMethod(
                        "Refresh", BindingFlags.Instance | BindingFlags.NonPublic);
                }

                if (_buttonRefreshMethod == null)
                    return;

                _buttonRefreshMethod.Invoke(button, null);
            }
            catch (Exception e)
            {
                // 这里最可能就是「图标不显示」的根因（ItemDisplay.Setup 内部异常），
                // 必须留下完整堆栈
                Diag.LogException($"刷新快捷栏按钮 {button.Index} 失败", e);
            }
        }

        /// <summary>调试用的简短描述。</summary>
        public static string Describe(Item item)
        {
            if (item == null)
                return "null";

            try
            {
                return $"{item.DisplayName}(TypeID={item.TypeID})";
            }
            catch
            {
                return "未知物品";
            }
        }
    }
}
