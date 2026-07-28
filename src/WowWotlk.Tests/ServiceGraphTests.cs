using Microsoft.Extensions.DependencyInjection;
using WowWotlk.Gui;
using Xunit;

namespace WowWotlk.Tests;

public class ServiceGraphTests
{
    /// <summary>
    /// A constructor parameter with no registration behind it compiles fine and throws on
    /// startup — surfacing as a crash the first time a user opens whichever page needs it.
    /// ValidateOnBuild resolves every call site without invoking a single factory, so this
    /// catches that without constructing view models or writing to the user's real log.
    /// </summary>
    [Fact]
    public void Every_registered_service_can_be_resolved()
    {
        using var provider = App.Registrations()
            .BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
            );

        Assert.NotNull(provider);
    }
}
