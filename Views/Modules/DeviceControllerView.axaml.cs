// ============================================================
//  DeviceControllerView.axaml.cs
// ============================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShockUI.ViewModels.Modules;

namespace ShockUI.Views.Modules;

public partial class DeviceControllerView : UserControl
{
    public DeviceControllerView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            Debug.WriteLine($"[{GetType().Name}] DataContextChanged fired; DataContext={DataContext?.GetType().Name ?? "null"}");
            WireUpControllerVisuals();
            WireUpLogAutoScroll();
            WireUpFocusAutoSend();
        };

        this.Loaded += (_, _) =>
        {
            Debug.WriteLine($"[{GetType().Name}] Loaded fired; DataContext={DataContext?.GetType().Name ?? "null"}");
            WireUpControllerVisuals();
            WireUpLogAutoScroll();
            WireUpFocusAutoSend();
        };

        AttachedToLogicalTree += (_, _) =>
        {
            Debug.WriteLine($"[DeviceControllerView] AttachedToLogicalTree fired; DataContext={DataContext?.GetType().Name ?? "null"}");
            WireUpControllerVisuals();
            WireUpLogAutoScroll();
            WireUpFocusAutoSend();
        };
    }

    // ── Controller visuals ──────────────────────────────────────────

    private void WireUpControllerVisuals()
    {
        if (DataContext is not DeviceControllerViewModel vm)
        {
            Debug.WriteLine("[DeviceControllerView] WireUp: DataContext is not VM");
            return;
        }
        int count = 0;
        foreach (var visual in FindAllVisuals(this))
        {
            visual.VirtualStateChanged -= vm.HandleControllerState;
            visual.VirtualButtonPressed -= vm.HandleControllerButton;
            visual.VirtualStateChanged += vm.HandleControllerState;
            visual.VirtualButtonPressed += vm.HandleControllerButton;
            count++;
        }
        Debug.WriteLine($"[DeviceControllerView] Wired {count} XboxControllerVisual(s)");
    }

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

    // ── Workspace log: selectable text + smart auto-scroll ──────────

    /// <summary>
    /// Tracks whether the operator was scrolled to (or near) the bottom
    /// at the last update; if so, new lines auto-scroll. If not, the
    /// current scroll position is preserved so an active selection
    /// isn't yanked out from under them.
    /// </summary>
    private bool _logScrollPinnedToBottom = true;

    private void WireUpLogAutoScroll()
    {
        var box = this.FindControl<TextBox>("LogTextBox");
        if (box is null) return;

        // Detach + re-attach defensively so this is idempotent across
        // multiple AttachedToLogicalTree / Loaded fires.
        box.PropertyChanged -= LogTextBoxOnPropertyChanged;
        box.PropertyChanged += LogTextBoxOnPropertyChanged;
    }

    private void LogTextBoxOnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box) return;

        // Track scroll position changes to know whether the user has
        // scrolled away from the bottom.
        if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ExtentProperty)
        {
            var sv = box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (sv is not null)
            {
                // Within ~10 px of the bottom counts as "at bottom".
                double bottomDist = sv.Extent.Height - sv.Viewport.Height - sv.Offset.Y;
                _logScrollPinnedToBottom = bottomDist <= 10;
            }
            return;
        }

        // When the bound text changes (new log line arrived), scroll to
        // the bottom only if we were pinned there beforehand.
        if (e.Property == TextBox.TextProperty && _logScrollPinnedToBottom)
        {
            // Defer to the next layout pass so the new lines exist in
            // the visual tree before we move the caret/scroll.
            Dispatcher.UIThread.Post(() =>
            {
                var sv = box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                sv?.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    // ── Workspace-panel "Save..." button handlers ─────────────────
    //
    // All three handlers below pass the appropriate text payload into
    // a single shared SaveTextWithPickerAsync() helper so the picker
    // dialog, fallback path, and error handling stay consistent.

    /// <summary>"Save..." in the Workspace Log header.</summary>
    private async void OnSaveLogClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceControllerViewModel vm) return;
        await SaveTextWithPickerAsync(
            text: vm.LogText,
            title: "Export Workspace Log",
            fileStem: "ShockUI-SystemController-Log",
            defaultExt: "log");
    }

    /// <summary>"Save..." in the Raw Packet Trace header.</summary>
    private async void OnSaveTraceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceControllerViewModel vm) return;
        // TraceLines is an ObservableCollection<string>; one frame per line,
        // already formatted with the timestamp and TX/RX direction.
        var text = string.Join(Environment.NewLine, vm.TraceLines);
        await SaveTextWithPickerAsync(
            text: text,
            title: "Export Raw Packet Trace",
            fileStem: "ShockUI-SystemController-Trace",
            defaultExt: "log");
    }

    /// <summary>"Save..." in the Decoded Frame header.</summary>
    private async void OnSaveDecodedClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceControllerViewModel vm) return;

        // Dump the decoded frame as a fixed-width table the user can paste
        // straight into a bug report or email. Empty (no frame yet) is
        // still saved as a single header line so the file isn't suspicious-blank.
        var sb = new System.Text.StringBuilder(1024);
        sb.AppendLine(vm.LastDecodedFrameHeader ?? "Decoded Frame");
        sb.AppendLine(new string('-', 70));
        sb.AppendLine($"{"Pos",-6} {"Label",-14} {"Hex",-8} Meaning");
        sb.AppendLine(new string('-', 70));
        foreach (var row in vm.LastDecodedFrameRows)
        {
            sb.AppendLine(
                $"{row.Position,-6} " +
                $"{row.Label,-14} " +
                $"{row.HexValue,-8} " +
                $"{row.Meaning}");
        }

        await SaveTextWithPickerAsync(
            text: sb.ToString(),
            title: "Export Decoded Frame",
            fileStem: "ShockUI-SystemController-Decoded",
            defaultExt: "txt");
    }

    /// <summary>"Copy" in the Raw Packet Trace header — whole trace to clipboard.</summary>
    private async void OnCopyTraceClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceControllerViewModel vm) return;
        var text = string.Join(Environment.NewLine, vm.TraceLines);
        await CopyToClipboardAsync(text);
    }

    /// <summary>"Copy" in the Decoded Frame header — whole breakdown to clipboard.</summary>
    private async void OnCopyDecodedClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeviceControllerViewModel vm) return;
        var sb = new System.Text.StringBuilder(1024);
        sb.AppendLine(vm.LastDecodedFrameHeader ?? "Decoded Frame");
        foreach (var row in vm.LastDecodedFrameRows)
        {
            sb.AppendLine(
                $"{row.Position,-6} " +
                $"{row.Label,-14} " +
                $"{row.HexValue,-8} " +
                $"{row.Meaning}");
        }
        await CopyToClipboardAsync(sb.ToString());
    }

    /// <summary>Put text on the system clipboard via the window's TopLevel.</summary>
    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    /// <summary>
    /// Shared save-text-to-file helper. Pops the platform save-file
    /// picker, writes the supplied text, and falls back to ~/Documents
    /// if the StorageProvider is unavailable. A short timestamp is
    /// appended to the suggested filename so repeated saves don't
    /// silently overwrite each other.
    /// </summary>
    private async Task SaveTextWithPickerAsync(
        string text, string title, string fileStem, string defaultExt)
    {
        var stamped = $"{fileStem}-{DateTime.Now:yyyyMMdd-HHmmss}.{defaultExt}";

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { } provider)
        {
            Debug.WriteLine($"[DeviceControllerView] StorageProvider unavailable for '{title}'; using Documents fallback.");
            await SaveToDocumentsFallback(text, stamped);
            return;
        }

        try
        {
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = stamped,
                DefaultExtension = defaultExt,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Log file")  { Patterns = new[] { "*.log" } },
                    new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } },
                    FilePickerFileTypes.All
                }
            });
            if (file is null) return;   // user cancelled

            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text);
            Debug.WriteLine($"[DeviceControllerView] Saved '{title}' to {file.Path.LocalPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceControllerView] Save '{title}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Fallback when StorageProvider isn't available (rare on desktop;
    /// possible on some headless setups). Writes to ~/Documents/ShockUI/.
    /// </summary>
    private static async Task SaveToDocumentsFallback(string text, string fileName)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ShockUI");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            await File.WriteAllTextAsync(path, text);
            Debug.WriteLine($"[DeviceControllerView] Saved (fallback) to {path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeviceControllerView] Fallback save failed: {ex.Message}");
        }
    }


    // ── NIR Focus nudge auto-send ─────────────────────────────────
    //
    // The Position and Speed NumericUpDowns both raise ValueChanged
    // on every nudge (▲/▼ buttons OR Up/Down keys when focused).
    // Each nudge re-sends the Focus command so the motor follows
    // the operator's keystrokes live.

    private void WireUpFocusAutoSend()
    {
        // Multiple layouts may host these controls (1-col vs 2-col),
        // so walk the visual tree rather than relying on FindControl
        // returning a single result.
        // Identify the focus nudge controls by their Tag string rather
        // than x:Name, because the 1-col and 2-col layouts both host
        // copies of each control. Avalonia's x:Name registry requires
        // unique names within a scope (would throw at XAML load),
        // whereas Tag is plain metadata with no such constraint.
        foreach (var nud in this.GetVisualDescendants()
                              .OfType<NumericUpDown>()
                              .Where(n => n.Tag is "NirFocusPositionBox" or "NirFocusSpeedBox"))
        {
            nud.ValueChanged -= OnNirFocusNumericChanged;
            nud.ValueChanged += OnNirFocusNumericChanged;
        }
    }

    private void OnNirFocusNumericChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (DataContext is not DeviceControllerViewModel vm) return;
        if (vm.SendNirFocusCommand.CanExecute(null))
            vm.SendNirFocusCommand.Execute(null);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}