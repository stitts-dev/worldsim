using System;
using UnityEngine;
using WorldSim.Core;
using uPLibrary.Networking.M2Mqtt.Messages;

namespace WorldSim.CatBasement
{
    /// <summary>
    /// Subscribes catsentry/deterrent/fire, reacts visibly ("sound" = chirp + ring,
    /// "air" = puff), publishes catsentry/deterrent/ack, and tells any squatting
    /// CatAgent in the scene to flee. Payload field names are frozen by
    /// docs/contract-catsentry-v1.md - never rename them.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class VirtualTurret : MonoBehaviour
    {
        [Header("MQTT")]
        public MqttBridge bridge; // assign in inspector; auto-finds one in the scene if left blank
        public string fireTopic = "catsentry/deterrent/fire";
        public string ackTopic = "catsentry/deterrent/ack";

        [Header("Effects")]
        public AudioClip chirpClip;
        public ParticleSystem ringEffect; // "sound" - expanding ring
        public ParticleSystem puffEffect; // "air" - white puff

        [Header("Aiming (rough - real aiming is phase 2 / issue #7)")]
        public Transform floorReference; // a plane/quad whose localScale spans the room footprint

        AudioSource _audio;

        // debug overlay state (OnGUI, per spec)
        string _lastCommandDebug = "(none)";
        string _lastAckDebug = "(none)";
        int _fireCount;

        void Awake()
        {
            _audio = GetComponent<AudioSource>();
            if (bridge == null) bridge = FindFirstObjectByType<MqttBridge>();
        }

        void OnEnable()
        {
            if (bridge == null)
            {
                Debug.LogWarning("VirtualTurret: no MqttBridge found in scene - deterrent commands won't be received.");
                return;
            }
            bridge.OnMessage += HandleMessage;
            // MqttBridge remembers subscriptions across reconnects, so a single
            // call here is enough regardless of current connection state.
            bridge.Subscribe(fireTopic, MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE);
        }

        void OnDisable()
        {
            if (bridge == null) return;
            bridge.OnMessage -= HandleMessage;
        }

        void HandleMessage(string topic, string payload)
        {
            if (topic != fireTopic) return;

            DeterrentFireMsg msg;
            try
            {
                msg = JsonUtility.FromJson<DeterrentFireMsg>(payload);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"VirtualTurret: malformed fire payload, ignoring. {e.Message}");
                return; // contract has no "bad request" ack - just drop it
            }

            _fireCount++;
            _lastCommandDebug = (msg.target != null && msg.target.Length >= 2)
                ? $"{msg.level} target=({msg.target[0]:F2},{msg.target[1]:F2}) dur={msg.duration_ms}ms \"{msg.reason}\""
                : $"{msg.level} dur={msg.duration_ms}ms \"{msg.reason}\"";

            Vector3 worldTarget = NormalizedToWorld(msg.target);
            bool ok = true;
            string detail;

            switch (msg.level)
            {
                case "sound":
                    if (chirpClip != null) _audio.PlayOneShot(chirpClip);
                    if (ringEffect != null) { ringEffect.transform.position = worldTarget; ringEffect.Play(); }
                    detail = $"played chirp {msg.duration_ms}ms";
                    break;

                case "air":
                    if (puffEffect != null) { puffEffect.transform.position = worldTarget; puffEffect.Play(); }
                    detail = "puff fired";
                    break;

                default:
                    ok = false;
                    detail = $"unknown level '{msg.level}'";
                    break;
            }

            NotifySquattingCats(worldTarget);
            PublishAck(msg.level, ok, detail);
        }

        Vector3 NormalizedToWorld(float[] target)
        {
            // ponytail: no camera-ray aiming yet (tracked as issue #7, phase 2).
            // Good enough to place effects/flee-direction sensibly: map normalized
            // [0..1] frame coords onto floorReference's local XZ footprint.
            if (floorReference == null || target == null || target.Length < 2)
                return transform.position;

            Vector3 floorSize = floorReference.localScale;
            float x = (target[0] - 0.5f) * floorSize.x;
            float z = (0.5f - target[1]) * floorSize.z; // frame origin is top-left, flip for world z
            return floorReference.position + new Vector3(x, 0f, z);
        }

        void NotifySquattingCats(Vector3 worldTarget)
        {
            foreach (var cat in CatAgent.AllCats)
            {
                if (cat.IsSquatting) cat.FleeFrom(worldTarget);
            }
        }

        void PublishAck(string level, bool ok, string detail)
        {
            var ack = new DeterrentAckMsg
            {
                ts = Iso8601Utc.Now(),
                level = level,
                ok = ok,
                detail = detail
            };
            string json = JsonUtility.ToJson(ack);
            bridge.Publish(ackTopic, json, MqttMsgBase.QOS_LEVEL_AT_LEAST_ONCE);
            _lastAckDebug = json;
        }

        void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 460, 100), GUI.skin.box);
            GUILayout.Label($"VirtualTurret  (mqtt connected: {(bridge != null && bridge.IsConnected)})");
            GUILayout.Label($"last command: {_lastCommandDebug}");
            GUILayout.Label($"last ack: {_lastAckDebug}");
            GUILayout.Label($"fires this session: {_fireCount}");
            GUILayout.EndArea();
        }
    }

    /// <summary>catsentry/deterrent/fire payload - field names frozen by docs/contract-catsentry-v1.md.</summary>
    [Serializable]
    public class DeterrentFireMsg
    {
        public string ts;
        public string level; // "sound" | "air"
        public float[] target; // normalized [x, y], origin top-left
        public int duration_ms;
        public string reason;
    }

    /// <summary>catsentry/deterrent/ack payload - field names frozen by docs/contract-catsentry-v1.md.</summary>
    [Serializable]
    public class DeterrentAckMsg
    {
        public string ts;
        public string level;
        public bool ok;
        public string detail;
    }
}
