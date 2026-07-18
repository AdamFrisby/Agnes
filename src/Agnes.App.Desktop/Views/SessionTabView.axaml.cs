using System.ComponentModel;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views;

public partial class SessionTabView : UserControl
{
    private const double SplitterWidth = 5;
    private Grid? _workspace;
    private INotifyPropertyChanged? _session;
    private double _leftWidth = 288;
    private double _rightWidth = 540;

    public SessionTabView()
    {
        InitializeComponent();
        _workspace = this.FindControl<Grid>("Workspace");
        if (_workspace is not null)
        {
            _workspace.DataContextChanged += (_, _) => HookSession();
            HookSession();
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void HookSession()
    {
        if (_session is not null)
        {
            _session.PropertyChanged -= OnSessionPropertyChanged;
        }

        _session = _workspace?.DataContext as INotifyPropertyChanged;
        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionPropertyChanged;
        }

        UpdateColumns();
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.ShowLeftPanel) or nameof(SessionViewModel.ShowRightPanel))
        {
            UpdateColumns();
        }
    }

    private void UpdateColumns()
    {
        if (_workspace?.DataContext is not SessionViewModel vm)
        {
            return;
        }

        var columns = _workspace.ColumnDefinitions;
        Apply(columns[0], columns[1], vm.ShowLeftPanel, ref _leftWidth);
        Apply(columns[4], columns[3], vm.ShowRightPanel, ref _rightWidth);
    }

    // Collapses a side column to 0 when hidden (remembering any dragged width) and restores it
    // when shown — so panels appear only when needed, and the GridSplitter keeps its width.
    private static void Apply(ColumnDefinition panel, ColumnDefinition splitter, bool show, ref double remembered)
    {
        if (show)
        {
            if (panel.Width.Value <= 0)
            {
                panel.Width = new GridLength(remembered, GridUnitType.Pixel);
            }

            splitter.Width = new GridLength(SplitterWidth, GridUnitType.Pixel);
        }
        else
        {
            if (panel.Width.IsAbsolute && panel.Width.Value > 0)
            {
                remembered = panel.Width.Value;
            }

            panel.Width = new GridLength(0);
            splitter.Width = new GridLength(0);
        }
    }
}
