using System;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace WorldSim.Core
{
    /// <summary>
    /// Camera -> RenderTexture -> JPEG -> MJPEG HTTP server. Attach to any Camera.
    ///
    ///   GET /stream   - multipart/x-mixed-replace MJPEG, throttled to targetFps
    ///   GET /snapshot - single JPEG
    ///
    /// Per docs/contract-catsentry-v1.md: 1280x720, ~q70, 10-15 fps, port 8089.
    /// HttpListener runs on a background accept thread; each client gets its own
    /// thread. Those threads only ever touch a locked byte[] buffer - never Unity
    /// API - so no MainThreadDispatcher hop is needed here.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class FrameStreamer : MonoBehaviour
    {
        [Header("Server")]
        public int port = 8089;

        [Header("Frame")]
        public int width = 1280;
        public int height = 720;
        [Range(1, 100)] public int jpegQuality = 70;
        [Range(1, 30)] public int targetFps = 12; // 10-15 fps per contract

        Camera _camera;
        RenderTexture _renderTexture;

        // Async readback path (preferred).
        Texture2D _asyncTex;
        bool _readbackInFlight;

        // ReadPixels fallback path - only allocated if AsyncGPUReadback ever errors.
        Texture2D _fallbackTex;
        bool _useFallback;

        float _captureInterval;
        float _timeSinceCapture;

        // Latest-frame buffer: drop, never queue. version bumps on every new frame
        // so stream clients can tell "is there something newer than what I sent".
        readonly object _frameLock = new object();
        byte[] _latestJpeg;
        int _frameVersion;

        HttpListener _listener;
        Thread _acceptThread;
        volatile bool _running;

        static readonly byte[] BoundaryLine = Encoding.ASCII.GetBytes("--frame\r\n");
        static readonly byte[] Crlf = Encoding.ASCII.GetBytes("\r\n");

        void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        void OnEnable()
        {
            _captureInterval = 1f / Mathf.Max(1, targetFps);
            _timeSinceCapture = 0f;

            _renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
            {
                name = "FrameStreamer_RT"
            };
            _camera.targetTexture = _renderTexture;

            StartServer();
        }

        void OnDisable()
        {
            StopServer();

            if (_camera != null) _camera.targetTexture = null;

            if (_renderTexture != null)
            {
                _renderTexture.Release();
                Destroy(_renderTexture);
                _renderTexture = null;
            }
            if (_asyncTex != null) { Destroy(_asyncTex); _asyncTex = null; }
            if (_fallbackTex != null) { Destroy(_fallbackTex); _fallbackTex = null; }
        }

        void Update()
        {
            _timeSinceCapture += Time.unscaledDeltaTime;
            if (_timeSinceCapture < _captureInterval) return;
            if (_readbackInFlight) return; // previous readback still pending, drop this tick

            _timeSinceCapture = 0f;
            CaptureFrame();
        }

        void CaptureFrame()
        {
            if (_useFallback)
            {
                CaptureWithReadPixels();
                return;
            }

            _readbackInFlight = true;
            UnityEngine.Rendering.AsyncGPUReadback.Request(_renderTexture, 0, OnReadbackComplete);
        }

        void OnReadbackComplete(UnityEngine.Rendering.AsyncGPUReadbackRequest req)
        {
            _readbackInFlight = false;

            if (req.hasError)
            {
                // ponytail: flip to ReadPixels for the rest of the session instead of
                // retrying async readback every frame - one bad GPU/driver combo
                // shouldn't spam failed requests forever.
                _useFallback = true;
                return;
            }

            try
            {
                if (_asyncTex == null)
                    _asyncTex = new Texture2D(width, height, TextureFormat.RGBA32, false);

                // RT is ARGB32 -> readback into an RGBA32 Texture2D is the standard
                // AsyncGPUReadback pairing; LoadRawTextureData avoids needing
                // GraphicsFormat/EncodeArrayToJPG plumbing entirely.
                _asyncTex.LoadRawTextureData(req.GetData<byte>());
                _asyncTex.Apply(false);

                byte[] jpeg = _asyncTex.EncodeToJPG(jpegQuality);
                PublishFrame(jpeg);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"FrameStreamer: async readback encode failed, falling back to ReadPixels. {e.Message}");
                _useFallback = true;
            }
        }

        void CaptureWithReadPixels()
        {
            if (_fallbackTex == null)
                _fallbackTex = new Texture2D(width, height, TextureFormat.RGB24, false);

            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = _renderTexture;
            _fallbackTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            _fallbackTex.Apply(false);
            RenderTexture.active = prevActive;

            byte[] jpeg = _fallbackTex.EncodeToJPG(jpegQuality);
            PublishFrame(jpeg);
        }

        void PublishFrame(byte[] jpeg)
        {
            lock (_frameLock)
            {
                _latestJpeg = jpeg;
                _frameVersion++;
            }
        }

        byte[] GetLatestFrame(out int version)
        {
            lock (_frameLock)
            {
                version = _frameVersion;
                return _latestJpeg;
            }
        }

        // ---- HTTP server (background threads - no Unity API calls past this point) ----

        void StartServer()
        {
            _listener = new HttpListener();
            // ponytail: no auth, no TLS - contract says LAN/tailnet only, same as
            // the mosquitto broker. Matches a real security cam's trust model here.
            _listener.Prefixes.Add($"http://+:{port}/");

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                // "+"  (all interfaces) needs a URL ACL reservation or admin rights
                // on Windows. Fall back to localhost-only so Play mode still works;
                // see SETUP.md for the netsh command to get LAN access back.
                Debug.LogWarning("FrameStreamer: bind to http://+:" + port + "/ was denied " +
                    "(Windows URL ACL / admin rights). Falling back to localhost-only. " +
                    "See SETUP.md if the detector needs to reach this from another machine.");
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
            }

            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "FrameStreamer.Accept" };
            _acceptThread.Start();
        }

        void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch (Exception) { /* already stopped */ }
            try { _listener?.Close(); } catch (Exception) { /* already closed */ }
            _listener = null;
            // AcceptLoop exits on its own once GetContext() throws on the closed listener.
        }

        void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext(); // blocks until a request or listener.Stop()
                }
                catch (Exception)
                {
                    break; // listener stopped/disposed
                }

                var clientThread = new Thread(() => HandleClient(ctx)) { IsBackground = true, Name = "FrameStreamer.Client" };
                clientThread.Start();
            }
        }

        void HandleClient(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
                if (path == "/snapshot")
                    ServeSnapshot(ctx);
                else if (path == "/stream" || path == "")
                    ServeStream(ctx);
                else
                    ctx.Response.StatusCode = 404;
            }
            catch (Exception)
            {
                // client disconnected mid-write, or similar - nothing to do
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch (Exception) { }
            }
        }

        void ServeSnapshot(HttpListenerContext ctx)
        {
            byte[] frame = GetLatestFrame(out _);
            if (frame == null)
            {
                ctx.Response.StatusCode = 503; // no frame captured yet
                return;
            }
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.ContentLength64 = frame.Length;
            ctx.Response.OutputStream.Write(frame, 0, frame.Length);
        }

        void ServeStream(HttpListenerContext ctx)
        {
            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
            ctx.Response.SendChunked = true;
            var outStream = ctx.Response.OutputStream;

            int lastVersion = -1;
            while (_running)
            {
                byte[] frame = GetLatestFrame(out int version);
                if (frame == null || version == lastVersion)
                {
                    Thread.Sleep(10);
                    continue;
                }
                lastVersion = version;

                byte[] header = Encoding.ASCII.GetBytes(
                    $"Content-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");

                outStream.Write(BoundaryLine, 0, BoundaryLine.Length);
                outStream.Write(header, 0, header.Length);
                outStream.Write(frame, 0, frame.Length);
                outStream.Write(Crlf, 0, Crlf.Length);
                outStream.Flush(); // throws if the client went away - caught by HandleClient
            }
        }
    }
}
