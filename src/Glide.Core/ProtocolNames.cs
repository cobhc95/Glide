namespace Glide.Core;

/// <summary>
/// Single source for IPC/registry/installer identity strings. The pipe + presence names are a
/// wire-compat surface (changing them orphans old/new coexistence), so they live here rather
/// than scattered across broker/registration/installer code (4.5.2 audit).
/// </summary>
public static class ProtocolNames
{
    public const string PipeName = "Glide3.ExternalOpen.v2";
    public const string PresenceName = @"Local\Glide3.ProcessPresence.v3";
    public const string ProtocolHeader = "GLIDE3\t2";
    public const string AppExe = "Glide.exe";
    public const string AppName = "Glide Image Viewer";
    public const string AppShortName = "Glide";
    public const string RegistryAppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\Glide.exe";
    public const string RegisteredApplicationsValue = "Glide Image Viewer";
}
