using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeadlockServerPicker.Core;
using DeadlockServerPicker.Core.Firewall;
using DeadlockServerPicker.Core.Sdr;

namespace DeadlockServerPicker.Gui;

/// <summary>
/// Code-only window that renders a <see cref="PickerModel"/>. All network and firewall work runs off
/// the UI thread; the UI is rebuilt from the model after every change (37 rows, cheap).
/// </summary>
internal sealed class MainWindow : Window
{
    private const string LimitationsUrl = "https://github.com/clintgalendez/deadlock-server-picker/blob/main/docs/limitations.md";

    private static readonly IBrush Muted = new SolidColorBrush(Color.FromArgb(0xB0, 0x80, 0x80, 0x80));
    private static readonly IBrush BlockedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x4F, 0x4F));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x1E));
    private static readonly IBrush OkBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0xA5, 0x5C));
    private static readonly IBrush WarnBack = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xB0, 0x20));
    private static readonly IBrush ErrorBack = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x40, 0x40));

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly StackPanel _banners = new() { Spacing = 6 };
    private readonly TextBlock _info = new() { Foreground = Muted };
    private readonly WrapPanel _regionToggles = new();
    private readonly Grid _table = new() { ColumnDefinitions = ColumnDefinitions.Parse("Auto,64,*,110,80,260") };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _overBlocking = new() { TextWrapping = TextWrapping.Wrap, Foreground = WarnBrush };
    private readonly SelectableTextBlock _tip = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Content = "Apply", IsDefault = true };
    private readonly Button _unblockAll = new() { Content = "Unblock all" };
    private readonly Button _refreshPings = new() { Content = "Refresh pings" };
    private readonly Button _readFirewall = new() { Content = "Load firewall status" };
    private readonly ComboBox _presetRegion = new() { MinWidth = 150 };
    private readonly NumericUpDown _presetMs = new() { Value = 80, Minimum = 10, Maximum = 500, Increment = 10, FormatString = "0", Width = 130 };
    private readonly Button _presetRegionButton = new() { Content = "Allow only this region" };
    private readonly Button _presetMsButton = new() { Content = "Allow only PoPs within (ms)" };

    private PickerModel? _model;
    private LoadedConfig? _loaded;
    private IFirewallService? _firewall;
    private string? _firewallError;
    private bool _busy;

    public MainWindow()
    {
        Title = $"Deadlock Server Picker {AppInfo.Version.Split('+')[0]}";
        Width = 1000;
        Height = 780;
        MinWidth = 820;
        MinHeight = 520;

        _apply.Click += async (_, _) => await RunAsync("Applying…", ApplyAsync);
        _unblockAll.Click += async (_, _) => await RunAsync("Removing all blocks…", UnblockAllAsync);
        _refreshPings.Click += async (_, _) => await RunAsync("Pinging relays…", PingAsync);
        _readFirewall.Click += async (_, _) => await RunAsync("Reading the firewall…", ReadFirewallAsync);
        _presetRegionButton.Click += (_, _) =>
        {
            if (_model is null || _presetRegion.SelectedIndex < 0)
                return;
            _model.OnlyRegion(_model.RegionsPresent[_presetRegion.SelectedIndex]);
            Render();
        };
        _presetMsButton.Click += (_, _) =>
        {
            if (_model is null)
                return;
            var unknown = _model.Within((int)(_presetMs.Value ?? 80));
            SetStatus(unknown.Count == 0 ? null : $"{unknown.Count} PoP(s) without a ping result were left allowed ({string.Join(", ", unknown)}). Some relays ignore ping.");
            Render();
        };

        Content = Layout();
        Opened += async (_, _) => await LoadAsync();
        Closing += (_, _) => _ = _firewall?.DisposeAsync().AsTask();
    }

    // ---- layout ----------------------------------------------------------------------------

    private Control Layout()
    {
        var header = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Deadlock Server Picker", FontSize = 22, FontWeight = FontWeight.SemiBold },
                _info,
            },
        };

        var presets = new WrapPanel
        {
            Children =
            {
                Label("Presets:"),
                _presetRegion, Spaced(_presetRegionButton),
                Spaced(_presetMs), Spaced(_presetMsButton),
            },
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _refreshPings, _readFirewall, _unblockAll, _apply },
        };

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                LinkButton("Why blocking is not a guarantee", () => Open(LimitationsUrl)),
                LinkButton("Open log folder", () => Open(Path.GetDirectoryName(Log.FilePath ?? Log.DefaultPath)!)),
                new SelectableTextBlock { Text = $"Log: {Log.FilePath ?? Log.DefaultPath}", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 },
            },
        };

        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { _overBlocking, _tip, buttons, _status, links } };
        var top = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 10), Children = { header, _banners, Row("Regions:", _regionToggles), presets } };

        var dock = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(footer);
        dock.Children.Add(new Border
        {
            BorderBrush = Muted,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new ScrollViewer { Content = _table, Padding = new Thickness(8, 4) },
        });
        return dock;
    }

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontWeight = FontWeight.SemiBold };

    private static Control Spaced(Control c)
    {
        c.Margin = new Thickness(8, 0, 0, 0);
        return c;
    }

    private static Control Row(string label, Control content) => new DockPanel { Children = { Label(label), content } };

    private static Button LinkButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Background = Brushes.Transparent, Padding = new Thickness(0), Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF)) };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ---- rendering -------------------------------------------------------------------------

    private void Render()
    {
        RenderBanners();
        if (_model is null)
        {
            _apply.IsEnabled = _unblockAll.IsEnabled = _refreshPings.IsEnabled = _readFirewall.IsEnabled = false;
            return;
        }

        _info.Text = $"SDR revision {_model.Config.Revision} | relay list {(_loaded!.Stale ? "from cache, " : "")}fetched {_loaded.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
            + (_firewall?.Backend is { } b ? $" | firewall: {b}" : "");

        RenderRegionToggles();
        RenderTable();

        if (_presetRegion.ItemCount == 0)
        {
            _presetRegion.ItemsSource = _model.RegionsPresent.Select(Regions.DisplayName).ToList();
            _presetRegion.SelectedIndex = 0;
        }

        var pending = _model.PendingChanges;
        _apply.Content = pending == 0 ? "Apply" : $"Apply ({pending} change{(pending == 1 ? "" : "s")})";
        _apply.IsEnabled = !_busy && pending > 0;
        _unblockAll.IsEnabled = !_busy && (_model.Firewall?.Present ?? true);
        _refreshPings.IsEnabled = !_busy;
        _readFirewall.IsEnabled = !_busy;
        _readFirewall.IsVisible = _model.Firewall is null;
        _presetRegionButton.IsEnabled = _presetMsButton.IsEnabled = !_busy;

        _overBlocking.Text = _model.OverBlockingWarning ?? "";
        _overBlocking.IsVisible = _model.OverBlockingWarning is not null;
        _tip.Text = _model.RegionOverrideTip ?? "";
        _tip.IsVisible = _model.RegionOverrideTip is not null;
    }

    private void RenderBanners()
    {
        _banners.Children.Clear();
        if (OperatingSystem.IsWindows() && !Elevation.IsElevated())
            _banners.Children.Add(Banner("Not running as administrator: you can look around, but Apply will fail. Restart the app and accept the Windows prompt.", ErrorBack));
        if (_loaded?.Stale == true)
            _banners.Children.Add(Banner($"Using a cached relay list from {_loaded.FetchedAt.ToLocalTime():yyyy-MM-dd HH:mm} ({_loaded.StaleReason}). Valve may have changed relays since; blocks use these cached addresses.", WarnBack));
        if (_firewallError is not null)
            _banners.Children.Add(Banner(_firewallError, ErrorBack));
        if (_model?.Stale is { Count: > 0 } stale)
        {
            var reapply = new Button { Content = "Re-apply now", IsEnabled = !_busy, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            reapply.Click += async (_, _) => await RunAsync("Updating blocks to Valve's current relays…", ResyncAsync);
            var text = $"Valve changed its relay list since you blocked these PoPs, so some new relays are not blocked: {string.Join("; ", stale.Select(x => x.Describe()))}.";
            var panel = new DockPanel { Children = { reapply, new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center } } };
            DockPanel.SetDock(reapply, Dock.Right);
            _banners.Children.Add(new Border { Background = WarnBack, CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6), Child = panel });
        }
        foreach (var w in _model?.Firewall?.Warnings ?? [])
            _banners.Children.Add(Banner(w, WarnBack));
    }

    private static Border Banner(string text, IBrush background) => new()
    {
        Background = background,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(10, 6),
        Child = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
    };

    private void RenderRegionToggles()
    {
        _regionToggles.Children.Clear();
        foreach (var region in _model!.RegionsPresent)
        {
            var total = _model.Rows.Count(r => r.Pop.Blockable && r.Region.Region == region);
            var allowed = _model.Rows.Count(r => r.Pop.Blockable && r.Region.Region == region && r.Allowed);
            var cb = new CheckBox
            {
                Content = $"{Regions.DisplayName(region)} ({allowed}/{total})",
                IsChecked = _model.RegionState(region),
                IsEnabled = !_busy,
                Margin = new Thickness(0, 0, 14, 0),
            };
            cb.IsCheckedChanged += (_, _) =>
            {
                _model.SetRegion(region, cb.IsChecked == true);
                Dispatcher.UIThread.Post(Render);
            };
            _regionToggles.Children.Add(cb);
        }
    }

    private void RenderTable()
    {
        _table.Children.Clear();
        _table.RowDefinitions.Clear();
        AddRow(Header("Allow"), Header("PoP"), Header("Location"), Header("Region"), Header("Ping"), Header("Firewall"));

        foreach (var group in _model!.Rows.GroupBy(r => r.Region.Region).OrderBy(g => g.Key))
        {
            var title = new TextBlock { Text = Regions.DisplayName(group.Key), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2) };
            Grid.SetColumnSpan(title, 6);
            AddRow(title);
            foreach (var row in group.OrderBy(r => !r.Pop.Blockable).ThenBy(r => r.PingMs ?? long.MaxValue).ThenBy(r => r.Pop.Code, StringComparer.Ordinal))
                AddPopRow(row);
        }
    }

    private void AddPopRow(PopRow row)
    {
        Control allow;
        if (row.Pop.Blockable)
        {
            var cb = new CheckBox { IsChecked = row.Allowed, IsEnabled = !_busy, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            ToolTip.SetTip(cb, "Checked: you may be matched here. Unchecked: block it.");
            cb.IsCheckedChanged += (_, _) =>
            {
                _model!.SetAllowed(row.Pop.Code, cb.IsChecked == true);
                Dispatcher.UIThread.Post(Render);
            };
            allow = cb;
        }
        else
        {
            allow = Cell("–", foreground: Muted);
            allow.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTip.SetTip(allow, "Valve publishes no relays for this location, so it can't be blocked. Block the relays it is reached through instead.");
        }

        var ping = !row.Pop.Blockable ? "" : !row.Pinged ? "…" : row.PingMs is { } ms ? $"{ms} ms" : "timeout";
        var (status, brush) = StatusText(row);
        AddRow(
            allow,
            Cell(row.Pop.Code, FontWeight.SemiBold),
            Cell(row.Pop.Description),
            Cell(Regions.DisplayName(row.Region.Region) + (row.Region.Guessed ? " (guessed)" : ""), foreground: row.Region.Guessed ? WarnBrush : null),
            Cell(ping, foreground: row.PingMs is null ? Muted : null),
            Cell(status, foreground: brush));
    }

    private (string Text, IBrush? Brush) StatusText(PopRow row)
    {
        var wantBlocked = row.Pop.Blockable && !row.Allowed;
        var pending = row.Status switch
        {
            PopStatus.Blocked or PopStatus.BlockedStale when !wantBlocked => " → unblock on Apply",
            PopStatus.Open or PopStatus.Unknown when wantBlocked => " → block on Apply",
            PopStatus.BlockedStale => " → refresh on Apply",
            _ => "",
        };
        return row.Status switch
        {
            PopStatus.NotBlockable => ("Not blockable" + (row.ReachedVia.Count > 0 ? $" · via {string.Join(", ", row.ReachedVia.Take(3).Select(v => v.Code))}" : ""), Muted),
            PopStatus.Unknown => ((wantBlocked ? "" : "—") + pending.TrimStart(), wantBlocked ? WarnBrush : Muted),
            PopStatus.Open => ("Open" + pending, wantBlocked ? WarnBrush : OkBrush),
            PopStatus.Blocked => ("Blocked" + pending, pending.Length > 0 ? WarnBrush : BlockedBrush),
            _ => ("Blocked · relays changed" + pending, WarnBrush),
        };
    }

    private static TextBlock Header(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Foreground = Muted, Margin = new Thickness(0, 0, 8, 4) };

    private static TextBlock Cell(string text, FontWeight weight = FontWeight.Normal, IBrush? foreground = null)
    {
        var t = new TextBlock { Text = text, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0) };
        if (foreground is not null)
            t.Foreground = foreground;
        return t;
    }

    private void AddRow(params Control[] cells)
    {
        var index = _table.RowDefinitions.Count;
        _table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetRow(cells[i], index);
            if (Grid.GetColumnSpan(cells[i]) == 1)
                Grid.SetColumn(cells[i], i);
            _table.Children.Add(cells[i]);
        }
    }

    private void SetStatus(string? text, IBrush? brush = null)
    {
        _status.Text = text ?? "";
        _status.Foreground = brush ?? Muted;
    }

    // ---- actions ---------------------------------------------------------------------------

    private async Task RunAsync(string busyText, Func<Task> action)
    {
        if (_busy)
            return;
        _busy = true;
        SetStatus(busyText);
        Render();
        try
        {
            await action();
        }
        catch (FirewallException e)
        {
            Log.Error("gui action failed", e);
            SetStatus(e.Message, BlockedBrush);
        }
        catch (Exception e)
        {
            // Last line of defence for button handlers: never crash the window, always leave a trace.
            Log.Error("gui action failed unexpectedly", e);
            SetStatus($"Something went wrong: {e.Message} Details are in the log file ({Log.FilePath}).", BlockedBrush);
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    private async Task LoadAsync()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"DeadlockServerPicker/{AppInfo.Version.Split('+')[0]} (+https://github.com/clintgalendez/deadlock-server-picker)");
        Render();
        await RunAsync("Downloading Valve's relay list…", async () =>
        {
            try
            {
                _loaded = await Task.Run(() => new SdrConfigSource(_http, Paths.ConfigDir).LoadAsync(AppInfo.DeadlockAppId));
            }
            catch (ConfigUnavailableException e)
            {
                SetStatus(e.Message + " Close and reopen the app to retry.", BlockedBrush);
                return;
            }
            _model = new PickerModel(_loaded.Config);
            _firewall = CreateFirewallService();
            SetStatus(null);
        });
        if (_model is null)
            return;

        // On Windows (elevated by the manifest) and as root, reading is free. On Linux it needs a
        // password, so wait until the user asks for it or presses Apply.
        if (_firewall is LocalFirewallService)
            await RunAsync("Reading the firewall…", ReadFirewallAsync);
        await RunAsync("Pinging relays…", PingAsync);

        // Screenshot hook for docs and visual checks: DSP_GUI_SNAPSHOT=out.png [DSP_GUI_SNAPSHOT_REGION=eu]
        if (Environment.GetEnvironmentVariable("DSP_GUI_SNAPSHOT") is { Length: > 0 } snapshot)
        {
            if (Regions.TryParse(Environment.GetEnvironmentVariable("DSP_GUI_SNAPSHOT_REGION") ?? "", out var region))
            {
                _model.OnlyRegion(region);
                Render();
            }
            await SnapshotAndCloseAsync(snapshot);
        }
    }

    private IFirewallService CreateFirewallService()
    {
        if (OperatingSystem.IsWindows() || Elevation.IsElevated())
        {
            var runner = new ProcessRunner();
            return new LocalFirewallService(FirewallBackends.CreateAsync(runner).GetAwaiter().GetResult(), runner);
        }
        return new AgentFirewallService(Path.Combine(AppContext.BaseDirectory, "dsp"));
    }

    private async Task ReadFirewallAsync()
    {
        try
        {
            var state = await Task.Run(() => _firewall!.ReadAsync());
            _firewallError = null;
            _model!.SetFirewall(state);
            SetStatus(state.Blocked.Count == 0 ? "Firewall read: nothing is blocked." : $"Firewall read: {state.Blocked.Count} PoP(s) blocked.");
        }
        catch (FirewallException e)
        {
            _firewallError = e.Message;
            throw;
        }
    }

    private async Task PingAsync()
    {
        var pops = _model!.Config.Pops.Values.ToList();
        var pings = await Task.Run(() => Latency.MeasureAsync(pops, new IcmpPinger()));
        _model.SetPings(pings);
        if (_status.Text?.StartsWith("Pinging", StringComparison.Ordinal) == true)
            SetStatus(_model.Firewall is null && _firewall is AgentFirewallService
                ? "Firewall status not loaded yet. \"Load firewall status\" (or Apply) asks for your password once per session."
                : null);
    }

    private async Task ApplyAsync()
    {
        var desired = _model!.Desired;
        var state = await Task.Run(() => _firewall!.ApplyAsync(desired));
        _firewallError = null;
        _model.SetFirewall(state);
        SetStatus(state.Blocked.Count == 0
            ? "Done. Verified in the firewall: nothing is blocked."
            : $"Done. Verified in the firewall: {state.Blocked.Count} PoP(s) blocked. Restart Steam if a game is running so it re-measures relays.", OkBrush);
    }

    /// <summary>Re-blocks exactly the PoPs blocked now with Valve's current IPs; keeps the user's pending edits.</summary>
    private async Task ResyncAsync()
    {
        var keep = _model!.Allowed.ToList();
        var desired = Planner.Resync(_model.Config, _model.Firewall!);
        var state = await Task.Run(() => _firewall!.ApplyAsync(desired));
        _model.SetFirewall(state);
        _model.SetAllowedSet(keep);
        SetStatus("Done. Blocked PoPs now use Valve's current relay list (verified in the firewall).", OkBrush);
    }

    private async Task UnblockAllAsync()
    {
        var state = await Task.Run(() => _firewall!.ApplyAsync(DesiredState.Empty));
        _firewallError = null;
        _model!.SetFirewall(state);
        SetStatus("Done. All blocks removed.", OkBrush);
    }

    private static void Open(string target)
    {
        try
        {
            // From an elevated process, explorer.exe hands the URL to the user's (unelevated) shell,
            // so the browser does not run as administrator.
            using var _ = OperatingSystem.IsWindows()
                ? Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = false })
                : Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Warn($"could not open {target}: {e.Message}");
        }
    }

    private async Task SnapshotAndCloseAsync(string path)
    {
        await Task.Delay(300);
        var size = new PixelSize((int)ClientSize.Width, (int)ClientSize.Height);
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(this);
        bitmap.Save(path, new PngBitmapEncoderOptions());
        Log.Info(string.Create(CultureInfo.InvariantCulture, $"snapshot saved to {path}"));
        Close();
    }
}
