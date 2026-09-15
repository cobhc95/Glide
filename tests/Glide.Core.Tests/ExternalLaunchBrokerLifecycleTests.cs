using Glide.App.Services;

namespace Glide.Core.Tests;

public sealed class ExternalLaunchBrokerLifecycleTests
{
    [Fact]
    public void Presence_ownership_is_idempotent_released_and_reacquirable()
    {
        if (!OperatingSystem.IsWindows()) return;

        ExternalLaunchBroker.Stop();
        Assert.False(ExternalLaunchBroker.ExistingProcessPresent());

        Assert.True(ExternalLaunchBroker.ClaimProcessPresence());
        Assert.True(ExternalLaunchBroker.ExistingProcessPresent());
        Assert.True(ExternalLaunchBroker.ClaimProcessPresence());

        ExternalLaunchBroker.Stop();
        Assert.False(ExternalLaunchBroker.ExistingProcessPresent());
        Assert.True(ExternalLaunchBroker.ClaimProcessPresence());
        ExternalLaunchBroker.Stop();
        Assert.False(ExternalLaunchBroker.ExistingProcessPresent());
    }
}
