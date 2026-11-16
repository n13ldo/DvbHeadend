using Microsoft.AspNetCore.SignalR;

namespace DvbHeadend.Web;

/// <summary>What the server pushes to connected clients (browser or .NET) over SignalR at /live.</summary>
public interface ILiveClient
{
    /// <summary>The same object as GET /api/status, sent whenever it changes.</summary>
    Task Status(object status);

    /// <summary>Channels whose current or next programme changed.</summary>
    Task NowNext(IReadOnlyList<NowNextUpdate> changes);

    /// <summary>Guide data changed for these channel keys; re-read whatever part of the guide is on screen.</summary>
    Task GuideChanged(IReadOnlyList<string> channelKeys);

    /// <summary>The channel list changed (scan, rename, favourite, logo, hide); re-read /api/channels.</summary>
    Task ChannelsChanged();

    /// <summary>A recording was added, started, finished or removed; re-read /api/recordings.</summary>
    Task RecordingsChanged();
}

public sealed record NowNextUpdate(string Key, Model.EpgEvent? Now, Model.EpgEvent? Next);

/// <summary>Counts connected clients so nothing is computed while nobody is listening.</summary>
public sealed class LiveClients
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    internal void Joined() => Interlocked.Increment(ref _count);

    internal void Left() => Interlocked.Decrement(ref _count);
}

public sealed class LiveHub(LiveClients clients) : Hub<ILiveClient>
{
    public override Task OnConnectedAsync()
    {
        clients.Joined();
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        clients.Left();
        return base.OnDisconnectedAsync(exception);
    }
}
