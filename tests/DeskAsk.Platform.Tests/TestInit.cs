using System.Runtime.CompilerServices;

static class TestInit
{
    // The tests receive web hooks on 127.0.0.1; production refuses private addresses (OutboundGuard).
    [ModuleInitializer] internal static void Init() => DeskAsk.Platform.OutboundGuard.AllowPrivate = true;
}
