// 职责：按持久化绑定把虚拟键 + 修饰键映射到查看器命令。
// 不变量：首个精确匹配胜出；MoveToFolder 命中时透传 TargetPath，ApplyTag 命中时透传 TagId；
//         Load 结果按 settings.json 的 LastWriteTimeUtc 做失效检查的内存缓存（决策 D7）——
//         文件未变更时每次击键不再读盘；文件被删除不视为变更（保持缓存，文件重新出现时时间戳必变而失效）。
// 调用链：MainWindow KeyDown → TryMatch → MainViewModel 命令。

using SimpleViewer.Models;
using Windows.System;

namespace SimpleViewer.Services;

/// <inheritdoc cref="IShortcutService" />
public sealed class ShortcutService : IShortcutService
{
    private readonly ISettingsService _settingsService;

    // 缓存状态（_cacheGate 保护）：null = 无缓存，下次调用必读盘一次。
    private readonly object _cacheGate = new();
    private AppSettings? _cachedSettings;
    private DateTime _cachedWriteTimeUtc;

    public ShortcutService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public ShortcutMatchResult? TryMatch(VirtualKey key, bool control, bool shift, bool menu)
    {
        var keyName = key.ToString();
        var settings = LoadSettingsCached();

        foreach (var binding in settings.Shortcuts)
        {
            if (!string.Equals(binding.VirtualKey, keyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ModifiersMatch(binding.Modifiers, control, shift, menu))
            {
                continue;
            }

            return new ShortcutMatchResult
            {
                Command = binding.Command,
                MoveTargetPath = binding.Command == ViewerCommand.MoveToFolder ? binding.TargetPath : null,
                TagId = binding.Command == ViewerCommand.ApplyTag ? binding.TagId : null,
            };
        }

        return null;
    }

    /// <summary>
    /// 读取设置（带内存缓存）：仅当 settings.json 存在且 LastWriteTimeUtc 与缓存时不一致才重新 Load；
    /// 文件不存在（被删除）不视为变更——沿用缓存直到文件重新出现（届时时间戳必不同而失效重读）。
    /// 消除每次击键的读盘热路径（决策 D7）。
    /// 已知边界：同一时钟粒度内的连续写入可能得到相同时间戳而漏失效一次——真实使用中
    /// “保存设置→下一次按键”的间隔（人工操作，百毫秒级以上）远大于文件时间戳粒度，不受影响。
    /// </summary>
    private AppSettings LoadSettingsCached()
    {
        lock (_cacheGate)
        {
            var currentWriteTime = File.Exists(_settingsService.SettingsFilePath)
                ? File.GetLastWriteTimeUtc(_settingsService.SettingsFilePath)
                : DateTime.MinValue;

            // 命中条件：已有缓存，且（文件仍不存在，或时间戳与缓存时一致）。
            if (_cachedSettings is not null
                && (currentWriteTime == DateTime.MinValue
                    || currentWriteTime == _cachedWriteTimeUtc))
            {
                return _cachedSettings;
            }

            var settings = _settingsService.Load();
            _cachedSettings = settings;
            _cachedWriteTimeUtc = File.Exists(_settingsService.SettingsFilePath)
                ? File.GetLastWriteTimeUtc(_settingsService.SettingsFilePath)
                : DateTime.MinValue;
            return settings;
        }
    }

    private static bool ModifiersMatch(IReadOnlyList<string> modifiers, bool control, bool shift, bool menu)
    {
        var wantsControl = modifiers.Any(static m => m.Equals("Control", StringComparison.OrdinalIgnoreCase));
        var wantsShift = modifiers.Any(static m => m.Equals("Shift", StringComparison.OrdinalIgnoreCase));
        var wantsMenu = modifiers.Any(static m => m.Equals("Menu", StringComparison.OrdinalIgnoreCase)
            || m.Equals("Alt", StringComparison.OrdinalIgnoreCase));

        return wantsControl == control && wantsShift == shift && wantsMenu == menu;
    }
}
