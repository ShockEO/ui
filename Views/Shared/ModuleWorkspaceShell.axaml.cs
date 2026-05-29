using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Input;

namespace ShockUI.Views.Shared;

public partial class ModuleWorkspaceShell : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> SubtitleProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, string>(nameof(Subtitle), string.Empty);

    public static readonly StyledProperty<string> ConnectionTextProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, string>(nameof(ConnectionText), string.Empty);

    public static readonly StyledProperty<string> ModeTextProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, string>(nameof(ModeText), string.Empty);

    public static readonly StyledProperty<string> CalibrationTextProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, string>(nameof(CalibrationText), string.Empty);

    public static readonly StyledProperty<bool> IsBannerVisibleProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, bool>(nameof(IsBannerVisible));

    public static readonly StyledProperty<string> BannerTextProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, string>(nameof(BannerText), string.Empty);

    public static readonly StyledProperty<IBrush?> BannerBackgroundProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, IBrush?>(nameof(BannerBackground));

    public static readonly StyledProperty<IBrush?> BannerBorderBrushProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, IBrush?>(nameof(BannerBorderBrush));

    public static readonly StyledProperty<IBrush?> BannerForegroundProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, IBrush?>(nameof(BannerForeground));

    public static readonly StyledProperty<object?> HeaderActionsProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, object?>(nameof(HeaderActions));

    public static readonly StyledProperty<object?> LeftContentProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, object?>(nameof(LeftContent));

    public static readonly StyledProperty<object?> RightTopContentProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, object?>(nameof(RightTopContent));

    public static readonly StyledProperty<object?> RightBottomContentProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, object?>(nameof(RightBottomContent));

    /// <summary>
    /// Optional video feed control. When null the placeholder is shown.
    /// Assign a live video control here when the pipeline is implemented.
    /// </summary>
    public static readonly StyledProperty<object?> VideoContentProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, object?>(nameof(VideoContent), defaultValue: null);

    /// <summary>
    /// Optional fourth content slot in the right column, displayed below the
    /// Raw Packet Trace. When unset, the row collapses to zero height.
    /// Used by modules that want to show a per-frame decoder panel.
    /// </summary>
    public static readonly StyledProperty<object?> RightExtraContentProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, object?>(nameof(RightExtraContent), defaultValue: null);

    /// <summary>
    /// Whether the video feed row is currently displayed. Defaults to false;
    /// toggled by the Show/Hide button (which itself is only visible when
    /// maintenance is unlocked).
    /// </summary>
    public static readonly StyledProperty<bool> IsVideoFeedVisibleProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, bool>(nameof(IsVideoFeedVisible), defaultValue: false);

    public static readonly StyledProperty<string> VideoToggleTextProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, string>(nameof(VideoToggleText), "📺 Show Video");

    public static readonly StyledProperty<ICommand?> ToggleVideoFeedCommandProperty =
        AvaloniaProperty.Register<ModuleWorkspaceShell, ICommand?>(nameof(ToggleVideoFeedCommand));

    public ModuleWorkspaceShell()
    {
        InitializeComponent();

        // The toggle command flips IsVideoFeedVisible and updates the
        // button caption so the user always sees the next action.
        ToggleVideoFeedCommand = new RelayCommand(() =>
        {
            IsVideoFeedVisible = !IsVideoFeedVisible;
            VideoToggleText = IsVideoFeedVisible ? "📺 Hide Video" : "📺 Show Video";
        });
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public string ConnectionText
    {
        get => GetValue(ConnectionTextProperty);
        set => SetValue(ConnectionTextProperty, value);
    }

    public string ModeText
    {
        get => GetValue(ModeTextProperty);
        set => SetValue(ModeTextProperty, value);
    }

    public string CalibrationText
    {
        get => GetValue(CalibrationTextProperty);
        set => SetValue(CalibrationTextProperty, value);
    }

    public bool IsBannerVisible
    {
        get => GetValue(IsBannerVisibleProperty);
        set => SetValue(IsBannerVisibleProperty, value);
    }

    public string BannerText
    {
        get => GetValue(BannerTextProperty);
        set => SetValue(BannerTextProperty, value);
    }

    public IBrush? BannerBackground
    {
        get => GetValue(BannerBackgroundProperty);
        set => SetValue(BannerBackgroundProperty, value);
    }

    public IBrush? BannerBorderBrush
    {
        get => GetValue(BannerBorderBrushProperty);
        set => SetValue(BannerBorderBrushProperty, value);
    }

    public IBrush? BannerForeground
    {
        get => GetValue(BannerForegroundProperty);
        set => SetValue(BannerForegroundProperty, value);
    }

    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }

    public object? LeftContent
    {
        get => GetValue(LeftContentProperty);
        set => SetValue(LeftContentProperty, value);
    }

    public object? RightTopContent
    {
        get => GetValue(RightTopContentProperty);
        set => SetValue(RightTopContentProperty, value);
    }

    public object? RightBottomContent
    {
        get => GetValue(RightBottomContentProperty);
        set => SetValue(RightBottomContentProperty, value);
    }

    public object? VideoContent
    {
        get => GetValue(VideoContentProperty);
        set => SetValue(VideoContentProperty, value);
    }

    public object? RightExtraContent
    {
        get => GetValue(RightExtraContentProperty);
        set => SetValue(RightExtraContentProperty, value);
    }

    public bool IsVideoFeedVisible
    {
        get => GetValue(IsVideoFeedVisibleProperty);
        set => SetValue(IsVideoFeedVisibleProperty, value);
    }

    public string VideoToggleText
    {
        get => GetValue(VideoToggleTextProperty);
        set => SetValue(VideoToggleTextProperty, value);
    }

    public ICommand? ToggleVideoFeedCommand
    {
        get => GetValue(ToggleVideoFeedCommandProperty);
        set => SetValue(ToggleVideoFeedCommandProperty, value);
    }
}