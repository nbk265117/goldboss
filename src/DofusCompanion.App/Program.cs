namespace DofusCompanion.App;

/// <summary>Entry point: Velopack's install / update / uninstall hooks must run before anything else.</summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        // When started by the installer or the updater for a hook, this handles it and exits.
        Velopack.VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
