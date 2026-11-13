using ExplorerPlus.App.Services;

namespace ExplorerPlus.App;

public static class Program
{
    /// <summary>Start with no window (the sign-in entry); does nothing when Explorer+ already runs.</summary>
    public const string BackgroundArg = "--background";

    /// <summary>Ask the running Explorer+ to close its windows and exit, and wait for it (installer upgrades and uninstall).</summary>
    public const string QuitArg = "--quit";

    [STAThread]
    public static int Main(string[] args)
    {
        // Folder links and "Show in folder" usually find Explorer+ already running. Hand the arguments over before
        // WPF, App.xaml and the theme load, so the second process lives for a few milliseconds instead of a second.
        SingleInstance? instance = null;
        if (!App.IsRegistrationSwitch(args))
        {
            instance = new SingleInstance();
            var quit = IsOnly(args, QuitArg);
            int? exitCode = null;
            if (instance.IsPrimary)
            {
                if (quit) exitCode = 0; // nothing running to quit
            }
            else if (IsOnly(args, BackgroundArg)) exitCode = 0; // already running
            else if (instance.TrySendToPrimary(args)) exitCode = quit && !instance.WaitForPrimaryExit(TimeSpan.FromSeconds(10)) ? 1 : 0;
            else if (quit) exitCode = 1; // running but not answering

            if (exitCode is { } code)
            {
                instance.Dispose();
                return code;
            }
        }

        var app = new App(instance);
        app.InitializeComponent();
        return app.Run();
    }

    public static bool IsOnly(string[] args, string arg) => args is [var only] && only.Equals(arg, StringComparison.OrdinalIgnoreCase);
}
