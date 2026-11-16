using DvbHeadend.Core;
using DvbHeadend.Model;

namespace DvbHeadend.Web;

public static class StreamEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/stream/channel/{number:int}", (int number, HttpContext http, StateStore state, TunerManager tuners) =>
            StreamAsync(state.FindChannelByNumber(number), http, state, tuners));

        app.MapGet("/stream/service/{key}", (string key, HttpContext http, StateStore state, TunerManager tuners) =>
            StreamAsync(state.FindService(key), http, state, tuners));
    }

    private static async Task StreamAsync(ServiceRecord? service, HttpContext http, StateStore state, TunerManager tuners)
    {
        var log = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Stream");
        var mux = service is null ? null : state.FindMux(service.MuxKey);
        if (service is null || mux is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsync("Unknown channel");
            return;
        }

        var cancel = http.RequestAborted;
        string client = http.Connection.RemoteIpAddress?.ToString() ?? "?";
        var streamer = new ServiceStreamer(service.ServiceId);
        Subscription subscription;
        try
        {
            subscription = await tuners.SubscribeAsync(mux, Priority.Live, $"{service.Name} → {client}", streamer, cancel);
        }
        catch (Exception ex) when (ex is NoTunerAvailableException or TuneFailedException or IOException)
        {
            log.LogWarning("{Client} wanted {Channel}: {Message}", client, service.Name, ex.Message);
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await http.Response.WriteAsync(ex.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using (subscription)
        {
            log.LogInformation("{Client} started {Channel}", client, service.Name);
            try
            {
                // Don't commit to a 200 until the service is really flowing.
                using var firstData = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                firstData.CancelAfter(TimeSpan.FromSeconds(10));
                bool any;
                try
                {
                    any = await streamer.Reader.WaitToReadAsync(firstData.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    any = false;
                }
                if (!any)
                {
                    http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await http.Response.WriteAsync(streamer.EndReason ?? "Tuned, but the service is not present in the mux");
                    return;
                }

                http.Response.ContentType = "video/mp2t";
                http.Response.Headers.CacheControl = "no-cache, no-store";
                await foreach (byte[] chunk in streamer.Reader.ReadAllAsync(cancel))
                {
                    await http.Response.Body.WriteAsync(chunk, cancel);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // client went away mid-write
            }
            finally
            {
                log.LogInformation("{Client} stopped {Channel} ({Mb:0.0} MB, {Dropped} chunks dropped)", client,
                    service.Name, streamer.BytesOut / 1e6, streamer.DroppedChunks);
            }
        }
    }
}
