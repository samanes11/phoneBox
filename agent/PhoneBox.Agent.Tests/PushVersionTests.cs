using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PushVersionTests
{
    [Fact]
    public void ConcurrentUpdatesEachAdvanceTheVersion()
    {
        var dir = Directory.CreateTempSubdirectory("phonebox-push-").FullName;
        var path = Path.Combine(dir, "data.json");
        try
        {
            var store = new PushStore(NullLogger<PushStore>.Instance, path);
            var original = store.Version;
            var value = PushStore.ParseValue("sample");
            const int writes = 8000;
            Parallel.For(0, writes, new ParallelOptions { MaxDegreeOfParallelism = 8 },
                _ => Assert.Null(store.Set("sample", value, null)));
            Assert.True(store.Version - original >= writes, "Every successful write must advance the version");
        }
        finally
        {
            // the writes scheduled one save 2 s later; let it land before removing the folder, or it would recreate it
            for (var i = 0; i < 100 && !File.Exists(path); i++) Thread.Sleep(100);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
