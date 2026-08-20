using UnityEngine;
using UnityEngine.AI;

namespace WorldSim.Core.Agents
{
    /// <summary>
    /// NavMeshAgent random wander: picks a reachable point inside a box volume,
    /// walks to it, idles 1-4 s (configurable), picks another. Reusable across
    /// scenarios - knows nothing about cats or litterboxes.
    ///
    /// Scenario code (e.g. CatAgent) can take over the shared NavMeshAgent for a
    /// scripted routine by calling <see cref="Pause"/> / <see cref="Resume"/>.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class WanderAgent : MonoBehaviour
    {
        [Header("Bounds (local box around boundsCenter)")]
        public Vector3 boundsCenter = Vector3.zero;
        public Vector3 boundsExtents = new Vector3(3f, 0f, 2f);

        [Header("Movement")]
        public float speed = 1.2f;
        public float minIdleSec = 1f;
        public float maxIdleSec = 4f;

        NavMeshAgent _agent;
        float _idleTimer;
        bool _idling;
        bool _paused;

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        void OnEnable()
        {
            _agent.speed = speed;
            if (!_paused) PickNewDestination();
        }

        void Update()
        {
            if (_paused) return;

            if (_idling)
            {
                _idleTimer -= Time.deltaTime;
                if (_idleTimer <= 0f) PickNewDestination();
                return;
            }

            if (!_agent.pathPending && _agent.remainingDistance <= _agent.stoppingDistance)
                StartIdle();
        }

        void StartIdle()
        {
            _idling = true;
            _idleTimer = Random.Range(minIdleSec, maxIdleSec);
        }

        void PickNewDestination()
        {
            _idling = false;
            Vector3 point = RandomPointInBounds();
            if (NavMesh.SamplePosition(point, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        Vector3 RandomPointInBounds()
        {
            Vector3 offset = new Vector3(
                Random.Range(-boundsExtents.x, boundsExtents.x),
                0f,
                Random.Range(-boundsExtents.z, boundsExtents.z));
            return boundsCenter + offset;
        }

        /// <summary>Stop wandering and release the NavMeshAgent for scripted use elsewhere.</summary>
        public void Pause()
        {
            _paused = true;
            _idling = false;
            if (_agent.isOnNavMesh) _agent.ResetPath();
        }

        /// <summary>Resume normal wander behavior (picks a fresh destination immediately).</summary>
        public void Resume()
        {
            _paused = false;
            _agent.speed = speed;
            PickNewDestination();
        }
    }
}
