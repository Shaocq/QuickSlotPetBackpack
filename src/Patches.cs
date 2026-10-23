using System;
using System.Collections.Generic;
using System.Reflection;
using Duckov;
using Duckov.UI;
using HarmonyLib;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.EventSystems;

namespace QuickSlotPetBackpack
{
    /// <summary>
    /// 放宽快捷栏有效性判断：宠物背包（含容器）里的非武器物品，以及
    /// 主背包容器内部的非武器物品，也算有效。
    /// 使用中的物品会短暂脱离背包（InInventory 为 null），同样允许保留在槽位里。
    ///
    /// **对齐原版：这里完全不看耐久**。原版 <c>IsItemValid</c> 只关心物品是否
    /// 还在主背包里，耐久为 0 的物品在被 <c>CA_UseItem.OnFinish</c> 销毁前仍然有效。
    /// 提前判无效会让槽位在「UI 仍显示 A」时改用 B，出现扣错物品耐久的问题。
    ///
    /// 另外必须把「绑定物品正在使用」也算作有效，否则原版 Get_Local
    /// 会把槽位标记成 dirty，下一帧 Update 直接把 items 清空（快捷栏变空）。
    /// </summary>
    [HarmonyPatch(typeof(ItemShortcut), nameof(ItemShortcut.IsItemValid))]
    internal static class ItemShortcutIsItemValidPatch
    {
        private static void Postfix(Item item, ref bool __result)
        {
            try
            {
                if (__result || item == null)
                    return;

                if (CharacterMainControl.Main == null)
                    return;

                // 扩展物品（宠物背包 / 主背包容器内的非武器物品）本身就算有效
                if (PetShortcutUtility.IsExtraBindableItem(item))
                {
                    __result = true;
                    return;
                }

                // 绑定实例正处于「使用中 / 刚用完」的瞬态：宠物物品会被 UseItem
                // 移出背包，这段时间必须算有效，否则原版 Update 会清空槽位。
                // 只认真正处于使用流程里的实例：物品被丢到地上、放进其它容器时
                // 这里返回 false，槽位按原版规则清空。
                if (PetSlotTracker.IsDetachedWithinGrace(item))
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                // 这里失败只是让原版判断结果生效，绝不能把异常抛回游戏
                SafeGuard.Report("放宽快捷栏有效性判断失败", e);
            }
        }
    }

    /// <summary>
    /// 记录/清除「扩展绑定槽位」标记（宠物背包 / 主背包容器内物品）：
    ///   - 绑定了扩展物品 → 记住槽位和 TypeID（物品用完后自动重绑的依据）；
    ///   - 换成主背包顶层物品 → 清除标记，该槽位完全交回原版。
    /// 原版 ItemShortcut.Load 走的是私有 Set_Local，不会触发这里，
    /// 所以原版恢复的主背包槽位不会被误标成扩展槽位。
    /// </summary>
    [HarmonyPatch(typeof(ItemShortcut), nameof(ItemShortcut.Set))]
    internal static class ItemShortcutSetPatch
    {
        private static void Postfix(int index, Item item, bool __result)
        {
            try
            {
                if (!__result)
                    return;

                // 扩展物品，或者「本来就是这里绑定的那一件」：
                // 后者可能是使用中又被别的逻辑重新 Set 了一次（此时它已脱离背包，
                // IsExtraBindableItem 认不出来），绝不能因此丢掉绑定
                if (item != null
                    && (PetShortcutUtility.IsExtraBindableItem(item)
                        || PetSlotTracker.IsRememberedItem(item)))
                {
                    PetSlotTracker.Remember(index, item);
                    Diag.Log($"槽位 {index + 3} 已绑定扩展物品：{PetShortcutUtility.Describe(item)}");
                }
                else
                {
                    PetSlotTracker.Forget(index);
                }

                // 绑定 / 改绑 / 清空都立即刷新一次快捷栏显示：
                // 宠物背包里的变化原版 HUD 收不到事件，只能靠这里主动通知
                PetShortcutUtility.NotifySlotChanged(index);
            }
            catch (Exception e)
            {
                SafeGuard.Report("更新快捷栏槽位标记失败", e);
            }
        }
    }

    /// <summary>
    /// 扩展绑定槽位（宠物背包 / 主背包容器）的核心逻辑，**对齐原版语义**。
    ///
    /// 原版 <c>ItemShortcut</c> 的模型是「**TypeID 为主，实例只是缓存**」：
    ///   - <c>IsItemValid</c> 完全不看耐久；
    ///   - 缓存实例失效（被销毁 / 不在背包里）时清空实例，但 <c>itemTypes</c> 原样保留——
    ///     槽位进入「待命」状态，以后同型号物品一出现就自动绑上；
    ///   - 补位时只找同 TypeID 的替代品，找到就 <c>Set</c>，并由原版 <c>Set_Local</c>
    ///     连坐清空其他同型号槽位；
    ///   - 找不到就什么都不做，保留 <c>itemTypes</c> 待命，**绝不置 -1**。
    ///
    /// 本 mod 做了这些扩展：
    ///   1. 搜索范围覆盖四个位置：主背包顶层、主背包容器、宠物背包格子、宠物背包容器；
    ///      **绑定物品原来所在的位置优先**，其次按
    ///      主背包顶层 → 主背包容器 → 宠物背包格子 → 宠物背包容器 的顺序搜索；
    ///   2. 宠物物品使用期间会被 <c>UseItem</c> 移出背包，这段时间保留**同一件**实例，
    ///      否则会出现「UI 显示 A、按键却用了 B」——原版主背包物品不会脱离背包，没有这个瞬态；
    ///   3. 原版主背包顶层槽位仍由原版维护，但原版找不到同型号物品时，Postfix 会追加搜索
    ///      主背包容器 → 宠物背包格子 → 宠物背包容器。
    /// </summary>
    [HarmonyPatch(typeof(ItemShortcut), "Get_Local")]
    internal static class ItemShortcutGetLocalPatch
    {
        private static bool Prefix(int index, ref Item __result)
        {
            try
            {
                if (!PetSlotTracker.TryGetType(index, out int typeID))
                {
                    // 未接管的槽位：如果它当前持有的物品已经位于宠物背包 / 主背包容器
                    // （典型场景：先绑定主背包顶层物品，交回原版后又把物品挪进容器），
                    // 必须在这里重新接管。否则紧接着原版 IsItemValid 会判无效并清空槽位，
                    // HUD 图标直接消失、存档里也只剩 -1。
                    if (!PetShortcutUtility.TryAdoptSlot(index)
                        || !PetSlotTracker.TryGetType(index, out typeID))
                    {
                        return true;
                    }
                }

                List<Item> items = PetShortcutUtility.GetShortcutItems();
                if (items == null || index < 0 || index >= items.Count)
                    return true;

                List<int> itemTypes = PetShortcutUtility.GetShortcutItemTypes();
                if (itemTypes == null || index < 0 || index >= itemTypes.Count)
                    return true;

                // 原版主动清空槽位（玩家操作，或 Set_Local 因同型号连坐）时会把
                // itemTypes 置为 -1。这类槽位不再自动重绑，交回原版处理；
                // 只有「物品用完、itemTypes 仍保留原 TypeID」才需要找替代品。
                if (items[index] == null && itemTypes[index] < 0)
                {
                    PetSlotTracker.Forget(index);
                    return true;
                }

                Item current = items[index];

                // 本次调用是否清掉了槽位里的实例（用于只刷新一次 UI，
                // 避免「待命」状态下每次扫描都无谓地重刷快捷栏）
                bool clearedInstance = false;

                // 1) 槽位里的实例还活着：与原版一样**不看耐久**
                if (current != null && !current.IsBeingDestroyed)
                {
                    if (ItemShortcut.IsItemValid(current))
                    {
                        // 仍在背包里：顺便刷新原位置 / 路径 / 最近有效时间
                        bool moved = PetSlotTracker.Observe(index, current);

                        // 槽位里已经是主背包顶层物品（原版兜底或玩家手动改绑），
                        // 这个槽位彻底交回原版，不再按扩展槽位处理
                        if (PetShortcutUtility.IsMainTopLevelItem(current))
                        {
                            PetSlotTracker.Forget(index);

                            // 交回原版时也强制刷新一次：物品刚被移到主背包顶层，
                            // 原版 HUD 未必收到对应事件，否则图标会停在空白/旧状态
                            PetShortcutUtility.NotifySlotChanged(index);

                            Diag.Log($"槽位 {index + 3} 已变成主背包顶层物品，交回原版：{PetShortcutUtility.Describe(current)}");
                            return true;
                        }

                        // 物品在四个位置之间移动（宠物背包 / 宠物背包容器 / 主背包 / 主背包容器）：
                        // 原版 HUD 收不到宠物背包那侧的内容变化，这里主动刷新一次
                        if (moved)
                        {
                            PetShortcutUtility.NotifySlotChanged(index);
                            Diag.Log($"槽位 {index + 3} 的物品位置已变化，刷新快捷栏 UI：{PetShortcutUtility.Describe(current)}");
                        }

                        return true;
                    }

                    // 实例还在，但原版判无效：使用中被 UseItem 移出背包、或刚用完
                    // 暂时挂在手上——这是宠物物品独有的瞬态，必须保留**同一件**实例。
                    // 换成别的同型号物品就会出现「UI 显示 A、按键用了 B」。
                    if (PetSlotTracker.IsDetachedWithinGrace(current))
                    {
                        // 正常情况下 items[index] 本来就还是这一件（Detach 不会改槽位）。
                        // 只有确实需要写回时才刷新 UI / 记日志——Get_Local 会被兜底
                        // 对账每 0.2 秒调用一次，无条件刷新会造成整个快捷栏反复重刷、
                        // 诊断日志每秒刷屏。
                        bool restored = items[index] != current || itemTypes[index] != current.TypeID;

                        items[index] = current;
                        itemTypes[index] = current.TypeID;

                        PetSlotTracker.Observe(index, current);
                        PetSlotTracker.Touch(current);

                        if (restored)
                        {
                            PetShortcutUtility.NotifySlotChanged(index);
                            Diag.Log(
                                $"槽位 {index + 3} 保留当前绑定实例（使用中 / 刚用完）："
                                + PetShortcutUtility.Describe(current));
                        }

                        __result = current;
                        return false;
                    }

                    // 活着但位置失效（被挪到别处）：与原版一致，只清实例、保留 itemTypes
                    items[index] = null;
                    clearedInstance = true;
                    Diag.Log(
                        $"槽位 {index + 3} 的绑定实例已不在背包内，清空实例、保留型号（TypeID={typeID}）："
                        + PetShortcutUtility.Describe(current));
                }
                else
                {
                    // 实例已被销毁（耐久耗尽 / 用完 / 被丢弃）：
                    // 与原版一致，只清实例、保留 itemTypes 待命
                    bool hadInstance = !ReferenceEquals(current, null);
                    items[index] = null;
                    current = null;
                    clearedInstance = hadInstance;

                    if (hadInstance)
                        Diag.Log($"槽位 {index + 3} 的绑定实例已销毁，保留型号待命（TypeID={typeID}）");
                }

                // 2) 按原版语义补位：只找同 TypeID 的替代品。
                //    刚搜索失败的 0.5 秒内不重复扫描背包，避免 UI 每帧刷新时反复遍历。
                if (!PetSlotTracker.TryEnterSearch(index))
                {
                    __result = items[index];
                    return false;
                }

                // 四位置搜索：绑定物品原来所在的位置优先，其次按
                // 主背包顶层 → 主背包容器 → 宠物背包格子 → 宠物背包容器 的顺序
                Item replacement = PetShortcutUtility.FindReplacementByOrigin(typeID, index, current);

                if (replacement != null && ItemShortcut.Set(index, replacement))
                {
                    // 原版 Set_Local 会连坐清空其他同型号槽位，那些槽位不再由本 mod 接管
                    ForgetCollateralSlots(items, itemTypes, index, typeID);

                    Diag.Log(
                        $"槽位 {index + 3} 已按型号补位到替代品："
                        + PetShortcutUtility.Describe(replacement));

                    PetShortcutUtility.NotifySlotChanged(index);

                    __result = replacement;
                    return false;
                }

                // 3) 四处都没有同型号物品：**保留 itemTypes 待命**（与原版一致），
                //    以后背包里再出现同型号物品会自动绑上；绝不置 -1，
                //    也绝不依赖原版 MainInventory.Find 兜底到别的物品。
                items[index] = null;
                itemTypes[index] = typeID;

                // 只有刚刚清掉了实例才需要刷新 HUD / 记日志；「待命」状态下
                // 每次扫描（0.5 秒冷却）都重复通知、重复写日志只会刷屏
                if (clearedInstance)
                {
                    PetShortcutUtility.NotifySlotChanged(index);
                    Diag.Log($"槽位 {index + 3} 暂未找到同型号替代品，保留型号待命（TypeID={typeID}）");
                }

                __result = null;
                return false;
            }
            catch (Exception e)
            {
                SafeGuard.Report("宠物背包物品用完后的补位失败", e);
                Diag.LogException("宠物背包物品用完后的补位失败", e);
                return true;
            }
        }

        /// <summary>
        /// 原版主背包顶层槽位的追加兜底。
        ///
        /// 原版 <c>Get_Local</c> 在缓存实例失效时，只用 <c>MainInventory.Find</c> 在
        /// **主背包顶层**找同型号物品；找不到就 <c>SetDirty</c> 并返回 null。
        /// 这里在原版确实没找到（返回 null）时追加搜索：
        ///   主背包容器 → 宠物背包格子 → 宠物背包容器。
        /// （原版已经搜过主背包顶层，无需重复。）
        ///
        /// 只处理未被本 mod 接管的槽位：扩展槽位由上面的 Prefix 负责四个位置
        /// 的「本身优先」搜索，这里不能抢它的结果。
        /// </summary>
        private static void Postfix(int index, ref Item __result, bool __runOriginal)
        {
            try
            {
                // Prefix 接管过（扩展槽位）时结果已经由 Prefix 给出
                if (!__runOriginal || __result != null)
                    return;

                if (PetSlotTracker.IsTracked(index))
                    return;

                List<int> itemTypes = PetShortcutUtility.GetShortcutItemTypes();
                if (itemTypes == null || index < 0 || index >= itemTypes.Count)
                    return;

                // itemTypes < 0 表示原版主动清空（例如 Set_Local 连坐），不自动重绑
                int typeID = itemTypes[index];
                if (typeID < 0)
                    return;

                // 与原版补位共用 0.5 秒冷却，避免 HUD 每次刷新都遍历背包
                if (!PetSlotTracker.TryEnterSearch(index))
                    return;

                Item replacement = PetShortcutUtility.FindMainContainerItemByType(typeID);
                if (replacement == null)
                    replacement = PetShortcutUtility.FindPetItemByType(typeID, null);

                if (replacement == null)
                    return;

                // IsItemValid 的后置补丁会把扩展物品判为有效，Set 能成功；
                // 成功后该槽位由本 mod 接管（Set 后置会 Remember）
                if (!ItemShortcut.Set(index, replacement))
                    return;

                Diag.Log(
                    $"槽位 {index + 3} 原版主背包顶层无同型号物品，已从扩展位置补位："
                    + PetShortcutUtility.Describe(replacement));

                __result = replacement;
            }
            catch (Exception e)
            {
                // 失败时保留原版结果（null），绝不抛回游戏
                SafeGuard.Report("原版槽位的扩展补位失败", e);
            }
        }

        /// <summary>
        /// 原版 <c>Set_Local</c> 会把其他槽位里同 TypeID 的绑定连坐清空
        /// （items 置 null、itemTypes 置 -1）。这里把被清空的槽位从本 mod 的接管表里移除，
        /// 与会话内原版行为保持一致。
        /// </summary>
        private static void ForgetCollateralSlots(
            List<Item> items, List<int> itemTypes, int keepIndex, int typeID)
        {
            try
            {
                int count = Math.Min(items.Count, itemTypes.Count);

                for (int i = 0; i < count; i++)
                {
                    if (i == keepIndex || !PetSlotTracker.IsTracked(i))
                        continue;

                    if (items[i] == null && itemTypes[i] < 0)
                    {
                        PetSlotTracker.Forget(i);
                        Diag.Log($"槽位 {i + 3} 因同型号连坐被原版清空，解除接管（TypeID={typeID}）");
                    }
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("清理被连坐清空的扩展槽位失败", e);
            }
        }
    }

    /// <summary>原版保存快捷栏时，顺带把宠物背包里的绑定写进游戏存档。</summary>
    [HarmonyPatch(typeof(ItemShortcut), "Save")]
    internal static class ItemShortcutSavePatch
    {
        private static void Postfix()
        {
            try
            {
                PetShortcutPersistence.Save();
            }
            catch (Exception e)
            {
                SafeGuard.Report("保存宠物背包快捷栏绑定失败", e);
            }
        }
    }

    /// <summary>
    /// 拖拽物品到快捷栏编辑格时，原版会把「不在角色身上」的物品塞进主背包。
    /// 宠物背包物品改为直接绑定，物品留在宠物背包里。
    /// </summary>
    [HarmonyPatch(typeof(ItemShortcutEditorEntry), nameof(ItemShortcutEditorEntry.OnDrop))]
    internal static class ItemShortcutEditorEntryOnDropPatch
    {
        // 字段反射放在第一次使用时做：静态构造里抛异常会连累整个补丁类初始化失败
        private static FieldInfo _indexField;
        private static bool _indexFieldSearched;

        private static FieldInfo GetIndexField()
        {
            if (!_indexFieldSearched)
            {
                _indexFieldSearched = true;

                try
                {
                    _indexField = AccessTools.Field(typeof(ItemShortcutEditorEntry), "index");
                }
                catch (Exception e)
                {
                    SafeGuard.Report("获取快捷栏编辑格序号字段失败", e);
                }
            }

            return _indexField;
        }

        private static bool Prefix(ItemShortcutEditorEntry __instance, PointerEventData eventData)
        {
            try
            {
                FieldInfo indexField = GetIndexField();
                if (__instance == null || eventData == null || eventData.pointerDrag == null || indexField == null)
                    return true;

                IItemDragSource source = eventData.pointerDrag.GetComponent<IItemDragSource>();
                if (source == null || !source.IsEditable())
                    return true;

                Item item = source.GetItem();
                if (item == null || !PetShortcutUtility.IsPetItem(item))
                    return true;

                eventData.Use();

                int index = (int)indexField.GetValue(__instance);
                Item targetItem = ItemShortcut.Get(index);

                // Set 内部会触发 OnSetItem，编辑格自己会刷新
                if (ItemShortcut.Set(index, item))
                    AudioManager.Post("UI/click");

                // 保留原版的「两个快捷栏格互换」行为
                if (source is ItemShortcutEditorEntry other
                    && other != __instance
                    && targetItem != null)
                {
                    ItemShortcut.Set((int)indexField.GetValue(other), targetItem);
                }

                return false;
            }
            catch (Exception e)
            {
                SafeGuard.Report("快捷栏拖入宠物背包物品失败", e);
                return true;
            }
        }
    }

    /// <summary>开始使用物品前记录它原来的位置（宠物背包 / 主背包容器内的物品）。</summary>
    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.UseItem))]
    internal static class CharacterMainControlUseItemPatch
    {
        private static void Prefix(CharacterMainControl __instance, Item item)
        {
            try
            {
                if (__instance == null || item == null)
                    return;

                // 先把「最近一次确认在背包里」的时间戳刷新到此刻：
                // 即使后面的使用跟踪因为其它 mod 的改写没有生效，
                // 使用期间也不会被当成「已经不在背包里」而清空/换绑。
                PetSlotTracker.Touch(item);

                // 不限定必须是 Main：临时控制其他角色时也可能用到主角宠物背包里的物品，
                // Record 内部会自行判断是不是本 mod 接管的扩展物品
                PetUseTracker.Record(item);

                // 开始使用就强制刷新一次快捷栏：物品接下来会被 Detach，
                // 原版 HUD 收到的是「物品离开背包」的变化，可能先把图标刷成空的
                PetSlotTracker.NotifySlotsForItem(item);
            }
            catch (Exception e)
            {
                SafeGuard.Report("记录宠物背包物品使用位置失败", e);
            }
        }

        /// <summary>
        /// UseItem 在启动使用动作之前就把扩展绑定物品 Detach 出来了。
        /// 角色正在跑动、交互、换弹或做其他动作时 StartAction 会失败，
        /// 这时 CA_UseItem.OnStop 永远不会触发，物品会一直悬空。
        /// 动作没真正跑起来就立刻放回原位置，并刷新快捷栏 UI。
        /// </summary>
        private static void Postfix(CharacterMainControl __instance, Item item)
        {
            try
            {
                if (__instance == null || item == null)
                    return;

                CA_UseItem action = __instance.useItemAction;
                if (action != null && action.Running)
                {
                    // 使用动作已经启动：物品已经被 Detach 并挂到角色身上，
                    // 再强制刷新一次快捷栏，保证使用过程中图标不会变空
                    PetSlotTracker.NotifySlotsForItem(item);
                    return;
                }

                TryReturnToOrigin(item);
            }
            catch (Exception e)
            {
                SafeGuard.Report("物品使用动作启动失败后回位出错", e);
            }
        }

        /// <summary>
        /// 放回原位置：优先用 UseItem 记录的原位置，记录丢失时（例如别的 mod
        /// 改写了 UseItem）退到槽位绑定里记住的原位置。放回成功后刷新快捷栏 UI。
        /// 返回 true 表示物品已经处理完，调用方应跳过原版拾取。
        /// </summary>
        internal static bool TryReturnToOrigin(Item item)
        {
            bool handled = PetUseTracker.TryReturnToOrigin(item);
            if (!handled)
                handled = PetSlotTracker.TryReturnToOrigin(item);

            if (handled)
            {
                Diag.Log("使用结束后物品已放回原位置：" + PetShortcutUtility.Describe(item));

                // 物品回到原位（或本来就在原位）后，主动让快捷栏重新读一次：
                // 使用期间 UI 可能已经因为原版事件被刷成空的了
                PetSlotTracker.NotifySlotsForItem(item);
            }

            return handled;
        }
    }

    /// <summary>
    /// 使用结束后原版会 PickupItem：不在主背包顶层就塞进主背包，主背包满了就掉落。
    /// 扩展绑定物品（宠物背包 / 主背包容器内）在这里放回原容器，
    /// 避免剩余堆叠跑到主背包顶层或掉在地上。
    /// </summary>
    [HarmonyPatch(typeof(CharacterMainControl), nameof(CharacterMainControl.PickupItem))]
    internal static class CharacterMainControlPickupItemPatch
    {
        private static bool Prefix(CharacterMainControl __instance, Item item, ref bool __result)
        {
            try
            {
                if (__instance == null || item == null)
                    return true;

                // 物品已经回到原容器里了（可能是刚刚回位成功，或是被其他逻辑放回去的），
                // 原版 PickupItem 会把它再拿出来塞进主背包顶层，这里直接拦掉
                if (PetShortcutUtility.IsExtraBindableItem(item))
                {
                    item.Inspected = true;
                    __result = true;

                    // 物品已经回到扩展背包里（使用完成）：强制刷新一次快捷栏显示
                    PetSlotTracker.NotifySlotsForItem(item);
                    return false;
                }

                // 只在物品与记录一致时才接管，不会影响其他角色正常拾取；
                // 使用跟踪丢失时用槽位绑定里记住的原位置兜底
                if (!CharacterMainControlUseItemPatch.TryReturnToOrigin(item))
                {
                    // 回位失败：接下来交给原版（塞进主背包或掉在地上），
                    // 使用流程到此结束，不能再让它享受「使用中」的宽限期，
                    // 否则丢在地上的物品会继续显示在快捷栏里
                    PetUseTracker.Forget(item);
                    return true;
                }

                // 与原版 PickupItem 保持一致
                item.Inspected = true;
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                SafeGuard.Report("把物品放回原来的背包位置失败", e);
                return true;
            }
        }
    }
}
