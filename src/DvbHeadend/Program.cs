using System.Text.Json.Serialization;
using DvbHeadend.Core;
using DvbHeadend.Model;
using DvbHeadend.Tuners;
using DvbHeadend.Web;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Site-specific overrides live outside the install directory so upgrades don't clobber them.
builder.Configuration.AddJsonFile("/etc/dvbheadend/settings.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.Configure<HeadendOptions>(builder.Configuration.GetSection("Headend"));
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSingleton<IEnumerable<ITuner>>(services =>
{
    var options = services.GetRequiredService<IOptions<HeadendOptions>>().Value;
    var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Tuners");
    var tuners = new List<ITuner>();

    foreach (var file in options.FileTuners)
    {
        tuners.Add(new FileTuner(file));
    }
    if (OperatingSystem.IsLinux())
    {
        var adapters = options.Adapters.Count > 0 ? options.Adapters : LinuxDvbTuner.FindAdapters();
        foreach (int adapter in adapters)
        {
            if (!File.Exists($"/dev/dvb/adapter{adapter}/frontend0"))
            {
                log.LogWarning("Configured adapter {Adapter} does not exist under /dev/dvb; skipping it", adapter);
                continue;
            }
            tuners.Add(new LinuxDvbTuner(adapter, log));
        }
    }

    foreach (var tuner in tuners)
    {
        log.LogInformation("Tuner {Id}: {Name}", tuner.Id, tuner.Name);
    }
    if (tuners.Count == 0)
    {
        log.LogWarning("No DVB adapters found under /dev/dvb. Is the tuner plugged in, and is this user in the 'video' group?");
    }
    return tuners;
});
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<EpgStore>();
builder.Services.AddSingleton<TunerManager>();
builder.Services.AddSingleton<Scanner>();
builder.Services.AddSingleton<SignalHistory>();
builder.Services.AddHostedService<SignalSampler>();
builder.Services.AddSingleton<MqttStatus>();
builder.Services.AddSingleton<StatusBuilder>();
builder.Services.AddSingleton<LiveClients>();
builder.Services.AddHostedService<LiveNotifier>();
builder.Services.AddSignalR().AddJsonProtocol(protocol =>
{
    protocol.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    protocol.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddHostedService<MqttPublisher>();
builder.Services.AddHostedService<RecordingService>();
builder.Services.AddHostedService<EpgGrabber>();
builder.Services.AddHostedService<AutoScanService>();

var app = builder.Build();

// Every tuned mux feeds the guide, whatever it was tuned for.
var epg = app.Services.GetRequiredService<EpgStore>();
app.Services.GetRequiredService<TunerManager>().SessionStarted += session => session.Si.EitReceived += epg.Ingest;

app.UseDefaultFiles();
// "no-cache" makes browsers re-check the page, so an upgrade shows up on a normal refresh.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
StreamEndpoints.Map(app);
GuideEndpoints.Map(app);
ApiEndpoints.Map(app);
LogoEndpoints.Map(app);
app.MapHub<LiveHub>("/live");

app.Run();
