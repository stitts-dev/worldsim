using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace WorldSim.Core
{
    /// <summary>
    /// Thin MonoBehaviour wrapper around M2Mqtt: connect with backoff reconnect,
    /// publish/subscribe JSON string payloads, periodic telemetry heartbeat.
    ///
    /// <see cref="OnMessage"/> always fires on the main thread (marshaled via
    /// MainThreadDispatcher) so subscribers (e.g. VirtualTurret) can touch Unity
    /// API directly without worrying about M2Mqtt's receiver thread.
    /// </summary>
    public class MqttBridge : MonoBehaviour
    {
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

        MqttClient _client;
        string _clientId;

        // Topics we want subscribed, replayed on every (re)connect - M2Mqtt doesn't
        // give us a persistent session here, so re-subscribing ourselves is simplest.
        readonly List<string> _subscribedTopics = new List<string>();
        readonly List<byte> _subscribedQos = new List<byte>();
        readonly object _subLock = new object();

        volatile bool _connecting;
        float _reconnectDelay;
        float _reconnectTimer;
        float _heartbeatTimer;

        void Awake()
        {
            _clientId = "worldsim-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _reconnectDelay = reconnectMinDelaySec;
        }

        void OnEnable()
        {
            TryConnect();
        }

        void OnDisable()
        {
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
            // ponytail: MqttClient.Connect() blocks on socket I/O. Running it off
            // the main thread keeps Play mode from hitching every time the broker
            // is down (which is exactly when reconnect-with-backoff needs to keep
            // firing without freezing the sim).
            ThreadPool.QueueUserWorkItem(_ => ConnectWorker());
        }

        void ConnectWorker()
        {
            try
            {
                var client = new MqttClient(brokerHost, brokerPort, false, MqttSslProtocols.None, null, null);
                client.MqttMsgPublishReceived += HandlePublishReceived;
                client.ConnectionClosed += HandleConnectionClosed;

                byte code = client.Connect(_clientId);
                if (code == MqttMsgConnack.CONN_ACCEPTED)
                {
                    _client = client;
                    _reconnectDelay = reconnectMinDelaySec; // reset backoff on success
                    ResubscribeAll();
                }
                else
                {
                    client.MqttMsgPublishReceived -= HandlePublishReceived;
                    client.ConnectionClosed -= HandleConnectionClosed;
                    ScheduleReconnect();
                }
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

        void HandleConnectionClosed(object sender, EventArgs e)
        {
            ScheduleReconnect();
        }

        void Disconnect()
        {
            if (_client == null) return;
            _client.MqttMsgPublishReceived -= HandlePublishReceived;
            _client.ConnectionClosed -= HandleConnectionClosed;
            try { if (_client.IsConnected) _client.Disconnect(); } catch (Exception) { /* going away anyway */ }
            _client = null;
        }

        // ---- pub/sub ----

        // ponytail: default qos is 0 (QOS_LEVEL_AT_MOST_ONCE) as a literal, not the
        // MqttMsgBase constant - default-parameter values must be compile-time
        // constants, and depending on the exact M2Mqtt build having that field be
        // `const` (vs. `static readonly`) isn't worth the compile risk.
        public void Subscribe(string topic, byte qos = 0)
        {
            lock (_subLock)
            {
                if (_subscribedTopics.Contains(topic)) return;
                _subscribedTopics.Add(topic);
                _subscribedQos.Add(qos);
            }
            if (IsConnected)
                _client.Subscribe(new[] { topic }, new[] { qos });
        }

        void ResubscribeAll()
        {
            lock (_subLock)
            {
                if (_subscribedTopics.Count == 0) return;
                _client.Subscribe(_subscribedTopics.ToArray(), _subscribedQos.ToArray());
            }
        }

        /// <summary>Fire-and-forget publish. No-op (and no queue) if not currently connected -
        /// ponytail: acceptable for a telemetry/deterrent-ack sim, not a store-and-forward broker.</summary>
        public void Publish(string topic, string jsonPayload, byte qos = 0)
        {
            if (!IsConnected) return;
            _client.Publish(topic, Encoding.UTF8.GetBytes(jsonPayload), qos, false);
        }

        void HandlePublishReceived(object sender, MqttMsgPublishEventArgs e)
        {
            string topic = e.Topic;
            string payload = Encoding.UTF8.GetString(e.Message);
            MainThreadDispatcher.Instance.Enqueue(() => OnMessage?.Invoke(topic, payload));
        }

        void PublishHeartbeat()
        {
            var msg = new TelemetryMsg
            {
                ts = Iso8601Utc.Now(),
                source = telemetrySource,
                status = "ok"
            };
            Publish(telemetryTopic, JsonUtility.ToJson(msg), MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE);
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
