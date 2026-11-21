using PhoneBox.Agent;
using Xunit;

public sealed class ConfigPathTests
{
    [Fact]
    public void BareRelativeConfigFilenamePersistsGeneratedTokens()
    {
        var path = $"phonebox-config-{Guid.NewGuid():N}.json";
        try
        {
            var store = new ConfigStore(path);
            var generated = store.Get().Lan;
            store.PersistGenerated();
            Assert.True(File.Exists(path));
            var loaded = new ConfigStore(path).Get().Lan;
            Assert.Equal(generated.Token, loaded.Token);
            Assert.Equal(generated.WebhookToken, loaded.WebhookToken);
        }
        finally { File.Delete(path); }
    }
}
