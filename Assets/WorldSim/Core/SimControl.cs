using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WorldSim.Core
{
    /// <summary>
    /// The control surface an external harness drives the sim through:
    ///
    ///   GET /doctor           - engine version, scene identity, capture readiness
    ///   GET /inspect          - sim state as plain JSON (what proves a feature exists)
    ///   GET /step?n=N         - advance exactly N frames, then return
    ///   GET /reset?seed=S     - reload the scene and reseed, restoring initial state
    ///   GET /capture          - PNG of the streaming camera at the current frame
    ///
    /// Separate from <see cref="FrameStreamer"/> on purpose and on its own port:
    /// that one serves video to the detector under test, this one drives the sim
    /// for a test harness. Different consumers, different lifetimes, no coupling.
    ///
    /// <b>Determinism.</b> Stepping sets <c>Time.captureDeltaTime</c>, which
    /// decouples the sim clock from wall clock entirely - frame N is always the
    /// same simulated instant no matter how slow the host is. /reset reloads the
    /// scene rather than nudging transforms, because the contract's reset has to
    /// restore state built at load time, and reseeding the RNG while leaving
    /// spawned entities where they lie is the classic determinism bug.
    ///
    /// Every Unity API call happens on the main thread. HTTP threads only ever
    /// enqueue work and wait for it.
    /// </summary>
    public class SimControl : MonoBehaviour
    {
        [Header("Server")]
        public int port = 8090;

        [Header("Determinism")]
        /// <summary>Fixed simulated seconds per frame while stepping. 1/60.</summary>
        public float captureDeltaTime = 1f / 60f;
        /// <summary>Seed applied on Awake and on every /reset that omits one.</summary>
        public int defaultSeed = 12345;

        [Header("Capture")]
        public Camera captureCamera;
        public int captureWidth = 1280;
        public int captureHeight = 720;

        /// <summary>Frames advanced since the last reset. The harness's frame numbers.</summary>
        public long Frame => _frame;

        long _frame;
        HttpListener _listener;
        Thread _acceptThread;
        volatile bool _running;

        // Main-thread work queue. HTTP threads push a job and block on its handle.
        readonly Queue<Job> _jobs = new Queue<Job>();
        readonly object _jobLock = new object();

        class Job
        {
            public Func<string> Work;          // returns a JSON body, runs on the main thread
            public Func<byte[]> BinaryWork;    // or produces bytes (PNG)
            public string Result;
            public byte[] Binary;
            public string Error;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        static SimControl _instance;
        volatile bool _sceneReloaded;

        // Free-running by default. The detector-facing surfaces (MJPEG, MQTT) expect a
        // live sim, so pausing is opt-in: it happens only when a harness asks to step.
        volatile bool _paused;
        int _stepsRemaining;

        void Awake()
        {
            // /reset reloads the scene, which destroys everything in it - including
            // this component, if it lived in the scene. The control surface has to
            // outlive the thing it controls, or the HTTP server dies mid-request and
            // the harness sees an empty reply. Surviving the load means the scene's
            // own copy arrives as a duplicate on every reload; the first one wins.
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            transform.SetParent(null);
            DontDestroyOnLoad(gameObject);
            SceneManager.sceneLoaded += OnSceneLoaded;

            ResolveCaptureCamera();
            UnityEngine.Random.InitState(defaultSeed);
            Time.captureDeltaTime = captureDeltaTime;
        }

        void OnDestroy()
        {
            if (_instance == this)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                _instance = null;
            }
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // The serialized camera reference pointed into the scene we just threw
            // away, so it is now a dangling Unity null. Re-resolve against the new one.
            captureCamera = null;
            ResolveCaptureCamera();
            Time.captureDeltaTime = captureDeltaTime;
            Interlocked.Exchange(ref _frame, 0);
            // A reset lands PAUSED, on frame 0. If it handed back a live sim, an
            // uncontrolled number of frames would elapse between the reset reply and
            // whatever came next, and two runs would start from different states -
            // measured as ~0.015 of position drift before this was pinned down.
            // /play resumes free-running for the detector-facing path.
            Interlocked.Exchange(ref _stepsRemaining, 0);
            SetPaused(true);
            _sceneReloaded = true;
        }

        /// <summary>Prefers the camera that feeds the MJPEG stream, so a captured frame
        /// is the same view the detector under test sees rather than a display twin.</summary>
        void ResolveCaptureCamera()
        {
            if (captureCamera != null) return;

            var streamer = FindFirstObjectByType<FrameStreamer>();
            if (streamer != null)
            {
                var cam = streamer.GetComponent<Camera>();
                if (cam != null) { captureCamera = cam; return; }
            }
            captureCamera = Camera.main;
        }

        void OnEnable()
        {
            // The scene's duplicate copy still gets OnEnable before Awake's Destroy
            // lands at end of frame. Without this it would try to bind the port the
            // surviving instance already holds, and its OnDisable would then reset the
            // capture clock out from under a run in progress.
            if (_instance != this) return;
            StartServer();
        }

        void OnDisable()
        {
            if (_instance != this) return;
            StopServer();
            // Leave the clock as we found it: a harness run must not change how a
            // normal Play session behaves after it stops. Restoring timeScale matters
            // most - a run that ended while stepped would otherwise leave the editor
            // frozen with no visible reason why.
            Time.captureDeltaTime = 0f;
            Time.timeScale = 1f;
            FailPendingJobs("SimControl disabled");
        }

        void Update()
        {
            // Paused means the sim clock stands still: frames only count when the sim
            // actually advances, or "capture at frame N" would mean nothing.
            if (!_paused)
            {
                _frame++;
                if (_stepsRemaining > 0 && Interlocked.Decrement(ref _stepsRemaining) == 0)
                    SetPaused(true);
            }
            DrainJobs();
        }

        /// <summary>Freezing via timeScale stops everything time-driven at once -
        /// NavMeshAgents, particles, physics - which is what makes a captured frame
        /// hold still long enough to be exact.</summary>
        void SetPaused(bool paused)
        {
            _paused = paused;
            Time.timeScale = paused ? 0f : 1f;
        }

        // ---- main-thread job pump ----

        void DrainJobs()
        {
            // Bounded per frame: a /step job must not be completed by the same
            // Update that enqueued it, or "advance n frames" would return early.
            int pending;
            lock (_jobLock) pending = _jobs.Count;

            for (int i = 0; i < pending; i++)
            {
                Job job;
                lock (_jobLock)
                {
                    if (_jobs.Count == 0) return;
                    job = _jobs.Dequeue();
                }

                try
                {
                    if (job.BinaryWork != null) job.Binary = job.BinaryWork();
                    else job.Result = job.Work();
                }
                catch (Exception e)
                {
                    job.Error = e.Message;
                }
                job.Done.Set();
            }
        }

        void FailPendingJobs(string why)
        {
            lock (_jobLock)
            {
                while (_jobs.Count > 0)
                {
                    Job job = _jobs.Dequeue();
                    job.Error = why;
                    job.Done.Set();
                }
            }
        }

        /// <summary>Runs <paramref name="work"/> on the main thread and waits for it.
        /// Called only from HTTP threads.</summary>
        Job Dispatch(Func<string> work, Func<byte[]> binaryWork, int timeoutMs)
        {
            var job = new Job { Work = work, BinaryWork = binaryWork };
            lock (_jobLock) _jobs.Enqueue(job);
            if (!job.Done.Wait(timeoutMs))
                job.Error = "timed out waiting for the main thread";
            return job;
        }

        // ---- HTTP ----

        void StartServer()
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SimControl: could not bind port {port} - {e.Message}");
                _listener = null;
                return;
            }

            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "SimControl.Accept" };
            _acceptThread.Start();
        }

        void StopServer()
        {
            _running = false;
            try { _listener?.Stop(); } catch (Exception) { }
            try { _listener?.Close(); } catch (Exception) { }
            _listener = null;
        }

        void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (Exception) { break; }

                var t = new Thread(() => HandleClient(ctx)) { IsBackground = true, Name = "SimControl.Client" };
                t.Start();
            }
        }

        void HandleClient(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimEnd('/');
                switch (path)
                {
                    case "/doctor": ServeJson(ctx, Dispatch(Doctor, null, 10000)); break;
                    case "/inspect": ServeJson(ctx, Dispatch(Inspect, null, 10000)); break;
                    case "/capture": ServePng(ctx, Dispatch(null, CapturePng, 20000)); break;

                    case "/step":
                        {
                            int n = QueryInt(ctx, "n", 1);
                            ServeJson(ctx, StepFrames(n));
                            break;
                        }

                    case "/reset":
                        {
                            int seed = QueryInt(ctx, "seed", defaultSeed);
                            ServeJson(ctx, ResetSim(seed));
                            break;
                        }

                    // Stepping leaves the sim frozen. /play hands it back to the
                    // detector-facing path, which needs a live clock.
                    case "/play":
                        ServeJson(ctx, Dispatch(() =>
                        {
                            Interlocked.Exchange(ref _stepsRemaining, 0);
                            SetPaused(false);
                            return "{\"paused\":false,\"frame\":" + Interlocked.Read(ref _frame) + "}";
                        }, null, 10000));
                        break;

                    case "/pause":
                        ServeJson(ctx, Dispatch(() =>
                        {
                            Interlocked.Exchange(ref _stepsRemaining, 0);
                            SetPaused(true);
                            return "{\"paused\":true,\"frame\":" + Interlocked.Read(ref _frame) + "}";
                        }, null, 10000));
                        break;

                    default:
                        ctx.Response.StatusCode = 404;
                        break;
                }
            }
            catch (Exception)
            {
                try { ctx.Response.StatusCode = 500; } catch (Exception) { }
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch (Exception) { }
            }
        }

        static int QueryInt(HttpListenerContext ctx, string key, int fallback)
        {
            string raw = ctx.Request.QueryString[key];
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        void ServeJson(HttpListenerContext ctx, Job job)
        {
            if (job.Error != null)
            {
                ctx.Response.StatusCode = 500;
                WriteBody(ctx, "application/json", Encoding.UTF8.GetBytes(
                    "{\"error\":" + Quote(job.Error) + "}"));
                return;
            }
            ctx.Response.StatusCode = 200;
            WriteBody(ctx, "application/json", Encoding.UTF8.GetBytes(job.Result));
        }

        void ServePng(HttpListenerContext ctx, Job job)
        {
            if (job.Error != null || job.Binary == null)
            {
                ctx.Response.StatusCode = 500;
                WriteBody(ctx, "application/json", Encoding.UTF8.GetBytes(
                    "{\"error\":" + Quote(job.Error ?? "no frame") + "}"));
                return;
            }
            ctx.Response.StatusCode = 200;
            WriteBody(ctx, "image/png", job.Binary);
        }

        static void WriteBody(HttpListenerContext ctx, string contentType, byte[] body)
        {
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
        }

        // ---- verbs (main thread unless noted) ----

        /// <summary>Blocks the calling HTTP thread until the main loop has run n more
        /// frames. Called from an HTTP thread - reads a volatile-enough long written
        /// only by Update.</summary>
        Job StepFrames(int n)
        {
            var job = new Job();
            if (n < 0)
            {
                job.Error = "n must be >= 0";
                return job;
            }

            long target = Interlocked.Read(ref _frame) + n;

            // Arm the counter, then release the clock. Update stops itself again the
            // moment the last of the n frames is done, so the sim comes to rest on
            // exactly frame `target` instead of drifting past it while this thread
            // notices - which is the difference between "capture at frame N" being a
            // guarantee and being a coincidence.
            Interlocked.Exchange(ref _stepsRemaining, n);
            Dispatch(() => { SetPaused(n == 0); return "{}"; }, null, 10000);

            // Generous: a cold frame can be slow, but this must never hang forever.
            int timeoutMs = 30000 + n * 200;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Interlocked.Read(ref _frame) < target)
            {
                if (!_running) { job.Error = "sim stopped while stepping"; return job; }
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    job.Error = $"only reached frame {Interlocked.Read(ref _frame)} of {target}";
                    return job;
                }
                Thread.Sleep(2);
            }

            job.Result = "{\"frame\":" + Interlocked.Read(ref _frame) +
                         ",\"stepped\":" + n + ",\"paused\":true}";
            return job;
        }

        /// <summary>Reload the scene and reseed. Reloading is the point: it rebuilds
        /// entities created at load time, which nudging transforms would not.</summary>
        Job ResetSim(int seed)
        {
            _sceneReloaded = false;

            Job load = Dispatch(() =>
            {
                // Seed before the load so anything randomising in Awake/Start on the
                // new scene draws from the seeded stream. Seeding afterwards would be
                // too late for exactly the load-time state reset is supposed to fix.
                UnityEngine.Random.InitState(seed);
                Time.captureDeltaTime = captureDeltaTime;
                SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex, LoadSceneMode.Single);
                return "{}";
            }, null, 20000);

            if (load.Error != null) return load;

            // LoadScene completes at the end of the frame, so the objects alive right
            // now are still the old ones. Wait for the actual sceneLoaded callback
            // rather than sleeping a guess, or /inspect reports the scene we discarded.
            var settle = new Job();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!_sceneReloaded)
            {
                if (!_running) { settle.Error = "sim stopped during reset"; return settle; }
                if (sw.ElapsedMilliseconds > 20000)
                {
                    settle.Error = "scene did not finish loading within 20s";
                    return settle;
                }
                Thread.Sleep(2);
            }

            settle.Result = "{\"frame\":" + Interlocked.Read(ref _frame) +
                            ",\"seed\":" + seed + ",\"paused\":true}";
            return settle;
        }

        string Doctor()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"engine\":\"unity\",");
            sb.Append("\"unityVersion\":").Append(Quote(Application.unityVersion)).Append(',');
            sb.Append("\"scene\":").Append(Quote(SceneManager.GetActiveScene().name)).Append(',');
            sb.Append("\"graphicsDevice\":").Append(Quote(SystemInfo.graphicsDeviceType.ToString())).Append(',');
            sb.Append("\"captureCamera\":").Append(Quote(captureCamera != null ? captureCamera.name : "")).Append(',');
            sb.Append("\"canCapture\":").Append(captureCamera != null ? "true" : "false").Append(',');
            sb.Append("\"captureDeltaTime\":").Append(Fmt(Time.captureDeltaTime)).Append(',');
            sb.Append("\"frame\":").Append(_frame);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Sim state as plain JSON. Deliberately structural, not a description:
        /// a gate asserts against these keys, so anything a test needs to prove has to
        /// be a field here. Position is included because a count alone cannot prove a
        /// cat went anywhere.</summary>
        string Inspect()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"frame\":").Append(_frame).Append(',');
            // Derived from the frame count, not Time.time: Time.time keeps climbing
            // across a scene reload, so using it would make two identical runs report
            // different state and no run would ever compare equal to another.
            sb.Append("\"simTime\":").Append(Fmt(_frame * captureDeltaTime)).Append(',');
            sb.Append("\"paused\":").Append(_paused ? "true" : "false").Append(',');

            var cats = SimInspectable.FindAll();
            sb.Append("\"entities\":[");
            for (int i = 0; i < cats.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(cats[i].InspectJson());
            }
            sb.Append("],");
            sb.Append("\"entityCount\":").Append(cats.Count);
            sb.Append('}');
            return sb.ToString();
        }

        byte[] CapturePng()
        {
            if (captureCamera == null) return null;

            var rt = new RenderTexture(captureWidth, captureHeight, 24);
            var tex = new Texture2D(captureWidth, captureHeight, TextureFormat.RGB24, false);
            RenderTexture prevTarget = captureCamera.targetTexture;
            RenderTexture prevActive = RenderTexture.active;

            try
            {
                captureCamera.targetTexture = rt;
                captureCamera.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, captureWidth, captureHeight), 0, 0);
                tex.Apply();
                return tex.EncodeToPNG();
            }
            finally
            {
                captureCamera.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                Destroy(tex);
                rt.Release();
                Destroy(rt);
            }
        }

        // ---- json helpers (no dependency on JsonUtility: these are dynamic shapes) ----

        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string Fmt(float f) =>
            f.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>Implemented by anything that wants to appear in /inspect. Keeping this
    /// in Core (rather than SimControl knowing about CatAgent) is what stops the
    /// control surface from becoming scenario-specific.</summary>
    public interface ISimInspectable
    {
        string InspectJson();
    }

    public static class SimInspectable
    {
        public static List<ISimInspectable> FindAll()
        {
            var found = new List<ISimInspectable>();
            foreach (MonoBehaviour mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsSortMode.None))
            {
                if (mb is ISimInspectable inspectable) found.Add(inspectable);
            }
            // Stable order, or /inspect output would depend on scene traversal order
            // and no two runs would compare equal.
            found.Sort((a, b) => string.CompareOrdinal(
                ((MonoBehaviour)a).gameObject.name, ((MonoBehaviour)b).gameObject.name));
            return found;
        }
    }
}
