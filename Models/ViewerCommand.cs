namespace SimpleViewer.Models;

/// <summary>
/// 绑定到快捷键或工具栏动作的应用命令。
/// </summary>
public enum ViewerCommand
{
    NextImage,
    PrevImage,
    RotateLeft,
    RotateRight,
    ToggleFullscreen,
    DeleteImage,
    ExitApp,
    MoveToFolder,

    /// <summary>
    /// 打标签（Step 12，决策 D7）：快捷键打标走既有“命令+参数”体系，
    /// 绑定必须携带 <see cref="ShortcutBinding.TagId"/>（引用 <see cref="TagDefinition.Id"/> 稳定 Id）。
    /// </summary>
    ApplyTag,
}
