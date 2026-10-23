using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WinGetToolbox;

internal static class PackageSelectionActions
{
    internal static bool InsideButton(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ButtonBase) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
    // Also used by the headless routed-event test. Never toggles/removes a selection.
    internal static void BindSelect(Button button, Func<bool> eligible, Action add, Action refresh)
    {
        button.Click += (_, e) =>
        {
            e.Handled = true;
            if (!eligible()) return;
            add(); refresh();
        };
    }
    internal static void OpenCard(DependencyObject? source, Action open)
    {
        if (!InsideButton(source)) open();
    }
}
