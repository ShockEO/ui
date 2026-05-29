using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ShockUI.Views.Modules;

public sealed class AutoScrollBehavior : AvaloniaObject
{
    public static readonly AttachedProperty<int> VersionProperty =
        AvaloniaProperty.RegisterAttached<AutoScrollBehavior, ScrollViewer, int>("Version");

    static AutoScrollBehavior()
    {
        VersionProperty.Changed.AddClassHandler<ScrollViewer>(OnVersionChanged);
    }

    private static void OnVersionChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            viewer.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    public static void SetVersion(AvaloniaObject element, int value)
    {
        element.SetValue(VersionProperty, value);
    }

    public static int GetVersion(AvaloniaObject element)
    {
        return element.GetValue(VersionProperty);
    }
}