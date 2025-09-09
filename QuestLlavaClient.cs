using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class LLaVAResponse
{
    public string text;
    public string error;
    public int latency_ms;
    public string model;
}

// keep the small helper class if you want to parse later
[Serializable] class ServerReply { public string text; public string error; public string build; public string shape; }


[Serializable]
public struct LlavaHeader
{
    public string prompt;
    public int image_len;
    public string client_sha256;   // optional: helps compare with server echo
    public float temperature;      // optional (unused unless you set it)
    public int max_tokens;         // optional (unused unless you set it)
    public bool tts;               // optional (unused unless you set it)
}

public class QuestLLaVAClient : MonoBehaviour
{
    [Header("Server")]
    [Tooltip("e.g., ws://192.168.1.23:19111/llava")]
    public string serverUri = "ws://192.168.2.29:19111/llava";
    
    [Header("Debug")]
    public bool showRawJsonInUI = true;
    
    [Header("UI (optional)")]
    public TMP_InputField promptInput;           // optional
    public TextMeshProUGUI responseText;         // optional; logs if null
    
    private SynchronizationContext _unityCtx;
    private bool _busy = false;
    
    // QuestLLaVAClient.cs
    void Awake()
    {
        _unityCtx = SynchronizationContext.Current;
        Debug.Log($"[LLaVA] serverUri={serverUri}");
        if (responseText != null) responseText.text = $"→ {serverUri}";
    }
    
    public void PingServer()
    {
        StartCoroutine(_Ping());
    }
    
    System.Collections.IEnumerator _Ping()
    {
        var http = serverUri.Replace("ws://", "http://").Replace("/llava", "/ping");
        using var req = UnityWebRequest.Get(http);
        yield return req.SendWebRequest();
        var txt = req.result == UnityWebRequest.Result.Success ? req.downloadHandler.text : req.error;
        PostToUI($"PING {txt}");
    }
    
    // QuestLLaVAClient.cs
    public AndroidTTS tts;   // assign in Inspector
    
    // Text-only, uses promptInput
    public void SendPrompt() => SendPromptWithImageBytes(null, null);
    
    // Text-only, explicit prompt
    public void SendPrompt(string prompt) => SendPromptWithImageBytes(null, prompt);
    
    /// <summary>Send prompt + optional JPG bytes to the LLaVA WS server and print reply.</summary>
    public async void SendPromptWithImageBytes(byte[] jpg, string promptOverride = null)
    {
        if (_busy) { Debug.Log("[LLaVA] Busy; ignoring trigger"); return; }
        _busy = true;
        
        string prompt = string.IsNullOrWhiteSpace(promptOverride)
        ? (!string.IsNullOrWhiteSpace(promptInput?.text) ? promptInput.text
        : "Describe this image:")
        : promptOverride;
        
        // Optional: short SHA256 to compare with server's echo
        string Hash(byte[] data)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(data ?? Array.Empty<byte>());
            var sb = new StringBuilder(h.Length * 2);
            foreach (var b in h) sb.Append(b.ToString("x2"));
            return sb.ToString(0, 16); // short hash
        }
        
        int len = jpg?.Length ?? 0;
        string clientSha = (len > 0) ? Hash(jpg) : null;
        Debug.Log($"[LLaVA] Will send JPG bytes: {len}");
        
        try
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "http://localhost");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            
            await ws.ConnectAsync(new Uri(serverUri), cts.Token);
            
            // Build ONE header from the actual length
            var header = new LlavaHeader
            {
                prompt = prompt,
                image_len = len,
                client_sha256 = clientSha,
                temperature = 0.3f,
                max_tokens = 256
                // tts = false,
            };
            byte[] headerBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(header));
            
            // Send header (server accepts text or binary; we send binary)
            await ws.SendAsync(new ArraySegment<byte>(headerBytes),
            WebSocketMessageType.Binary, true, cts.Token);
            
            // Optional image
            if (len > 0)
            {
                await ws.SendAsync(new ArraySegment<byte>(jpg),
                WebSocketMessageType.Binary, true, cts.Token);
            }
            
            // Robust receive (handles messages larger than a single frame)
            var recv = new byte[8192];
            using var ms = new MemoryStream();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(recv), cts.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;
                ms.Write(recv, 0, res.Count);
            } while (!res.EndOfMessage);
            
            string msg = Encoding.UTF8.GetString(ms.ToArray());
            PostToUI(msg);
            
            if (ws.State == WebSocketState.Open)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (Exception e)
        {
            PostToUI($"Send failed: {e.Message}");
        }
        finally { _busy = false; }
    }
    
    // Keep only ONE PostToUI in the class
    private void PostToUI(string s)
    {
        if (responseText == null) { Debug.Log($"[LLaVA] {s}"); return; }

        if (showRawJsonInUI) {     // show full JSON when debugging
            responseText.text = s;
            tts?.Speak(s);
            return;
        }

        try {
            var r = JsonUtility.FromJson<ServerReply>(s);
            var text = !string.IsNullOrEmpty(r?.text) ? r.text : s;
            responseText.text = text;
            tts?.Speak(text);
        } catch {
            responseText.text = s;
            tts?.Speak(s);
        }
    }
}
