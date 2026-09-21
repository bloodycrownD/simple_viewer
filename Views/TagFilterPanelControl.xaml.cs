// 职责：筛选面板内容控件 code-behind（tag-filter-tree Step 5）——条件树递归构造（组=卡片全层统一、
//       卡片嵌套卡片、嵌套层底色交替 + 左侧 accent 竖线）、组头连接词切换、条件行匹配词切换与
//       已选值 chips、「＋ 标签」行内值选择展开区（分组勾选列表 + 各标签计数 + 勾选态 ✓）、
//       表达式预览着色拼接（BuildExpression 段）、面板底部操作（清空条件 / 完成 / ✕）。
// 不变量：条件实时生效——所有编辑事件统一走 TagFilterPanelViewModel → MainViewModel.EditFilter
//         集中管线（树编辑 → 互斥清 untagged → ApplyTagFilter），编辑后本控件 RebuildAll 全量重建
//         （demo renderPanel 同构；树滚动偏移保持）；快照渲染——UI 元素不持有树状态，仅闭包引用
//         Core 节点做编辑定位（引用即 Id，会话内树对象，筛选条 chips 同款先例）；
//         组连接词「全部/任一」与匹配词「包含任一/不包含任一」为两态切换按钮组（选中态着色）而非
//         ComboBox——面板宿主是 Flyout（popup 层），ComboBox 下拉属二级浮层，popup 层
//         ThemeResource 解析不认 RootGrid.RequestedTheme 运行时覆盖（ContentDialog 已实证，
//         spec R1 警示类别），本节点不实机无法验证其渲染，按钮组零浮层零主题风险且语义等价
//         （两态点击切换）；「＋ 标签」值选择同理用行内展开（demo popover 同构降级，spec R1 预案）；
//         代码侧颜色一律 TagSidebarConverters 的 IsDarkTheme 双值（不依赖 ThemeResource）；
//         图标一律 BMP 文本字符（⧩✕✓＋－），交互一律 Click（不用 Tapped）。
// 调用链：MainWindow（Flyout 宿主构造注入 Main + TagFilterPanelViewModel）→ 本控件 →
//         TagFilterPanelViewModel → MainViewModel.EditFilter → TagFilterState 编辑函数。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using SimpleViewer.Services;
using SimpleViewer.ViewModels;
// 文本类型两命名空间分工（WinUI 3 投影口径）：FontWeights 静态属性集在 Microsoft.UI.Text；
// FontWeight / FontStyle 结构（Run/TextBlock 属性的实际类型）在 Windows.UI.Text。
using Microsoft.UI.Text;
using Windows.UI.Text;

namespace SimpleViewer.Views;

/// <summary>
/// 筛选面板内容控件（tag-filter-tree Step 5）：Flyout 宿主内的条件树编辑面板，
/// UI 形态基准 demo\filter.html 的 flyout / f-group-box / f-row / popover。
/// </summary>
public sealed partial class TagFilterPanelControl : UserControl
{
    public MainViewModel Main { get; }

    public TagFilterPanelViewModel ViewModel { get; }

    /// <summary>宿主关闭请求（头部 ✕ 与底部「完成」）：MainWindow 订阅后 Hide 宿主 Flyout。</summary>
    public event EventHandler? CloseRequested;

    public TagFilterPanelControl(MainViewModel main, TagFilterPanelViewModel viewModel)
    {
        Main = main;
        ViewModel = viewModel;
        InitializeComponent();
        RebuildAll();
    }

    /// <summary>整体重建：拉最新快照（树 / 值选择候选 / 表达式段）→ 重建条件树与表达式预览。
    /// 面板打开时与每次编辑后调用（全量 Rebuild 不可变快照惯例）。</summary>
    public void RebuildAll()
    {
        ViewModel.Rebuild();
        ApplyThemeColors();
        RebuildTree();
        RebuildExpr();
    }

    /// <summary>
    /// 骨架实底色施加（code-behind 双值）：面板底与预览区底为不透明实底（popup 层无 backdrop，
    /// 不可用半透明 ThemeResource 叠加），按当前 IsDarkTheme 标志重算——每次打开/编辑时随
    /// RebuildAll 重设，主题切换后重开即正确；字色/描边骨架用 ThemeResource（随面板根
    /// RequestedTheme 解析），树内构造颜色在各 Build* 工厂内直接取双值函数。
    /// </summary>
    private void ApplyThemeColors()
    {
        PanelRoot.Background = TagSidebarConverters.FilterPanelBackground();
        ExprPreviewBorder.Background = TagSidebarConverters.FilterPanelSubtleBackground();
    }

    // ==================== 条件树递归构造（demo renderGroup / renderCond 同构） ====================

    /// <summary>重建条件树 UI：TreeHost 整体替换（滚动偏移先记后恢复，对齐 demo scrollTop 保持）。</summary>
    private void RebuildTree()
    {
        var offset = TreeScroll.VerticalOffset;
        TreeHost.Children.Clear();
        if (ViewModel.Root is { } root)
        {
            TreeHost.Children.Add(BuildGroupCard(root));
        }

        if (offset > 0)
        {
            // 内容变矮时 WinUI 会自行钳制；禁动画避免恢复过程可见跳动。
            _ = TreeScroll.ChangeView(null, offset, null, disableAnimation: true);
        }
    }

    /// <summary>
    /// 构建组卡片（demo .f-group-box，全层统一含根组）：左边 3px accent 竖线 + 中性描边 + 圆角，
    /// 底色按深度交替（奇数层 bg-2 感 / 偶数层 panel 感）；内容 = 组头 + 子节点序列 + 组底添加栏。
    /// </summary>
    private Border BuildGroupCard(FilterPanelGroupModel group)
    {
        var content = new StackPanel { Spacing = 6, Margin = new Thickness(10, 8, 10, 10) };

        // 组头：满足以下 [全部|任一] 条件 + 弹性分隔线 + 非根组 ✕（demo .f-group-head）。
        var head = new Grid { ColumnSpacing = 6 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!group.IsRoot)
        {
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        head.Children.Add(MakeHeadLabel("满足以下"));
        var opSwitch = BuildTwoStateSwitch(
            ("全部", group.Op == FilterOp.And, () => ViewModel.SetGroupOp(group.Node, FilterOp.And)),
            ("任一", group.Op == FilterOp.Or, () => ViewModel.SetGroupOp(group.Node, FilterOp.Or)));
        Grid.SetColumn(opSwitch, 1);
        head.Children.Add(opSwitch);
        var tail = MakeHeadLabel("条件");
        Grid.SetColumn(tail, 2);
        head.Children.Add(tail);

        var separator = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill = TagSidebarConverters.TreeLineBrush(),
        };
        Grid.SetColumn(separator, 3);
        head.Children.Add(separator);

        if (!group.IsRoot)
        {
            var delete = MakeIconButton("✕", "删除此条件组", () =>
            {
                ViewModel.RemoveNode(group.Node);
                RebuildAll();
            });
            Grid.SetColumn(delete, 4);
            head.Children.Add(delete);
        }

        content.Children.Add(head);

        // 子节点序列：条件行 / 嵌套组卡片（卡片嵌套卡片，深度受限 MaxDepth）。
        foreach (var child in group.Children)
        {
            switch (child)
            {
                case FilterPanelCondModel condition:
                    content.Children.Add(BuildCondBlock(condition));
                    break;
                case FilterPanelGroupModel subGroup:
                    content.Children.Add(BuildGroupCard(subGroup));
                    break;
            }
        }

        // 组底添加栏：「＋ 条件」恒显示；「＋ 条件组（括号）」仅深度 < MaxDepth（UI 与状态层双保险）。
        // AutomationId 固定（UIA 走查定位——code-behind 构造按钮 Name 为空，见 RULE 实机走查节）。
        var addBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var addCondButton = MakeLinkButton("＋ 条件", () =>
        {
            ViewModel.AddCondition(group.Node);
            RebuildAll();
        });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(addCondButton, "FilterAddCondButton");
        addBar.Children.Add(addCondButton);
        if (group.CanAddGroup)
        {
            var addGroupButton = MakeLinkButton("＋ 条件组（括号）", () =>
            {
                ViewModel.AddGroup(group.Node);
                RebuildAll();
            }, subtle: true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(addGroupButton, "FilterAddGroupButton");
            addBar.Children.Add(addGroupButton);
        }

        content.Children.Add(addBar);

        // 卡片外壳：中性描边圆角 + 左 accent 竖线（Border 单一描边，竖线用内嵌 3px 列实现）。
        var accentBar = new Border
        {
            Width = 3,
            Background = TagSidebarConverters.FilterPanelAccentBrush(),
            CornerRadius = new CornerRadius(4, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(accentBar);
        grid.Children.Add(content);

        return new Border
        {
            Background = TagSidebarConverters.FilterPanelGroupBackground(group.Depth),
            BorderBrush = TagSidebarConverters.FilterPanelGroupBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid,
        };
    }

    /// <summary>条件行块：条件行卡片 + （展开时）行内值选择区。</summary>
    private StackPanel BuildCondBlock(FilterPanelCondModel condition)
    {
        var block = new StackPanel { Spacing = 4 };
        block.Children.Add(BuildCondRow(condition));
        if (ReferenceEquals(ViewModel.ExpandedValuesCond, condition.Node))
        {
            block.Children.Add(BuildValuesExpander(condition));
        }

        return block;
    }

    /// <summary>条件行（demo .f-row）：[包含任一|不包含任一] 切换 + 已选值 chips（✕ 移除）+「＋ 标签」+ 行尾 ✕。</summary>
    private Border BuildCondRow(FilterPanelCondModel condition)
    {
        var row = new Border
        {
            Background = TagSidebarConverters.FilterPanelBackground(),
            BorderBrush = TagSidebarConverters.FilterPanelGroupBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 6, 6),
        };

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 匹配词切换（demo .f-select）：NotIn 选中项红色语义（demo .f-select.op.neg）。
        var matcherSwitch = BuildTwoStateSwitch(
            ("包含任一", condition.Matcher == FilterMatcher.In,
                () => ViewModel.SetMatcher(condition.Node, FilterMatcher.In)),
            ("不包含任一", condition.Matcher == FilterMatcher.NotIn,
                () => ViewModel.SetMatcher(condition.Node, FilterMatcher.NotIn)),
            secondNegated: true);
        matcherSwitch.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(matcherSwitch, 0);
        grid.Children.Add(matcherSwitch);

        // 已选值 chips（流式换行）+ 空态提示 +「＋ 标签」。
        var values = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var value in condition.Values)
        {
            values.Children.Add(BuildValueChip(condition, value));
        }

        if (condition.Values.Count == 0)
        {
            values.Children.Add(new TextBlock
            {
                Text = "未选择标签（条件暂不生效）",
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Foreground = TagSidebarConverters.FilterPanelSecondaryForeground(),
                Margin = new Thickness(0, 2, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        values.Children.Add(BuildAddValueButton(condition));
        Grid.SetColumn(values, 1);
        grid.Children.Add(values);

        var delete = MakeIconButton("✕", "删除此条件", () =>
        {
            ViewModel.RemoveNode(condition.Node);
            RebuildAll();
        });
        Grid.SetColumn(delete, 2);
        grid.Children.Add(delete);

        row.Child = grid;
        return row;
    }

    /// <summary>已选标签 chip（demo .val-chip）：胶囊 + ✕ 移除值（否定行红语义）。</summary>
    private Border BuildValueChip(FilterPanelCondModel condition, string value)
    {
        var chip = new Border
        {
            Background = TagSidebarConverters.FilterPanelValueChipBackground(condition.Negated),
            BorderBrush = TagSidebarConverters.FilterPanelValueChipForeground(condition.Negated),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 1, 2, 1),
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = TagSidebarConverters.FilterPanelValueChipForeground(condition.Negated),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var remove = MakeIconButton("✕", "移除该标签", () =>
        {
            ViewModel.ToggleValue(condition.Node, value);
            RebuildAll();
        });
        remove.FontSize = 11;
        remove.Foreground = TagSidebarConverters.FilterPanelValueChipForeground(condition.Negated);
        panel.Children.Add(remove);
        chip.Child = panel;
        return chip;
    }

    /// <summary>「＋ 标签」按钮：切换该行的值选择行内展开（纯 UI 态；再次点击收起，单开语义）。
    /// AutomationId 固定 FilterAddValueButton（UIA 走查定位——code-behind 构造按钮 Name 为空，见 RULE 实机走查节）。</summary>
    private Button BuildAddValueButton(FilterPanelCondModel condition)
    {
        var expanded = ReferenceEquals(ViewModel.ExpandedValuesCond, condition.Node);
        var button = new Button
        {
            Content = expanded ? "－ 收起" : "＋ 标签",
            FontSize = 12,
            Padding = new Thickness(8, 1, 8, 1),
            CornerRadius = new CornerRadius(999),
            Style = GhostStyle(),
            Foreground = TagSidebarConverters.FilterPanelSecondaryForeground(),
            BorderBrush = TagSidebarConverters.FilterPanelGroupBorderBrush(),
            BorderThickness = new Thickness(1),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, "FilterAddValueButton");
        ToolTipService.SetToolTip(button, "选择标签（展开分组勾选列表）");
        button.Click += (_, _) =>
        {
            ViewModel.ToggleExpandedValues(condition.Node);
            RebuildAll();
        };
        return button;
    }

    // ==================== 值选择行内展开区（demo #popover 同构降级：分组勾选列表 + 计数 + 勾选态 ✓） ====================

    /// <summary>
    /// 值选择行内展开区：按配置组分区（组名 + 互斥标记），每行 = ✓ 方框 + 标签名 + 右对齐计数；
    /// 点击即 ToggleValue（勾选实时生效），勾选行强调淡底 + accent 字（demo .pop-tag.on）。
    /// </summary>
    private Border BuildValuesExpander(FilterPanelCondModel condition)
    {
        var list = new StackPanel { Spacing = 2 };
        if (ViewModel.ChoiceGroups.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "暂无标签组——在左栏「＋ 新建标签组」开始",
                FontSize = 12,
                Foreground = TagSidebarConverters.FilterPanelSecondaryForeground(),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10),
            });
        }

        foreach (var choiceGroup in ViewModel.ChoiceGroups)
        {
            list.Children.Add(new TextBlock
            {
                Text = choiceGroup.Exclusive ? $"{choiceGroup.Name}（互斥组）" : choiceGroup.Name,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = TagSidebarConverters.FilterPanelSecondaryForeground(),
                Margin = new Thickness(8, 6, 8, 2),
            });

            foreach (var tag in choiceGroup.Tags)
            {
                list.Children.Add(BuildChoiceRow(condition, tag));
            }
        }

        var scroll = new ScrollViewer
        {
            Content = list,
            MaxHeight = 264,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Auto,
        };

        return new Border
        {
            Background = TagSidebarConverters.FilterPanelBackground(),
            BorderBrush = TagSidebarConverters.FilterPanelGroupBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 2, 0, 0),
            Child = scroll,
        };
    }

    /// <summary>单个标签勾选行（demo .pop-tag）：✓ 方框 + 标签名 + 右对齐计数；整行 Button 点击即切换。</summary>
    private Button BuildChoiceRow(FilterPanelCondModel condition, FilterPanelTagChoice tag)
    {
        var isChecked = condition.Values.Contains(tag.Name, StringComparer.OrdinalIgnoreCase);

        var row = new Button
        {
            Style = GroupHeaderStyle(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontSize = 13,
            MinHeight = 32,
            Background = TagSidebarConverters.FilterPanelChoiceRowBackground(isChecked),
            Foreground = TagSidebarConverters.FilterPanelChoiceRowForeground(isChecked),
        };
        ToolTipService.SetToolTip(row, "点击切换该标签（实时生效）");

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ✓ 方框（demo .cb）：勾选 = accent 实底白 ✓；未勾 = 中性描边透明底。
        var box = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = isChecked
                ? TagSidebarConverters.FilterPanelAccentBrush()
                : TagSidebarConverters.FilterPanelGroupBorderBrush(),
            Background = isChecked
                ? TagSidebarConverters.FilterPanelAccentBrush()
                : TagSidebarConverters.TransparentBrushValue,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (isChecked)
        {
            box.Child = new TextBlock
            {
                Text = "✓",
                FontSize = 11,
                Foreground = TagSidebarConverters.FilterToggleBadgeForeground(),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        Grid.SetColumn(box, 0);
        grid.Children.Add(box);

        var name = new TextBlock { Text = tag.Name, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var count = new TextBlock
        {
            Text = tag.Count.ToString(),
            FontSize = 11,
            Foreground = TagSidebarConverters.FilterPanelSecondaryForeground(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 3);
        grid.Children.Add(count);

        row.Content = grid;
        row.Click += (_, _) =>
        {
            ViewModel.ToggleValue(condition.Node, tag.Name);
            RebuildAll();
        };
        return row;
    }

    // ==================== 表达式预览（demo .expr-text：BuildExpression 段着色拼接） ====================

    private void RebuildExpr()
    {
        ExprText.Inlines.Clear();
        if (ViewModel.HasNoFilter)
        {
            ExprText.Inlines.Add(MakeRun(
                "（无筛选：显示全部图片）",
                TagSidebarConverters.FilterPanelSecondaryForeground(),
                FontWeights.Normal));
            return;
        }

        foreach (var segment in ViewModel.ExprSegments)
        {
            switch (segment)
            {
                case CondSegment cond:
                    var values = cond.Values.Count == 0 ? "未选" : string.Join("/", cond.Values);
                    ExprText.Inlines.Add(MakeRun(
                        cond.Negated ? $"不含({values})" : $"含({values})",
                        cond.Negated
                            ? TagSidebarConverters.FilterPanelValueChipForeground(negated: true)
                            : TagSidebarConverters.FilterPanelTextForeground(),
                        cond.Negated ? FontWeights.Bold : FontWeights.Normal));
                    break;
                case OpSegment op:
                    ExprText.Inlines.Add(MakeRun(
                        op.Op == FilterOp.And ? " 且 " : " 或 ",
                        TagSidebarConverters.FilterPanelSecondaryForeground(),
                        FontWeights.SemiBold));
                    break;
                case ParenSegment paren:
                    ExprText.Inlines.Add(MakeRun(
                        paren.Open ? "(" : ")",
                        TagSidebarConverters.FilterPanelSecondaryForeground(),
                        FontWeights.SemiBold));
                    break;
            }
        }
    }

    /// <summary>构造着色 Run（表达式预览段）。</summary>
    private static Run MakeRun(string text, Brush foreground, FontWeight weight)
        => new()
        {
            Text = text,
            Foreground = foreground,
            FontWeight = weight,
        };

    // ==================== 小构件工厂（样式取自 App.xaml 全局资源） ====================

    /// <summary>组头前后缀文本（「满足以下」/「条件」，次要灰小字）。</summary>
    private static TextBlock MakeHeadLabel(string text)
        => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = TagSidebarConverters.FilterPanelSecondaryForeground(),
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>
    /// 两态切换按钮组（demo select 的 WinUI 化：两态点击切换即下拉选择等价形态，见类头注释）：
    /// 选中项强调淡底 + accent 字（negated 项选中 = 红语义）；点击已选项无操作。
    /// </summary>
    /// <param name="first">第一项（文本 / 是否选中 / 点击动作）。</param>
    /// <param name="second">第二项。</param>
    /// <param name="secondNegated">第二项选中态是否红色语义（匹配词「不包含任一」）。</param>
    private StackPanel BuildTwoStateSwitch(
        (string Text, bool Selected, Action Click) first,
        (string Text, bool Selected, Action Click) second,
        bool secondNegated = false)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        panel.Children.Add(MakeSwitchOption(first.Text, first.Selected, first.Click, negated: false));
        panel.Children.Add(MakeSwitchOption(second.Text, second.Selected, second.Click, negated: secondNegated));
        return panel;
    }

    /// <summary>两态切换的单个选项按钮：选中态着色（accent / negated 红），未选中次要灰透明底。</summary>
    private Button MakeSwitchOption(string text, bool selected, Action click, bool negated)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(6, 2, 6, 2),
            CornerRadius = new CornerRadius(6),
            Style = GhostStyle(),
            Background = selected ? TagSidebarConverters.FilterPanelValueChipBackground(negated) : TagSidebarConverters.TransparentBrushValue,
            Foreground = selected
                ? TagSidebarConverters.FilterPanelValueChipForeground(negated)
                : TagSidebarConverters.FilterPanelSecondaryForeground(),
        };
        if (!selected)
        {
            button.Click += (_, _) =>
            {
                click();
                RebuildAll();
            };
        }

        return button;
    }

    /// <summary>行内小图标按钮（✕ 删除类；GhostIconButtonStyle）。</summary>
    private static Button MakeIconButton(string glyph, string tooltip, Action click)
    {
        var button = new Button
        {
            Content = glyph,
            Style = GhostStyle(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>文本链接风按钮（组底「＋ 条件 / ＋ 条件组」；subtle = 次要灰，对齐 demo .add-btn.group）。</summary>
    private static Button MakeLinkButton(string text, Action click, bool subtle = false)
    {
        var button = new Button
        {
            Content = text,
            FontSize = subtle ? 11 : 12,
            Padding = new Thickness(6, 3, 6, 3),
            Style = GhostStyle(),
            Foreground = subtle
                ? TagSidebarConverters.FilterPanelSecondaryForeground()
                : TagSidebarConverters.FilterPanelAccentBrush(),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Style GhostStyle()
        => (Style)Microsoft.UI.Xaml.Application.Current.Resources["GhostButtonStyle"];

    private static Style GroupHeaderStyle()
        => (Style)Microsoft.UI.Xaml.Application.Current.Resources["GroupHeaderButtonStyle"];

    // ==================== 面板底部 / 头部操作 ====================

    /// <summary>「清空条件」：清空条件树（VM 内收起展开态），实时生效后重建。</summary>
    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearAll();
        RebuildAll();
    }

    /// <summary>「完成」：请求宿主关闭 Flyout（条件保持生效）。</summary>
    private void OnDoneClicked(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>头部 ✕：请求宿主关闭 Flyout。</summary>
    private void OnCloseClicked(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);
}
