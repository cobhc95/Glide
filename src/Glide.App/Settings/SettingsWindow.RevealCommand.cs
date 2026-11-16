using Avalonia.Threading;

namespace Glide.App.Settings;

public partial class SettingsWindow
{
    /// <summary>
    /// Another window asked for a setting (e.g. "Show in other file manager… Set up"): reveal it the same way
    /// Settings search's Open does, once the window has laid out.
    /// </summary>
    internal void RevealSettingFromCommand(string settingId) =>
        Dispatcher.UIThread.Post(() => RevealSetting(settingId), DispatcherPriority.Loaded);
}
