using DvbHeadend.Core;
using DvbHeadend.Model;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Web;

public sealed record ChannelUpdate(string? Name, int? Number, bool? Enabled, bool? Favourite);

public sealed record RecordingRequest(string ServiceKey, int? EventId, DateTime? StartUtc, DateTime? StopUtc, string? Title);

public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/status", (StatusBuilder status) => status.Build(includeTime: true));

        // ---- muxes ----
        api.MapGet("/muxes", (StateStore state) => state.GetMuxes().Select(m => new
        {
            key = m.Key,
            m.Name,
            m.System,
            m.FrequencyHz,
            m.BandwidthHz,
            m.PlpId,
            services = state.GetServices().Count(s => s.MuxKey == m.Key)
        }));

        api.MapPost("/muxes", (MuxDefinition mux, StateStore state) =>
        {
            if (mux.FrequencyHz < 40_000_000 || mux.FrequencyHz > 900_000_000)
            {
                return Results.BadRequest("frequencyHz must be in Hz, e.g. 674000000");
            }
            if (string.IsNullOrWhiteSpace(mux.Name))
            {
                mux = mux with { Name = $"{mux.FrequencyHz / 1e6:0.###} MHz" };
            }
            return state.AddMux(mux) ? Results.Ok(mux) : Results.Conflict("That mux already exists");
        });

        api.MapDelete("/muxes/{key}", (string key, StateStore state) =>
            state.RemoveMux(key) ? Results.NoContent() : Results.NotFound());

        // ---- scan ----
        api.MapPost("/scan", (Scanner scanner) =>
            scanner.Start() ? Results.Accepted() : Results.Conflict("A scan is already running"));

        api.MapGet("/scan", (Scanner scanner) => scanner.Status);

        // ---- channels ----
        api.MapGet("/channels", (StateStore state, EpgStore epg, bool? all) =>
            (all == true ? state.GetServices() : state.GetChannels()).Select(s =>
            {
                var (now, next) = epg.NowNext(s.Key);
                return new
                {
                    s.Key,
                    s.Number,
                    s.Name,
                    s.Kind,
                    s.Hd,
                    s.Enabled,
                    s.Scrambled,
                    s.Lcn,
                    s.MuxName,
                    s.Provider,
                    s.ServiceId,
                    s.Streams,
                    s.Favourite,
                    logo = s.LogoFile is null ? null : "/logos/" + Uri.EscapeDataString(s.LogoFile),
                    now,
                    next
                };
            }));

        api.MapPut("/channels/{key}", (string key, ChannelUpdate update, StateStore state) =>
        {
            if (update.Favourite.HasValue && !state.Decorate(key, s => s.Favourite = update.Favourite.Value))
            {
                return Results.NotFound();
            }
            if (update.Name is null && !update.Number.HasValue && !update.Enabled.HasValue)
            {
                return Results.NoContent();
            }
            bool found = state.UpdateService(key, s =>
            {
                if (!string.IsNullOrWhiteSpace(update.Name))
                {
                    s.Name = update.Name.Trim();
                }
                if (update.Number.HasValue)
                {
                    s.Number = update.Number.Value;
                }
                if (update.Enabled.HasValue)
                {
                    s.Enabled = update.Enabled.Value;
                }
            });
            return found ? Results.NoContent() : Results.NotFound();
        });

        // ---- guide ----
        api.MapGet("/guide", (StateStore state, EpgStore epg, DateTime? from, int? hours) =>
        {
            var start = (from ?? DateTime.UtcNow).ToUniversalTime();
            var end = start.AddHours(Math.Clamp(hours ?? 6, 1, 24));
            return new
            {
                fromUtc = start,
                toUtc = end,
                channels = state.GetServices().Select(s => new
                {
                    s.Key,
                    events = epg.GetEvents(s.Key, start, end).Select(e => new
                    {
                        e.EventId,
                        e.StartUtc,
                        e.DurationSeconds,
                        e.Title,
                        e.Description,
                        e.Genre
                    })
                }).Where(c => c.events.Any())
            };
        });

        api.MapGet("/signal", (SignalHistory history, StateStore state, int? hours) =>
            history.Query(state.GetMuxes(), TimeSpan.FromHours(Math.Clamp(hours ?? 24, 1, 168))));

        api.MapGet("/epg/{key}", (string key, EpgStore epg, int? hours) =>
            epg.GetEvents(key, DateTime.UtcNow, DateTime.UtcNow.AddHours(Math.Clamp(hours ?? 24, 1, 24 * 9))));

        // ---- recordings ----
        api.MapGet("/recordings", (StateStore state) => state.GetRecordings().OrderByDescending(r => r.StartUtc));

        api.MapPost("/recordings", (RecordingRequest request, StateStore state, EpgStore epg) =>
        {
            var service = state.FindService(request.ServiceKey);
            if (service is null)
            {
                return Results.NotFound("Unknown channel");
            }

            var record = new RecordingRecord { ServiceKey = service.Key, ChannelName = service.Name };
            if (request.EventId.HasValue)
            {
                var e = epg.Find(service.Key, request.EventId.Value);
                if (e is null)
                {
                    return Results.NotFound("Unknown guide entry");
                }
                record.Title = e.Title;
                record.Description = e.Description;
                record.StartUtc = e.StartUtc;
                record.StopUtc = e.EndUtc;
            }
            else if (request.StartUtc.HasValue && request.StopUtc.HasValue && request.StopUtc > request.StartUtc)
            {
                record.Title = string.IsNullOrWhiteSpace(request.Title) ? service.Name : request.Title.Trim();
                record.StartUtc = request.StartUtc.Value.ToUniversalTime();
                record.StopUtc = request.StopUtc.Value.ToUniversalTime();
            }
            else
            {
                return Results.BadRequest("Give either eventId, or startUtc and stopUtc");
            }

            if (record.StopUtc < DateTime.UtcNow)
            {
                return Results.BadRequest("That programme has already finished");
            }
            state.AddRecording(record);
            return Results.Ok(record);
        });

        api.MapDelete("/recordings/{id}", (string id, StateStore state, bool? deleteFile) =>
        {
            var record = state.FindRecording(id);
            if (record is null)
            {
                return Results.NotFound();
            }
            if (record.Status == RecordingStatus.Recording && deleteFile != true)
            {
                // Stop it but keep what was captured.
                state.UpdateRecording(record, r => r.Status = RecordingStatus.Cancelled);
                return Results.NoContent();
            }
            state.RemoveRecording(id);
            if (deleteFile == true && record.FilePath is not null && File.Exists(record.FilePath))
            {
                try
                {
                    File.Delete(record.FilePath);
                }
                catch (IOException)
                {
                    // still being written; the recorder closes it within a couple of seconds
                }
            }
            return Results.NoContent();
        });

        app.MapGet("/recordings/{id}/file", (string id, StateStore state) =>
        {
            var record = state.FindRecording(id);
            if (record?.FilePath is null || !File.Exists(record.FilePath))
            {
                return Results.NotFound();
            }
            var stream = new FileStream(record.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Results.File(stream, "video/mp2t", Path.GetFileName(record.FilePath), enableRangeProcessing: true);
        });
    }
}
