using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace FlowOfficeLab;

// Test-only accessibility actions exercise WPF command and selection routing
// without moving the user's mouse or changing the foreground application.
internal static class UiInputProbe
{
    public static void Click(FrameworkElement window,FrameworkElement target)
    {
        if(target is Button button){var peer=new ButtonAutomationPeer(button);if(peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)invoke.Invoke();else throw new IOException("The button has no accessibility invocation pattern.");}
        else if(target is MenuItem menu){var peer=new MenuItemAutomationPeer(menu);if(peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)invoke.Invoke();else throw new IOException("The menu has no accessibility invocation pattern.");}
        else if(target is ListBoxItem item){var list=ItemsControl.ItemsControlFromItemContainer(item) as ListBox??throw new IOException("The bookmark row has no list owner.");var peer=new ListBoxAutomationPeer(list);var entry=new ListBoxItemAutomationPeer(item.DataContext,peer);if(entry.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider selection)selection.Select();else throw new IOException("The bookmark has no accessibility selection pattern.");}
        else throw new IOException("Unsupported test control.");
    }
}
