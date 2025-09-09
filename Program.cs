using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;    // <-- needed for RandomNumberGenerator & SHA256
using System.Net.Http;
using System.Net.Http.Headers;
using KokoroSharp;
using System.Reflection;
using System.Diagnostics;



var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(60) });

// ---------- KokoroSharp init (no direct KokoroVoice references) ----------
string KOKORO_MODEL = Environment.GetEnvironmentVariable("KOKORO_MODEL") ?? ""; // optional path
string DEFAULT_VOICE_NAME = Environment.GetEnvironmentVariable("KOKORO_VOICE") ?? "af_heart";

try { Assembly.Load("KokoroSharp"); } catch {}
try { Assembly.Load("KokoroSharp.CPU"); } catch {}

// Try load KokoroSharp.KokoroTTS via reflection so we don't hard-bind to its types
object? KOKORO = TryLoadKokoroTTS(KOKORO_MODEL);






static object? TryLoadKokoroTTS(string modelPath)
{
    var ttsType = AppDomain.CurrentDomain.GetAssemblies()
        .SelectMany(a => SafeGetTypes(a))
        .FirstOrDefault(t => t.Name == "KokoroTTS");

    if (ttsType == null) return null;

    // Prefer LoadModel() with/without path
    var load0 = ttsType.GetMethod("LoadModel", Type.EmptyTypes);
    if (load0 != null) return load0.Invoke(null, null);

    var load1 = ttsType.GetMethod("LoadModel", new[] { typeof(string) });
    if (load1 != null) return load1.Invoke(null, new object[] { modelPath });

    return null;
}

static IEnumerable<Type> SafeGetTypes(Assembly a)
{
    try { return a.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
}



// llama-server base URL (you already have it running on 19112)
string LlamaServer = Environment.GetEnvironmentVariable("LLAMA_SERVER") ?? "http://127.0.0.1:19112";
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
var BUILD = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

app.MapGet("/ping", () => Results.Json(new { ok = true, build = BUILD, llama = LlamaServer }));

// Read one full websocket message
static async Task<(string kind, byte[] payload)> Recv(WebSocket ws, CancellationToken ct)
{
    var buf = new byte[8192];
    using var ms = new MemoryStream();
    WebSocketReceiveResult res;
    do
    {
        res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
        if (res.MessageType == WebSocketMessageType.Close) throw new WebSocketException();
        ms.Write(buf, 0, res.Count);
    } while (!res.EndOfMessage);
    return (res.MessageType == WebSocketMessageType.Text ? "text" : "bytes", ms.ToArray());
}

app.Map("/llava", async ctx =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var ct = ctx.RequestAborted;

    long t0 = Environment.TickCount64;
    string stage = "start";

    try
    {
        // 1) header
        stage = "recv_header";
        var (kind, headBytes) = await Recv(ws, ct);
        using var hdrDoc = JsonDocument.Parse(headBytes);
        var root = hdrDoc.RootElement;

        string prompt = root.TryGetProperty("prompt", out var p) ? (p.GetString() ?? "Describe briefly.") : "Describe briefly.";
        int imageLen  = root.TryGetProperty("image_len", out var il) ? il.GetInt32() : 0;
        double temp   = root.TryGetProperty("temperature", out var te) ? te.GetDouble() : 0.2;
        int maxTok    = root.TryGetProperty("max_tokens", out var mt) ? mt.GetInt32() : 128;
        
        if (!(temp > 0)) temp = 0.2;
        if (maxTok <= 0) maxTok = 128;

        // 2) image
        stage = imageLen > 0 ? "recv_image" : "no_image";
        byte[] img = Array.Empty<byte>();
        if (imageLen > 0)
        {
            var (_, imgBytes) = await Recv(ws, ct);
            img = imgBytes.Length > imageLen ? imgBytes.AsSpan(0, imageLen).ToArray() : imgBytes;
        }

        // 3) call llama-server (OpenAI-style /v1/chat/completions)
        stage = "call_llama_server";
        var (text, shape) = await CallLlamaServerAsync(http, LlamaServer, prompt, img, temp, maxTok, ct);

        var payload = new {
          build = BUILD,
          text = text ?? "",
          shape,                                // <- shows which schema worked
          request_ms = (int)(Environment.TickCount64 - t0),
          image_len_hdr = imageLen,
          image_len_rx = img.Length,
          image_sha16 = (img.Length > 0) ? Convert.ToHexString(SHA256.HashData(img)).ToLowerInvariant()[..16] : null,
          rx_header_kind = kind
        };
        
        

        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)),
                           WebSocketMessageType.Text, true, ct);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "ok", ct);
    }
    catch (Exception e)
    {
        var err = new { error = $"{e.GetType().Name}: {e.Message}", stage, build = BUILD };
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(err)),
                                 WebSocketMessageType.Text, true, ct); } catch {}
        try { await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "err", ct); } catch {}
    }
});


app.MapGet("/beep", () => Results.File(MakeBeepWav(24000, 440.0, 0.5), "audio/wav"));

static byte[] MakeBeepWav(int sampleRate, double freq, double seconds) {
    int n = (int)(sampleRate * seconds);
    var pcm = new byte[n * 2];
    for (int i = 0, j = 0; i < n; i++) {
        double t = i / (double)sampleRate;
        short s = (short)(Math.Sin(2 * Math.PI * freq * t) * short.MaxValue);
        pcm[j++] = (byte)(s & 0xFF); pcm[j++] = (byte)((s >> 8) & 0xFF);
    }
    using var ms = new MemoryStream(44 + pcm.Length);
    using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
    bw.Write(Encoding.ASCII.GetBytes("RIFF")); bw.Write(36 + pcm.Length);
    bw.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); bw.Write(16);
    bw.Write((short)1); bw.Write((short)1); bw.Write(sampleRate);
    bw.Write(sampleRate * 2); bw.Write((short)2); bw.Write((short)16);
    bw.Write(Encoding.ASCII.GetBytes("data")); bw.Write(pcm.Length); bw.Write(pcm);
    bw.Flush(); return ms.ToArray();
}



// ---------- Text-to-Speech over HTTP ----------
app.MapGet("/tts", async (HttpContext ctx) =>
{
    string text  = ctx.Request.Query["text"].ToString();
    string voice = ctx.Request.Query["voice"].ToString();  // optional
    string format = ctx.Request.Query["format"].ToString().ToLower();  // optional

    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { error = "missing ?text=" });

    string voiceName = string.IsNullOrWhiteSpace(voice) ? DEFAULT_VOICE_NAME : voice;

    // Prefer KokoroSharp if loaded
    if (KOKORO == null) {
        try { Assembly.Load("KokoroSharp"); Assembly.Load("KokoroSharp.CPU"); } catch {}
        KOKORO = TryLoadKokoroTTS(KOKORO_MODEL);
    }
    Console.WriteLine($"[/tts] engine={(KOKORO!=null ? "kokoro" : "say")} voice={voiceName} len={text.Length}");

    byte[] wav = null;

    if (KOKORO != null)
    {
        try
        {
            wav = SynthWithKokoroSharpToWav(KOKORO, voiceName, text, 24000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Kokoro] synth failed, falling back to say: {ex.Message}");
        }
    }

    if (wav == null && OperatingSystem.IsMacOS())
    {
        wav = await TtsMac(text);
    }

    if (wav == null)
        return Results.Problem("TTS unavailable (no KokoroSharp and not on macOS).");

    // 🔄 Convert to OGG if requested
    if (format == "ogg")
    {
        var wavPath = Path.GetTempFileName() + ".wav";
        var oggPath = Path.ChangeExtension(wavPath, ".ogg");
        await File.WriteAllBytesAsync(wavPath, wav);

        var ffmpeg = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -i \"{wavPath}\" -c:a libvorbis \"{oggPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = Process.Start(ffmpeg);
        proc.WaitForExit();

        var oggBytes = await File.ReadAllBytesAsync(oggPath);
        File.Delete(wavPath);
        File.Delete(oggPath);

        return Results.File(oggBytes, "audio/ogg");
    }

    return Results.File(wav, "audio/wav");
});




app.Run();



// ---------- helper: call llama-server ----------

static async Task<(string? Text, string Shape)> CallLlamaServerAsync(
    HttpClient http, string baseUrl, string prompt, byte[] imageBytes,
    double temperature, int maxTokens, CancellationToken ct)
{
    static async Task<string?> PostChatAsync(HttpClient http, string url, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json"){ CharSet="utf-8" };

        using var resp = await http.PostAsync(url, content, ct);
        var s = await resp.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(s);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var c0 = choices[0];
            if (c0.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var contentNode))
                return contentNode.GetString();
            if (c0.TryGetProperty("text", out var legacy))
                return legacy.GetString();
        }
        if (root.TryGetProperty("error", out var err)) throw new Exception(err.ToString());
        return null;
    }

    string? dataUri = (imageBytes != null && imageBytes.Length > 0)
        ? $"data:image/jpeg;base64,{Convert.ToBase64String(imageBytes)}"
        : null;

    // S1: OpenAI-style [{text}, {image_url:{url:data:...}}]
    {
        var content = (dataUri != null)
            ? new object[] {
                new { type = "text", text = prompt },
                new { type = "image_url", image_url = new { url = dataUri } }
              }
            : new object[] { new { type = "text", text = prompt } };

        var body = new { model="llava", temperature, max_tokens = maxTokens,
                         messages = new[] { new { role="user", content } } };

        var txt = await PostChatAsync(http, $"{baseUrl}/v1/chat/completions", body, ct);
        if (!string.IsNullOrWhiteSpace(txt)) return (txt, "s1:image_url");
    }

    // S2: Variant [{input_text}, {input_image:{image_url:{url:data:...}}}]
    if (dataUri != null)
    {
        var content2 = new object[] {
            new { type = "input_text",  text = prompt },
            new { type = "input_image", image_url = new { url = dataUri } }
        };
        var body2 = new { model="llava", temperature, max_tokens = maxTokens,
                          messages = new[] { new { role="user", content = content2 } } };

        var txt2 = await PostChatAsync(http, $"{baseUrl}/v1/chat/completions", body2, ct);
        if (!string.IsNullOrWhiteSpace(txt2)) return (txt2, "s2:input_image");
    }

    // S3: Minimal: image_url as string
    if (dataUri != null)
    {
        var content3 = new object[] {
            new { type = "text", text = prompt },
            new { type = "image_url", image_url = dataUri }
        };
        var body3 = new { model="llava", temperature, max_tokens = maxTokens,
                          messages = new[] { new { role="user", content = content3 } } };

        var txt3 = await PostChatAsync(http, $"{baseUrl}/v1/chat/completions", body3, ct);
        if (!string.IsNullOrWhiteSpace(txt3)) return (txt3, "s3:image_url:str");
    }

    // S4: Legacy /v1/completions (use n_predict)
    {
        object body4 = (imageBytes != null && imageBytes.Length > 0)
            ? new { prompt = prompt, temperature, n_predict = maxTokens,
                    images = new[] { Convert.ToBase64String(imageBytes) } }
            : new { prompt = prompt, temperature, n_predict = maxTokens };

        var json4 = JsonSerializer.Serialize(body4);
        using var c4 = new StringContent(json4, Encoding.UTF8);
        c4.Headers.ContentType = new MediaTypeHeaderValue("application/json"){ CharSet="utf-8" };

        using var r4 = await http.PostAsync($"{baseUrl}/v1/completions", c4, ct);
        var s4 = await r4.Content.ReadAsStringAsync(ct);

        using var d4 = JsonDocument.Parse(s4);
        if (d4.RootElement.TryGetProperty("choices", out var ch4) &&
            ch4.GetArrayLength() > 0 && ch4[0].TryGetProperty("text", out var t4))
            return (t4.GetString(), "s4:completions");
    }

    return (null, "none");
}



static async Task<byte[]> TtsMac(string text)
{
    string wav = Path.ChangeExtension(Path.GetTempFileName(), ".wav");
    // Ask 'say' to produce WAV + little-endian 16-bit PCM @ 24 kHz
    string args = $"--file-format=WAVE --data-format=LEI16@24000 -o \"{wav}\"";
    await RunProcessAsync("/usr/bin/say", args, text);

    var bytes = await File.ReadAllBytesAsync(wav);
    File.Delete(wav);
    return bytes;
}


static async Task RunProcessAsync(string fileName, string args, string? stdin=null)
{
    var psi = new ProcessStartInfo(fileName, args){
        RedirectStandardInput  = stdin != null,
        RedirectStandardError  = true,
        UseShellExecute        = false, CreateNoWindow = true
    };
    using var p = Process.Start(psi)!;
    if (stdin != null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
    string err = await p.StandardError.ReadToEndAsync();
    await p.WaitForExitAsync();
    if (p.ExitCode != 0) throw new Exception($"{fileName} exited {p.ExitCode}: {err}");
}


// Try to synthesize using KokoroSharp without compile-time types.
static byte[] SynthWithKokoroSharpToWav(object kokoro, string voiceName, string text, int sampleRate)
{
    var allTypes = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => SafeGetTypes(a)).ToArray();

    // Get voice object via KokoroVoiceManager.GetVoice(string)
    var voiceMgrType = allTypes.FirstOrDefault(t => t.Name == "KokoroVoiceManager");
    object? voice = voiceMgrType?.GetMethod("GetVoice", new[] { typeof(string) })
                                ?.Invoke(null, new object[] { voiceName });

    // 1) Try KokoroWavSynthesizer (returns WAV bytes directly in some builds)
    var wavSynthType = allTypes.FirstOrDefault(t => t.Name == "KokoroWavSynthesizer");
    if (wavSynthType != null && voice != null)
    {
        // ctor(KokoroTTS)
        var ctor = wavSynthType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 1);
        var wavSynth = ctor?.Invoke(new[] { kokoro });

        // Try common Synthesize signatures: (string, voice) or (voice, string)
        var synths = wavSynthType.GetMethods().Where(m => m.Name.StartsWith("Synthesize")).ToList();
        foreach (var m in synths)
        {
            var ps = m.GetParameters();
            if (ps.Length == 2)
            {
                try
                {
                    var ret = m.Invoke(wavSynth, ps[0].ParameterType == typeof(string)
                        ? new object[] { text, voice }
                        : new object[] { voice, text });
                    if (ret is byte[] wav) return wav;
                }
                catch { /* try next */ }
            }
        }
    }

    // 2) Fallback: get float[] samples from KokoroTTS and wrap to WAV
    var ttsType = kokoro.GetType();
    // Try Synthesize(text, voice) then SpeakFast(text, voice)
    if (voice != null)
    {
        var m = ttsType.GetMethod("Synthesize", new[] { typeof(string), voice.GetType() })
             ?? ttsType.GetMethod("SpeakFast", new[] { typeof(string), voice.GetType() });
        if (m != null)
        {
            var floats = (Array)m.Invoke(kokoro, new object[] { text, voice })!;
            return FloatToWav16(floats, sampleRate);
        }
    }
    // Last resort: Synthesize(text) or SpeakFast(text)
    {
        var m = ttsType.GetMethod("Synthesize", new[] { typeof(string) })
             ?? ttsType.GetMethod("SpeakFast", new[] { typeof(string) });
        if (m != null)
        {
            var floats = (Array)m.Invoke(kokoro, new object[] { text })!;
            return FloatToWav16(floats, sampleRate);
        }
    }

    throw new Exception("No compatible KokoroSharp synth method found.");
}

static byte[] FloatToWav16(Array samplesObj, int sampleRate)
{
    // Convert float[-1..1] -> PCM16 + RIFF/WAVE header
    var samples = samplesObj as float[] ?? samplesObj.Cast<object>().Select(o => (float)o).ToArray();
    var pcm = new byte[samples.Length * 2];
    int i = 0;
    foreach (var f in samples)
    {
        var clamped = MathF.Max(-1f, MathF.Min(1f, f));
        short s = (short)MathF.Round(clamped * short.MaxValue);
        pcm[i++] = (byte)(s & 0xFF);
        pcm[i++] = (byte)((s >> 8) & 0xFF);
    }

    using var ms = new MemoryStream(44 + pcm.Length);
    using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

    int byteRate = sampleRate * 2; // mono, 16-bit
    int blockAlign = 2;
    int subchunk2 = pcm.Length;
    int chunkSize = 36 + subchunk2;

    bw.Write(Encoding.ASCII.GetBytes("RIFF"));
    bw.Write(chunkSize);
    bw.Write(Encoding.ASCII.GetBytes("WAVE"));
    bw.Write(Encoding.ASCII.GetBytes("fmt "));
    bw.Write(16);             // PCM
    bw.Write((short)1);       // format = PCM
    bw.Write((short)1);       // channels = 1
    bw.Write(sampleRate);
    bw.Write(byteRate);
    bw.Write((short)blockAlign);
    bw.Write((short)16);      // bits
    bw.Write(Encoding.ASCII.GetBytes("data"));
    bw.Write(subchunk2);
    bw.Write(pcm);
    bw.Flush();
    return ms.ToArray();
}




