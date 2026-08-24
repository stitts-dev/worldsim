using UnityEngine;
using UnityEngine.AI;

namespace WorldSim.Core.Agents
{
    /// <summary>Discrete speed modes. A leg of a journey is walked at one of these
    /// rather than at a single global speed constant - the thing that reads as
    /// "robot" is not the speed, it is that the speed never changes.</summary>
    public enum Gait
    {
        /// <summary>Slow investigative creep.</summary>
        Prowl,
        /// <summary>Ordinary travel.</summary>
        Walk,
        /// <summary>Purposeful, quicker than a walk, still not a run.</summary>
        Trot,
        /// <summary>A bolt. Reserved for fleeing.</summary>
        Dart
    }

    /// <summary>
    /// Shapes how a <see cref="NavMeshAgent"/> moves along the path it has already
    /// found. Pathfinding, steering and position integration stay with the agent -
    /// this component only decides <i>how fast</i> and <i>which way the body faces</i>,
    /// which is where the unnatural motion actually lives.
    ///
    /// Three things it does, in order of how much they buy:
    ///
    ///   1. <b>Rotation is rate-limited and the body leads the movement.</b> The agent's
    ///      own rotation snaps to the steering direction, which is the single most
    ///      robotic tell. Here <c>updateRotation</c> is off and the body turns at a
    ///      bounded deg/s toward the current path corner.
    ///   2. <b>Forward speed is coupled to how sharp the current turn is.</b> A cat does
    ///      not arc through a 120 degree corner at cruise; it slows, plants, turns, and
    ///      goes. Above <see cref="turnStopDeg"/> the commanded speed collapses to
    ///      <see cref="pivotSpeedFactor"/>, so the cat pivots roughly in place and then
    ///      accelerates out. This coupling is also what keeps the body pointing where it
    ///      is going: the agent can only out-run the turn if the turn is gentle.
    ///   3. <b>Speed is ramped, never assigned.</b> Ease in from rest, ease down into a
    ///      destination, ease down for a corner - separate accel/decel rates because
    ///      stopping is faster than starting.
    ///
    /// Determinism: every value here advances on <see cref="Time.deltaTime"/> and this
    /// component draws no random numbers at all. At <c>timeScale = 0</c> deltaTime is 0,
    /// so speed, rotation and every timer freeze exactly, and one stepped frame advances
    /// exactly one frame's worth.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    public class NaturalLocomotion : MonoBehaviour
    {
        [Header("Speed")]
        /// <summary>Metres/second at Gait.Walk. Every gait multiplier is relative to this.
        /// WanderAgent overwrites this from its own <c>speed</c> field on enable, so a
        /// scene that configures WanderAgent keeps a single source of truth.</summary>
        public float baseSpeed = 1.2f;
        public float prowlMultiplier = 0.45f;
        public float walkMultiplier = 1f;
        public float trotMultiplier = 1.55f;
        /// <summary>Flee speed. 3x base crosses the 4.4 m wander box in about 1.2 s, which
        /// is the point - a bolt has to read as a different kind of motion, not a fast walk.</summary>
        public float dartMultiplier = 3f;

        [Header("Acceleration (m/s^2 on the commanded speed)")]
        public float accelRate = 2.5f;
        /// <summary>Deliberately larger than accelRate: a cat stops much faster than it starts.</summary>
        public float decelRate = 5f;
        /// <summary>A bolt is explosive. At the ordinary accelRate a Dart would need ~1.5 s
        /// to reach speed, by which time the flee is half over and reads as a jog.</summary>
        public float dartAccelRate = 9f;

        [Header("Arrival")]
        /// <summary>Distance from the destination at which the ease-down begins.</summary>
        public float slowDownDistance = 0.6f;
        /// <summary>Share of gait speed still commanded right at the stopping distance.
        /// Not zero, or the last few centimetres take forever.</summary>
        [Range(0f, 1f)] public float arrivalSpeedFactor = 0.2f;

        [Header("Turning")]
        /// <summary>Turn rate when standing still. A 180 degree pivot at 230 deg/s takes
        /// 0.78 s - fast enough to stay well inside CatAgent's 20 s walk watchdogs.</summary>
        public float pivotTurnDegPerSec = 230f;
        /// <summary>Turn rate at full gait speed. Lower than the pivot rate: a moving body
        /// has momentum and cannot rotate as freely as a planted one.</summary>
        public float movingTurnDegPerSec = 130f;
        /// <summary>Below this heading error a turn is free - no speed is given up.</summary>
        public float turnSlowStartDeg = 25f;
        /// <summary>At or above this heading error the cat gives up almost all forward
        /// speed and turns first. This is the stop-and-turn behaviour.</summary>
        public float turnStopDeg = 95f;
        /// <summary>Commanded speed during a hard turn, as a share of gait speed. Kept well
        /// above zero so a pivot always makes ground and no walk can stall into a timeout.</summary>
        [Range(0f, 1f)] public float pivotSpeedFactor = 0.18f;

        [Header("NavMeshAgent overrides (re-applied whenever the agent is re-enabled)")]
        /// <summary>The agent's own acceleration is set high on purpose: this component is
        /// the acceleration authority, and the agent should track the commanded speed
        /// closely rather than smoothing it a second time.</summary>
        public float agentAcceleration = 30f;
        /// <summary>Off for the same reason - <see cref="slowDownDistance"/> owns the
        /// arrival ease-down, and autoBraking on top of it decelerates twice.</summary>
        public bool agentAutoBraking = false;

        NavMeshAgent _agent;
        bool _agentWasEnabled;

        Gait _gait = Gait.Walk;
        float _currentSpeed;
        float _holdTimer;
        float _lookTimer;
        Vector3 _lookDir;
        bool _rotationSuspended;

        /// <summary>Commanded speed this frame, in m/s. Read-only view for debug/inspection.</summary>
        public float CurrentSpeed => _currentSpeed;

        public Gait CurrentGait => _gait;

        void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        void OnEnable()
        {
            ApplyAgentOverrides();
            _agentWasEnabled = _agent.enabled;
        }

        void OnDisable()
        {
            // Hand rotation back the way we found it. Leaving updateRotation off on a
            // component that is no longer driving rotation would freeze the cat's facing
            // for the rest of the session.
            if (_agent != null && _agent.enabled) _agent.updateRotation = true;
        }

        void ApplyAgentOverrides()
        {
            if (!_agent.enabled) return;
            _agent.acceleration = agentAcceleration;
            _agent.autoBraking = agentAutoBraking;
        }

        // ---- external control ----

        public void SetGait(Gait gait) => _gait = gait;

        /// <summary>Stand still for <paramref name="seconds"/>. Overrides the pivot floor -
        /// a pause has to actually be a pause, not a creep.</summary>
        public void Hold(float seconds) => _holdTimer = Mathf.Max(_holdTimer, seconds);

        /// <summary>Cancel any hold and any look request. Called whenever something else
        /// takes the agent over, so a leftover pause cannot bleed into a scripted routine.</summary>
        public void ClearHold()
        {
            _holdTimer = 0f;
            _lookTimer = 0f;
        }

        /// <summary>Turn to face <paramref name="worldDir"/> for the next
        /// <paramref name="seconds"/>, in preference to the path heading. Used for
        /// look-arounds and for settling on arrival.</summary>
        public void LookToward(Vector3 worldDir, float seconds)
        {
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 1e-6f) return;
            _lookDir = worldDir.normalized;
            _lookTimer = seconds;
        }

        /// <summary>Stop driving rotation and stop touching <c>updateRotation</c>, so
        /// scenario code can hold a pose (CatAgent's nose-down sniff tilt). Nothing here
        /// writes <c>transform.rotation</c> while suspended.</summary>
        public void SuspendRotation() => _rotationSuspended = true;

        public void ResumeRotation() => _rotationSuspended = false;

        // ---- per-frame ----

        void Update()
        {
            bool enabledNow = _agent.enabled;
            if (enabledNow && !_agentWasEnabled) ApplyAgentOverrides();
            _agentWasEnabled = enabledNow;

            float dt = Time.deltaTime;
            if (_holdTimer > 0f) _holdTimer -= dt;
            if (_lookTimer > 0f) _lookTimer -= dt;

            // The agent is switched off and the transform is driven directly during
            // CatAgent's litterbox step-up. Touching speed or rotation there would fight
            // that routine, so bail out entirely and forget the ramp.
            if (!enabledNow || !_agent.isOnNavMesh)
            {
                _currentSpeed = 0f;
                return;
            }

            Vector3 heading = CurrentHeading();
            float headingErrorDeg = heading.sqrMagnitude > 1e-6f
                ? Vector3.Angle(transform.forward, heading)
                : 0f;

            _currentSpeed = RampSpeed(TargetSpeed(headingErrorDeg), dt);
            _agent.speed = _currentSpeed;

            if (!_rotationSuspended)
            {
                _agent.updateRotation = false; // re-asserted every frame: scenario code flips it back on
                if (heading.sqrMagnitude > 1e-6f) TurnToward(heading, dt);
            }
        }

        /// <summary>Where the body wants to point. The path corner is used rather than
        /// <c>desiredVelocity</c> on purpose: desiredVelocity scales with the speed this
        /// component just commanded, so a hard turn would zero the speed, zero the desired
        /// velocity, erase the heading and deadlock the cat mid-pivot. The steering target
        /// is independent of speed. An explicit look request outranks both.</summary>
        Vector3 CurrentHeading()
        {
            if (_lookTimer > 0f) return _lookDir;

            if (_agent.hasPath || _agent.pathPending)
            {
                Vector3 toCorner = _agent.steeringTarget - transform.position;
                toCorner.y = 0f;
                if (toCorner.sqrMagnitude > 1e-4f) return toCorner;

                Vector3 desired = _agent.desiredVelocity;
                desired.y = 0f;
                if (desired.sqrMagnitude > 1e-4f) return desired;
            }

            // Nothing to face. Return zero rather than a guess - this is the case that
            // keeps us off CatAgent's sniff pose, where the cat has already arrived.
            return Vector3.zero;
        }

        float GaitSpeed()
        {
            switch (_gait)
            {
                case Gait.Prowl: return baseSpeed * prowlMultiplier;
                case Gait.Trot: return baseSpeed * trotMultiplier;
                case Gait.Dart: return baseSpeed * dartMultiplier;
                default: return baseSpeed * walkMultiplier;
            }
        }

        float TargetSpeed(float headingErrorDeg)
        {
            if (_holdTimer > 0f) return 0f;
            if (!_agent.hasPath && !_agent.pathPending) return 0f;
            if (!_agent.pathPending && _agent.remainingDistance <= _agent.stoppingDistance) return 0f;

            return GaitSpeed() * TurnFactor(headingErrorDeg) * ArrivalFactor();
        }

        /// <summary>1 for a gentle turn, falling to <see cref="pivotSpeedFactor"/> at
        /// <see cref="turnStopDeg"/> and beyond. Smooth-stepped so the transition into a
        /// corner is a deceleration rather than a gear change.</summary>
        float TurnFactor(float headingErrorDeg)
        {
            if (headingErrorDeg <= turnSlowStartDeg) return 1f;
            float span = Mathf.Max(0.01f, turnStopDeg - turnSlowStartDeg);
            float t = Mathf.Clamp01((headingErrorDeg - turnSlowStartDeg) / span);
            return Mathf.Lerp(1f, pivotSpeedFactor, Mathf.SmoothStep(0f, 1f, t));
        }

        float ArrivalFactor()
        {
            if (_agent.pathPending || slowDownDistance <= 0f) return 1f;
            float remaining = _agent.remainingDistance - _agent.stoppingDistance;
            if (float.IsInfinity(remaining) || remaining >= slowDownDistance) return 1f;
            return Mathf.Lerp(arrivalSpeedFactor, 1f, Mathf.Clamp01(remaining / slowDownDistance));
        }

        float RampSpeed(float target, float dt)
        {
            float rate = target > _currentSpeed
                ? (_gait == Gait.Dart ? dartAccelRate : accelRate)
                : decelRate;
            return Mathf.MoveTowards(_currentSpeed, target, rate * dt);
        }

        void TurnToward(Vector3 heading, float dt)
        {
            // Faster when planted, slower when moving. Measured against the gait's own top
            // speed so a prowling cat still turns like a slow cat, not like a stalled one.
            float speedFraction = Mathf.Clamp01(_currentSpeed / Mathf.Max(0.01f, GaitSpeed()));
            float rate = Mathf.Lerp(pivotTurnDegPerSec, movingTurnDegPerSec, speedFraction);

            Quaternion want = Quaternion.LookRotation(heading.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want, rate * dt);
        }
    }
}
