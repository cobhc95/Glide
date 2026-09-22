using Glide.App.Platform;
using Xunit;

namespace Glide.Core.Tests;

/// <summary>
/// Policy tests for the automatic Windows Open With integration. These exercise the pure decision
/// function so they never touch the real registry.
/// </summary>
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
}
