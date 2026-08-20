using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using WorldSim.Core.Agents;

namespace WorldSim.CatBasement
{
    /// <summary>
    /// Wanders via WanderAgent, plus a scripted misbehavior routine: every
    /// 60-120 s (random) or on hotkey P, walk to a point beside the litterboxes
    /// and squat (scale Y to ~60%) for squat_duration, then resume wandering.
    /// VirtualTurret calls <see cref="FleeFrom"/> to cancel a squat and sprint
    /// the cat to a far corner - that's what makes the closed-loop demo visible.
    /// </summary>
    [RequireComponent(typeof(WanderAgent))]
    public class CatAgent : MonoBehaviour
    {
        [Header("Misbehavior timing")]
        public float minTriggerSec = 60f;
        public float maxTriggerSec = 120f;
        public float squatDurationSec = 8f;

        [Header("Problem zone (beside the litterboxes, 1-2 m radius)")]
        public Transform problemZoneCenter;
        public float problemZoneRadius = 1.5f;

        [Header("Flee")]
        public Transform farCorner;
        public float fleeSpeedMultiplier = 2.5f;

        static readonly List<CatAgent> All = new List<CatAgent>();
        public static IReadOnlyList<CatAgent> AllCats => All;

        WanderAgent _wander;
        NavMeshAgent _navAgent;
        float _normalSpeed;
        float _triggerTimer;

        enum State { Wandering, WalkingToZone, Squatting, Fleeing }
        State _state = State.Wandering;
        float _stateTimer;

        public bool IsSquatting => _state == State.Squatting;

        void Awake()
        {
            _wander = GetComponent<WanderAgent>();
            _navAgent = GetComponent<NavMeshAgent>();
        }

        void OnEnable()
        {
            All.Add(this);
            _normalSpeed = _navAgent.speed;
            ResetTrigger();
        }

        void OnDisable()
        {
            All.Remove(this);
        }

        void ResetTrigger()
        {
            _triggerTimer = Random.Range(minTriggerSec, maxTriggerSec);
        }

        void Update()
        {
            // ponytail: spec says "hotkey P targets nearest cat" without saying
            // nearest-to-what. Camera.main is the natural stand-in for "the human
            // watching the stream"; falls back to world origin if nothing's tagged
            // MainCamera yet (fine for a 6x4 m room).
            if (Input.GetKeyDown(KeyCode.P) && _state == State.Wandering)
            {
                Vector3 reference = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                if (FindNearestWanderingTo(reference) == this)
                    BeginMisbehavior();
            }

            switch (_state)
            {
                case State.Wandering:
                    _triggerTimer -= Time.deltaTime;
                    if (_triggerTimer <= 0f) BeginMisbehavior();
                    break;

                case State.WalkingToZone:
                    if (_navAgent.HasArrived())
                        BeginSquat();
                    break;

                case State.Squatting:
                    _stateTimer -= Time.deltaTime;
                    if (_stateTimer <= 0f) EndSquat();
                    break;

                case State.Fleeing:
                    if (_navAgent.HasArrived())
                        EndFlee();
                    break;
            }
        }

        static CatAgent FindNearestWanderingTo(Vector3 worldPos)
        {
            CatAgent nearest = null;
            float best = float.MaxValue;
            foreach (var cat in All)
            {
                if (cat._state != State.Wandering) continue;
                float d = (cat.transform.position - worldPos).sqrMagnitude;
                if (d < best) { best = d; nearest = cat; }
            }
            return nearest;
        }

        void BeginMisbehavior()
        {
            _wander.Pause();
            _state = State.WalkingToZone;

            Vector3 target = ProblemZonePoint();
            if (_navAgent.isOnNavMesh && NavMesh.SamplePosition(target, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                _navAgent.SetDestination(hit.position);
        }

        Vector3 ProblemZonePoint()
        {
            Vector3 center = problemZoneCenter != null ? problemZoneCenter.position : transform.position;
            Vector2 offset = Random.insideUnitCircle * problemZoneRadius;
            return center + new Vector3(offset.x, 0f, offset.y);
        }

        void BeginSquat()
        {
            _state = State.Squatting;
            _stateTimer = squatDurationSec;
            SetSquatScale(true);
        }

        void EndSquat()
        {
            SetSquatScale(false);
            _state = State.Wandering;
            ResetTrigger();
            _wander.Resume();
        }

        void SetSquatScale(bool squatting)
        {
            Vector3 s = transform.localScale;
            s.y = squatting ? s.y * 0.6f : s.y / 0.6f;
            transform.localScale = s;
        }

        /// <summary>Cancel a squat and sprint to the far corner. No-op unless currently squatting.</summary>
        public void FleeFrom(Vector3 threatWorldPos)
        {
            if (_state != State.Squatting) return;

            SetSquatScale(false);
            _state = State.Fleeing;
            _navAgent.speed = _normalSpeed * fleeSpeedMultiplier;

            Vector3 target = farCorner != null
                ? farCorner.position
                : transform.position + (transform.position - threatWorldPos).normalized * 5f;

            if (_navAgent.isOnNavMesh)
            {
                if (NavMesh.SamplePosition(target, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    _navAgent.SetDestination(hit.position);
                else
                    _navAgent.SetDestination(target);
            }
        }

        void EndFlee()
        {
            _navAgent.speed = _normalSpeed;
            _state = State.Wandering;
            ResetTrigger();
            _wander.Resume();
        }
    }
}
