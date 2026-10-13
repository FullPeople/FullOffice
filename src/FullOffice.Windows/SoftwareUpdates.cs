using System.Diagnostics;
using System.Windows;

namespace FlowOfficeLab;

public sealed partial class WorkspaceWindow
{
    Task CheckSoftwareUpdates(bool background)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "FullToolsUpdater.exe");
        if (!File.Exists(helper))
        {
            if (!background) MessageBox.Show(this, "此构建未包含更新组件。请从官网下载完整安装包。", "FullOffice", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        }
        try
        {
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--product"); start.ArgumentList.Add("FullOffice");
            var version = typeof(WorkspaceWindow).Assembly.GetName().Version
                ?? throw new IOException("无法读取当前程序版本。");
            start.ArgumentList.Add("--version"); start.ArgumentList.Add($"{version.Major}.{version.Minor}.{version.Build}");
            if (background) start.ArgumentList.Add("--background");
            using var process = Process.Start(start);
        }
        catch (Exception error)
        {
            if (!background) MessageBox.Show(this, "无法启动更新检查：" + error.Message, "FullOffice", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return Task.CompletedTask;
    }
}
