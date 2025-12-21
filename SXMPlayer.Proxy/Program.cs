using Microsoft.AspNetCore.Server.Kestrel.Core;
using Blazorise;
using Blazorise.Bootstrap5;
using Blazorise.Icons.FontAwesome;
using Polly;
using SXMPlayer;
using SXMPlayer.Proxy.Services;
using System;

//dotnet run --urls="https://localhost:7777"
var builder = WebApplication.CreateBuilder(args);

// Read password from file specified by environment variable and override configuration
var passwordFile = Environment.GetEnvironmentVariable("SXM_PASSWORD_FILE");
if (!string.IsNullOrWhiteSpace(passwordFile) && File.Exists(passwordFile))
{
    try
    {
        var password = File.ReadAllText(passwordFile).Trim();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SXM:Password"] = password
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to read SXM_PASSWORD_FILE: {ex.Message}");
        //exit with error code
        Environment.Exit(1);
    }
}
//builder.Logging.AddFile(hostingContext.Configuration.GetSection("Logging"));
builder.WebHost.CaptureStartupErrors(false);
// Configure Kestrel timeouts
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(30);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(30);
    serverOptions.Limits.MinResponseDataRate = null;
    serverOptions.AllowSynchronousIO = false;
    serverOptions.Limits.MaxRequestBodySize = null; // No limit
    // Force HTTP/1.1 for better compatibility with some streaming clients (e.g., MPD/libcurl)
    serverOptions.ConfigureEndpointDefaults(lo => lo.Protocols = HttpProtocols.Http1);
});

builder.Host.UseSystemd();
// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SiriusXMPlayer>();
builder.Services.AddHostedService<LibraryM3UExporter>();
//builder.Services.AddControllers();
builder.Services
    .AddBlazorise(options =>
    {
        options.Immediate = true;
    })
    .AddBootstrap5Providers()
    .AddFontAwesomeIcons();
var app = builder.Build();
var logger = app.Services.GetRequiredService<ILogger<SiriusXMPlayer>>();
var sxm = app.Services.GetRequiredService<SiriusXMPlayer>();
// Configure the HTTP request pipeline.
var configuration = builder.Configuration;

app.MapGet("/nowplaying", () => sxm.GetNowPlaying() is null ? Results.NoContent() : TypedResults.Ok(sxm.GetNowPlaying()));

app.MapGet("/playlists/{id}.{ext?}", async (HttpContext ctx, string id, string? ext) =>
{
    logger.LogDebug($"Received request for {ctx.Request.Path}");
    try
    {
        var ipAddress = ctx.Connection.RemoteIpAddress;
        if (ext == "m3u8")
        {
            var client = sxm.TrackListenerIP(ipAddress);
            ctx.Response.ContentType = "application/x-mpegURL";
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            //#EXTINF:10,
            var playlistContent = await sxm.GetStreamPlaylist(id, client);
            if (playlistContent is null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            //ICYMetadataHandler.HandleICYHeaders(ctx, logger, sxm);
            var data = System.Text.Encoding.UTF8.GetBytes(playlistContent);
            ctx.Response.ContentLength = data.Length;
            await ctx.Response.Body.WriteAsync(data);
        }
        else if (ext == "m3u")
        {
            var channelName = id == SiriusXMPlayer.CURRENT_ID ? "Current" : sxm.GetChannelFromFilename(id)?.ChannelName;
            var channelId = id == SiriusXMPlayer.CURRENT_ID ? SiriusXMPlayer.CURRENT_ID : sxm.GetChannelFromFilename(id)?.Id!;
            _ = sxm.TrackListenerIP(ipAddress);
            var playlist = ctx.Request.Path.Value;
            var serverUrl = (ctx.Request.IsHttps ? "https" : "http") + "://" + ctx.Request.Host.ToString();
            ctx.Response.ContentType = "application/x-mpegURL";
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            string content = Tools.GeneratePlaylistContent(channelId, channelName ?? "-", serverUrl);
            //ICYMetadataHandler.HandleICYHeaders(ctx, logger, sxm);
            var data = System.Text.Encoding.UTF8.GetBytes(content);
            ctx.Response.ContentLength = data.Length;
            await ctx.Response.Body.WriteAsync(data);

        }
        else
        {
            ctx.Response.StatusCode = 404;
        }
        await ctx.Response.Body.FlushAsync();
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, $"Cannot serve request {ctx.Request.Path}");
        ctx.Response.StatusCode = 500;
    }

});

// /stream/v1/<token>/pri-1/AAC_Data/purejazz/purejazz_256k_1_122457514264_00114279_v3.aac
app.MapGet("stream/{channel:regex(.*)}/{version:regex(.*)}/{segmentId:regex(.*\\.aac)}",
    async (string channel, string version, string segmentId, HttpContext ctx) =>
    {
        var ipAddress = ctx.Connection.RemoteIpAddress;

        var fullPath = ctx.Request.Path.Value.Substring(1);
        logger.LogTrace($"Received request for {ctx.Request.Path}");
        try
        {
            ctx.Response.ContentType = "audio/x-aac";
            ctx.Response.Headers.Append("Cache-Control", "no-cache");
            var client = sxm.TrackListenerIP(ipAddress);
            using var segmentStream = await sxm.GetSegment(channel, version, segmentId, client);
            ctx.Response.ContentLength = segmentStream.Length;
            await segmentStream.CopyToAsync(ctx.Response.Body);
            await ctx.Response.Body.FlushAsync();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, $"Cannot serve segment {segmentId}");
            ctx.Response.StatusCode = 500;
        }
    });
app.MapGet("/key/{guid:regex(.*)}",
    async (string guid, HttpContext ctx) =>
    {
        logger.LogDebug($"Received request for Key {ctx.Request.Path}");
        ctx.Response.ContentType = "text/plain";
        ctx.Response.Headers.Append("Cache-Control", "no-cache");
        var key = await sxm.GetDecryptionKey(guid);
        ctx.Response.ContentLength = key.Length;
        await ctx.Response.Body.WriteAsync(key);
        await ctx.Response.Body.FlushAsync();

    });

// cover image
app.MapGet("/icecast/cover.jpg", async (HttpContext ctx, CancellationToken ct) =>
{
    var channelImage = await sxm.GetCurrentChannelImage();
    if (channelImage is null)
    {
        ctx.Response.StatusCode = 404;
        return;
    }

    try
    {
        using var http = new System.Net.Http.HttpClient();
        using var res = await http.GetAsync(channelImage, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Response.StatusCode = (int)res.StatusCode;
            return;
        }
        var contentBytes = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var contentType = res.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        // Allow client/proxies to cache the image for a short duration.
        ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength = contentBytes.Length;
        await ctx.Response.Body.WriteAsync(contentBytes, 0, contentBytes.Length, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to fetch channel image");
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 502;
        }
    }
});

// Icecast-like continuous AAC stream with optional ICY metadata (progressive)
app.MapGet("/icecast/{id}", async (HttpContext ctx, string id, CancellationToken ct) =>
{
    try
    {
        //cover.xxx requests are blocked
        if (id.EndsWith(".png") || id.EndsWith(".webp"))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        logger.LogInformation($"Received icecast request for channel '{id}' - {ctx.Request.Path} - {ctx.Connection.RemoteIpAddress}");

        var channelId = id == "current" ? SiriusXMPlayer.CURRENT_ID : id;

        await sxm.StreamIcecastAsync(channelId, ctx, ct);
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, $"Cannot serve icecast stream for {id}");
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 500;
        }
    }
});

// Respond to HEAD requests for /icecast/{id} to satisfy clients probing the stream
app.MapMethods("/icecast/{id}", new[] { "HEAD" }, (HttpContext ctx, string id) =>
{
    //cover.xxx requests are blocked
    if (id.EndsWith(".png") || id.EndsWith(".webp"))
    {
        return Results.NotFound();
    }
    // Mirror headers set by GET handler without sending a body
    var userAgent = ctx.Request.Headers["User-Agent"].ToString();
    bool injectMeta = ctx.Request.Headers.TryGetValue("Icy-MetaData", out var metaReq) && string.Equals(metaReq, "1", StringComparison.Ordinal);
    if (!injectMeta && !string.IsNullOrEmpty(userAgent) && userAgent.Contains("VLC", StringComparison.OrdinalIgnoreCase))
    {
        injectMeta = true;
    }
    if (injectMeta)
    {
        ctx.Response.Headers["icy-metaint"] = (sxm.ICYMetaInt ?? SiriusXMPlayer.ICY_META_BLOCK).ToString();
        ctx.Response.ContentType = "audio/aacp";
    }
    else
    {
        ctx.Response.ContentType = "audio/aac";
    }
    ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    ctx.Response.Headers["Pragma"] = "no-cache";
    ctx.Response.Headers["Expires"] = "0";
    ctx.Response.Headers["Accept-Ranges"] = "none";
    ctx.Response.Headers["Connection"] = "keep-alive";
    return Results.Ok();
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
//app.Run("http://localhost:3000");
