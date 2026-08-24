using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace WorldSim.Core
{
    /// <summary>
    /// Thin MonoBehaviour wrapper around MqttClientLite: connect with backoff
    /// reconnect, publish/subscribe JSON string payloads, periodic telemetry
    /// heartbeat.
    ///
    /// <see cref="OnMessage"/> always fires on the main thread (marshaled via the
    /// SynchronizationContext captured in Awake) so subscribers (e.g. VirtualTurret)
    /// can touch Unity API directly without worrying about the client's receive thread.
    /// </summary>
    public class MqttBridge : MonoBehaviour
    {
        /// <summary>QoS 0 - the contract uses this for telemetry.</summary>
        public const byte QosAtMostOnce = 0;
        /// <summary>QoS 1 - the contract requires this on deterrent/*.</summary>
        public const byte QosAtLeastOnce = 1;

        [Header("Broker")]
        public string brokerHost = "localhost";
        public int brokerPort = 1883;

        [Header("Telemetry heartbeat")]
        public string telemetryTopic = "catsentry/telemetry";
        public string telemetrySource = "sim";
        public float heartbeatIntervalSec = 10f;

        [Header("Reconnect backoff")]
        public float reconnectMinDelaySec = 1f;
        public float reconnectMaxDelaySec = 30f;

        public bool IsConnected => _client != null && _client.IsConnected;

        /// <summary>(topic, jsonPayload) - always invoked on the main thread.</summary>
        public event Action<string, string> OnMessage;

        MqttClientLite _client;
        string _clientId;
        SynchronizationContext _mainCtx;

        // Every filter a component has asked for, replayed on every (re)connect -
        // we always connect with a clean session, so re-subscribing ourselves is
        // simplest. Maps filter -> requested qos.
        readonly Dictionary<string, byte> _subscriptions = new Dictionary<string, byte>();
        // The subset of those actually on the wire, and at what qos. A broker
        // delivers one copy of a PUBLISH per matching subscription (MQTT 3.1.1
        // 3.3.5), so two overlapping filters on one client turn every message into
        // two OnMessage events - which is how one fire command produced two acks.
        // OnMessage is broadcast to every local listener and each listener filters
        // by topic itself, so the covering filter alone is enough.
        readonly Dictionary<string, byte> _wire = new Dictionary<string, byte>();
        readonly object _subLock = new object();

        volatile bool _connecting;
        volatile bool _stopping;
        float _reconnectDelay;
        float _reconnectTimer;
        float _heartbeatTimer;

        void Awake()
        {
            _clientId = "worldsim-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _reconnectDelay = reconnectMinDelaySec;
            _mainCtx = SynchronizationContext.Current;
        }

        void OnEnable()
        {
            _stopping = false;
            TryConnect();
        }

        void OnDisable()
        {
            _stopping = true;
            Disconnect();
        }

        void Update()
        {
            if (!IsConnected)
            {
                _reconnectTimer += Time.unscaledDeltaTime;
                if (_reconnectTimer >= _reconnectDelay)
                {
                    _reconnectTimer = 0f;
                    TryConnect();
                }
                return;
            }

            _heartbeatTimer += Time.unscaledDeltaTime;
            if (_heartbeatTimer >= heartbeatIntervalSec)
            {
                _heartbeatTimer = 0f;
                PublishHeartbeat();
            }
        }

        // ---- connect / reconnect ----

        void TryConnect()
        {
            if (_connecting) return;
            _connecting = true;
            // ponytail: Connect() blocks on socket I/O. Running it off the main
            // thread keeps Play mode from hitching every time the broker is down
            // (which is exactly when reconnect-with-backoff needs to keep firing
            // without freezing the sim).
            ThreadPool.QueueUserWorkItem(_ => ConnectWorker());
        }

        void ConnectWorker()
        {
            try
            {
                var client = new MqttClientLite();
                client.MessageReceived += HandlePublishReceived;
                client.ConnectionClosed += HandleConnectionClosed;
                client.Connect(brokerHost, brokerPort, _clientId);

                if (_stopping)
                {
                    // OnDisable fired while we were blocked in Connect() - don't
                    // adopt this client, just close it so its receive thread
                    // doesn't outlive Play-mode stop.
                    client.MessageReceived -= HandlePublishReceived;
                    client.ConnectionClosed -= HandleConnectionClosed;
                    try { client.Disconnect(); } catch (Exception) { /* going away anyway */ }
                    return;
                }

                if (_client != null) Disconnect();
                _client = client;
                _reconnectDelay = reconnectMinDelaySec; // reset backoff on success
                ResubscribeAll();
            }
            catch (Exception)
            {
                ScheduleReconnect();
            }
            finally
            {
                _connecting = false;
            }
        }

        void ScheduleReconnect()
        {
            _reconnectTimer = 0f;
            _reconnectDelay = Mathf.Min(_reconnectDelay * 2f, reconnectMaxDelaySec);
        }

        void HandleConnectionClosed()
        {
            ScheduleReconnect();
        }

        void Disconnect()
        {
            if (_client == null) return;
            _client.MessageReceived -= HandlePublishReceived;
            _client.ConnectionClosed -= HandleConnectionClosed;
            try { _client.Disconnect(); } catch (Exception) { /* going away anyway */ }
            _client = null;
        }

        // ---- pub/sub ----

        /// <summary>Register interest in a topic filter. Wildcards are honoured:
        /// '+' matches one level, '#' matches the rest. Only the filters no other
        /// requested filter already covers reach the broker.</summary>
        public void Subscribe(string topic, byte qos = QosAtMostOnce)
        {
            List<KeyValuePair<string, byte>> add = null;
            List<string> drop = null;
            lock (_subLock)
            {
                // TryGetValue, not TryAdd - keeps this on the Unity-supported .NET
                // API surface rather than the newer Dictionary APIs.
                byte requested;
                if (_subscriptions.TryGetValue(topic, out requested) && requested >= qos) return;
                _subscriptions[topic] = qos;
                if (!IsConnected) return;
                PlanWire(out add, out drop);
            }
            ApplyWire(add, drop);
        }

        void ResubscribeAll()
        {
            List<KeyValuePair<string, byte>> add = null;
            List<string> drop = null;
            lock (_subLock)
            {
                // Clean session: the broker remembers none of our previous filters.
                // Clearing before planning is what makes the replay re-send every
                // covering filter - without it the "already at this qos" check below
                // would skip them all and the client would go silent after a
                // reconnect. These two steps must stay in this order.
                _wire.Clear();
                PlanWire(out add, out drop);
            }
            ApplyWire(add, drop);
        }

        /// <summary>Work out what the wire needs, and record it as done. Recomputed
        /// from scratch every time rather than patched incrementally, so the outcome
        /// does not depend on the order components happen to register in - a broad
        /// filter arriving after narrow ones unsubscribes them, and a narrow one
        /// arriving after a broad one never reaches the wire at all.
        ///
        /// Planning is separate from sending so the socket writes happen outside
        /// _subLock: a component calling Subscribe from OnEnable must not be able to
        /// block another component's OnEnable behind an unresponsive broker.
        ///
        /// Caller holds _subLock.</summary>
        void PlanWire(out List<KeyValuePair<string, byte>> add, out List<string> drop)
        {
            add = null;
            drop = null;
            Dictionary<string, byte> wanted = CoveringSet();

            foreach (KeyValuePair<string, byte> have in _wire)
            {
                if (wanted.ContainsKey(have.Key)) continue;
                if (drop == null) drop = new List<string>();
                drop.Add(have.Key);
            }

            foreach (KeyValuePair<string, byte> want in wanted)
            {
                byte current;
                if (_wire.TryGetValue(want.Key, out current) && current == want.Value) continue;
                if (add == null) add = new List<KeyValuePair<string, byte>>();
                add.Add(want);
            }

            // Record the intended state now rather than after the writes. A write
            // that fails takes the socket with it, and the reconnect clears _wire
            // before replaying, so an optimistic record cannot go stale for long.
            if (add != null)
                for (int i = 0; i < add.Count; i++) _wire[add[i].Key] = add[i].Value;
            if (drop != null)
                for (int i = 0; i < drop.Count; i++) _wire.Remove(drop[i]);
        }

        /// <summary>Send a plan. Called without _subLock held - see PlanWire.</summary>
        void ApplyWire(List<KeyValuePair<string, byte>> add, List<string> drop)
        {
            MqttClientLite client = _client;
            if (client == null) return;
            try
            {
                // Subscribe first, unsubscribe second: the other order would leave a
                // window where nothing on the wire covers the narrow filters.
                // A repeated SUBSCRIBE on the same filter replaces the existing
                // subscription rather than adding a second one, so re-issuing to
                // raise the qos is safe.
                if (add != null)
                    for (int i = 0; i < add.Count; i++) client.Subscribe(add[i].Key, add[i].Value);
                if (drop != null) client.Unsubscribe(drop);
            }
            catch (Exception)
            {
                // socket dropped mid-write; the reconnect loop replays the covering set
            }
        }

        /// <summary>The requested filters that no other requested filter covers, each
        /// carrying the strongest qos any filter it absorbs asked for - dropping a
        /// narrow qos-1 filter under a broad qos-0 one would otherwise downgrade it.
        ///
        /// Caller holds _subLock.</summary>
        Dictionary<string, byte> CoveringSet()
        {
            var covering = new Dictionary<string, byte>();
            foreach (KeyValuePair<string, byte> candidate in _subscriptions)
            {
                bool covered = false;
                foreach (KeyValuePair<string, byte> other in _subscriptions)
                {
                    if (other.Key == candidate.Key) continue;
                    // Coverage is antisymmetric for distinct filters, so this can
                    // never eliminate both members of a pair.
                    if (Covers(other.Key, candidate.Key)) { covered = true; break; }
                }
                if (covered) continue;

                byte qos = candidate.Value;
                foreach (KeyValuePair<string, byte> absorbed in _subscriptions)
                    if (absorbed.Value > qos && Covers(candidate.Key, absorbed.Key)) qos = absorbed.Value;
                covering[candidate.Key] = qos;
            }
            return covering;
        }

        /// <summary>True when every topic <paramref name="narrow"/> could match is
        /// also matched by <paramref name="broad"/> - MQTT 3.1.1 4.7 wildcard rules
        /// applied filter-to-filter rather than filter-to-topic.</summary>
        internal static bool Covers(string broad, string narrow)
        {
            if (broad == narrow) return true;
            if (string.IsNullOrEmpty(broad) || string.IsNullOrEmpty(narrow)) return false;
            // A leading wildcard never matches the broker's own $SYS tree (4.7.2), so
            // it cannot stand in for a filter that names one explicitly.
            if (broad[0] != '$' && narrow[0] == '$') return false;

            string[] b = broad.Split('/');
            string[] n = narrow.Split('/');
            int i = 0;
            for (; i < b.Length; i++)
            {
                // '#' is always the final level and swallows every level left.
                if (b[i] == "#") return i <= n.Length;
                if (i >= n.Length) return false;
                // narrow reaches into arbitrary depth here; a single level cannot.
                //
                // This is deliberately stricter than set inclusion, and must stay
                // that way. "#" and "+/#" match exactly the same topics, so an exact
                // matcher would report coverage in BOTH directions, CoveringSet would
                // drop both, and the client would end up with no wire subscriptions
                // at all - total message loss. Refusing to let a '+' level stand in
                // for a '#' breaks the tie so "#" survives and "+/#" is absorbed.
                // Erring towards keeping a filter can only cost a duplicate; erring
                // the other way loses messages. Do not "correct" this asymmetry.
                if (n[i] == "#") return false;
                if (b[i] == "+") continue;
                if (b[i] != n[i]) return false;
            }
            return i == n.Length;
        }

        /// <summary>Fire-and-forget publish. No-op (and no queue) if not currently connected -
        /// ponytail: acceptable for a telemetry/deterrent-ack sim, not a store-and-forward broker.</summary>
        public void Publish(string topic, string jsonPayload, byte qos = QosAtMostOnce)
        {
            if (!IsConnected) return;
            try { _client.Publish(topic, jsonPayload, qos); }
            catch (Exception) { /* socket dropped mid-write; the reconnect loop picks it up */ }
        }

        void HandlePublishReceived(string topic, string payload)
        {
            _mainCtx.Post(_ => OnMessage?.Invoke(topic, payload), null);
        }

        void PublishHeartbeat()
        {
            var msg = new TelemetryMsg
            {
                ts = Iso8601Utc.Now(),
                source = telemetrySource,
                status = "ok"
            };
            Publish(telemetryTopic, JsonUtility.ToJson(msg), QosAtMostOnce);
        }
    }

    /// <summary>catsentry/telemetry payload - field names frozen by docs/contract-catsentry-v1.md.</summary>
    [Serializable]
    public class TelemetryMsg
    {
        public string ts;
        public string source;
        public string status;
    }

    /// <summary>ISO 8601 UTC, second precision, "Z" suffix - matches every timestamp
    /// example in the contract (e.g. "2026-08-19T21:04:03Z").</summary>
    public static class Iso8601Utc
    {
        public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
}
