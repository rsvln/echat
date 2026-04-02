using EChat.UI.Services;

namespace EChat.Maui.Services;

public class PlatformService : IPlatformService
{
#if WINDOWS
    public bool IsDesktop => true;
#else
    public bool IsDesktop => false;
#endif
}
