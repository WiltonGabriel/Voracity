using Voracity.Core.Data;

namespace Voracity.Tests.Unit;

public sealed class LocalizationAndSettingsTests
{
    [Theory]
    [InlineData("pt_BR.UTF-8", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("en_US.UTF-8", "en-US")]
    [InlineData("fr_FR.UTF-8", "en-US")]
    public void AutoLanguageDetection_ResolvesPortugueseOrEnglishFallback(string locale, string expected)
    {
        var detected = LocalizationCatalog.ResolveLanguageCode("auto", locale);
        Assert.Equal(expected, detected);
    }

    [Fact]
    public void ExplicitLanguageOverridesSystemLocale()
    {
        Assert.Equal("en-US", LocalizationCatalog.ResolveLanguageCode("en-US", "pt_BR.UTF-8"));
        Assert.Equal("pt-BR", LocalizationCatalog.ResolveLanguageCode("pt-BR", "en_US.UTF-8"));
    }

    [Fact]
    public void SettingsStorage_RoundtripsUserPreferencesCleanly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"voracity_settings_{Guid.NewGuid():N}");
        try
        {
            var settings = new GameSettings
            {
                LanguageMode = "pt-BR",
                WindowMode = "fullscreen",
                Vsync = false,
                UiScale = 1.15f,
                DefaultSpeed = 4,
                MasterVolume = 0.65f,
                DebugMode = true
            };

            SettingsStorage.Save(settings, tempDir);
            var loaded = SettingsStorage.Load(tempDir);

            Assert.Equal(settings, loaded);
            Assert.True(loaded.DebugMode);
            Assert.Equal("pt-BR", loaded.ResolveActiveLanguage());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
