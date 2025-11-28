// File: MediaInfoGrabber.cs
// Target: .NET 8 (Windows). Requires Windows SDK for Windows.Media.Control (Microsoft.Windows.SDK.Contracts)

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MediaInfoGrabber
{
    internal static class App
    {
        public static async Task Main(string[] args)
        {
            bool compactMode = false;
            bool portableMode = false;
            int port = 8080;
            string? appFilter = null;
            
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--compact":
                        compactMode = true;
                        break;
                    case "--portable":
                        portableMode = true;
                        break;
                    case "--port":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedPort))
                        {
                            port = parsedPort;
                            i++; // Skip the next argument since we consumed it
                        }
                        break;
                    case "--app":
                        if (i + 1 < args.Length)
                        {
                            appFilter = args[i + 1];
                            i++; // Skip the next argument since we consumed it
                        }
                        break;
                }
            }
            
            await new MediaInfoGrabber(compactMode, portableMode, port, appFilter).RunAsync();
        }
    }

    static class AtomicIO
    {
        public static void WriteText(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        public static void WriteBytes(string path, byte[] bytes)
        {
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        public static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        public static void EnsureFile(string path, string content)
        {
            if (!File.Exists(path))
                WriteText(path, content);
        }
    }

    class MediaInfoGrabber
    {
        readonly string Root = AppContext.BaseDirectory;
        readonly string JsonPath;
        readonly string CoverPath;
        readonly bool CompactMode;
        readonly bool PortableMode;
        readonly int Port;
        readonly string? AppFilter;

        GlobalSystemMediaTransportControlsSessionManager? mgr;
        GlobalSystemMediaTransportControlsSession? cur;

        readonly Mutex single;
        readonly FilterConfig filters;
        
        // Portable mode fields
        HttpListener? webServer;
        string? currentJsonData;
        byte[]? currentCoverData;
        static readonly HttpClient httpClient = new HttpClient();

        public MediaInfoGrabber(bool compactMode = false, bool portableMode = false, int port = 8080, string? appFilter = null)
        {
            CompactMode = compactMode;
            PortableMode = portableMode;
            Port = port;
            AppFilter = appFilter;
            JsonPath  = Path.Combine(Root, "nowplaying.json");
            CoverPath = Path.Combine(Root, "cover.jpg");

            var key = ("MediaInfoGrabber-" + Path.GetFullPath(Root).ToLowerInvariant()).GetHashCode();
            single = new Mutex(false, $@"Global\{key}");
            if (!single.WaitOne(0)) Environment.Exit(0);
            AppDomain.CurrentDomain.ProcessExit += (_, __) => 
            { 
                try 
                { 
                    webServer?.Stop();
                    webServer?.Close();
                    single.ReleaseMutex(); 
                } 
                catch { } 
            };

            if (!PortableMode)
            {
                AtomicIO.WriteText(JsonPath, @"{""status"":""Starting""}");
            }
            else
            {
                currentJsonData = @"{""status"":""Starting""}"; 
            }

            filters = new FilterConfig(Root, AppFilter);

            if (!PortableMode)
            {
                if (CompactMode)
                {
                    AtomicIO.EnsureFile(Path.Combine(Root, "overlay.html"), OverlayAssets.CompactHtml);
                }
                else
                {
                    AtomicIO.EnsureFile(Path.Combine(Root, "overlay.html"), OverlayAssets.Html);
                    AtomicIO.EnsureFile(Path.Combine(Root, "overlay.css"),  OverlayAssets.Css);
                    AtomicIO.EnsureFile(Path.Combine(Root, "overlay.js"),   OverlayAssets.Js);
                }
                
                AtomicIO.EnsureFile(Path.Combine(Root, "usage.md"), OverlayAssets.UsageMd);
            }
        }

        public async Task RunAsync()
        {
            if (PortableMode)
            {
                StartWebServer();
                Console.WriteLine($"Portable mode: Web server started on http://localhost:{Port}");
                Console.WriteLine($"Access overlay at: http://localhost:{Port}/overlay.html");
            }
            
            mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            mgr.CurrentSessionChanged += (_, __) => OnSessionChanged();
            OnSessionChanged();
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        
        void StartWebServer()
        {
            webServer = new HttpListener();
            webServer.Prefixes.Add($"http://localhost:{Port}/");
            webServer.Start();
            
            Task.Run(async () =>
            {
                while (webServer.IsListening)
                {
                    try
                    {
                        var context = await webServer.GetContextAsync();
                        _ = Task.Run(async () => await HandleRequest(context));
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (HttpListenerException)
                    {
                        break;
                    }
                }
            });
        }
        
        async Task HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            
            // Add CORS headers to allow cross-origin requests (needed for MusicBrainz API)
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");
            
            // Handle preflight OPTIONS requests
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Close();
                return;
            }
            
            try
            {
                var path = request.Url?.AbsolutePath?.TrimStart('/') ?? "";
                
                switch (path.ToLowerInvariant())
                {
                    case "":
                    case "overlay.html":
                        response.ContentType = "text/html; charset=utf-8";
                        var html = CompactMode ? OverlayAssets.CompactHtml : OverlayAssets.Html;
                        var htmlBytes = Encoding.UTF8.GetBytes(html);
                        response.ContentLength64 = htmlBytes.Length;
                        response.OutputStream.Write(htmlBytes, 0, htmlBytes.Length);
                        break;
                        
                    case "overlay.css":
                        if (!CompactMode)
                        {
                            response.ContentType = "text/css; charset=utf-8";
                            var cssBytes = Encoding.UTF8.GetBytes(OverlayAssets.Css);
                            response.ContentLength64 = cssBytes.Length;
                            response.OutputStream.Write(cssBytes, 0, cssBytes.Length);
                        }
                        else
                        {
                            response.StatusCode = 404;
                        }
                        break;
                        
                    case "overlay.js":
                        if (!CompactMode)
                        {
                            response.ContentType = "application/javascript; charset=utf-8";
                            var jsBytes = Encoding.UTF8.GetBytes(OverlayAssets.Js);
                            response.ContentLength64 = jsBytes.Length;
                            response.OutputStream.Write(jsBytes, 0, jsBytes.Length);
                        }
                        else
                        {
                            response.StatusCode = 404;
                        }
                        break;
                        
                    case "nowplaying.json":
                        response.ContentType = "application/json; charset=utf-8";
                        response.Headers.Add("Cache-Control", "no-store");
                        // Additional CORS headers for JSON endpoint
                        response.Headers.Add("Access-Control-Allow-Credentials", "true");
                        var jsonData = currentJsonData ?? @"{""status"":""Stopped""}";
                        var jsonBytes = Encoding.UTF8.GetBytes(jsonData);
                        response.ContentLength64 = jsonBytes.Length;
                        response.OutputStream.Write(jsonBytes, 0, jsonBytes.Length);
                        break;
                        
                    case "cover.jpg":
                        if (currentCoverData != null)
                        {
                            response.ContentType = "image/jpeg";
                            response.ContentLength64 = currentCoverData.Length;
                            response.OutputStream.Write(currentCoverData, 0, currentCoverData.Length);
                        }
                        else
                        {
                            response.StatusCode = 404;
                        }
                        break;
                        
                    case "usage.md":
                        response.ContentType = "text/markdown; charset=utf-8";
                        var usageBytes = Encoding.UTF8.GetBytes(OverlayAssets.UsageMd);
                        response.ContentLength64 = usageBytes.Length;
                        response.OutputStream.Write(usageBytes, 0, usageBytes.Length);
                        break;
                        
                    default:
                        // Check if it's a MusicBrainz proxy request
                        if (path.StartsWith("api/musicbrainz/"))
                        {
                            if (path == "api/musicbrainz/test")
                            {
                                // Simple test endpoint for proxy detection
                                response.StatusCode = 200;
                                response.ContentType = "application/json; charset=utf-8";
                                var testBytes = Encoding.UTF8.GetBytes("{\"proxy\":\"available\"}");
                                response.ContentLength64 = testBytes.Length;
                                response.OutputStream.Write(testBytes, 0, testBytes.Length);
                            }
                            else
                            {
                                await HandleMusicBrainzProxy(request, response);
                            }
                        }
                        else
                        {
                            response.StatusCode = 404;
                            var notFoundBytes = Encoding.UTF8.GetBytes("Not Found");
                            response.ContentLength64 = notFoundBytes.Length;
                            response.OutputStream.Write(notFoundBytes, 0, notFoundBytes.Length);
                        }
                        break;
                }
            }
            catch
            {
                response.StatusCode = 500;
            }
            finally
            {
                response.Close();
            }
        }
        
        async Task HandleMusicBrainzProxy(HttpListenerRequest request, HttpListenerResponse response)
        {
            try
            {
                var path = request.Url?.AbsolutePath?.TrimStart('/') ?? "";
                
                // Extract the MusicBrainz API path from our proxy path
                // Expected format: api/musicbrainz/ws/2/release-group/?query=...
                var mbPath = path.Substring("api/musicbrainz/".Length);
                var mbUrl = $"https://musicbrainz.org/{mbPath}";
                
                // Add query string if present
                if (!string.IsNullOrEmpty(request.Url?.Query))
                {
                    mbUrl += request.Url.Query;
                }
                
                // Set User-Agent to be respectful to MusicBrainz
                httpClient.DefaultRequestHeaders.Clear();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "MediaInfoGrabber/1.0 (https://github.com/user/repo)");
                httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
                
                // Make the request to MusicBrainz
                var mbResponse = await httpClient.GetAsync(mbUrl);
                
                // Copy response
                response.StatusCode = (int)mbResponse.StatusCode;
                response.ContentType = "application/json; charset=utf-8";
                
                // Add CORS headers
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                
                if (mbResponse.IsSuccessStatusCode)
                {
                    var content = await mbResponse.Content.ReadAsStringAsync();
                    var contentBytes = Encoding.UTF8.GetBytes(content);
                    response.ContentLength64 = contentBytes.Length;
                    response.OutputStream.Write(contentBytes, 0, contentBytes.Length);
                }
                else
                {
                    var errorBytes = Encoding.UTF8.GetBytes("{\"error\":\"MusicBrainz API error\"}");
                    response.ContentLength64 = errorBytes.Length;
                    response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
                }
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                var errorBytes = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                response.ContentLength64 = errorBytes.Length;
                response.OutputStream.Write(errorBytes, 0, errorBytes.Length);
            }
        }

        void OnSessionChanged()
        {
            var session = mgr?.GetCurrentSession();
            if (session is null) { WriteStopped(); return; }

            var appId = session.SourceAppUserModelId ?? "";
            Task.Run(async () =>
            {
                try
                {
                    var info   = await session.TryGetMediaPropertiesAsync();
                    var title  = info?.Title      ?? "";
                    var artist = (info?.Artist ?? "").Split(new[] { ';', ',' }, 2)[0].Trim();
                    var album  = info?.AlbumTitle ?? "";

                    if (!filters.Allows(appId, title, artist, album))
                    {
                        WriteStopped();
                        return;
                    }

                    AttachSession(session);
                    await WriteNowPlayingAsync();
                }
                catch { WriteStopped(); }
            });
        }

        void DetachSession()
        {
            if (cur is null) return;
            try
            {
                cur.MediaPropertiesChanged    -= OnMediaPropsChangedAsync;
                cur.TimelinePropertiesChanged -= OnTimelineChangedAsync;
                cur.PlaybackInfoChanged       -= OnPlaybackChangedAsync;
            }
            catch { }
        }

        void AttachSession(GlobalSystemMediaTransportControlsSession s)
        {
            if (ReferenceEquals(cur, s)) return;
            DetachSession();
            cur = s;
            cur.MediaPropertiesChanged    += OnMediaPropsChangedAsync;
            cur.TimelinePropertiesChanged += OnTimelineChangedAsync;
            cur.PlaybackInfoChanged       += OnPlaybackChangedAsync;
        }

        async void OnMediaPropsChangedAsync(GlobalSystemMediaTransportControlsSession _, object __) => await WriteNowPlayingAsync();
        async void OnTimelineChangedAsync(GlobalSystemMediaTransportControlsSession _, object __)   => await WriteNowPlayingAsync();
        async void OnPlaybackChangedAsync(GlobalSystemMediaTransportControlsSession _, object __)   => await WriteNowPlayingAsync();

        void WriteStopped()
        {
            if (PortableMode)
            {
                currentCoverData = null;
                currentJsonData = @"{""status"":""Stopped""}";
            }
            else
            {
                AtomicIO.TryDelete(CoverPath);
                AtomicIO.WriteText(JsonPath, @"{""status"":""Stopped""}");
            }
        }

        async Task WriteNowPlayingAsync()
        {
            var s = cur;
            if (s is null) { WriteStopped(); return; }

            try
            {
                var info      = await s.TryGetMediaPropertiesAsync();
                var timeline  = s.GetTimelineProperties();
                var pb        = s.GetPlaybackInfo();

                var status    = pb.PlaybackStatus;
                var isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                var artist = (info.Artist ?? "").Split(new[] { ';', ',' }, 2)[0].Trim();
                var title  = info.Title      ?? "";
                var album  = info.AlbumTitle ?? "";

                var durMs  = timeline.EndTime.TotalMilliseconds;
                var posMs  = timeline.Position.TotalMilliseconds;

                var coverOK = false;
                if (info.Thumbnail is not null)
                {
                    try
                    {
                        using IRandomAccessStream ras = await info.Thumbnail.OpenReadAsync();
                        if (ras.Size > 0)
                        {
                            using var input  = ras.GetInputStreamAt(0);
                            using var reader = new DataReader(input);
                            await reader.LoadAsync((uint)ras.Size);
                            var bytes = new byte[(int)ras.Size];
                            reader.ReadBytes(bytes);
                            
                            if (PortableMode)
                            {
                                currentCoverData = bytes;
                            }
                            else
                            {
                                AtomicIO.WriteBytes(CoverPath, bytes);
                            }
                            coverOK = true;
                        }
                    }
                    catch { }
                }
                if (!coverOK)
                {
                    if (PortableMode)
                    {
                        currentCoverData = null;
                    }
                    else
                    {
                        AtomicIO.TryDelete(CoverPath);
                    }
                }

                var payload = new
                {
                    status = status.ToString(),
                    is_playing = isPlaying,
                    title,
                    artist,
                    album,
                    duration_ms = (int)Math.Round(durMs),
                    progress_ms = (int)Math.Round(posMs),
                    app_id = s.SourceAppUserModelId,
                    cover_path = coverOK ? "cover.jpg" : null,
                    updated_at = DateTimeOffset.Now.ToString("o")
                };

                var jsonString = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = false
                });
                
                if (PortableMode)
                {
                    currentJsonData = jsonString;
                }
                else
                {
                    AtomicIO.WriteText(JsonPath, jsonString);
                }
            }
            catch
            {
                // keep running
            }
        }
    }

        static class OverlayAssets
    {
              public static string Html = @"<!doctype html>
<html lang=""en"">
<head>
    <meta charset=""utf-8"" />
    <meta http-equiv=""Cache-Control"" content=""no-store"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
    <title>Now Playing Overlay</title>
    <link rel=""stylesheet"" href=""overlay.css"" />
    <script src=""https://cdn.jsdelivr.net/npm/davidshimjs-qrcodejs@0.0.2/qrcode.min.js""></script>
</head>
<body>
    <div id=""player"" data-anim=""none"">
        <!-- Optional Title Bar -->
        <div id=""title-bar"">
            <span id=""title-bar-text""></span>
        </div>
        
        <div id=""content-wrapper"">
            <div id=""cover-container"">
                <img id=""cover"" alt="""" />
                <div id=""pause-overlay"" style=""z-index:5;"">
                    <div id=""pause-icon""></div>
                </div>
            </div>
            
            <div id=""qr-code-container"">
                <div id=""qr-code""></div>
            </div>

            <div id=""meta"">

            <div id=""qr-text""></div>

            <div id=""title"" class=""line"">
                <div class=""scroll""><span></span><span class=""clone""></span></div>
                <div class=""ellipsis""></div>
            </div>

            <div id=""artist"" class=""line small"">
                <div class=""scroll""><span></span><span class=""clone""></span></div>
                <div class=""ellipsis""></div>
            </div>

            <div id=""album"" class=""line small dim"">
                <div class=""scroll""><span></span><span class=""clone""></span></div>
                <div class=""ellipsis""></div>
            </div>

            <div id=""extra"" class=""small dim""></div>

            <div id=""bar"">
                <div id=""progress""></div>
            </div>

            <div id=""times"">
                <span id=""elapsed"">0:00</span>
                <span id=""duration"">0:00</span>
            </div>
        </div>
        </div>
    </div>

    <script src=""overlay.js""></script>
</body>
</html>
";
              
                      public static string Css = @"/* ===========================================
   Animation mapping (JS only sets data-anim + state classes)
   =========================================== */

/* Direction → CSS variables */
#player[data-anim=""fly-right""]  { --tx: var(--fly-dist);  --ty: 0px; }
#player[data-anim=""fly-left""]   { --tx: calc(-1 * var(--fly-dist)); --ty: 0px; }
#player[data-anim=""fly-up""]     { --tx: 0px; --ty: calc(-1 * var(--fly-dist)); }
#player[data-anim=""fly-down""]   { --tx: 0px; --ty: var(--fly-dist); }
#player[data-anim=""none""]       { --tx: 0px; --ty: 0px; }

/* Enter / Leave states (no fade by default) */
#player.is-entering.fx { animation: flyInOnly var(--anim-dur) cubic-bezier(.2,.8,.2,1) both; }
#player.is-leaving.fx  { animation: flyOutOnly var(--anim-dur) cubic-bezier(.2,.8,.2,1) both; }

/* Fade overrides when body.use-fade is set */
body.use-fade #player.is-entering.fx { animation: flyIn var(--anim-dur) cubic-bezier(.2,.8,.2,1) both; }
body.use-fade #player.is-leaving.fx  { animation: flyOut var(--anim-dur) cubic-bezier(.2,.8,.2,1) both; }

/* Keyframes */
@keyframes flyInOnly  { from { transform: translate(var(--tx,0), var(--ty,0)); } to { transform: translate(0,0); } }
@keyframes flyOutOnly { from { transform: translate(0,0); } to { transform: translate(var(--tx,0), var(--ty,0)); } }
@keyframes flyIn      { from { opacity:0; transform: translate(var(--tx,0), var(--ty,0)); } to { opacity:1; transform: translate(0,0); } }
@keyframes flyOut     { from { opacity:1; } to { opacity:0; transform: translate(var(--tx,0), var(--ty,0)); } }
@keyframes scrollX    { from { transform: translate3d(0,0,0);} to { transform: translate3d(var(--toX,-100%),0,0);} }

/* ===========================================
   Your original styling (4-space indentation)
   =========================================== */

:root {
    /* Cover-based scaling system - everything scales from cover size */
    --cover-size: 400px;  /* Master control - change this to scale everything */
    
    /* Derived dimensions */
    --w: calc(var(--cover-size) * 4);           /* Total width: cover + content */
    --h: var(--cover-size);                     /* Height (min) always the cover size */
    
    /* Spacing scales with cover size */
    --pad: calc(var(--cover-size) * 0.08);      /* 20px when cover-size is 250px */
    --gap: calc(var(--cover-size) * 0.12);      /* 30px when cover-size is 250px */
    
    /* Typography scales with cover size */
    --font-base: calc(var(--cover-size) * 0.10);  /* 20px base font when cover-size is 200px */
    --font-title: calc(var(--font-base) * 1.4);   /* 28px title */
    --font-artist: calc(var(--font-base) * 1.0);  /* 20px artist */
    --font-album: calc(var(--font-base) * 0.85);  /* 17px album */
    --font-extra: calc(var(--font-base) * 0.75);  /* 15px extra */
    
    /* Visual properties */
    --shadow: 0 5px 15px rgba(0,0,0,.5);
    --bg: rgba(10,12,16,.78);
    --fg: #fff;
    --sub: #c6cfdb;
    --dim: #9aa7bb;
    --brand: #1db954;
  
    /* Animation Configuration - can be overridden via URL or custom CSS */
    --anim-type: none;           /* none | fly-right | fly-left | fly-up | fly-down */
    --anim-dur: 1000ms;          /* animation duration */
    --fade-dur: 0ms;             /* fade duration (0 = no fade) */
    --fly-dist: var(--w);        /* fly distance - defaults to widget width, but can be overridden */
    --duration-ms: 0;            /* auto-hide duration (0 = permanent) */
    --interval-ms: 0;            /* repeat interval (0 = no repeat) */
    --skip-settle-ms: 0;         /* time to wait before committing to a track */
    --enrich-meta: 1;            /* metadata enrichment (1 = enabled, 0 = disabled) */
    --poll-ms: 500;              /* polling interval */

    /* ========== Title Bar Configuration ========== */
    --title-bar-text: '';                           /* Empty = hidden, 'Your text here' = shown */
    --title-bar-height: calc(var(--cover-size) * 0.15);  /* 60px when cover-size is 400px */
    --title-bar-font-size: calc(var(--font-base) * 1.2);
    --title-bar-bg: rgba(0,0,0,.6);
    --title-bar-color: var(--fg);
    --title-bar-align: center;                      /* left | center | right */

    /* ========== QR Code Configuration ========== */
    --qr-enabled: 0;                                /* 0 = disabled, 1 = enabled */
    --qr-url: '';                                   /* URL/text to encode */
    --qr-text: 'Scan to join!';                     /* Text below QR code */
    --qr-music-interval-ms: 8000;                   /* Show music for X ms before switching to QR */
    --qr-interval-ms: 8000;                         /* Show QR for X ms before switching back */
    --qr-fade-dur: 400ms;                           /* Fade transition duration */
}

* { box-sizing: border-box; }

html, body {
    width: 100%; height: 100%; margin: 0; background: transparent;
    font-family: Inter, Segoe UI, Arial, sans-serif; color: var(--fg);
}

/* ===========================================
   Title Bar (inside player)
   =========================================== */
#title-bar {
    display: none;
    width: 100%;
    font-size: var(--title-bar-font-size);
    font-weight: 600;
    padding-bottom: calc(var(--cover-size) * 0.04);
    margin-bottom: calc(var(--cover-size) * 0.04);
    border-bottom: 1px solid rgba(255,255,255,.1);
    color: var(--title-bar-color);
    text-align: var(--title-bar-align);
    -webkit-font-smoothing: antialiased;
}

#title-bar.show {
    display: block;
}

/* NO transitions — only keyframe animations */
#player {
    width: min(var(--w), 100vw);
    min-height: var(--h);
    display: flex;
    flex-direction: column;
    padding: var(--pad);
    border-radius: calc(var(--cover-size) * 0.08);
    background: var(--bg);
    box-shadow: var(--shadow);
    -webkit-font-smoothing: antialiased;
    text-rendering: optimizeLegibility;
    position: relative;
}

/* Content wrapper for horizontal layout */
#content-wrapper {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: var(--gap);
    flex: 1;
    position: relative;
}

/* Layout - shared by cover and qr-code containers */
#cover-container,
#qr-code-container {
    width: var(--cover-size); 
    height: var(--cover-size);
    position: absolute;
    left: 0;
    top: 0;
    border-radius: calc(var(--cover-size) * 0.048);
    overflow: hidden;
    flex-shrink: 0;
    transition: opacity var(--qr-fade-dur) ease;
}

#cover-container {
    /* Always show fallback background */
    background: linear-gradient(135deg,
        rgba(255,255,255,0.05) 0%,
        rgba(255,255,255,0.02) 100%
    );
    opacity: 1;
    z-index: 1;
}

#qr-code-container {
    background: white;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: calc(var(--cover-size) * 0.05);
    opacity: 0;
    z-index: 2;
}

#qr-code {
    display: inline-block;
}

/* Wrapper for cover containers to maintain space */
#content-wrapper > #cover-container,
#content-wrapper > #qr-code-container {
    position: absolute;
}

/* Spacer to maintain layout */
#content-wrapper::before {
    content: '';
    width: var(--cover-size);
    height: var(--cover-size);
    flex-shrink: 0;
}

/* Always show the music note */
#cover-container::before {
    content: '♪';
    position: absolute;
    top: 50%; left: 50%;
    transform: translate(-50%, -50%);
    font-size: calc(var(--cover-size) * 0.192);
    color: rgba(255,255,255,.3);
    z-index: 1;
}

#cover {
    width: 100%; height: 100%; 
    border-radius: calc(var(--cover-size) * 0.048);
    object-fit: cover;
    position: absolute;
    top: 0; left: 0;
    z-index: 2; /* Above the fallback */
    display: none; /* Hidden until loaded */
}

/* Only show the img when we have a cover */
#cover-container.has-cover #cover {
    display: block;
}

/* Pause overlay - real DOM elements */
#pause-overlay {
    position: absolute;
    top: 0; left: 0; right: 0; bottom: 0;
    background: rgba(0,0,0,.6);
    border-radius: calc(var(--cover-size) * 0.048); /* 12px when cover-size is 250px */
    display: none;
    align-items: center;
    justify-content: center;
}

#pause-overlay.show {
    display: flex;
}

#pause-icon {
    width: calc(var(--cover-size) * 0.22); /* 55px when cover-size is 250px */
    height: calc(var(--cover-size) * 0.2);  /* 50px when cover-size is 250px */
    background: var(--brand);
    border-radius: 50%;
    display: flex;
    align-items: center;
    justify-content: center;
    box-shadow: 0 2px 8px rgba(0,0,0,.3);
    position: relative;
}

/* Create pause bars using pseudo-elements for perfect centering */
#pause-icon::before,
#pause-icon::after {
    content: '';
    width: calc(var(--cover-size) * 0.024);   /* 6px when cover-size is 250px */
    height: calc(var(--cover-size) * 0.12);  /* 30px when cover-size is 250px */
    background: rgba(0,0,0,.8); 
    border-radius: calc(var(--cover-size) * 0.008); /* 2px when cover-size is 250px */
    position: absolute;
}

#pause-icon::before {
    left: calc(var(--cover-size) * 0.072); /* 18px when cover-size is 250px */
}

#pause-icon::after {
    right: calc(var(--cover-size) * 0.072); /* 18px when cover-size is 250px */
}

#meta {
    flex: 1 1 auto;
    min-width: 0;
    display: grid;
    grid-template-rows: 
        calc(var(--font-title) * 1.9)   /* Title row */
        calc(var(--font-artist) * 1.7)  /* Artist row */
        calc(var(--font-album) * 1.8)   /* Album row */
        calc(var(--font-extra) * 1.6)   /* Extra metadata row */
        auto auto;                       /* Progress and time rows */
    align-content: center;
    position: relative;
}

.line { position: relative; min-height: 1em; width: 100%; min-width: 0; }
.small { opacity: .95; }
.dim { opacity: .9; }

#title  .ellipsis, #title  .scroll { 
    font-size: calc(var(--font-title) * 1.33); /* 24px when root-size is 200px */
    line-height: calc(var(--font-title) * 1.9); 
    overflow: visible;
}
#artist .ellipsis, #artist .scroll { 
    font-size: var(--font-title); /* 18px when root-size is 200px */
    line-height: calc(var(--font-artist) * 1.7); 
    color: var(--sub); 
}
#album  .ellipsis, #album  .scroll { 
    font-size: var(--font-base); /* 16px when root-size is 200px */
    line-height: calc(var(--font-album) * 1.8); 
    color: var(--dim); 
}
#extra  .ellipsis, #extra  .scroll { 
    font-size: var(--font-artist); /* 14px when root-size is 200px */
    line-height: calc(var(--font-extra) * 1.6); 
    color: var(--dim); 
}

.ellipsis {
    display: none; width: 100%;
    overflow: hidden; white-space: nowrap; text-overflow: ellipsis;
}

.scroll {
    display: none; width: 100%;
    position: relative; overflow: hidden; height: 1.2em;
}
.scroll > span {
    position: absolute; left: 0; top: 0; white-space: nowrap; transform: translate3d(0,0,0); will-change: transform;
}
.scroll > span.clone { opacity: 0; }

.scroll.scrolling {
    mask-image: linear-gradient(to right, transparent 0, black calc(var(--cover-size) * 0.056), black calc(100% - var(--cover-size) * 0.056), transparent 100%);
    -webkit-mask-image: linear-gradient(to right, transparent 0, black calc(var(--cover-size) * 0.056), black calc(100% - var(--cover-size) * 0.056), transparent 100%);
}
.scrolling > span {
    animation-name: scrollX;
    animation-timing-function: linear;
    animation-iteration-count: infinite;
}


#extra { 
    font-size: var(--font-artist);
    line-height: calc(var(--font-extra) * 1.6); 
    color: var(--dim); 
    min-height: calc(var(--font-extra) * 1.6); 
}

#bar { 
    width: 100%; 
    height: calc(var(--cover-size) * 0.064);
    background: rgba(255,255,255,.14);
    border-radius: calc(var(--cover-size) * 0.04);
    overflow: hidden; 
    margin-top: calc(var(--cover-size) * 0.048);
}
#progress { height: 100%; width: 0%; background: linear-gradient(90deg, var(--brand), #4fe37a); }

#times { 
    display: flex; 
    justify-content: space-between; 
    font-size: var(--font-album);
    color: var(--sub); 
    margin-top: calc(var(--cover-size) * 0.04);
}

/* ===========================================
   QR Code Display Toggle (Fade Effect)
   =========================================== */

#qr-text {
    position: absolute;
    left: 0;
    top: 0;
    width: 100%;
    height: 100%;
    display: flex;
    align-items: center;
    font-size: calc(var(--font-title) * 1.33);
    font-weight: 600;
    color: var(--fg);
    line-height: 1.4;
    opacity: 0;
    z-index: 2;
    transition: opacity var(--qr-fade-dur) ease;
    pointer-events: none;
}

/* Meta children */
#meta > .line,
#meta > #extra,
#meta > #bar,
#meta > #times {
    transition: opacity var(--qr-fade-dur) ease;
    z-index: 1;
}

/* Toggle between music and QR views with smooth crossfade */
#player.show-qr #cover-container {
    opacity: 0;
}

#player.show-qr #qr-code-container {
    opacity: 1;
}

/* Hide meta children when showing QR */
#player.show-qr #meta > .line,
#player.show-qr #meta > #extra,
#player.show-qr #meta > #bar,
#player.show-qr #meta > #times {
    opacity: 0;
}

#player.show-qr #qr-text {
    opacity: 1;
    pointer-events: auto;
}
              ";
              
              
public static string Js = @"//////////////////// Constants ////////////////////
const JSON_PATH  = 'nowplaying.json';
const COVER_PATH = 'cover.jpg';

// Enrichment cache (memory + localStorage) with TTL
const ENRICH_TTL_MS  = 24 * 3600 * 1000;  // 24h
const ENRICH_MEM      = new Map();        // in-memory cache for speed
const ENRICH_INFLIGHT = new Map();        // promises to dedupe concurrent fetches


//////////////////// DOM Helpers ////////////////////
const $ = (sel) => document.querySelector(sel);
const ui = {
    body: document.body,
    player: $('#player'),
    titleBar: $('#title-bar'),
    titleBarText: $('#title-bar-text'),
    coverWrap: $('#cover-container'),
    cover: $('#cover'),
    pauseOverlay: $('#pause-overlay'),
    titleBox: $('#title'),
    artistBox: $('#artist'),
    albumBox: $('#album'),
    elapsed: $('#elapsed'),
    duration: $('#duration'),
    progressBar: $('#progress'),
    extraLine: $('#extra'),
    qrCode: $('#qr-code'),
    qrText: $('#qr-text'),
};

//////////////////// Config (CSS vars + URL) ////////////////////
function parseMs(value) {
    const str = String(value).trim();
    const num = parseFloat(str);
    if (str.endsWith('ms')) return num;
    if (str.endsWith('s'))  return num * 1000;
    return isNaN(num) ? 0 : num;
}
function getCssVar(name, fallback = '') {
    return getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;
}
function loadConfig() {
    const urlParams = new URLSearchParams(location.search);
    const cssDefaults = {
        anim:         getCssVar('--anim-type', 'none'),
        animDur:      parseMs(getCssVar('--anim-dur', '600ms')),
        fadeDur:      parseMs(getCssVar('--fade-dur', '0ms')),
        distPx:       parseFloat(getCssVar('--fly-dist', '1600')),
        durationMs:   parseMs(getCssVar('--duration-ms', '0')),
        intervalMs:   parseMs(getCssVar('--interval-ms', '0')),
        pollMs:       parseMs(getCssVar('--poll-ms', '1000ms')),
        settleMs:     parseMs(getCssVar('--skip-settle-ms', '0ms')),
        titleBarText: getCssVar('--title-bar-text', '').replace(/^[""']|[""']$/g, ''),
        titleBarAlign: getCssVar('--title-bar-align', 'center'),
        qrEnabled:    parseInt(getCssVar('--qr-enabled', '0')) === 1,
        qrUrl:        getCssVar('--qr-url', '').replace(/^[""']|[""']$/g, ''),
        qrText:       getCssVar('--qr-text', 'Scan to join!').replace(/^[""']|[""']$/g, ''),
        qrMusicMs:    parseMs(getCssVar('--qr-music-interval-ms', '8000')),
        qrIntervalMs: parseMs(getCssVar('--qr-interval-ms', '8000')),
    };
    return {
        anim:       urlParams.get('anim')    || (cssDefaults.anim !== 'none' ? cssDefaults.anim : null),
        fadeMs:     urlParams.has('fade')     ? Math.max(0, +urlParams.get('fade'))     : cssDefaults.fadeDur,
        speedMs:    urlParams.has('speed')    ? Math.max(100, +urlParams.get('speed'))  : cssDefaults.animDur,
        durationMs: urlParams.has('duration') ? Math.max(0, +urlParams.get('duration')) : cssDefaults.durationMs,
        intervalMs: urlParams.has('interval') ? Math.max(0, +urlParams.get('interval')) : cssDefaults.intervalMs,
        distPx:     urlParams.has('dist')     ? Math.max(0, +urlParams.get('dist'))     : cssDefaults.distPx,
        pollMs:     urlParams.has('poll')     ? Math.max(100, +urlParams.get('poll'))   : cssDefaults.pollMs,
        settleMs:   urlParams.has('settle')   ? Math.max(0, +urlParams.get('settle'))   : cssDefaults.settleMs,
        textMode:   { title: 'marquee', artist: 'ellipsis', album: 'ellipsis' },
        marquee:    { pxPerSec: 60, gapPx: 24 },
        titleBarText: urlParams.get('title') || cssDefaults.titleBarText,
        titleBarAlign: urlParams.get('titlealign') || cssDefaults.titleBarAlign,
        qrEnabled:    urlParams.has('qr') ? urlParams.get('qr') === 'true' : cssDefaults.qrEnabled,
        qrUrl:        urlParams.get('qrurl') || cssDefaults.qrUrl,
        qrText:       urlParams.get('qrtext') || cssDefaults.qrText,
        qrMusicMs:    urlParams.has('qrmusic') ? Math.max(0, +urlParams.get('qrmusic')) : cssDefaults.qrMusicMs,
        qrIntervalMs: urlParams.has('qrinterval') ? Math.max(0, +urlParams.get('qrinterval')) : cssDefaults.qrIntervalMs,
    };
}
let CFG = loadConfig();

//////////////////// General Helpers ////////////////////
const isPlayable = (d) => Boolean(d && d.status !== 'Stopped' && d.title);
const makeTrackId = (d) => `${d.title || ''}||${d.artist || ''}||${d.album || ''}`;
function setFadeClass() {
    ui.body.classList.toggle('use-fade', CFG.fadeMs > 0);
}
function msToMinSec(ms) {
    const totalSec = Math.max(0, Math.round(ms / 1000));
    const m = Math.floor(totalSec / 60);
    const s = totalSec % 60;
    return `${m}:${String(s).padStart(2, '0')}`;
}
function isPausedLike(d) {
    return d && (d.status === 'Paused' || d.is_playing === false);
}
function isVisible() {
    return getComputedStyle(ui.player).display !== 'none';
}

//////////////////// Title Bar ////////////////////
function updateTitleBar() {
    if (CFG.titleBarText) {
        ui.titleBarText.textContent = CFG.titleBarText;
        ui.titleBar.classList.add('show');
        ui.player.classList.add('has-title');
    } else {
        ui.titleBar.classList.remove('show');
        ui.player.classList.remove('has-title');
    }
}

//////////////////// QR Code Generation ////////////////////
let qrCodeInstance = null;

function generateQR(text) {
    if (!text || typeof QRCode === 'undefined') return;
    
    try {
        // Clear existing QR code
        ui.qrCode.innerHTML = '';
        
        // Generate new QR code
        qrCodeInstance = new QRCode(ui.qrCode, {
            text: text,
            width: 290,
            height: 290,
            colorDark: '#000000',
            colorLight: '#ffffff',
            correctLevel: QRCode.CorrectLevel.H
        });
        
        ui.qrText.textContent = CFG.qrText;
    } catch (err) {
        console.error('QR generation failed:', err);
    }
}

//////////////////// Lean Animation (CSS-driven) ////////////////////
function applyAnimConfig() {
    ui.player.setAttribute('data-anim', CFG.anim || 'none');
    ui.body.classList.toggle('use-fade', CFG.fadeMs > 0);
}
function waitForAnimEnd() {
    return new Promise((resolve) => {
        const finish = () => { ui.player.removeEventListener('animationend', finish); resolve(); };
        ui.player.addEventListener('animationend', finish, { once: true });
        setTimeout(resolve, 1500); // fallback if CSS event doesn't fire
    });
}
async function animate(direction /* 'in' | 'out' */) {
    applyAnimConfig();

    // No animation at all
    if ((CFG.anim === null || CFG.anim === 'none') && CFG.fadeMs <= 0) {
        ui.player.style.display = direction === 'in' ? 'flex' : 'none';
        return;
    }

    ui.player.classList.remove('is-entering', 'is-leaving', 'fx');

    if (direction === 'in') {
        ui.player.style.display = 'flex';
        ui.player.classList.add('fx', 'is-entering');
    } else {
        ui.player.classList.add('fx', 'is-leaving');
    }

    await waitForAnimEnd();

    if (direction === 'out') {
        ui.player.style.display = 'none';
    }
    ui.player.classList.remove('is-entering', 'is-leaving', 'fx');
}

//////////////////// Text + Cover ////////////////////
function setLine(container, text, mode) {
    const scroll = container.querySelector('.scroll');
    const ellipsis = container.querySelector('.ellipsis');

    if (mode === 'marquee') {
        scroll.style.display = 'block';
        ellipsis.style.display = 'none';

        const span1 = scroll.querySelector('span:nth-child(1)');
        const span2 = scroll.querySelector('span.clone');
        span1.style.animation = 'none';
        span2.style.animation = 'none';
        span1.textContent = text || '';
        span2.textContent = text || '';
        void scroll.offsetWidth;

        setTimeout(() => {
            const containerWidth = scroll.clientWidth;
            const textWidth = span1.scrollWidth;
            if (text && textWidth > containerWidth) {
                const gap = CFG.marquee.gapPx;
                span2.style.left = `${textWidth + gap}px`;
                span2.style.opacity = '1';
                const seconds = (textWidth + gap) / CFG.marquee.pxPerSec;
                const anim = `scrollX ${seconds}s linear infinite`;
                void span1.offsetWidth;
                span1.style.animation = anim;
                span2.style.animation = anim;
                scroll.style.setProperty('--toX', `${-(textWidth + gap)}px`);
                scroll.classList.add('scrolling');
            } else {
                span2.style.opacity = '0';
                scroll.classList.remove('scrolling');
            }
        }, 60);
    } else {
        ellipsis.style.display = 'block';
        scroll.style.display = 'none';
        ellipsis.textContent = text || '';
    }
}

function applyStaticTrackData(track) {
    setLine(ui.titleBox,  track.title  || '', CFG.textMode.title);
    setLine(ui.artistBox, track.artist || '', CFG.textMode.artist);
    setLine(ui.albumBox,  track.album  || '', CFG.textMode.album);

    // Show cached enrichment immediately (prevents empty line on fly-in)
    const cached = getCachedEnrichment(track.title || '', track.artist || '', track.album || '');
    if (ui.extraLine) {
        if (cached) {
            ui.extraLine.textContent = formatMeta(cached);
        } else {
            ui.extraLine.textContent = (track.release_date || track.genres || '');
        }
    }
}

function ensureCover() {
    if (!ui.coverWrap.classList.contains('has-cover')) {
        ui.cover.onload = () => {
            ui.coverWrap.classList.add('has-cover');
        };
        ui.cover.onerror = () => {
            ui.coverWrap.classList.remove('has-cover');
            ui.cover.removeAttribute('src');
        };
        ui.cover.src = `${COVER_PATH}?t=${Date.now()}`;
    }
}

//////////////////// Progress ////////////////////
let progressRaf = null;
function updateLiveBits(track) {
    const isPaused = track.status === 'Paused' || track.is_playing === false;
    ui.pauseOverlay.classList.toggle('show', !!isPaused);

    if (progressRaf) cancelAnimationFrame(progressRaf);

    const durationMs = Math.max(1, track.duration_ms || 0);
    ui.duration.textContent = msToMinSec(durationMs);

    const updatedAt = track.updated_at ? Date.parse(track.updated_at) : Date.now();
    const isStale = Date.now() - updatedAt > 5000;

    let elapsedMs = isPaused || isStale
        ? (track.progress_ms || 0)
        : (track.progress_ms || 0) + (Date.now() - updatedAt);

    let lastNow = performance.now();
    const tick = (now) => {
        const delta = now - lastNow;
        lastNow = now;
        if (!isPaused && !isStale) elapsedMs += delta;

        const clamped = Math.max(0, Math.min(durationMs, elapsedMs));
        ui.progressBar.style.width = `${(clamped / durationMs) * 100}%`;
        ui.elapsed.textContent = msToMinSec(clamped);
        progressRaf = requestAnimationFrame(tick);
    };
    progressRaf = requestAnimationFrame(tick);
}

//////////////////// Enrichment (RG + REC, Proxy→Direct, cached) ////////////////////
// Basic helpers
function primaryArtist(artistString) {
    if (!artistString) return '';
    return artistString.split(/\s+(?:feat\.?|ft\.?|featuring|&|,|\|)\s+/i)[0].trim();
}
function buildRGQuery(artist, album) {
    if (!artist || !album) return null;
    return `?query=artist:%22${encodeURIComponent(artist)}%22%20AND%20releasegroup:%22${encodeURIComponent(album)}%22&fmt=json`;
}
function buildRecQuery(artist, title) {
    if (!artist || !title) return null;
    return `?query=artist:%22${encodeURIComponent(artist)}%22%20AND%20recording:%22${encodeURIComponent(title)}%22&fmt=json`;
}
async function fetchJsonQuiet(url) {
    try {
        const res = await fetch(url, { headers: { 'Accept': 'application/json' }, cache: 'no-store' });
        if (!res.ok) return null;
        return await res.json();
    } catch { return null; }
}
async function fetchFirstOk(urls) {
    for (const url of urls) {
        if (!url) continue;
        const json = await fetchJsonQuiet(url);
        if (json) return json;
    }
    return null;
}
async function enrichMetadata(title, artist, album) {
    const mainArtist = primaryArtist(artist || '');
    const proxyBase  = 'api/musicbrainz/ws/2';
    const directBase = 'https://musicbrainz.org/ws/2';

    const rgQuery  = buildRGQuery(mainArtist, album);
    const recQuery = buildRecQuery(mainArtist, title);

    const rgUrls  = rgQuery  ? [`${proxyBase}/release-group/${rgQuery}`, `${directBase}/release-group/${rgQuery}`] : [];
    const recUrls = recQuery ? [`${proxyBase}/recording/${recQuery}`,    `${directBase}/recording/${recQuery}`]    : [];

    let rgYear = null, rgGenres = [];
    if (rgUrls.length) {
        const rgJson = await fetchFirstOk(rgUrls);
        const rgHit  = rgJson?.['release-groups']?.[0];
        if (rgHit) {
            rgYear   = (rgHit['first-release-date'] || '').slice(0, 4) || null;
            rgGenres = (rgHit.tags || []).map(t => t.name).filter(Boolean);
        }
    }

    let recYear = null, recGenres = [];
    if (recUrls.length) {
        const recJson = await fetchFirstOk(recUrls);
        const recHit  = recJson?.recordings?.[0];
        if (recHit) {
            recYear   = recHit['first-release-date'] ? recHit['first-release-date'].slice(0, 4) : null;
            recGenres = (recHit.tags || []).map(t => t.name).filter(Boolean);
        }
    }

    const year = rgYear || recYear || null;
    const genres = Array.from(new Set([...(rgGenres || []), ...(recGenres || [])]));
    return { year, genres };
}

// Cache helpers
function enrichKey(title, artist, album) {
    return `mb:${(title||'')}::${(artist||'')}::${(album||'')}`.toLowerCase();
}
function readLocalEnrich(key) {
    try {
        const raw = localStorage.getItem(key);
        if (!raw) return null;
        const obj = JSON.parse(raw);
        if (!obj || typeof obj !== 'object') return null;
        if (Date.now() - (obj.ts || 0) > ENRICH_TTL_MS) return null;
        return obj.data || null;
    } catch { return null; }
}
function writeLocalEnrich(key, data) {
    try {
        localStorage.setItem(key, JSON.stringify({ ts: Date.now(), data }));
    } catch {}
}
function getCachedEnrichment(title, artist, album) {
    const key = enrichKey(title, artist, album);
    if (ENRICH_MEM.has(key)) return ENRICH_MEM.get(key);
    const ls = readLocalEnrich(key);
    if (ls) {
        ENRICH_MEM.set(key, ls);
        return ls;
    }
    return null;
}
function cacheEnrichment(title, artist, album, data) {
    const key = enrichKey(title, artist, album);
    ENRICH_MEM.set(key, data);
    writeLocalEnrich(key, data);
}
async function ensureEnrichment(title, artist, album) {
    const key = enrichKey(title, artist, album);

    // Memory/Local cached?
    const cached = getCachedEnrichment(title, artist, album);
    if (cached) return cached;

    // De-duplicate concurrent
    if (ENRICH_INFLIGHT.has(key)) return ENRICH_INFLIGHT.get(key);

    const p = (async () => {
        try {
            const data = await enrichMetadata(title, artist, album);
            cacheEnrichment(title, artist, album, data);
            return data;
        } finally {
            ENRICH_INFLIGHT.delete(key);
        }
    })();

    ENRICH_INFLIGHT.set(key, p);
    return p;
}
function formatMeta(meta) {
    if (!meta) return '';
    const parts = [];
    if (meta.year) parts.push(meta.year);
    if (meta.genres && meta.genres.length) parts.push(meta.genres.slice(0, 3).join(', '));
    return parts.join(' • ');
}

//////////////////// App ////////////////////
class NowPlayingApp {
    constructor() {
        this.currentTrack = null;
        this.durationTimer = null;
        this.intervalTimer = null;
        this.qrTimer = null;
        this.showingQR = false;
        
        setFadeClass();
        updateTitleBar();
        
        // Initialize QR if enabled
        if (CFG.qrEnabled && CFG.qrUrl) {
            generateQR(CFG.qrUrl);
        }
        
        this.startPolling();
    }

    sameTrack(a, b) {
        return a && b && makeTrackId(a) === makeTrackId(b);
    }

    stopTimers() {
        if (this.durationTimer) { clearTimeout(this.durationTimer); this.durationTimer = null; }
        if (this.intervalTimer) { clearTimeout(this.intervalTimer); this.intervalTimer = null; }
        if (this.qrTimer) { clearTimeout(this.qrTimer); this.qrTimer = null; }
    }

    showQR() {
        if (!CFG.qrEnabled || !CFG.qrUrl) return;
        this.showingQR = true;
        ui.player.classList.add('show-qr');
        
        if (CFG.qrIntervalMs > 0) {
            this.qrTimer = setTimeout(() => this.hideQR(), CFG.qrIntervalMs);
        }
    }

    hideQR() {
        this.showingQR = false;
        ui.player.classList.remove('show-qr');
        
        if (CFG.qrEnabled && CFG.qrUrl && CFG.qrMusicMs > 0) {
            this.qrTimer = setTimeout(() => this.showQR(), CFG.qrMusicMs);
        }
    }

    startTimersAfterInitialShow() {
        if (CFG.durationMs > 0) {
            this.durationTimer = setTimeout(async () => {
                await animate('out');
                if (CFG.intervalMs > 0) {
                    this.intervalTimer = setTimeout(() => this.forceRefresh(), CFG.intervalMs);
                }
            }, CFG.durationMs);
        } else if (CFG.intervalMs > 0) {
            this.intervalTimer = setTimeout(() => this.forceRefresh(), CFG.intervalMs);
        }

        // Start QR cycle if enabled
        if (CFG.qrEnabled && CFG.qrUrl && CFG.qrMusicMs > 0 && !this.showingQR) {
            this.qrTimer = setTimeout(() => this.showQR(), CFG.qrMusicMs);
        }
    }

    async forceRefresh() {
        try {
            const res = await fetch(`${JSON_PATH}?t=${Date.now()}`, { cache: 'no-store' });
            if (res.ok) {
                const data = await res.json();
                await this.onIncomingData(data, /*force*/ true);
            }
        } catch { /* silent */ }
    }

    async onIncomingData(data, force = false) {
        // Keep config dynamic
        CFG = loadConfig();
        setFadeClass();
        updateTitleBar();
        ui.player.setAttribute('data-anim', CFG.anim || 'none');

        // Re-generate QR if config changed
        if (CFG.qrEnabled && CFG.qrUrl) {
            generateQR(CFG.qrUrl);
        }

        if (!isPlayable(data)) {
            this.stopTimers();
            await animate('out');
            this.currentTrack = null;
            return;
        }

        const was = this.currentTrack;
        const isNew = !this.sameTrack(data, was);
        const pauseChanged = !!was && (isPausedLike(data) !== isPausedLike(was));
        const visible = isVisible();

        // 1) Real track change OR external force → full cycle with animation
        if (isNew || force) {
            this.stopTimers();

            if (CFG.settleMs > 0 && isNew) {
                await new Promise(r => setTimeout(r, CFG.settleMs));
            }

            const enrichP = ensureEnrichment(data.title || '', data.artist || '', data.album || '');

            await animate('out');

            // reset cover only on real track change
            ui.coverWrap.classList.remove('has-cover');
            ui.cover.removeAttribute('src');

            // Reset to music view
            this.showingQR = false;
            ui.player.classList.remove('show-qr');

            ensureCover();
            applyStaticTrackData(data);
            updateLiveBits(data);
            await animate('in');

            (async () => {
                try {
                    const meta = await enrichP;
                    if (this.currentTrack && this.sameTrack(this.currentTrack, data)) {
                        const txt = formatMeta(meta);
                        if (txt && ui.extraLine) ui.extraLine.textContent = txt;
                    }
                } catch {}
            })();

            this.currentTrack = data;
            this.startTimersAfterInitialShow();

        // 2) Pause/Play toggled while same track
        } else if (pauseChanged) {
            this.stopTimers();

            if (!visible) {
                // Not visible → behave like force to inform the user
                const enrichP = ensureEnrichment(data.title || '', data.artist || '', data.album || '');
                await animate('out');
                applyStaticTrackData(data);
                updateLiveBits(data);
                await animate('in');
                (async () => {
                    try {
                        const meta = await enrichP;
                        if (this.currentTrack && this.sameTrack(this.currentTrack, data)) {
                            const txt = formatMeta(meta);
                            if (txt && ui.extraLine) ui.extraLine.textContent = txt;
                        }
                    } catch {}
                })();
            } else {
                // Visible → NO animations, only live update
                updateLiveBits(data);
            }

            this.currentTrack = data;
            this.startTimersAfterInitialShow();

        // 3) Normal polling tick on same track
        } else {
            updateLiveBits(data);
            this.currentTrack = data;
        }

        // Try cover (cheap retry) if not yet present
        ensureCover();
    }

    startPolling() {
        const pollOnce = async () => {
            try {
                CFG = loadConfig(); // update cfg every tick
                const res = await fetch(`${JSON_PATH}?t=${Date.now()}`, { cache: 'no-store' });
                if (res.ok) {
                    const data = await res.json();
                    await this.onIncomingData(data);
                }
            } catch {}
            setTimeout(pollOnce, CFG.pollMs);
        };
        pollOnce();
    }
}

//////////////////// Bootstrap ////////////////////
document.addEventListener('DOMContentLoaded', () => {
    window.app = new NowPlayingApp();
});";

              
public static string CompactHtml = @"<!doctype html>
  <html lang=""en"">
  <head>
    <meta charset=""utf-8"" />
    <meta http-equiv=""Cache-Control"" content=""no-store"" />
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"" />
    <title>Now Playing Overlay</title>
    <style>" + Css + @"</style>
  </head>
  <body>
    <div id=""player"">
      <div id=""cover-container"">
        <img id=""cover"" alt="""" />
        <div id=""pause-overlay"">
          <div id=""pause-icon""></div>
        </div>
      </div>
      <div id=""meta"">
        <div id=""title""  class=""line"">
          <div class=""scroll""><span></span><span class=""clone""></span></div>
          <div class=""ellipsis""></div>
        </div>
        <div id=""artist"" class=""line small"">
          <div class=""scroll""><span></span><span class=""clone""></span></div>
          <div class=""ellipsis""></div>
        </div>
        <div id=""album""  class=""line small dim"">
          <div class=""scroll""><span></span><span class=""clone""></span></div>
          <div class=""ellipsis""></div>
        </div>
  
        <div id=""extra"" class=""small dim""></div>
  
        <div id=""bar""><div id=""progress""></div></div>
        <div id=""times"">
          <span id=""elapsed"">0:00</span>
          <span id=""duration"">0:00</span>
        </div>
      </div>
    </div>
    <script>" + Js + @"</script>
  </body>
  </html>
  ";
  
public static string UsageMd = @"# 🎨 MediaInfoGrabber Widget Customization Guide
  
  This guide explains how to customize the media overlay widget to fit your specific needs.
  
  ## 🚀 Quick Start
  
  The widget is controlled entirely through CSS variables in the `:root` section (or controlled by URI parameters, see further below). 
  Change these values to customize appearance and behavior:
  
  ```css
  :root {
    --cover-size: 400px;  /* Master control - scales everything */
    --anim-type: fly-left;
    --duration-ms: 5000;
    /* ... more options below */
  }
  ```
  
  ## 📐 Master Scaling System
  
  Everything scales from the cover size for consistent proportions:
  
  ```css
  --cover-size: 200px;  /* Small widget: 800x200 */
  --cover-size: 400px;  /* Medium widget: 1600x400 */
  --cover-size: 600px;  /* Large widget: 2400x600 */
  ```
  
  **Derived dimensions:**
  - Widget width: `cover-size × 4`
  - Widget height: `cover-size × 1`
  - All fonts, spacing, and elements scale proportionally
  
  ## 🎬 Animation Configuration
  
  ### Animation Types
  ```css
  --anim-type: none;        /* No animation */
  --anim-type: fly-left;    /* Slides in from left */
  --anim-type: fly-right;   /* Slides in from right */
  --anim-type: fly-up;      /* Slides in from top */
  --anim-type: fly-down;    /* Slides in from bottom */
  ```
  
  ### Animation Timing
  ```css
  --anim-dur: 1000ms;       /* Animation speed */
  --fade-dur: 500ms;        /* Fade effect (0 = no fade) */
  --fly-dist: var(--w);     /* Fly distance (default: widget width) */
  ```
  
  ### Auto-Hide & Repeat
  ```css
  --duration-ms: 5000;      /* Show for 5 seconds (0 = permanent) */
  --interval-ms: 10000;     /* Repeat every 10 seconds (0 = no repeat) */
  ```
  
  ## ⏱️ Track Change Behavior
  
  ```css
  --skip-settle-ms: 1250;   /* Wait time before showing new track */
  ```
  
  When tracks change rapidly (skipping songs), the widget waits before updating to avoid flickering. Set to `0` for immediate updates.
  
  ## 🏷️ Title Bar (NEW)
  
  Add an optional title bar above the widget with custom text and alignment:
  
  ```css
  --title-bar-text: 'Now Playing';           /* Empty = hidden, 'text' = shown */
  --title-bar-align: center;                 /* left | center | right */
  --title-bar-font-size: calc(var(--font-base) * 1.2);
  --title-bar-color: var(--fg);
  ```
  
  **URL Parameters:**
  - `title` - Title bar text
  - `titlealign` - Alignment (left/center/right)
  
  **Example:**
  ```
  overlay.html?title=Stream%20Overlay&titlealign=left
  ```
  
  ## 📲 QR Code Display (NEW)
  
  Toggle between music info and a QR code with smooth fade transitions:
  
  ```css
  --qr-enabled: 1;                           /* 0 = disabled, 1 = enabled */
  --qr-url: 'https://spotify.com/...';       /* URL or text to encode */
  --qr-text: 'Scan to follow!';              /* Text displayed next to QR */
  --qr-music-interval-ms: 8000;              /* Show music for X ms */
  --qr-interval-ms: 8000;                    /* Show QR for X ms */
  --qr-fade-dur: 400ms;                      /* Fade transition duration */
  ```
  
  **URL Parameters:**
  - `qr` - Enable QR code (true/false)
  - `qrurl` - QR code URL or text
  - `qrtext` - Display text
  - `qrmusic` - Music display duration (ms)
  - `qrinterval` - QR display duration (ms)
  
  **Example:**
  ```
  overlay.html?qr=true&qrurl=https://twitch.tv/yourname&qrtext=Follow%20me!&qrmusic=10000&qrinterval=5000
  ```
  
  **Behavior:**
  - Shows music info for `--qr-music-interval-ms`
  - Fades to QR code for `--qr-interval-ms`
  - Repeats cycle
  - Set either interval to `0` to disable auto-switching
  
  ## 🎨 Visual Customization
  
  ### Colors & Theme
  ```css
  --bg: rgba(10,12,16,.78);     /* Background color */
  --fg: #fff;                   /* Main text color */
  --sub: #c6cfdb;               /* Secondary text (artist) */
  --dim: #9aa7bb;               /* Dimmed text (album, time) */
  --brand: #1db954;             /* Accent color (progress bar) */
  --shadow: 0 5px 15px rgba(0,0,0,.5);  /* Drop shadow */
  ```
  
  ## 🔗 URL Parameters
  
  Override any setting via URL parameters for testing:
  
  ```
  overlay.html?anim=fly-right&duration=3000&fade=200&title=Now%20Playing
  ```
  
  **Available parameters:**
  - `anim` - Animation type
  - `speed` - Animation duration (ms)
  - `fade` - Fade duration (ms)
  - `dist` - Fly distance (px)
  - `duration` - Auto-hide time (ms)
  - `interval` - Repeat interval (ms)
  - `settle` - Track settle time (ms)
  - `poll` - Update frequency (ms)
  - `title` - Title bar text (NEW)
  - `titlealign` - Title alignment: left/center/right (NEW)
  - `qr` - Enable QR: true/false (NEW)
  - `qrurl` - QR code URL or text (NEW)
  - `qrtext` - QR display text (NEW)
  - `qrmusic` - Music display duration (ms) (NEW)
  - `qrinterval` - QR display duration (ms) (NEW)
  
  ## 💡 Common Use Cases
  
  ### Stream Overlay (Bottom Corner)
  ```css
  --cover-size: 300px;
  --anim-type: fly-up;
  --duration-ms: 8000;
  --interval-ms: 0;
  ```
  
  ### Full Screen Display
  ```css
  --cover-size: 600px;
  --anim-type: fade;
  --fade-dur: 1000ms;
  --duration-ms: 0;  /* Always visible */
  ```
  
  ### Desktop Widget (Center Screen)
  ```css
  --cover-size: 400px;
  --anim-type: fly-left;
  --fly-dist: 1920px;  /* Fly from screen edge */
  --duration-ms: 6000;
  --interval-ms: 15000;
  ```
  
  ### Stream Overlay with QR Code
  ```css
  --cover-size: 350px;
  --anim-type: fly-right;
  --duration-ms: 0;                    /* Always visible */
  --title-bar-text: 'Now Streaming';   /* Add title */
  --qr-enabled: 1;                     /* Enable QR */
  --qr-url: 'https://twitch.tv/yourname';
  --qr-text: 'Follow me on Twitch!';
  --qr-music-interval-ms: 12000;       /* Show music for 12s */
  --qr-interval-ms: 5000;              /* Show QR for 5s */
  ```
  
  ## 🔌 Integration Examples
  
  ### OBS Studio
  1. Add **Browser Source**
  2. URL: `file:///path/to/overlay.html`
  3. Width: 1600, Height: 400 (for 400px cover)
  4. Enable ""Refresh browser when scene becomes active""
  
  ### Streamlabs
  1. Add **Browser Source**
  2. Point to `overlay.html` file
  3. Set custom CSS if needed
  
  For complete documentation, visit the usage.md file created alongside the overlay files.
  ";

  }


    class FilterConfig
    {
        readonly string path;
        readonly string? appFilter;
        public List<(string Kind, Regex Pattern)> Rules = new();
        FileSystemWatcher? fsw;

        public FilterConfig(string root, string? appFilter = null)
        {
            this.appFilter = appFilter;
            path = Path.Combine(root, "whitelist.txt");
            
            if (!string.IsNullOrEmpty(appFilter))
            {
                // Use app filter instead of whitelist file
                Rules = new List<(string, Regex)> { ("app", WildToRegex($"*{appFilter}*")) };
            }
            else
            {
                // Use whitelist file
                if (!File.Exists(path))
                    AtomicIO.WriteText(path, GetWhitelistTemplate());
                Load();
                try
                {
                    fsw = new FileSystemWatcher(root, "whitelist.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
                    fsw.Changed += (_, __) => SafeReload();
                    fsw.Created += (_, __) => SafeReload();
                    fsw.Deleted += (_, __) => SafeReload();
                    fsw.Renamed += (_, __) => SafeReload();
                    fsw.EnableRaisingEvents = true;
                }
                catch { }
            }
        }

        void SafeReload() { try { Load(); } catch { } }

        static Regex WildToRegex(string pattern)
        {
            var escaped = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public void Load()
        {
            // Skip loading if app filter is provided
            if (!string.IsNullOrEmpty(appFilter)) return;
            
            var list = new List<(string, Regex)>();
            if (File.Exists(path))
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                    var idx = line.IndexOf(':'); if (idx <= 0) continue;
                    var kind = line[..idx].Trim().ToLowerInvariant();
                    var pat  = line[(idx+1)..].Trim();
                    if (string.IsNullOrEmpty(pat)) continue;
                    list.Add((kind, WildToRegex(pat)));
                }
            }
            Rules = list.Count > 0 ? list : new List<(string, Regex)> { ("app", WildToRegex("spotify")) };
        }

        public bool Allows(string appAumid, string title, string artist, string album)
        {
            foreach (var (kind, regex) in Rules)
            {
                switch (kind)
                {
                    case "app":    if (regex.IsMatch(appAumid ?? "")) return true; break;
                    case "title":  if (regex.IsMatch(title ?? ""))    return true; break;
                    case "artist": if (regex.IsMatch(artist ?? ""))   return true; break;
                    case "album":  if (regex.IsMatch(album ?? ""))    return true; break;
                }
            }
            return false;
        }

        static string GetWhitelistTemplate() =>
@"# NowPlaying Whitelist
# Format: <field>:<pattern>  | Fields: app | title | artist | album
# Wildcards: * ?  | OR-Logik: eine passende Regel reicht.

# Active rules (apps)
app:*spotify*
app:*applemusic*
app:*apple*music*
app:*deezer*

# Optional browsers (disabled by default)
# app:msedge
# app:chrome
# app:brave
# app:firefox
# app:opera
# app:vivaldi
";
    }
}
