using System.Diagnostics;
namespace FlowOfficeLab;
public sealed partial class WorkspaceWindow
{
    static Task OpenDefaultApplicationSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
