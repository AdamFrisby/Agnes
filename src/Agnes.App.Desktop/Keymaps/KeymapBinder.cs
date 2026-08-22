using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Agnes.App.Desktop.Keymaps;

public sealed class KeymapBinder : AvaloniaObject
{
    private static readonly ConditionalWeakTable<Control, BinderState> States = new();

    public static readonly AttachedProperty<KeymapContext?> ContextProperty =
        AvaloniaProperty.RegisterAttached<KeymapBinder, Control, KeymapContext?>("Context");

    public static readonly AttachedProperty<KeymapService?> ServiceProperty =
        AvaloniaProperty.RegisterAttached<KeymapBinder, Control, KeymapService?>("Service", inherits: true);

    static KeymapBinder()
    {
        ContextProperty.Changed.AddClassHandler<Control>((control, _) => Rebuild(control));
        ServiceProperty.Changed.AddClassHandler<Control>((control, _) => Rebuild(control));
    }

    public static void SetContext(Control element, KeymapContext? value) => element.SetValue(ContextProperty, value);
    public static KeymapContext? GetContext(Control element) => element.GetValue(ContextProperty);
    public static void SetService(Control element, KeymapService? value) => element.SetValue(ServiceProperty, value);
    public static KeymapService? GetService(Control element) => element.GetValue(ServiceProperty);

    private static void Rebuild(Control control)
    {
        var state = States.GetValue(control, static target => new BinderState(target));
        state.Rebuild();
    }

    private sealed class BinderState
    {
        private readonly Control _target;
        private readonly List<KeyBinding> _installed = [];
        private KeymapService? _service;

        public BinderState(Control target)
        {
            _target = target;
            _target.DataContextChanged += (_, _) => Rebuild();
            _target.AttachedToVisualTree += (_, _) => Rebuild();
            _target.DetachedFromVisualTree += (_, _) => Subscribe(null);
        }

        public void Rebuild()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(Rebuild);
                return;
            }

            foreach (var binding in _installed) _target.KeyBindings.Remove(binding);
            _installed.Clear();

            var context = GetContext(_target);
            var service = GetService(_target);
            Subscribe(service);
            if (context is null || service is null) return;

            foreach (var rule in service.Effective.For(context.Value))
            {
                var bound = rule.Command is { } command
                    ? CommandCatalogue.Definition(command).Bind(_target)
                    : new CommandBinding(BlockCommand.Instance);
                if (bound is null) continue;
                var keyBinding = new KeyBinding
                {
                    Gesture = rule.Gesture,
                    Command = bound.Command,
                };
                if (bound.Parameter is not null) keyBinding.CommandParameter = bound.Parameter;
                _target.KeyBindings.Add(keyBinding);
                _installed.Add(keyBinding);
            }
        }

        private void Subscribe(KeymapService? service)
        {
            if (ReferenceEquals(_service, service)) return;
            if (_service is not null) _service.Changed -= OnChanged;
            _service = service;
            if (_service is not null) _service.Changed += OnChanged;
        }

        private void OnChanged(object? sender, EventArgs e) => Rebuild();
    }

    private sealed class BlockCommand : ICommand
    {
        public static BlockCommand Instance { get; } = new();
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { }
        public event EventHandler? CanExecuteChanged
        {
            add => _ = value;
            remove => _ = value;
        }
    }
}
