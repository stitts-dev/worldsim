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

        // Topics we want subscribed, replayed on every (re)connect - we always
        // connect with a clean session, so re-subscribing ourselves is simplest.
        // Maps topic -> qos.
        readonly Dictionary<string, byte> _subscriptions = new Dictionary<string, byte>();
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

        public void Subscribe(string topic, byte qos = QosAtMostOnce)
        {
            lock (_subLock)
            {
                // ContainsKey, not TryAdd - keeps this on the Unity-supported .NET
                // API surface rather than the newer Dictionary APIs.
                if (_subscriptions.ContainsKey(topic)) return;
                _subscriptions[topic] = qos;
            }
            if (IsConnected)
                _client.Subscribe(topic, qos);
        }

        void ResubscribeAll()
        {
            lock (_subLock)
            {
                foreach (KeyValuePair<string, byte> sub in _subscriptions)
                    _client.Subscribe(sub.Key, sub.Value);
            }
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
