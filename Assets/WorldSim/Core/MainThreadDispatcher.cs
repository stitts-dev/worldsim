using System;
using System.Collections.Generic;
using UnityEngine;

namespace WorldSim.Core
{
    /// <summary>
    /// Tiny cross-thread -> main-thread queue. FrameStreamer's HttpListener thread
    /// never touches Unity API (it only reads a byte[] buffer), so it doesn't need
    /// this. MqttBridge's M2Mqtt callbacks fire on M2Mqtt's internal receiver
    /// thread and DO need it - that's the one real consumer today.
    ///
    /// ponytail: one global FIFO queue, not a per-system dispatcher. A homelab sim
    /// pumping a handful of MQTT messages a second doesn't need more than this.
    /// </summary>
    public class MainThreadDispatcher : MonoBehaviour
    {
        static MainThreadDispatcher _instance;

        readonly Queue<Action> _queue = new Queue<Action>();
        readonly object _lock = new object();

        public static MainThreadDispatcher Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("MainThreadDispatcher");
                    _instance = go.AddComponent<MainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>Safe to call from any thread.</summary>
        public void Enqueue(Action action)
        {
            if (action == null) return;
            lock (_lock)
            {
                _queue.Enqueue(action);
            }
        }

        void Update()
        {
            // Drain into a local list before invoking so we're not holding the lock
            // (and blocking other threads trying to Enqueue) while callbacks run.
            List<Action> pending = null;
            lock (_lock)
            {
                if (_queue.Count > 0)
                {
                    pending = new List<Action>(_queue);
                    _queue.Clear();
                }
            }

            if (pending == null) return;
            for (int i = 0; i < pending.Count; i++)
            {
                try { pending[i].Invoke(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }
    }
}
