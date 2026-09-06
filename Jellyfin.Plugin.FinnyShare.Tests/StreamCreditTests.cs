using Jellyfin.Plugin.FinnyShare;
using Xunit;

namespace Jellyfin.Plugin.FinnyShare.Tests;

/// <summary>
/// Flow control seen from the sending side. Without it the plugin pushes media at disk speed
/// into a relay that can only forward it as fast as one viewer reads: the relay's buffer
/// fills, its shared read loop parks, and every other viewer of this Jellyfin stalls behind
/// the slow one. That was the Swiftfin black screen.
/// </summary>
public class StreamCreditTests
{
    private static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(150);

    [Fact]
    public async Task NothingMayBeSentBeforeTheRelayGrantsRoom()
    {
        var credit = new StreamCredit();

        var send = credit.WaitAsync(1024, CancellationToken.None).AsTask();

        Assert.False(send.IsCompleted, "the plugin would send into a relay that has no room");
        await Task.WhenAny(send, Task.Delay(Brief));
        Assert.False(send.IsCompleted);
    }

    [Fact]
    public async Task AGrantReleasesASenderAlreadyWaiting()
    {
        var credit = new StreamCredit();
        var send = credit.WaitAsync(1024, CancellationToken.None).AsTask();

        credit.Grant(1024);

        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task GrantsAccumulateSoAViewerReadingSlowlyStillMakesProgress()
    {
        // The relay replenishes one written chunk at a time. A sender waiting for a chunk
        // larger than any single grant must add them up, not wait forever for one big one.
        var credit = new StreamCredit();
        var send = credit.WaitAsync(3000, CancellationToken.None).AsTask();

        credit.Grant(1000);
        credit.Grant(1000);
        await Task.WhenAny(send, Task.Delay(Brief));
        Assert.False(send.IsCompleted, "2000 bytes of room is not enough for 3000");

        credit.Grant(1000);
        Assert.True(await send.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CreditIsConsumedSoTheWindowIsNotSpentTwice()
    {
        var credit = new StreamCredit();
        credit.Grant(1024);

        Assert.True(await credit.WaitAsync(1024, CancellationToken.None));

        var second = credit.WaitAsync(1024, CancellationToken.None).AsTask();
        await Task.WhenAny(second, Task.Delay(Brief));
        Assert.False(second.IsCompleted, "the same grant was spent twice");
    }

    [Fact]
    public async Task AWaitingSenderIsReleasedWhenItsStreamIsCancelled()
    {
        // A viewer who leaves stops being granted credit, so without this the response task
        // waits forever holding a Jellyfin connection open behind it.
        var credit = new StreamCredit();
        using var cts = new CancellationTokenSource();

        var send = credit.WaitAsync(1024, cts.Token).AsTask();
        cts.Cancel();

        Assert.False(await send.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
