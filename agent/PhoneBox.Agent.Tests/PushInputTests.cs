using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using PhoneBox.Agent;
using Xunit;

public sealed class PushInputTests
{
    [Fact]
    public void APartialPushSnapshotKeepsItsValidEntriesWithoutBreakingState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"phonebox-push-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"missing":null,"partial":{},"noTime":{"Data":"x","At":null},"text":"oops","valid":{"Data":"ready","At":1}}""");
        try
        {
            var store = new PushStore(NullLogger<PushStore>.Instance, path);
            var items = store.Snapshot();
            var item = Assert.Single(items);
            Assert.Equal("valid", item.Key);
            Assert.Equal("ready", item.Value.Data.GetString());
            Assert.Contains("ready", JsonSerializer.Serialize(items));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("\"later\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    public void NonNumericPayloadTtlRemainsOrdinaryData(string ttl)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new PushStore(NullLogger<PushStore>.Instance, path);
        var data = JsonSerializer.Deserialize<JsonElement>("{\"ttl\":"+ttl+",\"value\":1}");
        Assert.Null(store.Set("input", data, null));
        Assert.Null(store.Get("input")!.ExpiresAt);
        Assert.Equal(data.GetRawText(), store.Get("input")!.Data.GetRawText());
        Assert.Null(store.Set("numeric", JsonSerializer.Deserialize<JsonElement>("{\"ttl\":60}"), null));
        Assert.NotNull(store.Get("numeric")!.ExpiresAt);
        Assert.Null(store.Set("explicit", data, 60));
        Assert.NotNull(store.Get("explicit")!.ExpiresAt);
        store.Clear();
        store.Dispose(); // saves now instead of 2 s later, so the file can be removed
        File.Delete(path);
    }

    [Theory]
    [InlineData("界", 30000, true)]
    [InlineData("a", 65536, false)]
    [InlineData("a", 65537, true)]
    [InlineData("界", 20000, false)]
    public async Task UnknownLengthRequestLimitCountsBytes(string symbol, int count, bool rejected)
    {
        var text=string.Concat(Enumerable.Repeat(symbol,count));
        var request=new DefaultHttpContext().Request;
        request.Body=new MemoryStream(Encoding.UTF8.GetBytes(text));
        Assert.Null(request.ContentLength);
        var result=await PushStore.ReadBody(request);
        Assert.Equal(rejected, result.Error is not null);
        if (!rejected) Assert.Equal(text, result.Data!.Value.GetString());
    }
}
