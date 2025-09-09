# LLava_vision_Quest

# 🧠 QuestLLaVAClient: Unity WebSocket Client for LLaVA

This Unity project provides a WebSocket-based client for interacting with a [LLaVA](https://llava-vl.github.io/) server — a multimodal language model capable of processing text and images. Designed for use in VR/AR or mobile environments (e.g., Meta Quest), it enables real-time prompt submission and response handling, with optional image input and TTS playback.

---

## 🚀 Features

- 🔌 Connects to a LLaVA server via WebSocket (`ws://...`)
- 🖼️ Sends text prompts and optional image bytes (JPG)
- 🔐 Includes SHA256 hashing for image verification
- 🔊 Optional TTS playback via `AndroidTTS` integration
- 🧠 Parses and displays JSON responses in Unity UI
- 🧪 Includes ping test for server connectivity

---

## 🧰 Components

- `QuestLLaVAClient.cs`: Main MonoBehaviour script for WebSocket communication
- `LLaVAResponse`: Response structure for parsed server output
- `LlavaHeader`: Struct for request metadata (prompt, image length, temperature, etc.)
- `ServerReply`: Lightweight helper for simplified JSON parsing

---

## 🖼️ Image + Prompt Flow

1. Capture or load a JPG image
2. Construct a `LlavaHeader` with metadata
3. Send header + image bytes over WebSocket
4. Receive and parse the response
5. Display result in UI and optionally speak it via TTS

---

## 🛠️ Usage

1. Attach `QuestLLaVAClient` to a Unity GameObject
2. Assign `TMP_InputField` and `TextMeshProUGUI` for UI (optional)
3. Set `serverUri` to your LLaVA server (e.g., `ws://192.168.2.29:19111/llava`)
4. Call `SendPrompt()` or `SendPromptWithImageBytes()` to trigger interaction

---

## 📡 Ping Test

Use `PingServer()` to verify connectivity with your LLaVA server. This sends an HTTP GET to `/ping` and logs the result.

---

## 🗣️ TTS Integration

If `AndroidTTS` is assigned in the inspector, responses will be spoken aloud. Toggle `showRawJsonInUI` to control whether full JSON or parsed text is shown.

---

## 🧪 Example Prompt

```csharp
client.SendPrompt("Describe this image");
```


📦 Requirements
Unity 2021+
TextMeshPro
AndroidTTS (optional)
LLaVA server running with WebSocket support
📜 License
MIT — feel free to modify and extend for your own projects.
🙌 Credits
Inspired by the LLaVA project and designed for real-time multimodal interaction in Unity environments.
Code

---

Let me know if you want to add setup instructions, screenshots, or usage demos. I can also help you package this for Unity Asset Store or GitHub release.


## Scene 1
<img width="1083" height="1031" alt="image" src="https://github.com/user-attachments/assets/6803465a-5053-4a9d-a5e5-92e45dccdcfc" />


## Scene 2
<img width="1207" height="1235" alt="image" src="https://github.com/user-attachments/assets/a9e1c3a6-dfc7-43a5-b0e5-17c2b92084ad" />

## Scene 3
<img width="877" height="1235" alt="image" src="https://github.com/user-attachments/assets/c5d2e9c5-45cc-4987-9fa3-70f34d0da98c" />


