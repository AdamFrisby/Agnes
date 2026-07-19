using System.ComponentModel;
using Agnes.App.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Agnes.App.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
    }

    // Focus the palette's search box the moment it opens, so the user can type immediately.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsPaletteOpen)
            && sender is MainWindowViewModel { IsPaletteOpen: true }
            && this.FindControl<TextBox>("PaletteBox") is { } box)
        {
            Dispatcher.UIThread.Post(() => box.Focus());
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
