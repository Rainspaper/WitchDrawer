using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using WitchDrawer.App.Features.DesktopItems;
using WitchDrawer.App.ViewModels;
using WitchDrawer.Native.Files;

namespace WitchDrawer.App.Tests;

public sealed class DesktopBoxWindowTemplateTests
{
    [Theory]
    [InlineData(MouseButton.Left, true)]
    [InlineData(MouseButton.Right, false)]
    [InlineData(MouseButton.Middle, false)]
    public void ItemDoubleClick_OnlyOpensWithTheLeftButton(
        MouseButton changedButton,
        bool expected)
    {
        Assert.Equal(expected, DesktopItemInputRules.ShouldOpenOnDoubleClick(changedButton));
    }

    [Theory]
    [InlineData("program.exe", true)]
    [InlineData("shortcut.lnk", true)]
    [InlineData("script.cmd", true)]
    [InlineData("document.txt", false)]
    public void AdministratorAction_IsLimitedToExecutableFileTypes(string path, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsFileShellActions.CanRunAsAdministrator(path, isDirectory: false));
    }

    [Fact]
    public void ItemContextMenu_IsWiredWithoutChangingItemContainerTemplates()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var handlers = document
            .Descendants()
            .Attributes("PreviewMouseRightButtonUp")
            .Select(attribute => attribute.Value)
            .ToArray();

        Assert.Equal(4, handlers.Length);
        Assert.Equal(2, handlers.Count(value => value == "OnIconPreviewMouseRightButtonUp"));
        Assert.Contains("OnDrawerSecondaryIconMouseRightButtonUp", handlers);
        Assert.Contains("OnDrawerCoverIconMouseRightButtonUp", handlers);

        var iconList = Assert.Single(
            document.Descendants(PresentationNamespace + "ListBox"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "IconList");
        Assert.Equal(
            "{StaticResource DesktopIconListItemStyle}",
            (string?)iconList.Attribute("ItemContainerStyle"));
        Assert.Empty(iconList.Elements(PresentationNamespace + "ListBox.ItemContainerStyle"));
    }

    [Fact]
    public void SelectionStyles_AreLoadedFromAnIsolatedResourceDictionary()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var sources =
            document.Descendants(PresentationNamespace + "ResourceDictionary")
                .Attributes("Source")
                .Select(attribute => attribute.Value)
                .ToArray();

        Assert.Contains(
            "/WitchDrawer.App;component/Views/Styles/DesktopBoxSelectionStyles.xaml",
            sources);
        Assert.Contains(
            "/WitchDrawer.App;component/Views/Styles/DesktopBoxControlStyles.xaml",
            sources);
    }

    private static Thickness ParseThickness(string? value) =>
        (Thickness)new ThicknessConverter().ConvertFromInvariantString(value ?? "0")!;

    private static readonly XNamespace PresentationNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void IconGridTemplate_CentersContentVertically()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var iconList = Assert.Single(
            document.Descendants(PresentationNamespace + "ListBox"),
            element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "IconList");
        var itemTemplate = Assert.Single(
            iconList.Elements(PresentationNamespace + "ListBox.ItemTemplate"));
        var dataTemplate = Assert.Single(
            itemTemplate.Elements(PresentationNamespace + "DataTemplate"));
        var templateRoot = Assert.Single(
            dataTemplate.Elements(),
            element => !element.Name.LocalName.Contains('.', StringComparison.Ordinal));

        Assert.Equal("StackPanel", templateRoot.Name.LocalName);
        Assert.Equal("Center", (string?)templateRoot.Attribute("VerticalAlignment"));
    }

    [Fact]
    public void IconGrid_GrowsWithAdaptiveContentWithoutScrollBars()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var iconList = Assert.Single(
            document.Descendants(PresentationNamespace + "ListBox"),
            element =>
                (string?)element.Attribute(XamlNamespace + "Name") == "IconList");

        Assert.Null(iconList.Attribute("MaxWidth"));
        Assert.Null(iconList.Attribute("MaxHeight"));
        Assert.Equal(
            "Disabled",
            (string?)iconList.Attribute("ScrollViewer.HorizontalScrollBarVisibility"));
        Assert.Equal(
            "Disabled",
            (string?)iconList.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
    }

    [Fact]
    public void SelectedIcon_UsesAnOuterOutlineWithoutRecoloringTheIconBorder()
    {
        var document = XDocument.Load(GetDesktopBoxSelectionStylesXamlPath());
        var itemStyle = Assert.Single(
            document.Descendants(PresentationNamespace + "Style"),
            element => (string?)element.Attribute(XamlNamespace + "Key") == "DesktopIconListItemStyle");
        var outline = Assert.Single(
            itemStyle.Descendants(PresentationNamespace + "Border"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "SelectionOutline");
        var selectedTrigger = Assert.Single(
            itemStyle.Descendants(PresentationNamespace + "Trigger"),
            element =>
                (string?)element.Attribute("Property") == "IsSelected"
                && (string?)element.Attribute("Value") == "True"
                && element.Elements(PresentationNamespace + "Setter").Any(
                    setter => (string?)setter.Attribute("TargetName") == "SelectionOutline"));
        var transform = Assert.Single(
            outline.Descendants(PresentationNamespace + "TranslateTransform"));

        Assert.Equal("1.2", (string?)outline.Attribute("BorderThickness"));
        Assert.Equal("False", (string?)outline.Attribute("SnapsToDevicePixels"));
        Assert.Equal("-0.30", (string?)transform.Attribute("X"));
        Assert.Equal("-0.30", (string?)transform.Attribute("Y"));
        Assert.Contains(
            selectedTrigger.Elements(PresentationNamespace + "Setter"),
            setter =>
                (string?)setter.Attribute("TargetName") == "SelectionOutline"
                && (string?)setter.Attribute("Property") == "BorderBrush");
    }

    [Fact]
    public void DrawerSelection_UsesTheSameOuterOutlineAsTheIconGrid()
    {
        var document = XDocument.Load(GetDesktopBoxSelectionStylesXamlPath());
        var drawerStyle = Assert.Single(
            document.Descendants(PresentationNamespace + "Style"),
            element => (string?)element.Attribute(XamlNamespace + "Key") == "DrawerTileButtonStyle");
        var outline = Assert.Single(
            drawerStyle.Descendants(PresentationNamespace + "Border"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "DrawerSelectionOutline");
        var selectedTrigger = Assert.Single(
            drawerStyle.Descendants(PresentationNamespace + "DataTrigger"),
            element =>
                (string?)element.Attribute("Binding") == "{Binding IsSelected}"
                && (string?)element.Attribute("Value") == "True"
                && element.Elements(PresentationNamespace + "Setter").Any(
                    setter => (string?)setter.Attribute("TargetName") == "DrawerSelectionOutline"));
        var transform = Assert.Single(
            outline.Descendants(PresentationNamespace + "TranslateTransform"));

        Assert.Equal("1.2", (string?)outline.Attribute("BorderThickness"));
        Assert.Equal("False", (string?)outline.Attribute("SnapsToDevicePixels"));
        Assert.Equal("-0.30", (string?)transform.Attribute("X"));
        Assert.Equal("-0.30", (string?)transform.Attribute("Y"));
        Assert.Contains(
            selectedTrigger.Elements(PresentationNamespace + "Setter"),
            setter =>
                (string?)setter.Attribute("TargetName") == "DrawerSelectionOutline"
                && (string?)setter.Attribute("Property") == "BorderBrush");

        var windowDocument = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var coverItems = Assert.Single(
            windowDocument.Descendants(PresentationNamespace + "ItemsControl"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "DrawerCoverItems");
        Assert.DoesNotContain(
            coverItems.Descendants(PresentationNamespace + "Border")
                .Descendants(PresentationNamespace + "DataTrigger"),
            trigger => (string?)trigger.Attribute("Binding") == "{Binding IsSelected}");
    }

    [Fact]
    public void DrawerTemplates_ShowFileNamesAndUseTheTextAwareCellHeight()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var coverFileName = Assert.Single(
            document.Descendants(PresentationNamespace + "TextBlock"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "DrawerCoverFileName");
        var secondaryFileName = Assert.Single(
            document.Descendants(PresentationNamespace + "TextBlock"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "DrawerSecondaryFileName");
        var expandFileName = Assert.Single(
            document.Descendants(PresentationNamespace + "TextBlock"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "DrawerExpandFileName");
        var secondaryPanel = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "CenteredUniformPanel");

        Assert.Equal("{Binding Item.DisplayName}", (string?)coverFileName.Attribute("Text"));
        Assert.Equal("{Binding DisplayName}", (string?)secondaryFileName.Attribute("Text"));
        Assert.Equal(
            "{Binding DataContext.DrawerExpandTileLabel, RelativeSource={RelativeSource AncestorType=Window}}",
            (string?)expandFileName.Attribute("Text"));
        Assert.Equal(
            "{Binding DataContext.IsFileNameVisible, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)coverFileName.Attribute("Visibility"));
        Assert.Equal(
            "{Binding DataContext.IsFileNameVisible, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)secondaryFileName.Attribute("Visibility"));
        Assert.Equal(
            "{Binding DataContext.IsFileNameVisible, RelativeSource={RelativeSource AncestorType=Window}, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)expandFileName.Attribute("Visibility"));
        Assert.Equal(
            "{Binding DataContext.LayoutSettings.ItemSlotHeight, RelativeSource={RelativeSource AncestorType={x:Type ListBox}}}",
            (string?)secondaryPanel.Attribute("CellHeight"));
    }

    [Fact]
    public void DrawerSecondaryPopup_ChromeReserveMatchesXamlBorderAndListMargin()
    {
        // DesktopBoxViewModel.DrawerSecondaryPanelChrome 必须与弹窗 XAML 中的
        // 根 Border 描边 + ListBox Margin + ListBox 默认(Aero2)模板内 Border 的
        // Padding (1px × 2，硬编码在主题模板中) 一一对应，否则内容区会比可视口
        // 大出几像素，最下列图标会被弹窗下边缘裁掉。
        const double listBoxTemplateBorderPadding = 2;
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var popupRoot = Assert.Single(
            document.Descendants(PresentationNamespace + "Border"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "DrawerSecondaryPopupRoot");
        var listBox = Assert.Single(popupRoot.Descendants(PresentationNamespace + "ListBox"));

        var borderThickness = ParseThickness((string?)popupRoot.Attribute("BorderThickness"));
        var listMargin = ParseThickness((string?)listBox.Attribute("Margin"));

        Assert.Equal(
            DesktopBoxViewModel.DrawerSecondaryPanelChrome,
            borderThickness.Left + borderThickness.Right + listMargin.Left + listMargin.Right
                + listBoxTemplateBorderPadding);
        Assert.Equal(
            DesktopBoxViewModel.DrawerSecondaryPanelChrome,
            borderThickness.Top + borderThickness.Bottom + listMargin.Top + listMargin.Bottom
                + listBoxTemplateBorderPadding);
    }

    [Fact]
    public void RollUpButton_KeepsHeaderAndCollapsesTheContentRow()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var rootGrid = Assert.Single(
            document.Descendants(PresentationNamespace + "Grid"),
            element => (string?)element.Attribute("MouseLeftButtonDown") == "OnSurfaceMouseLeftButtonDown");
        var rows = Assert.Single(rootGrid.Elements(PresentationNamespace + "Grid.RowDefinitions"));
        var definitions = rows.Elements(PresentationNamespace + "RowDefinition").ToArray();
        var button = Assert.Single(
            rootGrid.Descendants(PresentationNamespace + "Button"),
            element => (string?)element.Attribute("Click") == "OnToggleRollUpClick");

        Assert.Equal("{Binding HeaderRowHeight}", (string?)definitions[0].Attribute("Height"));
        Assert.Equal("{Binding TabBarRowHeight}", (string?)definitions[1].Attribute("Height"));
        Assert.Equal("{Binding ContentRowHeight}", (string?)definitions[2].Attribute("Height"));
        Assert.Equal("2", (string?)button.Attribute("Grid.Column"));
    }

    [Fact]
    public void MergedTabHost_RendersTabsInADedicatedRowWithoutWideningTheBox()
    {
        // 合并为多标签总盒子后，标签文字曾经叠在一起：
        // (1) Tab 栏与标题栏同处第 0 行 → 标签压住标题与关闭按钮；
        // (2) 标签按钮直接继承 MappingModeButtonStyle 的 18×18 尺寸 → 文字溢出按钮互相覆盖。
        // 这里把两条不变量固化下来。
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var contentGrid = Assert.Single(
            document.Descendants(PresentationNamespace + "Grid"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "BoxContentGrid");

        var tabStrip = Assert.Single(
            contentGrid.Descendants(PresentationNamespace + "ItemsControl"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "TabStrip");

        // 标签栏独占一行且必须排在标题栏之下：上面一行显示当前选中的盒子名，下面列出全部标签。
        var tabRow = Assert.Single(
            tabStrip.Ancestors(PresentationNamespace + "Canvas"));
        Assert.Equal("1", (string?)tabRow.Attribute("Grid.Row"));
        Assert.Equal(
            "{Binding IsTabHost, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)tabRow.Attribute("Visibility"));

        var headerGrid = Assert.Single(
            contentGrid.Elements(PresentationNamespace + "Grid"),
            element => (string?)element.Attribute("Grid.Row") == "0");
        Assert.Equal(
            "{Binding IsHeaderVisible, Converter={StaticResource BooleanToVisibilityConverter}}",
            (string?)headerGrid.Attribute("Visibility"));
        Assert.True(
            int.Parse((string)tabRow.Attribute("Grid.Row")!) > int.Parse((string)headerGrid.Attribute("Grid.Row")!),
            "标签栏必须位于标题栏下方。");

        // 标签条放在 Canvas 中：宽度不参与窗口测量，宿主盒子不会被标签撑宽。
        var tabRowWidthBindings = tabRow.Elements()
            .Select(element => (string?)element.Attribute("Width"))
            .Where(value => value is not null)
            .ToArray();
        Assert.NotEmpty(tabRowWidthBindings);
        Assert.All(
            tabRowWidthBindings,
            value =>
            {
                Assert.Contains("ActualWidth", value);
                Assert.Contains("BoxContentGrid", value);
            });

        // 标签栏不画底衬/分隔线，靠行高留白与上下拉开距离。
        Assert.Empty(tabRow.Elements(PresentationNamespace + "Rectangle"));

        // 标签按钮自带 TabChipButtonStyle（不再是 18×18 的图标按钮样式），
        // 固定宽度由样式给出，文字用省略号截断，不会溢出到相邻标签上。
        var tabButton = Assert.Single(
            contentGrid.Descendants(PresentationNamespace + "Button"),
            element => (string?)element.Attribute("Click") == "OnTabButtonClick");
        Assert.Equal("{StaticResource TabChipButtonStyle}", (string?)tabButton.Attribute("Style"));
        var tabText = Assert.Single(tabButton.Descendants(PresentationNamespace + "TextBlock"));
        Assert.Equal("CharacterEllipsis", (string?)tabText.Attribute("TextTrimming"));

        // 标签文字必须绑「盒子自身名字 TabLabel」而不是 Name：
        // 宿主盒的 Name 会跟随当前选中标签，用它会让第一格标签的文字跟着选中项一起变。
        Assert.Equal("{Binding TabLabel}", (string?)tabText.Attribute("Text"));
        Assert.Equal("{Binding TabLabel}", (string?)tabButton.Attribute("ToolTip"));

        // 绑定名和 VM 属性名两侧一起锁住：改名会让 WPF 绑定静默失效，界面上表现为文字空白。
        Assert.NotNull(typeof(DesktopBoxViewModel).GetProperty("TabLabel"));

        // 标签宽度由多值绑定算出：标签栏实际宽度 ÷ 标签数（上下限见 TabChipMetrics）。
        var widthElement = Assert.Single(tabButton.Elements(PresentationNamespace + "Button.Width"));
        var multiBinding = Assert.Single(widthElement.Elements(PresentationNamespace + "MultiBinding"));
        Assert.Equal("{StaticResource TabChipWidthConverter}", (string?)multiBinding.Attribute("Converter"));
        var widthBindings = multiBinding.Elements(PresentationNamespace + "Binding").ToArray();
        Assert.Equal(2, widthBindings.Length);
        Assert.Contains(widthBindings, binding => (string?)binding.Attribute("Path") == "ActualWidth");
        Assert.Contains(
            widthBindings,
            binding => (string?)binding.Attribute("Path") == "DataContext.TabVms.Count");

        // 转换器必须真的注册在窗口资源里，否则多值绑定会静默取不到值。
        var converterElement = Assert.Single(
            document.Descendants(XNamespace.Get("clr-namespace:WitchDrawer.App.Infrastructure") + "TabChipWidthConverter"));
        Assert.Equal("TabChipWidthConverter", (string?)converterElement.Attribute(XamlNamespace + "Key"));
    }

    [Fact]
    public void TabChipStyle_LeavesWidthToTheScalingSchemeAndFollowsTheActiveTheme()
    {
        var style = Assert.Single(
            XDocument.Load(GetDesktopBoxControlStylesXamlPath())
                .Descendants(PresentationNamespace + "Style"),
            element => (string?)element.Attribute(XamlNamespace + "Key") == "TabChipButtonStyle");

        // 宽度不能写死在样式里：改由 ItemTemplate 的多值绑定按「标签栏宽度 ÷ 标签数」计算。
        Assert.DoesNotContain(
            style.Elements(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Width");
        Assert.Contains(
            style.Elements(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Height"
                && !string.IsNullOrWhiteSpace((string?)setter.Attribute("Value")));
        Assert.Contains(
            style.Elements(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Padding");

        // 主题适配：默认文字色、选中态底色/文字色/描边都必须是盒子自身那套动态资源，
        // 这样标签栏底色会随「主题 + 盒子不透明度」一起变。
        Assert.Contains(
            style.Elements(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Foreground"
                && (string?)setter.Attribute("Value") == "{DynamicResource TextMutedBrush}");
        Assert.Contains(
            style.Elements(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "BorderThickness"
                && (string?)setter.Attribute("Value") == "1");

        var activeTrigger = Assert.Single(
            style.Descendants(PresentationNamespace + "DataTrigger"),
            element => (string?)element.Attribute("Binding") == "{Binding IsActiveTab}");
        var activeSetters = activeTrigger.Elements(PresentationNamespace + "Setter").ToArray();
        Assert.Contains(
            activeSetters,
            setter => (string?)setter.Attribute("TargetName") == "Chip"
                && (string?)setter.Attribute("Property") == "Background"
                && (string?)setter.Attribute("Value") == "{DynamicResource GlassInnerBrush}");
        Assert.Contains(
            activeSetters,
            setter => (string?)setter.Attribute("TargetName") == "Chip"
                && (string?)setter.Attribute("Property") == "BorderBrush"
                && (string?)setter.Attribute("Value") == "{DynamicResource GlassStrokeBrush}");
        Assert.Contains(
            activeSetters,
            setter => (string?)setter.Attribute("Property") == "Foreground"
                && (string?)setter.Attribute("Value") == "{DynamicResource AccentBrush}");

        // 选中项只做轻微高亮：不换字重（换字重会让标签宽度抖动、位置跳动）。
        Assert.DoesNotContain(
            activeSetters,
            setter => (string?)setter.Attribute("Property") == "FontWeight");

        var hoverTrigger = Assert.Single(
            style.Descendants(PresentationNamespace + "Trigger"),
            element => (string?)element.Attribute("Property") == "IsMouseOver");
        Assert.Contains(
            hoverTrigger.Elements(PresentationNamespace + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Foreground"
                && (string?)setter.Attribute("Value") == "{DynamicResource TextPrimaryBrush}");

        // 模板内多个触发器按声明顺序生效：活动态必须排在悬停之后才会生效。
        var triggerNames = style.Descendants()
            .Where(element => element.Name.LocalName is "Trigger" or "DataTrigger")
            .Select(element => (string?)element.Attribute("Binding") ?? (string?)element.Attribute("Property"))
            .ToArray();
        Assert.True(
            Array.IndexOf(triggerNames, "{Binding IsActiveTab}")
                > Array.IndexOf(triggerNames, "IsMouseOver"),
            "活动标签的触发器必须声明在悬停触发器之后。");
    }

    [Fact]
    public void MappingList_UsesPersistedWidthAndExposesRightEdgeResizeThumb()
    {
        var document = XDocument.Load(GetDesktopBoxWindowXamlPath());
        var fileList = Assert.Single(
            document.Descendants(PresentationNamespace + "ListBox"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "FileList");
        var thumb = Assert.Single(
            document.Descendants(PresentationNamespace + "Thumb"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "MappingListResizeThumb");
        var insertionIndicator = Assert.Single(
            document.Descendants(PresentationNamespace + "Border"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "MappingListInsertionIndicator");

        Assert.Equal("{Binding MappingListWidth}", (string?)fileList.Attribute("Width"));
        Assert.Equal("Right", (string?)thumb.Attribute("HorizontalAlignment"));
        Assert.Equal("SizeWE", (string?)thumb.Attribute("Cursor"));
        Assert.Equal("OnMappingListResizeStarted", (string?)thumb.Attribute("DragStarted"));
        Assert.Equal("OnMappingListResizeDelta", (string?)thumb.Attribute("DragDelta"));
        Assert.Equal("OnMappingListResizeCompleted", (string?)thumb.Attribute("DragCompleted"));
        Assert.Equal("2", (string?)insertionIndicator.Attribute("Height"));
        Assert.Equal("{DynamicResource AccentBrush}", (string?)insertionIndicator.Attribute("Background"));
        Assert.DoesNotContain(
            thumb.Descendants(PresentationNamespace + "Border"),
            element => (string?)element.Attribute(XamlNamespace + "Name") == "ResizeLine");
    }

    private static string GetDesktopBoxWindowXamlPath() =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "WitchDrawer.App",
                "Views",
                "DesktopBoxWindow.xaml"));

    private static string GetDesktopBoxSelectionStylesXamlPath() =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "WitchDrawer.App",
                "Views",
                "Styles",
                "DesktopBoxSelectionStyles.xaml"));

    private static string GetDesktopBoxControlStylesXamlPath() =>
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "src",
                "WitchDrawer.App",
                "Views",
                "Styles",
                "DesktopBoxControlStyles.xaml"));
}
