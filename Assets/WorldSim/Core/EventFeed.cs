using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace WorldSim.Core
{
    /// <summary>Colour bucket for a feed line / ping.</summary>
    public enum LogKind
    {
        Info,   // telemetry, status, anything unclassified - grey
        Detect, // catsentry/event from the detector - cyan
        Fire,   // catsentry/deterrent/fire - amber
        Ack,    // catsentry/deterrent/ack - green
        Cat     // locally observed cat behaviour - white
    }

    /// <summary>
    /// In-frame decision overlay: a scrolling event feed plus transient world-space
    /// "ping" popups, drawn as real geometry so it survives the trip through
    /// FrameStreamer's RenderTexture.
    ///
    /// ponytail: OnGUI/IMGUI and Screen-Space-Overlay canvases are composited to the
    /// backbuffer, never to a camera's targetTexture, so neither shows up in the MJPEG
    /// stream. Everything here is TextMesh + MeshRenderer parented to the streaming
    /// camera instead - world-space geometry the camera actually renders. TextMesh also
    /// avoids needing a Canvas or a TextMeshPro asset import.
    ///
    /// Nothing in here is allowed to throw into the sim: every payload parse, every
    /// reflection call and the whole cat poll are guarded.
    /// </summary>
    public class EventFeed : MonoBehaviour
    {
        // ---- required API (wired from the scene builder) ----

        [Header("MQTT")]
        public MqttBridge bridge;                       // assign in inspector; auto-finds one in the scene if left blank
        public string topicFilter = "catsentry/#";      // one wildcard sub sees detector + turret + telemetry

        [Header("Feed")]
        public int maxLines = 12;                       // entries retained; one entry can render as up to 3 rows
        public float pingLifetimeSec = 2.5f;

        // ---- optional tuning (safe defaults; auto-resolved when left blank) ----

        [Header("Placement")]
        [Tooltip("Camera the panel is parented to. Blank = the camera carrying FrameStreamer, else Camera.main, else the first enabled camera.")]
        public Camera targetCamera;
        [Tooltip("Floor whose localScale spans the room, used to map normalized fire targets to world space. Blank = taken from VirtualTurret, else a GameObject named \"Floor\".")]
        public Transform floorReference;
        [Range(0.18f, 0.6f)] public float panelWidthFraction = 0.36f;
        [Tooltip("Metres in front of the camera. Must clear the near clip plane.")]
        public float panelDistance = 0.6f;
        [Range(0f, 1f)] public float panelBackingAlpha = 0.62f;

        [Header("Detector score")]
        [Tooltip("Grade the detector live against the sim's own ground truth.")]
        public bool showScore = true;
        [Tooltip("Seconds after a misbehavior squat ends within which a fire still counts as a catch.")]
        public float scoreGraceSec = 2f;

        [Header("Pings")]
        public float pingSizeMeters = 0.10f;
        public float pingRiseMeters = 0.35f;
        public bool watchCats = true;
        public float catPingHeightMeters = 0.45f;

        // ---- text metrics ----

        // TextMesh generates its mesh at characterSize * 0.1 local units per font
        // pixel; every size below is derived from that, so the panel scales itself
        // to whatever FOV / aspect the streaming camera ends up with.
        const int FontPixelSize = 48;
        const float UnitsPerFontPixel = 0.1f;
        const float LineSpacing = 1.15f;
        const int WrapChars = 48;      // characters per rendered row (see WidthSample)
        const int MaxRowsPerEntry = 3; // enough for a full fire reason (level + target + duration + reason)
        const int MaxPings = 12;
        const string WidthSample = "00:00:00 FIRE sound target 0.50,0.50 1500ms squa"; // exactly WrapChars long
        // Fallback if the font refuses to report metrics: average advance as a
        // fraction of the font pixel size for a mixed alphanumeric line.
        const float FallbackAdvanceEm = 0.55f;

        static readonly string[] KindHex =
        {
            "9AA0A6FF", // Info   - grey
            "4DD9FFFF", // Detect - cyan
            "FFB020FF", // Fire   - amber
            "5FE08AFF", // Ack    - green
            "FFFFFFFF"  // Cat    - white
        };

        struct Entry
        {
            public string Clock;
            public string Body;
            public LogKind Kind;
        }

        struct ActivePing
        {
            public Transform Root;
            public TextMesh Text;
            public Vector3 BasePos;
            public Color BaseColor;
            public float Born;
            public float Lifetime;
        }

        readonly List<Entry> _entries = new List<Entry>();
        readonly List<ActivePing> _pings = new List<ActivePing>();
        readonly List<string> _rowScratch = new List<string>();
        readonly StringBuilder _sb = new StringBuilder(2048);

        Font _font;
        Transform _panelRoot;
        TextMesh _panelText;
        MeshRenderer _panelTextRenderer;
        Transform _panelBacking;
        Material _backingMat;
        Mesh _backingMesh;

        bool _textDirty = true;
        bool _lastConnected;

        // cached layout, recomputed only when the camera's framing changes
        float _lastFov, _lastAspect, _lastDistance, _lastWidthFraction;
        bool _lastOrtho;
        float _lineHeight, _textScale, _panelLeft, _panelTop, _panelWidth, _panelZ, _pad;
        int _maxRows = 12;

        // Local width of WidthSample at characterSize 1. Seeded from the font's own
        // glyph advances, then replaced on the first frames of play by the mesh the
        // TextMesh actually generates - so the panel sizes itself correctly even if
        // the pixels-per-unit assumption above is off. -1 = calibration finished.
        float _sampleUnits;
        float _samplePixels;
        float _unitsPerFontPixel = UnitsPerFontPixel;
        int _calibFrames = -1;

        // duplicate suppression: a retry from the publisher, or a QoS-1 redelivery,
        // can put the same PUBLISH on screen twice, which looks like a bug on camera.
        // (Overlapping subscriptions used to do this too - MqttBridge now keeps only
        // a covering set on the wire, so that source is gone.)
        string _lastTopic, _lastPayload;
        float _lastMessageTime = -1f;
        const float DuplicateWindowSec = 0.25f;

        float _nextDetectPing;
        const float DetectPingCooldownSec = 1f;

        // ---- cat watching (reflection - see ResolveCatType) ----
        Type _catType;
        MethodInfo _allCatsGetter;
        MethodInfo _behaviorGetter;
        UnityEngine.Object[] _catFallback;
        float _catFallbackRefresh;
        readonly Dictionary<int, string> _behaviorState = new Dictionary<int, string>();
        bool _catWatchBroken;

        // ------------------------------------------------------------------
        // lifecycle
        // ------------------------------------------------------------------

        void Awake()
        {
            if (bridge == null) bridge = FindFirstObjectByType<MqttBridge>();
            _font = LoadFont();
            BuildPanel();
        }

        void OnEnable()
        {
            if (bridge == null)
            {
                Debug.LogWarning("EventFeed: no MqttBridge in scene - the feed will only show local events.");
            }
            else
            {
                bridge.OnMessage += HandleMessage;
                // MqttBridge replays subscriptions on every reconnect, so one call is enough.
                bridge.Subscribe(topicFilter, MqttBridge.QosAtLeastOnce);
            }
            if (_panelRoot != null) _panelRoot.gameObject.SetActive(true);
        }

        void OnDisable()
        {
            if (bridge != null) bridge.OnMessage -= HandleMessage;
            if (_panelRoot != null) _panelRoot.gameObject.SetActive(false);
            ClearPings();
        }

        void Start()
        {
            if (watchCats) ResolveCatType();
            if (floorReference == null) floorReference = ResolveFloorReference();
            Log("event feed online", LogKind.Info);
        }

        void OnDestroy()
        {
            ClearPings();
            // The panel is parented to the camera, not to this GameObject, so it
            // does not go away on its own when the component is destroyed.
            if (_panelRoot != null) Destroy(_panelRoot.gameObject);
            if (_backingMat != null) Destroy(_backingMat);
            if (_backingMesh != null) Destroy(_backingMesh);
        }

        void LateUpdate()
        {
            Camera cam = ResolveCamera();
            if (cam == null) return;

            UpdateLayout(cam);
            PollCats();
            UpdateConnectionHeader();
            if (_calibFrames >= 0) Calibrate();
            else if (_textDirty) RebuildText();
            UpdatePings(cam);
        }

        // ------------------------------------------------------------------
        // public API
        // ------------------------------------------------------------------

        /// <summary>Push a line onto the feed. Safe to call from anywhere on the main thread.</summary>
        public void Log(string line, LogKind kind)
        {
            if (string.IsNullOrEmpty(line)) return;

            _entries.Add(new Entry
            {
                Clock = DateTime.Now.ToString("HH:mm:ss"),
                Body = Sanitize(line),
                Kind = kind
            });

            int keep = Mathf.Clamp(maxLines, 1, 60);
            if (_entries.Count > keep) _entries.RemoveRange(0, _entries.Count - keep);
            _textDirty = true;
        }

        /// <summary>
        /// Spawn a short-lived world-space label at <paramref name="worldPos"/> that rises
        /// and fades over <see cref="pingLifetimeSec"/>, then destroys itself.
        /// </summary>
        public void Ping(Vector3 worldPos, string text, LogKind kind)
        {
            if (_font == null || string.IsNullOrEmpty(text)) return;

            while (_pings.Count >= MaxPings) DestroyPingAt(0);

            var go = new GameObject("EventPing");
            go.transform.position = worldPos;

            TextMesh tm = MakeText(go, TextAnchor.MiddleCenter, TextAlignment.Center);
            tm.text = Sanitize(text);
            tm.richText = false;
            Color c = KindColor(kind);
            tm.color = c;
            // Pings live out in the room, so they are sized in metres rather than
            // derived from the panel's frame-relative layout.
            float scale = pingSizeMeters / (FontPixelSize * _unitsPerFontPixel);
            go.transform.localScale = new Vector3(scale, scale, scale);

            _pings.Add(new ActivePing
            {
                Root = go.transform,
                Text = tm,
                BasePos = worldPos,
                BaseColor = c,
                Born = Time.time,
                Lifetime = Mathf.Max(0.2f, pingLifetimeSec)
            });
        }

        // ------------------------------------------------------------------
        // MQTT
        // ------------------------------------------------------------------

        void HandleMessage(string topic, string payload)
        {
            try
            {
                if (IsDuplicate(topic, payload)) return;

                switch (topic)
                {
                    case "catsentry/deterrent/fire": HandleFire(payload); return;
                    case "catsentry/deterrent/ack": HandleAck(payload); return;
                    case "catsentry/telemetry": HandleTelemetry(payload); return;
                    case "catsentry/event": HandleDetectorEvent(payload); return;
                    case "catsentry/groundtruth": HandleGroundTruth(payload); return;
                }

                Log(LeafOf(topic) + " " + Truncate(payload, 96), LogKind.Info);
            }
            catch (Exception e)
            {
                // A malformed payload must never take the sim down with it.
                Log(LeafOf(topic) + " unreadable payload", LogKind.Info);
                Debug.LogWarning("EventFeed: failed to render '" + topic + "'. " + e.Message);
            }
        }

        bool IsDuplicate(string topic, string payload)
        {
            float now = Time.time;
            bool dup = _lastMessageTime >= 0f
                       && now - _lastMessageTime < DuplicateWindowSec
                       && string.Equals(topic, _lastTopic, StringComparison.Ordinal)
                       && string.Equals(payload, _lastPayload, StringComparison.Ordinal);
            _lastTopic = topic;
            _lastPayload = payload;
            _lastMessageTime = now;
            return dup;
        }

        void HandleFire(string payload)
        {
            // Scored on arrival, before parsing: a fire the detector could not even
            // format correctly still fired the deterrent at a real cat.
            NoteFireForScore();

            FeedFireMsg msg = TryParse<FeedFireMsg>(payload);
            if (msg == null)
            {
                Log("FIRE " + Truncate(payload, 88), LogKind.Fire);
                PingAtPanel("FIRE", LogKind.Fire);
                return;
            }

            string level = Blank(msg.level) ? "?" : msg.level;
            bool haveTarget = msg.target != null && msg.target.Length >= 2;

            var sb = new StringBuilder(80);
            sb.Append("FIRE ").Append(level);
            if (haveTarget)
                sb.Append("  target ").Append(msg.target[0].ToString("0.00")).Append(',').Append(msg.target[1].ToString("0.00"));
            if (msg.duration_ms > 0) sb.Append("  ").Append(msg.duration_ms).Append("ms");
            if (!Blank(msg.reason)) sb.Append("  ").Append(msg.reason);
            Log(sb.ToString(), LogKind.Fire);

            Vector3 world;
            if (haveTarget && TryNormalizedToWorld(msg.target, out world))
                Ping(world + Vector3.up * 0.25f, "FIRE " + level, LogKind.Fire);
            else
                PingAtPanel("FIRE " + level, LogKind.Fire);
        }

        void HandleAck(string payload)
        {
            FeedAckMsg msg = TryParse<FeedAckMsg>(payload);
            if (msg == null)
            {
                Log("ACK " + Truncate(payload, 88), LogKind.Ack);
                return;
            }

            var sb = new StringBuilder(64);
            sb.Append("ACK ").Append(Blank(msg.level) ? "?" : msg.level);
            sb.Append(' ').Append(msg.ok ? "ok" : "FAILED");
            if (!Blank(msg.detail)) sb.Append(" - ").Append(msg.detail);
            Log(sb.ToString(), LogKind.Ack);
        }

        void HandleTelemetry(string payload)
        {
            TelemetryMsg msg = TryParse<TelemetryMsg>(payload);
            if (msg == null || (Blank(msg.source) && Blank(msg.status)))
            {
                Log("heartbeat " + Truncate(payload, 80), LogKind.Info);
                return;
            }
            Log("heartbeat " + (Blank(msg.source) ? "?" : msg.source) + " " + (Blank(msg.status) ? "?" : msg.status), LogKind.Info);
        }

        /// <summary>catsentry/groundtruth - what a cat is ACTUALLY doing, published by
        /// the sim itself. Reads as "Cat_Gray sniff" / "Cat_Black box_use (in box)"
        /// so the truth can be eyeballed against the detector's calls right above it
        /// in the same feed.</summary>
        void HandleGroundTruth(string payload)
        {
            GroundTruthMsg msg = TryParse<GroundTruthMsg>(payload);
            if (msg == null || Blank(msg.cat) || Blank(msg.behavior))
            {
                Log("groundtruth " + Truncate(payload, 80), LogKind.Info);
                return;
            }

            NoteBehaviorForScore(msg.cat, msg.behavior);

            // Wandering is the resting state; logging every return to it buries the
            // three behaviours that actually matter.
            if (msg.behavior == "wander") return;

            // Misbehaviour is the one the detector is supposed to catch, so give it
            // the same colour the fire lines use rather than burying it in grey.
            LogKind kind = msg.behavior == "misbehavior" ? LogKind.Fire : LogKind.Cat;
            string where = msg.in_box ? " (in box)" : string.Empty;
            Log("truth " + msg.cat + " " + msg.behavior + where, kind);
        }

        void HandleDetectorEvent(string payload)
        {
            // The detector's schema is not frozen past contract-catsentry-v1's
            // example, so pretty-print whatever fields showed up and fall back to
            // the raw payload rather than dropping an event we don't recognise.
            FeedEventMsg msg = TryParse<FeedEventMsg>(payload);
            if (msg == null || Blank(msg.type))
            {
                Log("EVENT " + Truncate(payload, 88), LogKind.Detect);
                return;
            }

            var sb = new StringBuilder(96);
            sb.Append(msg.type.ToUpperInvariant());
            if (!Blank(msg.cat_id)) sb.Append(' ').Append(msg.cat_id);
            if (!Blank(msg.zone)) sb.Append("  zone ").Append(msg.zone);
            if (msg.confidence > 0f) sb.Append("  conf ").Append(msg.confidence.ToString("0.00"));
            if (msg.bbox != null && msg.bbox.Length >= 2)
            {
                sb.Append("  at ").Append(msg.bbox[0].ToString("0.00")).Append(',').Append(msg.bbox[1].ToString("0.00"));
                if (msg.bbox.Length >= 4)
                    sb.Append(" ").Append(msg.bbox[2].ToString("0.00")).Append('x').Append(msg.bbox[3].ToString("0.00"));
            }
            Log(sb.ToString(), LogKind.Detect);

            // Every event gets a line, but only state *transitions* get a ping.
            // A detector publishing cat_detected at a few Hz would otherwise churn
            // a dozen overlapping labels and their GameObjects inside the capture
            // loop. The throttle covers a detector that repeats a transition.
            if (!IsPingWorthy(msg.type)) return;
            if (Time.time < _nextDetectPing) return;
            if (msg.bbox == null || msg.bbox.Length < 2) return;

            Vector3 world;
            if (TryNormalizedToWorld(msg.bbox, out world))
            {
                _nextDetectPing = Time.time + DetectPingCooldownSec;
                Ping(world + Vector3.up * 0.2f, msg.type, LogKind.Detect);
            }
        }

        /// <summary>Event types worth a popup: the ones that mark a change of state,
        /// not the continuous "still see a cat" stream.</summary>
        static bool IsPingWorthy(string type)
        {
            if (string.IsNullOrEmpty(type)) return false;
            switch (type.ToLowerInvariant())
            {
                case "squat_suspected":
                case "zone_enter":
                case "deterrent_fired":
                    return true;
                default:
                    return false;
            }
        }

        static T TryParse<T>(string payload) where T : class
        {
            if (string.IsNullOrEmpty(payload)) return null;
            try { return JsonUtility.FromJson<T>(payload); }
            catch (Exception) { return null; } // not JSON, or not shaped like T
        }

        // ------------------------------------------------------------------
        // cats - resolved by reflection, never by assembly reference
        // ------------------------------------------------------------------

        // WorldSim.CatBasement references WorldSim.Core, so Core referencing
        // CatAgent back would be a circular asmdef dependency and nothing in the
        // project would compile. Reflection keeps the dependency one-directional:
        // if the scenario assembly is absent (or CatAgent is renamed) the feed
        // simply stops watching cats instead of failing to build. CatAgent is a
        // MonoBehaviour, so name/transform come through the base type for free and
        // only IsSquatting actually needs a reflected call.
        void ResolveCatType()
        {
            try
            {
                _catType = Type.GetType("WorldSim.CatBasement.CatAgent, WorldSim.CatBasement");
                if (_catType == null)
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < assemblies.Length && _catType == null; i++)
                    {
                        try { _catType = assemblies[i].GetType("WorldSim.CatBasement.CatAgent", false); }
                        catch (Exception) { /* dynamic or unloadable assembly - skip */ }
                    }
                }

                if (_catType == null)
                {
                    _catWatchBroken = true;
                    return;
                }

                PropertyInfo all = _catType.GetProperty("AllCats", BindingFlags.Public | BindingFlags.Static);
                _allCatsGetter = all != null ? all.GetGetMethod() : null;

                // CurrentBehavior, not IsSquatting. IsSquatting deliberately covers
                // correct litterbox use as well as the floor squat (VirtualTurret
                // gates its flee call on it, and a deterrent must be able to startle
                // a cat that is in a box), so watching it would make the feed report
                // "started squatting" for a cat behaving perfectly - a false positive
                // in the very display meant to expose false positives.
                PropertyInfo behavior = _catType.GetProperty("CurrentBehavior", BindingFlags.Public | BindingFlags.Instance);
                _behaviorGetter = behavior != null ? behavior.GetGetMethod() : null;

                if (_behaviorGetter == null)
                {
                    _catWatchBroken = true;
                    Debug.LogWarning("EventFeed: CatAgent.CurrentBehavior not found - cat state lines disabled.");
                }
            }
            catch (Exception e)
            {
                _catWatchBroken = true;
                Debug.LogWarning("EventFeed: cat reflection setup failed, cat lines disabled. " + e.Message);
            }
        }

        void PollCats()
        {
            if (!watchCats || _catWatchBroken || _behaviorGetter == null) return;

            try
            {
                IEnumerable cats = null;
                if (_allCatsGetter != null)
                    cats = _allCatsGetter.Invoke(null, null) as IEnumerable;

                if (cats == null)
                {
                    // AllCats missing: fall back to a periodic scene scan.
                    if (Time.time >= _catFallbackRefresh)
                    {
                        _catFallbackRefresh = Time.time + 2f;
                        _catFallback = FindObjectsByType(_catType, FindObjectsSortMode.None);
                    }
                    cats = _catFallback;
                    if (cats == null) return;
                }

                if (_behaviorState.Count > 64) _behaviorState.Clear();

                foreach (object o in cats)
                {
                    var mb = o as MonoBehaviour;
                    if (mb == null) continue;

                    string now = _behaviorGetter.Invoke(mb, null) as string;
                    if (string.IsNullOrEmpty(now)) continue;
                    int id = mb.GetInstanceID();

                    string was;
                    bool known = _behaviorState.TryGetValue(id, out was);
                    if (known && was == now) continue;

                    _behaviorState[id] = now;
                    if (now == "wander") continue; // idling, and returning to idle, is not news

                    // Only misbehavior is what the detector has to catch, so it gets
                    // the alarm colour. Box use and sniffing are the negatives - worth
                    // showing, because they are what a bad detector fires on, but not
                    // worth shouting about.
                    bool bad = now == "misbehavior";
                    Log(mb.name + " " + DescribeBehavior(now), bad ? LogKind.Fire : LogKind.Cat);
                    if (bad || now == "box_use")
                        Ping(mb.transform.position + Vector3.up * catPingHeightMeters,
                            bad ? "SQUAT (floor)" : "in box", bad ? LogKind.Fire : LogKind.Cat);
                }
            }
            catch (Exception e)
            {
                _catWatchBroken = true;
                Debug.LogWarning("EventFeed: cat poll failed, cat lines disabled. " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // detector scoring
        // ------------------------------------------------------------------
        //
        // The sim knows what every cat is actually doing, and it sees every fire
        // the detector issues. That is both halves of a confusion matrix, so the
        // detector can be graded live instead of eyeballed:
        //
        //   hit    - fired while a cat was squatting on the floor
        //   false  - fired when no cat was misbehaving (box use, sniffing, wandering)
        //   miss   - a floor squat ended without any fire
        //
        // Firing on a cat that is legitimately using a litterbox is the expensive
        // mistake in the real world: deterrents near the boxes risk litterbox
        // aversion, which makes the original problem worse. So false positives are
        // counted separately rather than folded into one accuracy number.

        int _hits, _falseAlarms, _misses;
        int _misbehavingCount;          // cats currently squatting on the floor
        float _misbehaviorEndedAt = -1; // for the grace window after a squat ends
        bool _firedThisEpisode;

        void NoteBehaviorForScore(string cat, string behavior)
        {
            string prev;
            bool known = _scoreBehavior.TryGetValue(cat, out prev);
            if (known && prev == behavior) return;
            _scoreBehavior[cat] = behavior;

            bool wasBad = known && prev == "misbehavior";
            bool isBad = behavior == "misbehavior";
            if (wasBad == isBad) return;

            if (isBad)
            {
                _misbehavingCount++;
                _firedThisEpisode = false;
            }
            else
            {
                _misbehavingCount = Mathf.Max(0, _misbehavingCount - 1);
                _misbehaviorEndedAt = Time.time;
                if (_misbehavingCount == 0 && !_firedThisEpisode)
                {
                    _misses++;
                    Log("MISS - floor squat ended, detector never fired", LogKind.Fire);
                }
            }
        }

        readonly Dictionary<string, string> _scoreBehavior = new Dictionary<string, string>();

        void NoteFireForScore()
        {
            // A fire landing just after the squat ends is still a catch - the
            // detector needs dwell time before it can be confident, so demanding
            // it fire strictly inside the squat would penalise correct caution.
            bool inGrace = _misbehaviorEndedAt >= 0f &&
                           Time.time - _misbehaviorEndedAt <= scoreGraceSec;

            if (_misbehavingCount > 0 || inGrace)
            {
                if (!_firedThisEpisode) _hits++;
                _firedThisEpisode = true;
            }
            else
            {
                _falseAlarms++;
            }
        }

        string ScoreLine()
        {
            int fired = _hits + _falseAlarms;
            int actual = _hits + _misses;
            string precision = fired == 0 ? "--" : Mathf.RoundToInt(100f * _hits / fired) + "%";
            string recall = actual == 0 ? "--" : Mathf.RoundToInt(100f * _hits / actual) + "%";
            return "SCORE  hit " + _hits + "  false " + _falseAlarms + "  miss " + _misses +
                   "   prec " + precision + "  recall " + recall;
        }

        string ScoreHex()
        {
            // Amber the moment the detector has cried wolf or slept through one.
            return (_falseAlarms > 0 || _misses > 0)
                ? KindHex[(int)LogKind.Fire]
                : KindHex[(int)LogKind.Ack];
        }

        static string DescribeBehavior(string behavior)
        {
            switch (behavior)
            {
                case "misbehavior": return "squatting on the FLOOR";
                case "box_use": return "using a litterbox";
                case "sniff": return "sniffing";
                case "flee": return "fled";
                default: return behavior;
            }
        }

        Transform ResolveFloorReference()
        {
            // Prefer whatever VirtualTurret aims at, so pings land exactly where
            // the turret's effects do. Same reflection rule as CatAgent.
            try
            {
                Type turretType = Type.GetType("WorldSim.CatBasement.VirtualTurret, WorldSim.CatBasement");
                if (turretType != null)
                {
                    UnityEngine.Object[] turrets = FindObjectsByType(turretType, FindObjectsSortMode.None);
                    FieldInfo field = turretType.GetField("floorReference", BindingFlags.Public | BindingFlags.Instance);
                    if (turrets != null && turrets.Length > 0 && field != null)
                    {
                        var t = field.GetValue(turrets[0]) as Transform;
                        if (t != null) return t;
                    }
                }
            }
            catch (Exception) { /* fall through to the name lookup */ }

            var byName = GameObject.Find("Floor");
            return byName != null ? byName.transform : null;
        }

        /// <summary>
        /// Normalized frame coords [0..1], origin top-left, onto the floor's XZ
        /// footprint - the same mapping VirtualTurret.NormalizedToWorld uses.
        /// </summary>
        bool TryNormalizedToWorld(float[] normalized, out Vector3 world)
        {
            world = Vector3.zero;
            if (floorReference == null || normalized == null || normalized.Length < 2) return false;

            Vector3 size = floorReference.localScale;
            float x = (normalized[0] - 0.5f) * size.x;
            float z = (0.5f - normalized[1]) * size.z; // frame origin is top-left, flip for world z
            float y = floorReference.position.y + size.y * 0.5f + 0.02f;
            world = new Vector3(floorReference.position.x + x, y, floorReference.position.z + z);
            return true;
        }

        // ------------------------------------------------------------------
        // panel construction and layout
        // ------------------------------------------------------------------

        static Font LoadFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch (Exception) { /* renamed in 2022+; try the old name below */ }
            if (f == null)
            {
                try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch (Exception) { /* fall through */ }
            }
            if (f == null)
            {
                try { f = Font.CreateDynamicFontFromOSFont("Helvetica", FontPixelSize); }
                catch (Exception) { /* fall through */ }
            }
            if (f == null)
                Debug.LogWarning("EventFeed: no built-in font available - the overlay will not draw.");
            return f;
        }

        void BuildPanel()
        {
            if (_font == null) return;

            var root = new GameObject("EventFeedPanel");
            _panelRoot = root.transform;

            // Backing quad first so the transparent queue sorts it behind the text.
            var backing = new GameObject("Backing");
            backing.transform.SetParent(_panelRoot, false);
            _panelBacking = backing.transform;

            _backingMesh = BuildQuadMesh();
            backing.AddComponent<MeshFilter>().sharedMesh = _backingMesh;
            var backingRenderer = backing.AddComponent<MeshRenderer>();
            _backingMat = BuildBackingMaterial(new Color(0.03f, 0.04f, 0.06f, Mathf.Clamp01(panelBackingAlpha)));
            backingRenderer.sharedMaterial = _backingMat;
            StripLighting(backingRenderer);

            var text = new GameObject("Text");
            text.transform.SetParent(_panelRoot, false);
            _panelText = MakeText(text, TextAnchor.UpperLeft, TextAlignment.Left);
            _panelTextRenderer = text.GetComponent<MeshRenderer>();

            _samplePixels = MeasureSamplePixels();
            _unitsPerFontPixel = UnitsPerFontPixel;
            _sampleUnits = _samplePixels * _unitsPerFontPixel;

            // Calibration pass: render WidthSample fully transparent for a frame or
            // two and read back the mesh it produces, so the character budget is
            // fitted to real geometry instead of assumed glyph metrics.
            _panelText.richText = false;
            _panelText.color = new Color(1f, 1f, 1f, 0f);
            _panelText.text = WidthSample;
            _calibFrames = 0;
        }

        void Calibrate()
        {
            _calibFrames++;

            float measured = 0f;
            try
            {
                if (_panelTextRenderer != null) measured = _panelTextRenderer.localBounds.size.x;
            }
            catch (Exception) { /* renderer has no mesh yet - retry next frame */ }

            // Sanity band: accept the mesh measurement only if it implies a
            // units-per-font-pixel within 5x of the documented 0.1. Anything wilder
            // is a placeholder/empty bounds, not a real glyph run.
            float implied = _samplePixels > 0.01f ? measured / _samplePixels : 0f;
            if (measured > 0.001f && implied > UnitsPerFontPixel * 0.2f && implied < UnitsPerFontPixel * 5f)
            {
                _sampleUnits = measured;
                _unitsPerFontPixel = implied;
            }
            else if (_calibFrames <= 30)
            {
                return; // mesh not generated yet - the TextMesh builds it at render time
            }

            _calibFrames = -1;
            _panelText.richText = true; // per-line colour without one TextMesh per line
            _panelText.color = Color.white;
            _lastFov = -1f; // force UpdateLayout to redo the fit against the new width
            _textDirty = true;
        }

        TextMesh MakeText(GameObject go, TextAnchor anchor, TextAlignment alignment)
        {
            var tm = go.AddComponent<TextMesh>(); // [RequireComponent(MeshRenderer)] adds the renderer
            tm.font = _font;
            tm.fontSize = FontPixelSize;
            tm.characterSize = 1f;
            tm.lineSpacing = LineSpacing;
            tm.anchor = anchor;
            tm.alignment = alignment;
            tm.offsetZ = 0f;

            // TextMesh declares [RequireComponent(MeshRenderer)] so the renderer is
            // normally already there; adding it only when missing keeps this working
            // either way without tripping the "component already added" error.
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _font.material; // shared: TextMesh.color writes vertex colours, not the material
            StripLighting(mr);
            return tm;
        }

        static void StripLighting(Renderer r)
        {
            // The panel sits 0.6 m from the lens; without this it casts a shadow
            // across the whole room and gets probe-lit like set dressing.
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        /// <summary>Unit quad in local XY, visible face toward -Z. Double-wound so
        /// winding conventions can't make it invisible.</summary>
        static Mesh BuildQuadMesh()
        {
            var mesh = new Mesh { name = "EventFeedBacking" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f)
            };
            mesh.normals = new[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
            mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        static Material BuildBackingMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Standard");

            var mat = new Material(shader) { name = "EventFeedBacking" };

            // URP/Unlit ships opaque; this is the documented switch to alpha blend.
            if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            if (mat.HasProperty("_AlphaClip")) mat.SetFloat("_AlphaClip", 0f);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.renderQueue = (int)RenderQueue.Transparent;

            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            mat.color = color;
            return mat;
        }

        /// <summary>Advance width of <see cref="WidthSample"/> in font pixels, taken
        /// from the font's own glyph metrics so the panel fits its character budget
        /// instead of guessing at glyph widths.</summary>
        float MeasureSamplePixels()
        {
            float fallback = WrapChars * FallbackAdvanceEm * FontPixelSize;
            if (_font == null) return fallback;

            try
            {
                _font.RequestCharactersInTexture(WidthSample, FontPixelSize, FontStyle.Normal);
                float pixels = 0f;
                for (int i = 0; i < WidthSample.Length; i++)
                {
                    CharacterInfo info;
                    if (_font.GetCharacterInfo(WidthSample[i], out info, FontPixelSize, FontStyle.Normal))
                        pixels += info.advance;
                }
                return pixels > 0.01f ? pixels : fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        Camera ResolveCamera()
        {
            if (targetCamera != null) return targetCamera;

            // The panel has to land in the MJPEG frame, so the streaming camera wins
            // over Camera.main (which in this scene is the display-only twin).
            var streamer = FindFirstObjectByType<FrameStreamer>();
            if (streamer != null)
            {
                var streamCam = streamer.GetComponent<Camera>();
                if (streamCam != null) { targetCamera = streamCam; return targetCamera; }
            }

            if (Camera.main != null) { targetCamera = Camera.main; return targetCamera; }

            Camera[] all = Camera.allCameras; // enabled cameras only
            if (all.Length > 0) { targetCamera = all[0]; return targetCamera; }
            return null;
        }

        void UpdateLayout(Camera cam)
        {
            if (_panelRoot == null) return;

            if (_panelRoot.parent != cam.transform)
            {
                _panelRoot.SetParent(cam.transform, false);
                _panelRoot.localPosition = Vector3.zero;
                _panelRoot.localRotation = Quaternion.identity;
                _panelRoot.localScale = Vector3.one;
                _textDirty = true;
            }

            float distance = Mathf.Max(panelDistance, cam.nearClipPlane * 1.5f + 0.02f);
            bool changed = !Mathf.Approximately(_lastFov, cam.fieldOfView)
                           || !Mathf.Approximately(_lastAspect, cam.aspect)
                           || !Mathf.Approximately(_lastDistance, distance)
                           || !Mathf.Approximately(_lastWidthFraction, panelWidthFraction)
                           || _lastOrtho != cam.orthographic;
            if (!changed) return;

            _lastFov = cam.fieldOfView;
            _lastAspect = cam.aspect;
            _lastDistance = distance;
            _lastWidthFraction = panelWidthFraction;
            _lastOrtho = cam.orthographic;

            float frameHeight = cam.orthographic
                ? cam.orthographicSize * 2f
                : 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float frameWidth = frameHeight * Mathf.Max(0.1f, cam.aspect);

            _panelWidth = frameWidth * Mathf.Clamp(panelWidthFraction, 0.18f, 0.6f);
            _pad = frameHeight * 0.012f;
            float usable = Mathf.Max(0.001f, _panelWidth - 2f * _pad);

            _textScale = usable / Mathf.Max(0.0001f, _sampleUnits);
            _lineHeight = FontPixelSize * _unitsPerFontPixel * LineSpacing * _textScale;

            float marginX = frameWidth * 0.02f;
            float marginY = frameHeight * 0.03f;
            _panelLeft = -frameWidth * 0.5f + marginX;
            _panelTop = frameHeight * 0.5f - marginY;
            _panelZ = distance;

            // Half the frame height, not all of it: a burst of 3-row fire entries
            // would otherwise stretch a semi-opaque panel down the entire left edge
            // of the stream the detector is reading.
            float available = frameHeight * 0.5f - 2f * _pad;
            _maxRows = Mathf.Max(4, Mathf.FloorToInt(available / Mathf.Max(0.0001f, _lineHeight)));

            _panelText.transform.localScale = new Vector3(_textScale, _textScale, _textScale);
            _textDirty = true;
        }

        void UpdateConnectionHeader()
        {
            bool connected = bridge != null && bridge.IsConnected;
            if (connected == _lastConnected) return;
            _lastConnected = connected;
            _textDirty = true;
        }

        // ------------------------------------------------------------------
        // text assembly
        // ------------------------------------------------------------------

        void RebuildText()
        {
            if (_panelText == null) return;
            _textDirty = false;

            _sb.Length = 0;
            int rows = 0;

            bool connected = bridge != null && bridge.IsConnected;
            AppendRow(_sb, connected ? KindHex[(int)LogKind.Ack] : KindHex[(int)LogKind.Fire],
                "CATSENTRY FEED  mqtt " + (connected ? "up" : "down"));
            rows++;

            if (showScore)
            {
                AppendRow(_sb, ScoreHex(), ScoreLine());
                rows++;
            }

            int budget = Mathf.Max(1, _maxRows - rows);
            int prefixLen = 9; // "HH:MM:SS "
            int bodyWidth = Mathf.Max(8, WrapChars - prefixLen);
            string indent = new string(' ', prefixLen);

            // Newest at the top: walk the buffer backwards, stop when the frame is full.
            for (int i = _entries.Count - 1; i >= 0 && rows < budget + 1; i--)
            {
                Entry e = _entries[i];
                WrapBody(e.Body, bodyWidth, MaxRowsPerEntry, _rowScratch);
                if (_rowScratch.Count == 0) continue;
                if (rows - 1 + _rowScratch.Count > budget) break;

                string hex = KindHex[(int)e.Kind];
                for (int r = 0; r < _rowScratch.Count; r++)
                {
                    AppendRow(_sb, hex, (r == 0 ? e.Clock + " " : indent) + _rowScratch[r]);
                    rows++;
                }
            }

            if (_sb.Length > 0 && _sb[_sb.Length - 1] == '\n') _sb.Length--; // no trailing blank row
            _panelText.text = _sb.ToString();
            LayoutPanelGeometry(rows);
        }

        static void AppendRow(StringBuilder sb, string hex, string content)
        {
            sb.Append("<color=#").Append(hex).Append('>').Append(content).Append("</color>\n");
        }

        void LayoutPanelGeometry(int rows)
        {
            if (_panelText == null || _panelBacking == null) return;

            float height = Mathf.Max(1, rows) * _lineHeight + 2f * _pad;

            // Text hangs from the panel's top-left; the quad is centred on the rect.
            _panelText.transform.localPosition = new Vector3(_panelLeft + _pad, _panelTop - _pad, _panelZ - 0.004f);
            _panelBacking.localPosition = new Vector3(_panelLeft + _panelWidth * 0.5f, _panelTop - height * 0.5f, _panelZ);
            _panelBacking.localScale = new Vector3(_panelWidth, height, 1f);
        }

        static void WrapBody(string body, int bodyWidth, int maxRows, List<string> rows)
        {
            rows.Clear();
            if (string.IsNullOrEmpty(body)) return;
            if (bodyWidth < 8) bodyWidth = 8;

            int pos = 0;
            while (pos < body.Length && rows.Count < maxRows)
            {
                int remaining = body.Length - pos;
                if (remaining <= bodyWidth)
                {
                    rows.Add(body.Substring(pos));
                    pos = body.Length;
                    break;
                }

                int take = bodyWidth;
                int space = body.LastIndexOf(' ', pos + take - 1, take);
                if (space > pos) take = space - pos;

                rows.Add(body.Substring(pos, take).TrimEnd());
                pos += take;
                while (pos < body.Length && body[pos] == ' ') pos++;
            }

            if (pos < body.Length && rows.Count > 0)
            {
                int last = rows.Count - 1;
                string s = rows[last];
                int keep = Mathf.Max(0, bodyWidth - 3);
                if (s.Length > keep) s = s.Substring(0, keep);
                rows[last] = s.TrimEnd() + "...";
            }
        }

        /// <summary>Strips markup and control characters so a hostile or merely odd
        /// payload can't inject rich-text tags or blow up the line layout.</summary>
        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { sb.Append('['); continue; }
                if (c == '>') { sb.Append(']'); continue; }
                if (c == '\r') continue;
                if (c == '\n' || c == '\t') { sb.Append(' '); continue; }
                if (char.IsControl(c)) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        static string LeafOf(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return "(no topic)";
            int slash = topic.LastIndexOf('/');
            return slash >= 0 && slash < topic.Length - 1 ? topic.Substring(slash + 1) : topic;
        }

        static bool Blank(string s) => string.IsNullOrEmpty(s);

        static Color KindColor(LogKind kind)
        {
            switch (kind)
            {
                case LogKind.Detect: return new Color(0.30f, 0.85f, 1f);
                case LogKind.Fire: return new Color(1f, 0.69f, 0.13f);
                case LogKind.Ack: return new Color(0.37f, 0.88f, 0.54f);
                case LogKind.Cat: return Color.white;
                default: return new Color(0.60f, 0.63f, 0.65f);
            }
        }

        // ------------------------------------------------------------------
        // pings
        // ------------------------------------------------------------------

        void PingAtPanel(string text, LogKind kind)
        {
            if (_font == null || string.IsNullOrEmpty(text)) return;
            Camera cam = ResolveCamera();
            if (cam == null) return;
            // No floor mapping available: park it just right of the panel so the
            // event is still visible in frame.
            float z = Mathf.Max(panelDistance, cam.nearClipPlane * 1.5f + 0.02f);
            float frameHeight = cam.orthographic
                ? cam.orthographicSize * 2f
                : 2f * z * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float frameWidth = frameHeight * Mathf.Max(0.1f, cam.aspect);
            Vector3 local = new Vector3(-frameWidth * 0.5f + _panelWidth + frameWidth * 0.06f, -frameHeight * 0.25f, z);

            var go = new GameObject("EventPing");
            go.transform.SetPositionAndRotation(cam.transform.TransformPoint(local), cam.transform.rotation);

            TextMesh tm = MakeText(go, TextAnchor.MiddleCenter, TextAlignment.Center);
            if (tm == null) return;
            tm.text = Sanitize(text);
            tm.richText = false;
            Color c = KindColor(kind);
            tm.color = c;
            // Sized against the frame rather than in metres - this one lives in
            // screen space, not out in the room.
            float scale = (frameHeight * 0.045f) / (FontPixelSize * _unitsPerFontPixel);
            go.transform.localScale = new Vector3(scale, scale, scale);

            _pings.Add(new ActivePing
            {
                Root = go.transform,
                Text = tm,
                BasePos = go.transform.position,
                BaseColor = c,
                Born = Time.time,
                Lifetime = Mathf.Max(0.2f, pingLifetimeSec)
            });
        }

        void UpdatePings(Camera cam)
        {
            for (int i = _pings.Count - 1; i >= 0; i--)
            {
                ActivePing p = _pings[i];
                if (p.Root == null) { _pings.RemoveAt(i); continue; }

                float t = (Time.time - p.Born) / p.Lifetime;
                if (t >= 1f) { DestroyPingAt(i); continue; }

                p.Root.position = p.BasePos + Vector3.up * (pingRiseMeters * t);
                // Recomputed every frame: the camera is free to move.
                p.Root.rotation = cam.transform.rotation;

                if (p.Text != null)
                {
                    float alpha = t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f;
                    Color c = p.BaseColor;
                    c.a = Mathf.Clamp01(alpha);
                    p.Text.color = c;
                }
            }
        }

        void DestroyPingAt(int index)
        {
            ActivePing p = _pings[index];
            _pings.RemoveAt(index);
            if (p.Root != null) Destroy(p.Root.gameObject);
        }

        void ClearPings()
        {
            for (int i = _pings.Count - 1; i >= 0; i--) DestroyPingAt(i);
        }
    }

    // ------------------------------------------------------------------
    // payload shapes
    // ------------------------------------------------------------------
    // Core deliberately re-declares these rather than reusing VirtualTurret's
    // copies: those live in WorldSim.CatBasement, which references Core. Field
    // names are frozen by docs/contract-catsentry-v1.md - never rename them.

    /// <summary>catsentry/deterrent/fire payload, read-only view for the feed.</summary>
    [Serializable]
    public class FeedFireMsg
    {
        public string ts;
        public string level;   // "sound" | "air"
        public float[] target; // normalized [x, y], origin top-left
        public int duration_ms;
        public string reason;
    }

    /// <summary>catsentry/deterrent/ack payload, read-only view for the feed.</summary>
    [Serializable]
    public class FeedAckMsg
    {
        public string ts;
        public string level;
        public bool ok;
        public string detail;
    }

    /// <summary>catsentry/groundtruth payload - sim-only, additive to catsentry-v1.
    /// Field names must match CatAgent's publisher exactly.</summary>
    [Serializable]
    public class GroundTruthMsg
    {
        public string ts;
        public string cat;
        public string behavior;
        public bool in_box;
    }

    /// <summary>catsentry/event payload. The detector's schema is not frozen past
    /// the contract's example, so unknown fields are ignored and missing ones just
    /// drop out of the rendered line.</summary>
    [Serializable]
    public class FeedEventMsg
    {
        public string ts;
        public string type;
        public string cat_id;
        public string zone;
        public float confidence;
        public float[] bbox; // [x, y, w, h] normalized
        public string snapshot_path;
    }
}
