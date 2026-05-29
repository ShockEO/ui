// ============================================================
//  PanTiltControllerView.axaml.cs
// ============================================================
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using System.Diagnostics;
using Avalonia.Markup.Xaml;
using ShockUI.ViewModels.Modules;

namespace ShockUI.Views.Modules;

public partial class PanTiltControllerView : UserControl
{
    public PanTiltControllerView()
    {
        InitializeComponent();

        // Wire each XboxControllerVisual in this view to forward its
        // virtual click/drag events to the ViewModel's controller handlers.
        // The 2-col and 1-col layouts each have a visual, so we walk the
        // logical tree once the view is attached.
        DataContextChanged += (_, _) =>
        {
            Debug.WriteLine($"[{GetType().Name}] DataContextChanged fired; DataContext={DataContext?.GetType().Name ?? "null"}");
            WireUpControllerVisuals();
        };

        // Loaded fires after the visual tree is fully built — best moment
        // to walk it. AttachedToLogicalTree and DataContextChanged are kept
        // as safety nets in case Loaded doesn't fire (unlikely).
        this.Loaded += (_, _) =>
        {
            Debug.WriteLine($"[{GetType().Name}] Loaded fired; DataContext={DataContext?.GetType().Name ?? "null"}");
            WireUpControllerVisuals();
        };

        AttachedToLogicalTree += (_, _) =>
        {
            Debug.WriteLine($"[PanTiltControllerView] AttachedToLogicalTree fired; DataContext={DataContext?.GetType().Name ?? "null"}");
            WireUpControllerVisuals();
        };
    }

    private void WireUpControllerVisuals()
    {
        if (DataContext is not PanTiltControllerViewModel vm)
        {
            Debug.WriteLine("[PanTiltControllerView] WireUp: DataContext is not VM");
            return;
        }
        int count = 0;
        foreach (var visual in FindAllVisuals(this))
        {
            // Idempotent — safe if AttachedToLogicalTree fires more than once.
            visual.VirtualStateChanged -= vm.HandleControllerState;
            visual.VirtualButtonPressed -= vm.HandleControllerButton;
            visual.VirtualStateChanged += vm.HandleControllerState;
            visual.VirtualButtonPressed += vm.HandleControllerButton;
            count++;
        }
        Debug.WriteLine($"[PanTiltControllerView] Wired {count} XboxControllerVisual(s)");
    }

    /// <summary>Walk the logical tree looking for XboxControllerVisual instances.</summary>
    /// <summary>
    /// Walk the VISUAL tree (not logical) — ContentPresenter slots in
    /// ModuleWorkspaceShell hide our visuals from the logical tree, so the
    /// logical-tree walk returns nothing. The visual tree includes them.
    /// </summary>
    private static IEnumerable<XboxControllerVisual> FindAllVisuals(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is XboxControllerVisual v) yield return v;
            if (child is Visual vChild)
                foreach (var nested in FindAllVisuals(vChild))
                    yield return nested;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}