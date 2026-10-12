using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;

namespace FlowOfficeLab;

static class OfficeLicenses
{
    public static Image OriginalLogo(double height)
    {
        // Reuse the upstream SVG paths unchanged; the original artwork is CC BY-SA 4.0.
        using var stream = typeof(OfficeLicenses).Assembly.GetManifestResourceStream("FullOffice.ONLYOFFICELogo")
            ?? throw new IOException("缺少文档编辑器标识资源。");
        var svg = XDocument.Load(stream);
        var drawing = new DrawingGroup();
        foreach (var path in svg.Root!.Elements().Where(p => p.Name.LocalName == "path"))
            drawing.Children.Add(new GeometryDrawing((Brush)new BrushConverter().ConvertFromString(path.Attribute("fill")!.Value)!, null,
                Geometry.Parse(path.Attribute("d")!.Value)));
        drawing.Freeze();
        return new Image { Source = new DrawingImage(drawing), Height = height, Stretch = Stretch.Uniform, Margin = new(5) };
    }

    public static void Show(Window owner)
    {
        var dialog = new Window { Owner = owner, Title = "文档与表格编辑器", Width = 620, Height = 590,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = owner.FontFamily, FontSize = 13 };
        var root = new DockPanel { Margin = new(22) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(OriginalLogo(45));
        top.Children.Add(new TextBlock { Text = "ONLYOFFICE Desktop Editors 9.4.0.129\n版权所有 © Ascensio System SIA。FullOffice 使用独立的文档与表格编辑组件。\n编辑组件使用 GNU AGPL 第 3 版；原始图形资源使用 CC BY-SA 4.0。\n保留上游产品标识与附加声明，不表示上游为 FullOffice 提供担保。",
            TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 16) });
        var links = new WrapPanel(); DockPanel.SetDock(links, Dock.Bottom); root.Children.Add(links);
        foreach (var (label, url) in new[] {
            ("编辑器源码", "https://github.com/ONLYOFFICE/DesktopEditors/tree/v9.4.0"),
            ("原生接口源码", "https://github.com/FullPeople/FullOffice/tree/main/src/FullOffice.OfficeEngine.Native"),
            ("AGPL 许可证", "https://www.gnu.org/licenses/agpl-3.0.html"),
            ("图形资源许可证", "https://creativecommons.org/licenses/by-sa/4.0/") }) {
            var button = new Button { Content = label, Margin = new(0, 12, 9, 0), Padding = new(8, 5, 8, 5) };
            button.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); links.Children.Add(button);
        }
        string path = Path.Combine(AppContext.BaseDirectory, "licenses", "ONLYOFFICE-AGPL-3.0.txt");
        var text = new TextBox { Text = File.Exists(path) ? File.ReadAllText(path) : "完整许可证随源码及完整编辑版本附带。", IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(12) };
        root.Children.Add(text); dialog.Content = root; dialog.ShowDialog();
    }
}
