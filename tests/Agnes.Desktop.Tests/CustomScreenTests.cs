using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Plugins;
using Dock.Model.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>A client plugin's custom screen opens as a dock document (tab), the same way the built-in
/// Settings screen replaces the conversation view (see .ideas/00d-event-spine-and-ui-extensibility.md, AC7).</summary>
public class CustomScreenTests
{
    private sealed record FakeScreen(string ScreenId, string Title, string? Icon, object Vm) : ICustomScreenProvider
    {
        public object CreateViewModel() => Vm;
    }

    private static IDocumentDock DocumentDock(MainWindowViewModel vm)
        => (IDocumentDock)vm.Layout.VisibleDockables![0];

    private static MainWindowViewModel NewVm()
        => new(new SimulatedConnector(), ImmediateDispatcher.Instance, new SessionStateStore(), new HostRegistryStore());

    [Fact]
    public void Opening_a_custom_screen_adds_a_plugin_document_to_the_dock()
    {
        var vm = NewVm();
        var screenVm = new object();
        var provider = new FakeScreen("myplugin.dashboard", "Dashboard", "📊", screenVm);

        vm.OpenCustomScreen(provider);

        var doc = Assert.Single(DocumentDock(vm).VisibleDockables!.OfType<PluginScreenDocument>());
        Assert.Equal("myplugin.dashboard", doc.Id);
        Assert.Equal("Dashboard", doc.Title);
        Assert.Same(screenVm, doc.ScreenViewModel);
    }

    [Fact]
    public void Opening_the_same_custom_screen_twice_reuses_the_one_document()
    {
        var vm = NewVm();
        var provider = new FakeScreen("myplugin.dashboard", "Dashboard", null, new object());

        vm.OpenCustomScreen(provider);
        vm.OpenCustomScreen(provider);

        Assert.Single(DocumentDock(vm).VisibleDockables!.OfType<PluginScreenDocument>());
    }

    private sealed record FakeView(string Name);

    [Fact]
    public void A_plugin_supplied_view_is_what_the_tab_presents()
    {
        // The declared seam: a plugin says how to build the view for its own view-model type, and the tab
        // presents that rather than the view-model. Without it a plugin could only get a view onto the
        // screen by handing back a control from CreateViewModel — which works, but only when its build
        // happens not to copy Avalonia beside its DLL, so whether a plugin rendered was a property of its
        // csproj rather than of this contract.
        var screenVm = new object();
        var view = new FakeView("codeybox");
        var plugins = ClientPluginHost.FromModules([new ViewModule(screenVm, view)]);

        var doc = new PluginScreenDocument(
            new FakeScreen("myplugin.dashboard", "Dashboard", null, screenVm), plugins.CreateView);

        Assert.Same(view, doc.ScreenView);
        Assert.Same(view, doc.ScreenContent);
        Assert.Same(screenVm, doc.ScreenViewModel); // still available; the view is an addition, not a swap
    }

    [Fact]
    public void A_screen_with_no_registered_view_still_presents_its_view_model()
    {
        // A plugin that registers no factory is not broken — the head falls back to its own templates, and
        // past plugins that never knew about this seam keep working unchanged.
        var screenVm = new object();
        var plugins = ClientPluginSet.Empty;

        var doc = new PluginScreenDocument(
            new FakeScreen("myplugin.dashboard", "Dashboard", null, screenVm), plugins.CreateView);

        Assert.Null(doc.ScreenView);
        Assert.Same(screenVm, doc.ScreenContent);
    }

    [Fact]
    public void A_factory_only_claims_its_own_view_model_type()
    {
        // Matching is by exact type: a factory for one view-model must not be handed another plugin's, or
        // the head's own, just because they share a base.
        var plugins = ClientPluginHost.FromModules([new TypedViewModule()]);

        Assert.IsType<FakeView>(plugins.CreateView(new Claimed()));
        Assert.Null(plugins.CreateView(new NotClaimed()));
        Assert.Null(plugins.CreateView(new object()));
    }

    private sealed class Claimed { }

    private sealed class NotClaimed { }

    private sealed class ViewModule(object viewModel, object view) : IClientPluginModule
    {
        public void Register(ClientPluginCollector collector)
            => collector.AddViewFactory(new ExactFactory(viewModel.GetType(), view));
    }

    private sealed class ExactFactory(Type vmType, object view) : IViewFactory
    {
        public Type ViewModelType => vmType;
        public object? CreateView(object viewModel) => view;
    }

    private sealed class TypedViewModule : IClientPluginModule
    {
        public void Register(ClientPluginCollector collector)
            => collector.AddViewFactory<Claimed>(_ => new FakeView("claimed"));
    }
}
