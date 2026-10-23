using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Overlay.Core.Beatmap;
using Overlay.Core.Settings;
using Overlay.Core.Timeline;

namespace Overlay.App;

public partial class ControlWindow : Window
{
    private readonly AppController app;
    private readonly OverlaySettings settings;
    private bool loading = true; // controls raise change events while InitializeComponent builds them

    public ControlWindow(AppController app)
    {
        this.app = app;
        settings = app.Settings;
        InitializeComponent();
        RefreshFromSettings();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { if (IsVisible && DetailsExpander.IsExpanded) DebugText.Text = app.StatusText(); };
        timer.Start();
    }

    /// <summary>Closing the window hides it to the tray; the app keeps running until "Exit" in the tray menu.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Updates every control from the settings (after loading, or when a hotkey changed something).</summary>
    public void RefreshFromSettings()
    {
        loading = true;
        ZoomSlider.Value = Math.Log(settings.PxPerMs / Zoom.Min) / Math.Log(Zoom.Max / Zoom.Min);
        NowSlider.Value = settings.NowFraction;
        GridCheck.IsChecked = settings.ShowGrid;
        OpacitySlider.Value = settings.Opacity;
        BackgroundSlider.Value = settings.BackgroundOpacity;
        DebugCheck.IsChecked = settings.ShowDebug;
        EnabledCheck.IsChecked = settings.Enabled;
        SpaceKeyCheck.IsChecked = settings.UseSpaceKey;
        WheelCheck.IsChecked = settings.UseScrollWheel;
        StartInTrayCheck.IsChecked = settings.StartInTray;
        LayoutCheck.IsChecked = app.Overlay?.LayoutMode == true;
        StyleBox.SelectedIndex = settings.Style == TimelineStyle.OsuTimeline ? 0 : 1;
        BarTintCheck.IsChecked = settings.ManiaColumnTint;
        TintSlider.Value = settings.ManiaColumnTintOpacity;
        BarSwitchBox.Text = string.Join(", ", settings.ManiaColumnColourSwitches);
        UpdateStyleControls();
        HotkeyText.Text = string.Join("\n", settings.Hotkeys
            .Where(h => !string.IsNullOrWhiteSpace(h.Value))
            .Select(h => $"{Describe(h.Value),-26} {Label(h.Key)}"));
        loading = false;
        RefreshLabels();
    }

    private static string Describe(string hotkey) => hotkey
        .Replace("OemPlus", "=").Replace("OemMinus", "-").Replace("OemOpenBrackets", "[").Replace("OemCloseBrackets", "]")
        .Replace("OemPeriod", ".").Replace("OemComma", ",");

    private static string Label(HotkeyAction a) => a switch
    {
        HotkeyAction.ToggleOverlay => "overlay on / off",
        HotkeyAction.LayoutMode => "layout mode",
        HotkeyAction.ZoomIn => "zoom in",
        HotkeyAction.ZoomOut => "zoom out",
        HotkeyAction.OffsetPlus1 => "offset +1 ms",
        HotkeyAction.OffsetMinus1 => "offset −1 ms",
        HotkeyAction.OffsetPlus5 => "offset +5 ms",
        HotkeyAction.OffsetMinus5 => "offset −5 ms",
        HotkeyAction.NextDifficulty => "next std difficulty",
        HotkeyAction.PreviousDifficulty => "previous std difficulty",
        _ => a.ToString(),
    };

    public void ShowSummary(string text, AppController.Health health, bool offerStartTosu)
    {
        SummaryText.Text = text;
        (SummaryBorder.Background, SummaryBorder.BorderBrush) = health switch
        {
            AppController.Health.Ok => (Brush("#E8F5E9"), Brush("#81C784")),
            AppController.Health.Waiting => (Brush("#FFF8E1"), Brush("#FFD54F")),
            _ => (Brush("#FFEBEE"), Brush("#E57373")),
        };
        StartTosuButton.Visibility = offerStartTosu ? Visibility.Visible : Visibility.Collapsed;
        var problems = app.Hotkeys?.Problems;
        HotkeyProblems.Text = problems is { Count: > 0 } ? "Not registered: " + string.Join("; ", problems) : "";
        HotkeyProblems.Visibility = HotkeyProblems.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    public void ShowSource(TimeSourceKind kind)
    {
        loading = true;
        SourceBox.SelectedIndex = Array.IndexOf(SourceOrder, kind);
        loading = false;
    }

    public void ShowDifficulties(IReadOnlyList<DifficultyInfo> list, DifficultyInfo? selected, DifficultyInfo? pinned)
    {
        loading = true;
        DifficultyBox.Items.Clear();
        foreach (var d in list)
        {
            bool isPinned = pinned != null && d.Path == pinned.Path;
            string label = isPinned ? $"📌 {d.Version} — {System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(d.Path))}" : d.Version;
            DifficultyBox.Items.Add(new ComboBoxItem { Content = label, Tag = d, ToolTip = d.Path });
        }
        DifficultyBox.SelectedIndex = selected == null ? -1 : list.ToList().FindIndex(d => d.Path == selected.Path);
        DifficultyBox.IsEnabled = list.Count > 0;
        loading = false;
    }

    private void RefreshLabels()
    {
        ZoomLabel.Text = $"{settings.PxPerMs * 1000:0} px/s";
        NowLabel.Text = $"{settings.NowFraction:P0}";
        OpacityLabel.Text = $"{settings.Opacity:P0}";
        BackgroundLabel.Text = $"{settings.BackgroundOpacity:P0}";
        OffsetLabel.Text = $"{settings.OffsetMs:+0;-0;0} ms";
        TintLabel.Text = $"{settings.ManiaColumnTintOpacity:P0}";
    }

    /// <summary>Applies a change from a control, refreshes the labels and schedules a save.</summary>
    private void Changed(Action apply)
    {
        if (loading) return;
        apply();
        RefreshLabels();
        app.SaveSoon();
    }

    private void SourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Changed(() => app.SetSource(SourceOrder[Math.Max(0, SourceBox.SelectedIndex)]));

    /// <summary>The time sources in the order of the Time source list.</summary>
    private static readonly TimeSourceKind[] SourceOrder = { TimeSourceKind.Memory, TimeSourceKind.Tosu, TimeSourceKind.Demo };

    private void DifficultyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || DifficultyBox.SelectedItem is not ComboBoxItem { Tag: DifficultyInfo d }) return;
        app.ChooseDifficulty(d);
    }

    private void UseOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (app.UseOpenBeatmap() is { } error)
            MessageBox.Show(this, error, "Use beatmap open in editor", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "osu! beatmaps (*.osu)|*.osu", Title = "Choose an osu!standard difficulty" };
        if (dialog.ShowDialog(this) == true) app.OpenFile(dialog.FileName);
    }

    private void StartTosuButton_Click(object sender, RoutedEventArgs e)
    {
        if (app.StartTosu()) return;
        var dialog = new OpenFileDialog { Filter = "tosu|tosu.exe|Programs (*.exe)|*.exe", Title = "Where is tosu.exe?" };
        if (dialog.ShowDialog(this) != true) return;
        settings.TosuPath = dialog.FileName;
        app.SaveSoon();
        app.StartTosu();
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Changed(() => settings.PxPerMs = Zoom.Min * Math.Pow(Zoom.Max / Zoom.Min, ZoomSlider.Value));

    private void NowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Changed(() => settings.NowFraction = NowSlider.Value);

    private void GridCheck_Click(object sender, RoutedEventArgs e) => Changed(() => settings.ShowGrid = GridCheck.IsChecked == true);

    private void StyleBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => Changed(() =>
    {
        settings.Style = StyleBox.SelectedIndex == 0 ? TimelineStyle.OsuTimeline : TimelineStyle.Strip;
        app.ApplyStyle();
        LayoutCheck.IsChecked = app.Overlay.LayoutMode;
        UpdateStyleControls();
    });

    /// <summary>On osu!'s timeline, position, zoom, "now" line, grid and background all come from osu!.</summary>
    private void UpdateStyleControls()
    {
        bool strip = settings.Style == TimelineStyle.Strip;
        foreach (var c in new Control[] { ZoomSlider, NowSlider, GridCheck, BackgroundSlider, LayoutCheck }) c.IsEnabled = strip;
    }


    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Changed(() => settings.Opacity = OpacitySlider.Value);

    private void BackgroundSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Changed(() => settings.BackgroundOpacity = BackgroundSlider.Value);

    private void Offset_Click(object sender, RoutedEventArgs e) =>
        Changed(() => settings.OffsetMs += double.Parse((string)((Button)sender).Tag));

    private void TintSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Changed(() => settings.ManiaColumnTintOpacity = TintSlider.Value);

    private void BarTintCheck_Click(object sender, RoutedEventArgs e) => Changed(() => settings.ManiaColumnTint = BarTintCheck.IsChecked == true);

    private void BarSwitchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ApplyBarSwitches();
    }

    private void BarSwitchBox_LostKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => ApplyBarSwitches();

    /// <summary>Reads the comma-separated column list; anything that isn't a positive whole number is ignored.</summary>
    private void ApplyBarSwitches() => Changed(() =>
    {
        settings.ManiaColumnColourSwitches = BarSwitchBox.Text
            .Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0).Distinct().OrderBy(n => n).ToArray();
        BarSwitchBox.Text = string.Join(", ", settings.ManiaColumnColourSwitches);
    });

    private void WheelCheck_Click(object sender, RoutedEventArgs e) => Changed(() => settings.UseScrollWheel = WheelCheck.IsChecked == true);

    private void SpaceKeyCheck_Click(object sender, RoutedEventArgs e) => Changed(() => settings.UseSpaceKey = SpaceKeyCheck.IsChecked == true);

    private void EnabledCheck_Click(object sender, RoutedEventArgs e) => Changed(() => settings.Enabled = EnabledCheck.IsChecked == true);

    private void LayoutCheck_Click(object sender, RoutedEventArgs e) => app.Overlay.LayoutMode = LayoutCheck.IsChecked == true;

    private void DebugCheck_Click(object sender, RoutedEventArgs e) => Changed(() => settings.ShowDebug = DebugCheck.IsChecked == true);

    private void StartInTrayCheck_Click(object sender, RoutedEventArgs e) => Changed(() => settings.StartInTray = StartInTrayCheck.IsChecked == true);
}
