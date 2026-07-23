using System;
using System.Text;
using Agnes.Ui.Core.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Iciclecreek.Terminal;

namespace Agnes.App.Desktop.Views;

/// <summary>
/// Desktop host for the embedded terminal (platform/03): binds the Iciclecreek <see cref="TerminalControl"/>
/// (a VT emulator) to a framework-agnostic <see cref="TerminalPanelViewModel"/> — feeding the session's
/// streamed output <i>into</i> the emulator and forwarding the user's keystrokes <i>out</i> via the VM's
/// <c>WriteTerminal</c> path. All transport logic lives in the VM (which is unit-tested); this file is the
/// non-headless-testable Avalonia glue only. The Iciclecreek control is process-oriented by design, so we
/// deliberately never call <c>LaunchProcess</c> — we drive its <see cref="XTerm.Terminal"/> emulator directly.
/// </summary>
public partial class TerminalPanelView : UserControl
{
    private TerminalControl? _control;
    private XTerm.Terminal? _emulator;
    private TerminalPanelViewModel? _vm;

    public TerminalPanelView()
    {
        InitializeComponent();
        _control = this.FindControl<TerminalControl>("Terminal");
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => TryBindEmulator();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
        {
            _vm.OutputAppended -= OnOutput;
        }

        _vm = DataContext as TerminalPanelViewModel;
        if (_vm is not null)
        {
            _vm.OutputAppended += OnOutput;
            // Prime the emulator with whatever scrollback the VM already holds.
            Feed(_vm.Output);
        }
    }

    private void TryBindEmulator()
    {
        _control ??= this.FindControl<TerminalControl>("Terminal");
        var emulator = _control?.Terminal;
        if (emulator is null || ReferenceEquals(emulator, _emulator))
        {
            return;
        }

        _emulator = emulator;
        // Keystrokes/paste the emulator produced from user input → the remote PTY via the VM.
        _emulator.DataReceived += OnEmulatorData;
        // The emulator recomputed its grid → tell the host the new size.
        _emulator.Resized += OnEmulatorResized;
    }

    private void OnOutput(string chunk) => Dispatcher.UIThread.Post(() => Feed(chunk));

    private void Feed(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _emulator?.Write(text);
        }
    }

    private void OnEmulatorData(object? sender, XTerm.Events.TerminalEvents.DataEventArgs e)
    {
        if (_vm is not null && !string.IsNullOrEmpty(e.Data))
        {
            _ = _vm.SendInputAsync(Encoding.UTF8.GetBytes(e.Data));
        }
    }

    private void OnEmulatorResized(object? sender, XTerm.Events.TerminalEvents.ResizeEventArgs e)
    {
        if (_vm is not null && _emulator is not null)
        {
            _ = _vm.ResizeAsync(_emulator.Cols, _emulator.Rows);
        }
    }
}
