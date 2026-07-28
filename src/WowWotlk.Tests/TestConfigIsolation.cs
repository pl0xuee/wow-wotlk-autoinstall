using System.Runtime.CompilerServices;

namespace WowWotlk.Tests;

internal static class TestConfigIsolation
{
    /// <summary>
    /// Points this process's config directory at a scratch folder before any test runs.
    ///
    /// <c>AppSettings.AppDataPath</c> resolves from XDG_CONFIG_HOME, and the services under
    /// test write settings.json and installed-addons.json there for real. Without this, a test
    /// run would overwrite the install paths, realm address and addon record of whoever is
    /// running it — and the first symptom would be their next real install going to the wrong
    /// folder.
    ///
    /// A module initializer is the only hook early enough: AppDataPath is a static readonly
    /// field, so it is fixed the first time anything touches AppSettings.
    /// </summary>
    [ModuleInitializer]
    internal static void Redirect()
    {
        var scratch = Path.Join(
            Path.GetTempPath(),
            "wowwotlk-tests-config-" + Environment.ProcessId
        );
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
    }
}
