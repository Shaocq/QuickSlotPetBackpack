using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace QuickSlotPetBackpack
{
    /// <summary>
    /// 诊断日志：写到 mod 目录下的 QuickSlotPetBackpack.log。
    ///
    /// 游戏自身的日志渠道（latest.log / Player.log）会被其它 mod 的日志处理器
    /// 过滤或接管，实测拿不到本 mod 的 Debug.Log，因此需要自己落一份文件。
    /// 只在关键状态变化（绑定 / 写回 / 重绑 / 解绑 / 使用 / 回位 / UI 刷新异常）
    /// 时写入，避免每帧刷盘。
    /// </summary>
    internal static class Diag
    {
        /// <summary>超过这个大小就留一份 .1 备份并重新开始。</summary>
        private const long MaxBytes = 512 * 1024;

        private static string _path;
        private static bool _resolved;
        private static bool _disabled;
        private static int _consecutiveFailures;

        /// <summary>连续写失败多少次后放弃本次会话的日志。</summary>
        private const int MaxConsecutiveFailures = 5;

        public static void Log(string message)
        {
            if (_disabled)
                return;

            try
            {
                string path = ResolvePath();
                if (string.IsNullOrEmpty(path))
                    return;

                RotateIfNeeded(path);

                string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message + Environment.NewLine;
                File.AppendAllText(path, line, Encoding.UTF8);
                _consecutiveFailures = 0;
            }
            catch
            {
                // 写日志失败不能影响游戏。单次失败（例如文件被短暂占用）不能
                // 永久关闭日志，否则后续真正的问题就看不到了；连续失败才放弃
                if (++_consecutiveFailures >= MaxConsecutiveFailures)
                    _disabled = true;
            }
        }

        public static void LogException(string context, Exception e)
        {
            Log(context + "：" + (e == null ? "未知异常" : e.ToString()));
        }

        private static string ResolvePath()
        {
            if (_resolved)
                return _path;

            _resolved = true;

            try
            {
                string directory = ModBehaviour.ModDirectory;
                if (string.IsNullOrEmpty(directory))
                    directory = Application.persistentDataPath;

                if (string.IsNullOrEmpty(directory))
                    return null;

                _path = Path.Combine(directory, "QuickSlotPetBackpack.log");
                return _path;
            }
            catch
            {
                return null;
            }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length < MaxBytes)
                    return;

                string backup = path + ".1";
                if (File.Exists(backup))
                    File.Delete(backup);

                File.Move(path, backup);
            }
            catch
            {
                // 轮转失败就继续往原文件追加
            }
        }
    }
}
