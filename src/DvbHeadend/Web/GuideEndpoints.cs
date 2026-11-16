using System.Security.Cryptography;
using System.Text;
using System.Xml;
using DvbHeadend.Core;
using DvbHeadend.Model;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Web;

/// <summary>
/// What media servers consume: an M3U playlist, an XMLTV guide, and an
/// HDHomeRun-compatible HTTP API so Jellyfin, Plex and Emby see a network tuner.
/// </summary>
public static class GuideEndpoints
{
    private static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";

    private static string DeviceId(HeadendOptions options)
    {
        string seed = Environment.MachineName + options.FriendlyName;
        try
        {
            seed += File.ReadAllText("/etc/machine-id");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(seed)), 0, 4);
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/playlist.m3u", (HttpContext http, StateStore state, string? kind, bool? favourites) =>
        {
            string baseUrl = BaseUrl(http);
            var sb = new StringBuilder();
            sb.Append("#EXTM3U url-tvg=\"").Append(baseUrl).Append("/xmltv.xml\"\n");
            foreach (var channel in state.GetChannels().Where(c => (kind is null || c.Kind == kind) && (favourites != true || c.Favourite)))
            {
                string name = channel.Name.Replace(",", " ").Replace("\"", "'");
                sb.Append($"#EXTINF:-1 tvg-id=\"{channel.Key}\" tvg-chno=\"{channel.Number}\" tvg-name=\"{name}\"");
                if (channel.LogoFile is not null)
                {
                    sb.Append($" tvg-logo=\"{baseUrl}/logos/{Uri.EscapeDataString(channel.LogoFile)}\"");
                }
                sb.Append($" group-title=\"{(channel.Kind == "radio" ? "Radio" : "TV")}\"");
                if (channel.Kind == "radio")
                {
                    sb.Append(" radio=\"true\"");
                }
                sb.Append(',').Append(name).Append('\n');
                sb.Append(baseUrl).Append("/stream/channel/").Append(channel.Number).Append('\n');
            }
            return Results.Text(sb.ToString(), "audio/x-mpegurl", Encoding.UTF8);
        });

        app.MapGet("/play/{key}", (string key, HttpContext http, StateStore state) =>
        {
            var service = state.FindService(key);
            if (service is null)
            {
                return Results.NotFound();
            }
            string name = service.Name.Replace(",", " ");
            string playlist = $"#EXTM3U\n#EXTINF:-1,{name}\n{BaseUrl(http)}/stream/service/{service.Key}\n";
            string fileName = string.Concat(service.Name.Select(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' ? c : '_')).Trim();
            return Results.File(Encoding.UTF8.GetBytes(playlist), "audio/x-mpegurl", $"{fileName}.m3u");
        });

        app.MapGet("/xmltv.xml", (HttpContext http, StateStore state, EpgStore epg) =>
        {
            using var buffer = new MemoryStream();
            var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
            using (var xml = XmlWriter.Create(buffer, settings))
            {
                xml.WriteStartElement("tv");
                xml.WriteAttributeString("generator-info-name", "dvbheadend");
                var channels = state.GetChannels();
                foreach (var channel in channels)
                {
                    xml.WriteStartElement("channel");
                    xml.WriteAttributeString("id", channel.Key);
                    xml.WriteElementString("display-name", channel.Name);
                    xml.WriteElementString("display-name", channel.Number.ToString());
                    xml.WriteElementString("lcn", channel.Number.ToString());
                    if (channel.LogoFile is not null)
                    {
                        xml.WriteStartElement("icon");
                        xml.WriteAttributeString("src", $"{BaseUrl(http)}/logos/{Uri.EscapeDataString(channel.LogoFile)}");
                        xml.WriteEndElement();
                    }
                    xml.WriteEndElement();
                }
                var from = DateTime.UtcNow.AddHours(-1);
                var to = DateTime.UtcNow.AddDays(9);
                foreach (var channel in channels)
                {
                    foreach (var e in epg.GetEvents(channel.Key, from, to))
                    {
                        xml.WriteStartElement("programme");
                        xml.WriteAttributeString("start", e.StartUtc.ToString("yyyyMMddHHmmss") + " +0000");
                        xml.WriteAttributeString("stop", e.EndUtc.ToString("yyyyMMddHHmmss") + " +0000");
                        xml.WriteAttributeString("channel", channel.Key);
                        xml.WriteStartElement("title");
                        xml.WriteAttributeString("lang", "en");
                        xml.WriteString(Sanitize(e.Title));
                        xml.WriteEndElement();
                        if (e.Description.Length > 0)
                        {
                            xml.WriteStartElement("desc");
                            xml.WriteAttributeString("lang", "en");
                            xml.WriteString(Sanitize(e.Description));
                            xml.WriteEndElement();
                        }
                        if (e.Genre is not null)
                        {
                            xml.WriteElementString("category", e.Genre);
                        }
                        xml.WriteEndElement();
                    }
                }
                xml.WriteEndElement();
            }
            return Results.Bytes(buffer.ToArray(), "application/xml; charset=utf-8");
        });

        // ---- HDHomeRun emulation ----

        app.MapGet("/discover.json", (HttpContext http, IOptions<HeadendOptions> options, TunerManager tuners) => Results.Json(new
        {
            FriendlyName = options.Value.FriendlyName,
            Manufacturer = "Silicondust",
            ModelNumber = "HDHR3-EU",
            FirmwareName = "hdhomerun3_dvbt",
            FirmwareVersion = "20240101",
            DeviceID = DeviceId(options.Value),
            DeviceAuth = "dvbheadend",
            BaseURL = BaseUrl(http),
            LineupURL = BaseUrl(http) + "/lineup.json",
            TunerCount = Math.Max(1, tuners.TunerCount)
        }, HdhrJson));

        app.MapGet("/lineup_status.json", (Scanner scanner) => Results.Json(new
        {
            ScanInProgress = scanner.Status.Running ? 1 : 0,
            ScanPossible = 1,
            Source = "Antenna",
            SourceList = new[] { "Antenna" }
        }, HdhrJson));

        app.MapGet("/lineup.json", (HttpContext http, StateStore state) => Results.Json(
            state.GetChannels().Select(c => new
            {
                GuideNumber = c.Number.ToString(),
                GuideName = c.Name,
                VideoCodec = c.Streams.FirstOrDefault(s => s.Kind == "video")?.Codec switch
                {
                    "h264" => "H264",
                    "hevc" => "HEVC",
                    "mpeg2video" => "MPEG2",
                    _ => null
                },
                HD = c.Hd ? 1 : 0,
                URL = $"{BaseUrl(http)}/stream/channel/{c.Number}"
            }), HdhrJson));

        app.MapMethods("/lineup.post", ["GET", "POST"], (Scanner scanner, string? scan) =>
        {
            if (scan == "start")
            {
                scanner.Start();
            }
            return Results.Ok();
        });

        app.MapGet("/device.xml", (HttpContext http, IOptions<HeadendOptions> options) =>
        {
            string id = DeviceId(options.Value);
            string xml = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <root xmlns="urn:schemas-upnp-org:device-1-0">
                  <specVersion><major>1</major><minor>0</minor></specVersion>
                  <URLBase>{BaseUrl(http)}</URLBase>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:MediaServer:1</deviceType>
                    <friendlyName>{System.Security.SecurityElement.Escape(options.Value.FriendlyName)}</friendlyName>
                    <manufacturer>Silicondust</manufacturer>
                    <modelName>HDHR3-EU</modelName>
                    <modelNumber>HDHR3-EU</modelNumber>
                    <serialNumber>{id}</serialNumber>
                    <UDN>uuid:{id}-dvbheadend</UDN>
                  </device>
                </root>
                """;
            return Results.Text(xml, "application/xml", Encoding.UTF8);
        });
    }

    private static readonly System.Text.Json.JsonSerializerOptions HdhrJson = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string Sanitize(string text)
    {
        // XML 1.0 forbids most control characters outright.
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n' || c == '\t' || !char.IsControl(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
