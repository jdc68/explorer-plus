using System.Runtime.CompilerServices;

namespace ExplorerPlus.Core.Tests;

internal static class TestProfile
{
    // keeps the app's settings and logs out of the real data folder
    [ModuleInitializer]
    internal static void Init() => Environment.SetEnvironmentVariable("EXPLORERPLUS_PROFILE", "tests");
}
