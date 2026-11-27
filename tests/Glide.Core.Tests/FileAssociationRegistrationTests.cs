using Glide.App.Platform;

namespace Glide.Core.Tests;

/// <summary>
/// Policy tests for the automatic Windows Open With integration. These exercise the pure decision
/// function so they never touch the real registry.
/// </summary>
[Trait("Area", "Shell")]
public sealed class FileAssociationRegistrationTests
{
    [Fact]
    public void Auto_registers_on_first_launch()
        => Assert.True(WindowsFileAssociationRegistration.ShouldAutoRegister(
            machineWideRegistered: false, optedOut: false, registeredVersion: null, currentVersion: "4.2.6.0"));

    [Fact]
    public void Skips_when_a_machine_wide_install_already_covers_the_executable()
        => Assert.False(WindowsFileAssociationRegistration.ShouldAutoRegister(
            machineWideRegistered: true, optedOut: false, registeredVersion: null, currentVersion: "4.2.6.0"));

    [Fact]
    public void Skips_after_the_user_explicitly_removed_the_registration()
        => Assert.False(WindowsFileAssociationRegistration.ShouldAutoRegister(
            machineWideRegistered: false, optedOut: true, registeredVersion: null, currentVersion: "4.2.6.0"));

    [Fact]
    public void Skips_when_already_registered_for_this_version()
        => Assert.False(WindowsFileAssociationRegistration.ShouldAutoRegister(
            machineWideRegistered: false, optedOut: false, registeredVersion: "4.2.6.0", currentVersion: "4.2.6.0"));

    [Fact]
    public void Re_registers_when_the_version_changes()
        => Assert.True(WindowsFileAssociationRegistration.ShouldAutoRegister(
            machineWideRegistered: false, optedOut: false, registeredVersion: "4.2.5.0", currentVersion: "4.2.6.0"));

    [Fact]
    public void Extensions_with_a_document_icon_get_their_own_progid()
    {
        Assert.Equal("Glide.Image.png", WindowsFileAssociationRegistration.ProgIdFor(".PNG", hasTypeIcon: true));
        Assert.Equal("Glide.Image", WindowsFileAssociationRegistration.ProgIdFor(".pcx", hasTypeIcon: false));
        Assert.True(WindowsFileAssociationRegistration.IsGlideProgId("Glide.Image.jpg"));
        Assert.True(WindowsFileAssociationRegistration.IsGlideProgId("Glide.Image"));
        Assert.False(WindowsFileAssociationRegistration.IsGlideProgId("Glide.Imagery"));
        Assert.False(WindowsFileAssociationRegistration.IsGlideProgId(null));
    }

    [Fact]
    public void Document_icon_path_is_the_extension_ico_in_the_file_types_folder()
    {
        var path = WindowsFileAssociationRegistration.FileTypeIconPath(@"C:\Glide", ".JPG", _ => true);
        Assert.Equal(Path.Combine(@"C:\Glide", "FileTypes", "jpg.ico"), path);
        Assert.Null(WindowsFileAssociationRegistration.FileTypeIconPath(@"C:\Glide", ".jpg", _ => false));
        Assert.Null(WindowsFileAssociationRegistration.FileTypeIconPath(@"C:\Glide", ".nii.gz", _ => true));
    }

    [Fact]
    public void A_typed_glide_progid_counts_as_glide_for_claims_and_release()
    {
        var facts = new WindowsFileAssociationRegistration.DefaultClaimFacts(".png", "Glide.Image.png", "Glide.Image", null, false, new[] { "Glide.Image.png" });
        Assert.True(WindowsFileAssociationRegistration.ShouldClaimDefault(facts));
        Assert.True(WindowsFileAssociationRegistration.ShouldReleaseClaim("Glide.Image.png"));
        Assert.False(WindowsFileAssociationRegistration.ShouldReleaseClaim("Other.png"));
    }

    [Fact]
    public void Every_shipped_document_icon_has_all_icon_frame_sizes()
    {
        var dir = AppContext.BaseDirectory;
        var folder = Path.Combine(dir, WindowsFileAssociationRegistration.FileTypeIconFolder);
        Assert.True(Directory.Exists(folder), folder);
        var icons = Directory.GetFiles(folder, "*.ico");
        Assert.Contains(icons, f => Path.GetFileName(f) == "png.ico");
        Assert.Contains(icons, f => Path.GetFileName(f) == "jpg.ico");
        foreach (var icon in icons)
        {
            var bytes = File.ReadAllBytes(icon);
            Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
            Assert.Equal(9, BitConverter.ToUInt16(bytes, 4)); // 16 20 24 32 40 48 64 128 256
        }
    }
}
