using DvbHeadend.Core;
using DvbHeadend.Model;
using Microsoft.Extensions.Options;

namespace DvbHeadend.Web;

/// <summary>Channel logos supplied by the user: one per channel, or a batch matched by file name.</summary>
public static class LogoEndpoints
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private static string LogoDir(HeadendOptions options) => Path.Combine(options.DataDir, "logos");

    /// <summary>Returns the extension for a supported image, judged by its content rather than its name.</summary>
    internal static string? DetectImage(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return ".png";
        }
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ".jpg";
        }
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ".webp";
        }
        if (data.Length >= 6 && data[..4].SequenceEqual("GIF8"u8))
        {
            return ".gif";
        }
        return null;
    }

    internal static string Normalise(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static async Task<byte[]?> ReadAsync(IFormFile file)
    {
        if (file.Length == 0 || file.Length > MaxBytes)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static void Store(StateStore state, HeadendOptions options, ServiceRecord service, byte[] data, string extension)
    {
        string dir = LogoDir(options);
        Directory.CreateDirectory(dir);
        Remove(options, service);
        // The timestamp makes a replaced logo a new URL, so browsers never show a stale one.
        string fileName = $"{service.Key}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}{extension}";
        File.WriteAllBytes(Path.Combine(dir, fileName), data);
        state.Decorate(service.Key, s => s.LogoFile = fileName);
    }

    private static void Remove(HeadendOptions options, ServiceRecord service)
    {
        if (service.LogoFile is null)
        {
            return;
        }
        string path = Path.Combine(LogoDir(options), Path.GetFileName(service.LogoFile));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/logos/{file}", (string file, HttpContext http, IOptions<HeadendOptions> options) =>
        {
            if (file != Path.GetFileName(file))
            {
                return Results.NotFound();
            }
            string path = Path.Combine(LogoDir(options.Value), file);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }
            string type = Path.GetExtension(file) switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif" => "image/gif",
                _ => "application/octet-stream"
            };
            http.Response.Headers.CacheControl = "public, max-age=604800, immutable";
            return Results.File(path, type);
        });

        app.MapPost("/api/channels/{key}/logo", async (string key, HttpRequest request, StateStore state,
            IOptions<HeadendOptions> options) =>
        {
            var service = state.FindService(key);
            if (service is null)
            {
                return Results.NotFound();
            }
            var form = await request.ReadFormAsync();
            byte[]? data = form.Files.Count == 1 ? await ReadAsync(form.Files[0]) : null;
            string? extension = data is null ? null : DetectImage(data);
            if (data is null || extension is null)
            {
                return Results.BadRequest("Send one PNG, JPEG, WebP or GIF image of up to 2 MB");
            }
            Store(state, options.Value, service, data, extension);
            return Results.NoContent();
        });

        app.MapDelete("/api/channels/{key}/logo", (string key, StateStore state, IOptions<HeadendOptions> options) =>
        {
            var service = state.FindService(key);
            if (service is null)
            {
                return Results.NotFound();
            }
            Remove(options.Value, service);
            state.Decorate(key, s => s.LogoFile = null);
            return Results.NoContent();
        });

        // Batch upload: "1.png" matches channel number 1, "BBC ONE Scot.png" matches by name.
        app.MapPost("/api/logos", async (HttpRequest request, StateStore state, IOptions<HeadendOptions> options) =>
        {
            var form = await request.ReadFormAsync();
            var services = state.GetServices();
            var matched = new List<string>();
            var unmatched = new List<string>();
            foreach (var file in form.Files)
            {
                string stem = Path.GetFileNameWithoutExtension(file.FileName);
                var service = int.TryParse(stem, out int number)
                    ? services.FirstOrDefault(s => s.Enabled && s.Number == number)
                    : services.FirstOrDefault(s => Normalise(s.Name) == Normalise(stem));
                byte[]? data = service is null ? null : await ReadAsync(file);
                string? extension = data is null ? null : DetectImage(data);
                if (service is null || data is null || extension is null)
                {
                    unmatched.Add(file.FileName);
                    continue;
                }
                Store(state, options.Value, service, data, extension);
                matched.Add($"{file.FileName} → {service.Number} {service.Name}");
            }
            return Results.Ok(new { matched, unmatched });
        });
    }
}
