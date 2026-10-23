using System;
using System.Collections.Generic;
using Duckov;
using ItemStatsSystem;
using UnityEngine;

namespace QuickSlotPetBackpack
{
    /// <summary>
    /// 记录「正在使用的扩展绑定物品」原来的位置：
    ///   - 宠物背包（含容器）里的物品；
    ///   - 主背包容器内部的物品（原版会把它们挪到主背包顶层）。
    ///
    /// 原版 CA_UseItem 在结束时会调用 CharacterMainControl.PickupItem：
    /// 物品不在主背包顶层时，会被塞进主背包；主背包满了就直接掉在地上。
    /// 从这两类槽位使用物品时，这会让剩余堆叠离开原容器，甚至掉到地上。
    /// 这里在开始使用时记住原位置，结束时把物品放回原处。
    ///
    /// 注意：调用 UseItem 不等于使用动作一定开始（角色可能在跑动、交互或做其他动作），
    /// 而 UseItem 在动作开始前就已经把物品从宠物背包里 Detach 出来了。
    /// 动作启动失败时 CA_UseItem.OnStop 永远不会触发，物品会一直悬空，
    /// 所以这里同时要支持「多个物品」和「启动失败后立即回位」。
    /// </summary>
    internal static class PetUseTracker
    {
        private sealed class Entry
        {
            public Item Item;
            public Inventory Inventory;
            public int Index;
            public string Path;
            public float RecordTime;
        }

        /// <summary>超过这个时间仍未回位的记录视为失效，避免异常中断后永久占用。</summary>
        private const float MaxTrackingSeconds = 90f;

        /// <summary>同时跟踪的数量上限，防止异常情况下无限增长。</summary>
        private const int MaxEntries = 8;

        private static readonly List<Entry> _pending = new List<Entry>();

        private static float Now => Time.realtimeSinceStartup;

        /// <summary>
        /// 开始使用物品前调用。
        /// 同一件物品重复调用（例如使用中又按了一次快捷键）只刷新时间，保留原位置记录；
        /// 不会清空其他物品的记录，否则「使用 A 的过程中按了 B」会让 A 失去保护。
        /// </summary>
        public static void Record(Item item)
        {
            try
            {
                Prune();

                if (item == null)
                    return;

                // 已经在跟踪同一件物品：此时物品很可能已经脱离背包，重新记录会丢失原位置；
                // 如果它又回到了背包里（例如上一轮没用上），顺便刷新原位置
                Entry existing = Find(item);
                if (existing != null)
                {
                    existing.RecordTime = Now;

                    Inventory currentInventory = item.InInventory;
                    if (currentInventory != null)
                    {
                        int currentIndex = currentInventory.GetIndex(item);
                        if (currentIndex >= 0)
                        {
                            existing.Inventory = currentInventory;
                            existing.Index = currentIndex;
                        }

                        if (PetShortcutUtility.TryGetBindingPath(item, out string currentPath))
                            existing.Path = currentPath;
                    }

                    return;
                }

                // 扩展物品，或快捷栏里记录过的绑定物品（即使 IsExtraBindableItem
                // 因某些瞬态没认出来，只要实例还活着就仍然需要保护）
                if (!PetShortcutUtility.IsExtraBindableItem(item)
                    && !PetSlotTracker.IsRememberedItem(item))
                {
                    return;
                }

                Inventory inventory = item.InInventory;
                if (inventory == null)
                    return;

                int index = inventory.GetIndex(item);
                if (index < 0)
                    return;

                // 记录带来源前缀的路径（宠物背包 "2/0"、主背包容器 "main:3/1"），
                // 使用中存档时可以直接沿用
                string path = null;
                if (PetShortcutUtility.TryGetBindingPath(item, out string bindingPath))
                    path = bindingPath;

                if (_pending.Count >= MaxEntries)
                    _pending.RemoveAt(0);

                _pending.Add(new Entry
                {
                    Item = item,
                    Inventory = inventory,
                    Index = index,
                    Path = path,
                    RecordTime = Now
                });
            }
            catch
            {
                // 这里出错不能影响游戏本身，丢弃本条记录即可
            }
        }

        /// <summary>
        /// 该物品是否正在被使用（使用期间它已脱离背包，InInventory 为 null）。
        /// 用于让快捷栏有效性判断在「使用中」不要清空槽位。
        /// </summary>
        public static bool IsTracking(Item item)
        {
            try
            {
                Prune();

                Entry entry = Find(item);
                if (entry == null)
                    return false;

                // 只有真正脱离背包（使用中 / 启动失败后悬空）才需要豁免；
                // 物品已经回到任意背包里时，按原版规则判断即可
                return item.InInventory == null && item.ParentObject == null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 该物品还有未结束的使用记录（不管此刻是否真的脱离了背包）。
        ///
        /// 用于区分两种「物品不在任何背包里」的情况：
        ///   - 使用流程中的瞬态（有记录）→ 允许保留绑定实例；
        ///   - 玩家把物品丢到地上（没有任何使用记录）→ 必须立即失效。
        /// </summary>
        public static bool HasPending(Item item)
        {
            try
            {
                Prune();
                return Find(item) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 结束跟踪：物品已经交回原版处理（原版把它放回主背包或掉在地上），
        /// 不能再让它继续享用「使用中」的宽限期。
        /// </summary>
        public static void Forget(Item item)
        {
            try
            {
                Remove(Find(item));
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        /// <summary>
        /// 物品正在使用（已脱离背包）时，取记录下来的原路径，供存档使用。
        /// 物品正常在背包里时返回 null，调用方优先用实时路径。
        /// </summary>
        public static string GetPathFor(Item item)
        {
            try
            {
                Entry entry = Find(item);
                return entry?.Path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 尝试把物品放回原来的背包/容器格子（宠物背包或主背包容器）。
        /// 返回 true 表示已经处理完（可能什么都没做），调用方应跳过原版 PickupItem。
        /// </summary>
        public static bool TryReturnToOrigin(Item item)
        {
            try
            {
                Entry entry = Find(item);
                if (entry == null)
                    return false;

                if (item == null || item.IsBeingDestroyed)
                {
                    Remove(entry);
                    return false;
                }

                // 物品仍在原背包里（角色在做其他动作、物品不可用等分支，或中途换过格子）：
                // 不需要移动，结束跟踪并交回原版逻辑
                Inventory current = item.InInventory;
                if (current != null && current == entry.Inventory)
                {
                    Remove(entry);
                    return true;
                }

                Inventory inventory = entry.Inventory;
                if (inventory == null)
                {
                    Remove(entry);
                    return false;
                }

                // 使用过程中物品已被 Detach；确保父级为空后再放回背包
                if (item.ParentObject != null)
                    item.Detach();

                // 与原版 PickupItem 一致：放回前先释放物品身上的 Agent，
                // 避免拿着用过的模型/组件进容器
                item.AgentUtilities.ReleaseActiveAgent();

                // 优先放回原格；原格被占或越界时，再尝试合并/放进第一个空位
                if (entry.Index >= 0
                    && entry.Index < inventory.Capacity
                    && inventory.GetItemAt(entry.Index) == null
                    && inventory.AddAt(item, entry.Index))
                {
                    Remove(entry);
                    return true;
                }

                if (inventory.AddAndMerge(item))
                {
                    Remove(entry);
                    return true;
                }

                if (inventory.AddItem(item))
                {
                    Remove(entry);
                    return true;
                }
            }
            catch (Exception e)
            {
                SafeGuard.Report("把物品放回原来的背包位置失败", e);
            }

            // 放回失败：保留记录。物品仍悬空时快捷栏槽位不会被清掉，
            // 下一次得到机会（再次使用结束、玩家拾取）还能继续尝试回位。
            return false;
        }

        /// <summary>切地图 / 禁用 mod 时丢弃残留记录。</summary>
        public static void Reset()
        {
            try
            {
                _pending.Clear();
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        private static Entry Find(Item item)
        {
            try
            {
                if (item == null)
                    return null;

                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    Entry entry = _pending[i];
                    if (entry != null && entry.Item == item)
                        return entry;
                }
            }
            catch
            {
                // 查找失败只当作没有记录，不影响游戏
            }

            return null;
        }

        private static void Remove(Entry entry)
        {
            try
            {
                if (entry != null)
                    _pending.Remove(entry);
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        /// <summary>清理已销毁或超时的记录。</summary>
        private static void Prune()
        {
            try
            {
                float now = Now;

                for (int i = _pending.Count - 1; i >= 0; i--)
                {
                    Entry entry = _pending[i];
                    if (entry == null || entry.Item == null || now - entry.RecordTime > MaxTrackingSeconds)
                        _pending.RemoveAt(i);
                }
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }
    }
}
