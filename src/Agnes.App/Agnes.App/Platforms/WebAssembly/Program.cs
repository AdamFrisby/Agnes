using Microsoft.UI.Xaml;

namespace Agnes.App;

public class Program
{
    private static App? _app;

    public static int Main(string[] args)
    {
        App.InitializeLogging();
        Application.Start(_ => _app = new App());
        return 0;
    }
}
