using UnityEngine;
using UnityEngine.AI;
using WorldSim.Core;

namespace WorldSim.Core.Agents
{
    /// <summary>
    /// NavMeshAgent random wander. Picks a reachable point inside a box volume, walks
    /// to it at a gait chosen for that leg, may stop once on the way to look around,
    /// settles on arrival, then idles before picking another. Reusable across scenarios
    /// - knows nothing about cats or litterboxes.
    ///
    /// The wandering an animal does is not uniform: destinations favour the edges of the
    /// space over its middle, legs are walked at different speeds, and idles are usually
    /// short with the occasional long one. All three are what this class adds on top of
    /// "pick a point, go there". <see cref="NaturalLocomotion"/> owns the dynamics -
    /// acceleration, turn rate, turn-coupled speed - and is added automatically if the
    /// GameObject does not already carry one, so scenes built before it existed get the
    /// behaviour without a rebuild.
    ///
    /// Scenario code (e.g. CatAgent) can take over the shared NavMeshAgent for a
    /// scripted routine by calling <see cref="Pause"/> / <see cref="Resume"/>.
    ///
    /// Determinism: every timer runs on <see cref="Time.deltaTime"/> and every choice
    /// draws from <see cref="UnityEngine.Random"/>, which the harness seeds on reset.
    /// Nothing here reads wall-clock time or real elapsed time.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class WanderAgent : MonoBehaviour
    {
        [Header("Bounds (local box around boundsCenter)")]
        public Vector3 boundsCenter = Vector3.zero;
        public Vector3 boundsExtents = new Vector3(3f, 0f, 2f);

        [Header("Movement")]
        /// <summary>Base walk speed in m/s. Pushed into <see cref="NaturalLocomotion.baseSpeed"/>
        /// on enable, so a scene that configures this field configures both.</summary>
        public float speed = 1.2f;

        [Header("Idle between legs")]
        public float minIdleSec = 1f;
        public float maxIdleSec = 4f;
        /// <summary>Exponent on a uniform draw, so idles cluster short with an occasional
        /// long one instead of being uniformly spread. 1 = the old uniform behaviour.
        /// A flat distribution of idle lengths is itself a robotic tell - every pause
        /// looks the same length.</summary>
        [Min(1f)] public float idleShortBias = 2f;

        [Header("Gait mix (relative weights, drawn per leg)")]
        public float prowlWeight = 0.3f;
        public float walkWeight = 0.5f;
        public float trotWeight = 0.2f;

        [Header("Edge preference")]
        /// <summary>Share of destinations pushed out to the perimeter of the bounds volume.
        /// Animals hug edges and avoid open middles; straight lines across the centre of
        /// the room are the thing that looks wrong. Note this is the perimeter of the
        /// wander bounds, which are inset from the actual walls - it reads as "prefers the
        /// edges of the space", not as wall contact.</summary>
        [Range(0f, 1f)] public float edgeBias = 0.65f;
        /// <summary>Thickness of that perimeter band, as a fraction of the extent. Small
        /// values pin destinations to a line; 0.25 keeps some spread.</summary>
        [Range(0.01f, 1f)] public float edgeBand = 0.25f;

        [Header("Mid-route hesitation")]
        /// <summary>Chance a leg contains one stop-and-look-around.</summary>
        [Range(0f, 1f)] public float hesitationChance = 0.45f;
        /// <summary>When a leg hesitates, how far into it (seconds) the stop happens.</summary>
        public float minHesitationDelaySec = 0.8f;
        public float maxHesitationDelaySec = 3f;
        public float minHesitationSec = 0.6f;
        public float maxHesitationSec = 1.8f;
        /// <summary>Maximum yaw, either side of the current heading, that the look-around
        /// turns to. The cat looks off its route, then turns back and carries on.</summary>
        public float hesitationLookDeg = 70f;

        [Header("Settle on arrival")]
        /// <summary>Arriving and stopping dead is robotic. On arrival the cat holds
        /// briefly and reorients before the idle timer starts.</summary>
        public float minSettleSec = 0.4f;
        public float maxSettleSec = 1.1f;
        /// <summary>Maximum yaw change during that settle, either side of arrival heading.</summary>
        public float settleTurnDeg = 90f;

        enum Phase { Travelling, Settling, Idling }

        NavMeshAgent _agent;
        NaturalLocomotion _locomotion;
        Phase _phase = Phase.Idling;
        float _phaseTimer;
        float _hesitationTimer; // negative once this leg's hesitation is spent or never scheduled
        bool _paused;

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();

            // Added rather than required: CatBasement.unity is a committed asset whose cat
            // GameObjects predate this component, and RequireComponent only fires when a
            // component is added in the editor - never at scene load. Adding it here means
            // the existing scene gets natural locomotion with no rebuild, while the scene
            // builder still adds and configures one explicitly for the next rebuild.
            _locomotion = GetComponent<NaturalLocomotion>();
            if (_locomotion == null) _locomotion = gameObject.AddComponent<NaturalLocomotion>();
        }

        void OnEnable()
        {
            _agent.speed = speed;
            _locomotion.baseSpeed = speed;
            if (!_paused) PickNewDestination();
        }

        void Update()
        {
            if (_paused) return;

            switch (_phase)
            {
                case Phase.Travelling:
                    TickHesitation();
                    if (_agent.HasArrived()) StartSettle();
                    break;

                case Phase.Settling:
                    _phaseTimer -= Time.deltaTime;
                    if (_phaseTimer <= 0f) StartIdle();
                    break;

                case Phase.Idling:
                    _phaseTimer -= Time.deltaTime;
                    if (_phaseTimer <= 0f) PickNewDestination();
                    break;
            }
        }

        // ---- phases ----

        void TickHesitation()
        {
            if (_hesitationTimer < 0f) return;
            _hesitationTimer -= Time.deltaTime;
            if (_hesitationTimer > 0f) return;

            // One per leg: spend it. A hesitation only pauses movement - the path is kept,
            // so the cat resumes the same route rather than re-deciding where it was going.
            _hesitationTimer = -1f;
            float duration = Random.Range(minHesitationSec, maxHesitationSec);
            _locomotion.Hold(duration);
            _locomotion.LookToward(YawedForward(hesitationLookDeg), duration);
        }

        void StartSettle()
        {
            _phase = Phase.Settling;
            _phaseTimer = Random.Range(minSettleSec, maxSettleSec);
            _locomotion.Hold(_phaseTimer);
            _locomotion.LookToward(YawedForward(settleTurnDeg), _phaseTimer);
        }

        void StartIdle()
        {
            _phase = Phase.Idling;
            // u^bias on a uniform draw: most idles land near the minimum, a few run long.
            float u = Mathf.Pow(Random.value, Mathf.Max(1f, idleShortBias));
            _phaseTimer = Mathf.Lerp(minIdleSec, maxIdleSec, u);
            _locomotion.Hold(_phaseTimer);
        }

        void PickNewDestination()
        {
            _phase = Phase.Travelling;
            _locomotion.ClearHold();
            _locomotion.SetGait(PickGait());
            _hesitationTimer = Random.value < hesitationChance
                ? Random.Range(minHesitationDelaySec, maxHesitationDelaySec)
                : -1f;

            Vector3 point = RandomPointInBounds();
            if (_agent.isOnNavMesh && NavMesh.SamplePosition(point, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
        }

        // Gait.Dart is deliberately not in the mix - it is what a flee looks like, and it
        // stops meaning that if a cat wanders at it.
        Gait PickGait()
        {
            float total = Mathf.Max(0.0001f, prowlWeight + walkWeight + trotWeight);
            float roll = Random.value * total;
            if (roll < prowlWeight) return Gait.Prowl;
            if (roll < prowlWeight + walkWeight) return Gait.Walk;
            return Gait.Trot;
        }

        /// <summary>Current heading rotated by a random yaw within +/- <paramref name="maxDeg"/>.
        /// Falls back to world forward if the transform somehow has no horizontal facing.</summary>
        Vector3 YawedForward(float maxDeg)
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            return Quaternion.Euler(0f, Random.Range(-maxDeg, maxDeg), 0f) * forward.normalized;
        }

        Vector3 RandomPointInBounds()
        {
            float x = Random.Range(-boundsExtents.x, boundsExtents.x);
            float z = Random.Range(-boundsExtents.z, boundsExtents.z);

            if (Random.value < edgeBias)
            {
                // Keep the coordinate along the chosen edge and push the other one out into
                // the perimeter band. Sampling the band rather than snapping to the extent
                // keeps the routes from collapsing onto one rectangle.
                bool alongX = Random.value < 0.5f;
                float band = Mathf.Clamp01(edgeBand);
                if (alongX)
                    z = Mathf.Sign(z) * Mathf.Lerp(boundsExtents.z * (1f - band), boundsExtents.z, Random.value);
                else
                    x = Mathf.Sign(x) * Mathf.Lerp(boundsExtents.x * (1f - band), boundsExtents.x, Random.value);
            }

            return boundsCenter + new Vector3(x, 0f, z);
        }

        /// <summary>Stop wandering and release the NavMeshAgent for scripted use elsewhere.
        /// Clears any pending hold, so a pause drawn for a wander leg cannot bleed into the
        /// routine that just took over.</summary>
        public void Pause()
        {
            _paused = true;
            _phase = Phase.Idling;
            _phaseTimer = 0f;
            _hesitationTimer = -1f;
            _locomotion.ClearHold();
            if (_agent.isOnNavMesh) _agent.ResetPath();
        }

        /// <summary>Resume normal wander behavior (picks a fresh destination immediately).</summary>
        public void Resume()
        {
            _paused = false;
            _agent.speed = speed;
            _locomotion.baseSpeed = speed;
            PickNewDestination();
        }
    }
}
