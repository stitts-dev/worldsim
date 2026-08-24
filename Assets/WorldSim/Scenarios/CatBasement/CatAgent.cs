using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using WorldSim.Core;
using WorldSim.Core.Agents;

namespace WorldSim.CatBasement
{
    /// <summary>
    /// Wanders via WanderAgent, plus three scripted routines the detector has to
    /// tell apart:
    ///
    ///   - <b>misbehavior</b> - squat on the floor beside the litterboxes. The
    ///     positive class; the detector must fire on this.
    ///   - <b>box_use</b> - walk to a litterbox, step onto it, squat there, leave.
    ///     The negative class; the detector must never fire on this.
    ///   - <b>sniff</b> - a short investigative pause, often right next to a box.
    ///     False-positive bait: it looks like the start of a squat and isn't.
    ///
    /// Every transition is published to <c>catsentry/groundtruth</c> so detector
    /// precision/recall can be scored offline. That topic is sim-only and additive
    /// - it adds nothing to and renames nothing in catsentry-v1, and a detector
    /// that has never heard of it simply never subscribes.
    ///
    /// VirtualTurret calls <see cref="FleeFrom"/> to cancel a squat and sprint the
    /// cat to a far corner - that's what makes the closed-loop demo visible.
    /// </summary>
    [RequireComponent(typeof(WanderAgent))]
    public class CatAgent : MonoBehaviour, ISimInspectable
    {
        [Header("Misbehavior timing")]
        public float minTriggerSec = 60f;
        public float maxTriggerSec = 120f;
        public float squatDurationSec = 8f;

        [Header("Problem zone (beside the litterboxes, 1-2 m radius)")]
        public Transform problemZoneCenter;
        public float problemZoneRadius = 1.5f;

        [Header("Litterboxes (correct use - the detector must NOT fire on this)")]
        // The three box transforms. Empty is tolerated: the cat then only wanders,
        // sniffs and misbehaves.
        public Transform[] litterboxes;
        public float sniffDurationSec = 4f;
        public float boxUseDurationSec = 12f;
        /// <summary>Share of periodic triggers that misbehave; the rest use a box properly.</summary>
        [Range(0f, 1f)] public float misbehaviorChance = 0.35f;

        [Header("Litterbox approach / mount")]
        // How far out from the box centre to probe for a floor staging point.
        public float boxApproachDistance = 0.55f;
        /// <summary>NavMesh.SamplePosition radius used to snap that probe onto the floor.</summary>
        public float boxApproachSampleRadius = 1.5f;
        /// <summary>Seconds for the scripted step up onto (and back off) the box.</summary>
        public float boxMountDurationSec = 0.5f;
        /// <summary>Height of the little arc on that step, so it reads as a step-up.</summary>
        public float boxHopHeight = 0.06f;
        /// <summary>Clearance above the box's top surface so the cat doesn't z-fight the litter.</summary>
        public float boxSurfaceLift = 0.01f;

        [Header("Sniffing (own, lighter schedule)")]
        public float minSniffTriggerSec = 12f;
        public float maxSniffTriggerSec = 25f;
        /// <summary>Share of sniffs that happen right beside a litterbox rather than
        /// out on open floor. Box-side sniffs are the ambiguous case worth having.</summary>
        [Range(0f, 1f)] public float sniffNearBoxChance = 0.5f;
        public float sniffRoamRadius = 1.5f;
        /// <summary>How far from the box centre a box-side sniff stands.</summary>
        public float sniffBoxStandoff = 0.55f;

        [Header("Pose (all absolute, all reversible)")]
        public float squatScaleY = 0.6f;
        public float sniffScaleY = 0.85f;
        /// <summary>Nose-down pitch held during a sniff.</summary>
        public float sniffPitchDeg = 14f;

        [Header("Flee")]
        public Transform farCorner;
        public float fleeSpeedMultiplier = 2.5f;

        [Header("Ground truth (sim-only, additive to catsentry-v1)")]
        // Optional - auto-found in the scene when left blank.
        public WorldSim.Core.MqttBridge bridge;
        public string groundTruthTopic = "catsentry/groundtruth";

        [Header("Safety")]
        // Watchdog on every walking state. A cat that somehow never reaches its
        // destination finishes the routine anyway instead of hanging forever.
        public float walkTimeoutSec = 20f;

        static readonly List<CatAgent> All = new List<CatAgent>();
        public static IReadOnlyList<CatAgent> AllCats => All;

        WanderAgent _wander;
        NavMeshAgent _navAgent;
        float _normalSpeed;
        float _triggerTimer;
        float _sniffTimer;

        enum State
        {
            Wandering,
            WalkingToZone, Squatting,          // misbehavior
            WalkingToBox, MountingBox, UsingBox, DismountingBox, // correct use
            WalkingToSniff, Sniffing,          // investigation
            Fleeing
        }

        State _state = State.Wandering;
        float _stateTimer;

        // Pose. localScale is always recomputed from _baseScale and rotation is
        // always restored to an absolute upright yaw, so no sequence of overlapping
        // transitions can compound a squash or strand a tilt.
        Vector3 _baseScale = Vector3.one;
        bool _tilted;
        float _uprightYaw;

        // Box mount. _stagingNavPos is a confirmed NavMesh.SamplePosition hit,
        // captured before the cat ever leaves the mesh - it is the point we warp
        // back to, so there is no path that strands the cat off-mesh.
        Transform _targetBox;
        Vector3 _stagingNavPos;
        bool _hasStaging;
        Vector3 _groundReturnPos;
        Vector3 _boxStandPos;
        bool _offMesh;

        Vector3 _hopFrom, _hopTo;
        Quaternion _hopFromRot, _hopToRot;
        float _hopT;

        // Ground-truth dedup: the payload carries no state field, so consecutive
        // identical records would be indistinguishable to a consumer.
        bool _gtPublished;
        string _gtBehavior;
        bool _gtInBox;

        /// <summary>True while the cat is holding a squat pose or is committed to a
        /// litterbox squat (step-up, use, step-down). Deliberately covers correct box
        /// use as well as the floor squat: VirtualTurret gates its flee notification on
        /// this, and a deterrent has to be able to startle a cat that is in a box.
        /// Consumers that mean "misbehaving" want <c>IsSquatting &amp;&amp; !IsUsingBox</c>,
        /// or better, <see cref="CurrentBehavior"/> == "misbehavior".</summary>
        public bool IsSquatting =>
            _state == State.Squatting || _state == State.MountingBox ||
            _state == State.UsingBox || _state == State.DismountingBox;

        /// <summary>True while the cat is on a litterbox - i.e. behaving correctly.</summary>
        public bool IsUsingBox =>
            _state == State.MountingBox || _state == State.UsingBox || _state == State.DismountingBox;

        /// <summary>Ground-truth label for what the cat is doing right now:
        /// "wander" | "sniff" | "box_use" | "misbehavior" | "flee".</summary>
        public string CurrentBehavior => BehaviorFor(_state);

        void Awake()
        {
            _wander = GetComponent<WanderAgent>();
            _navAgent = GetComponent<NavMeshAgent>();
            _baseScale = transform.localScale; // captured once, never derived from a posed scale
            if (bridge == null) bridge = FindFirstObjectByType<MqttBridge>();
        }

        void OnEnable()
        {
            All.Add(this);
            // A disable that landed mid-mount left the agent switched off, and
            // nothing else ever switches it back on. OnEnable is the safe place for
            // this - unlike OnDisable it never runs during teardown.
            if (!_navAgent.enabled) _navAgent.enabled = true;
            // Read the configured speed, not the agent's current one. WanderAgent
            // also writes NavMeshAgent.speed, and OnDisable restores scale, rotation
            // and position but never speed - so a disable during a flee leaves the
            // agent at the flee multiple and this line would adopt it as the new
            // "normal", making the next flee faster again.
            _normalSpeed = _wander.speed;
            ResetTrigger();
            ResetSniffTimer();
            SetState(State.Wandering);
        }

        void OnDisable()
        {
            All.Remove(this);
            // Never leave the cat squashed, tilted or floating on a box because the
            // component (or Play mode) went away mid-routine. Deliberately touches
            // only the transform - the NavMeshAgent may already be tearing down.
            transform.localScale = _baseScale;
            if (_tilted) { transform.rotation = Quaternion.Euler(0f, _uprightYaw, 0f); _tilted = false; }
            if (_offMesh) { transform.position = _groundReturnPos; _offMesh = false; }
            // Assigned directly rather than through SetState: no ground truth is
            // published for this transition, because a teardown is not something the
            // cat did. OnEnable republishes on the way back in.
            _state = State.Wandering;
        }

        void ResetTrigger() => _triggerTimer = UnityEngine.Random.Range(minTriggerSec, maxTriggerSec);
        void ResetSniffTimer() => _sniffTimer = UnityEngine.Random.Range(minSniffTriggerSec, maxSniffTriggerSec);

        void Update()
        {
            // ponytail: spec says "hotkey P targets nearest cat" without saying
            // nearest-to-what. Camera.main is the natural stand-in for "the human
            // watching the stream"; falls back to world origin if nothing's tagged
            // MainCamera yet (fine for a 6x4 m room).
            if (HotkeyTargetsMe(KeyCode.P))
            {
                BeginMisbehavior();
            }
            else if (HotkeyTargetsMe(KeyCode.O))
            {
                if (!BeginBoxUse())
                    Debug.LogWarning(name + ": hotkey O - no litterbox with a reachable approach point; is CatAgent.litterboxes wired?");
            }
            else if (HotkeyTargetsMe(KeyCode.I))
            {
                BeginSniff();
            }

            switch (_state)
            {
                case State.Wandering:
                    _triggerTimer -= Time.deltaTime;
                    _sniffTimer -= Time.deltaTime;
                    if (_triggerTimer <= 0f)
                    {
                        // Sniffing runs on its own timer and must never touch
                        // _triggerTimer, or a frequent sniff schedule would postpone
                        // misbehavior and box use forever.
                        bool misbehave = UnityEngine.Random.value < misbehaviorChance;
                        if (!misbehave && !BeginBoxUse()) misbehave = true; // no usable box wired
                        if (misbehave) BeginMisbehavior();
                    }
                    else if (_sniffTimer <= 0f)
                    {
                        BeginSniff();
                    }
                    break;

                case State.WalkingToZone:
                    _stateTimer -= Time.deltaTime;
                    // Squatting where it stands on timeout is still honest ground
                    // truth - a floor squat is a floor squat wherever it happens.
                    if (ArrivedAtDestination() || _stateTimer <= 0f) BeginSquat();
                    break;

                case State.Squatting:
                    _stateTimer -= Time.deltaTime;
                    if (_stateTimer <= 0f) ReturnToWander(true);
                    break;

                case State.WalkingToBox:
                    _stateTimer -= Time.deltaTime;
                    // Both conditions, not just arrival: the hotkey path starts this
                    // state and falls into this switch in the same frame, and the
                    // mount is a direct transform move - starting it from across the
                    // room would slide the cat over the floor onto the box.
                    if (ArrivedAtDestination() && NearStagingPoint())
                    {
                        BeginBoxMount();
                    }
                    else if (_stateTimer <= 0f)
                    {
                        // Otherwise this is invisible. WalkingToBox and Wandering both
                        // publish "wander", so the ground-truth dedup swallows the
                        // transition, and a box-use attempt that starts and never
                        // arrives leaves no record anywhere. This file generates the
                        // labels a detector is scored against - correct box use
                        // silently ceasing to be produced is worse than a crash,
                        // because the only symptom is its absence.
                        Debug.LogWarning(name + ": gave up walking to the litterbox after "
                                         + walkTimeoutSec + "s - no box_use recorded for this attempt.");
                        ReturnToWander(true);
                    }
                    break;

                case State.MountingBox:
                    if (StepHop()) BeginBoxSquat();
                    break;

                case State.UsingBox:
                    _stateTimer -= Time.deltaTime;
                    if (_stateTimer <= 0f) BeginBoxDismount();
                    break;

                case State.DismountingBox:
                    if (StepHop()) ReturnToWander(true);
                    break;

                // Sniffing keeps the misbehavior countdown running. It fires often
                // enough that pausing the countdown for every sniff would visibly
                // stretch the configured 60-120 s window in wall-clock terms, which
                // is not the cadence those two numbers are set to mean. (The exact
                // stretch depends on how often sniffs land; it has not been measured.)
                case State.WalkingToSniff:
                    _stateTimer -= Time.deltaTime;
                    _triggerTimer -= Time.deltaTime;
                    if (ArrivedAtDestination() || _stateTimer <= 0f) BeginSniffHold();
                    break;

                case State.Sniffing:
                    _stateTimer -= Time.deltaTime;
                    _triggerTimer -= Time.deltaTime;
                    if (_stateTimer <= 0f) ReturnToWander(false);
                    break;

                case State.Fleeing:
                    _stateTimer -= Time.deltaTime;
                    if (ArrivedAtDestination() || _stateTimer <= 0f) EndFlee();
                    break;
            }
        }

        bool HotkeyTargetsMe(KeyCode key)
        {
            if (!Input.GetKeyDown(key) || _state != State.Wandering) return false;
            Vector3 reference = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            return FindNearestWanderingTo(reference) == this;
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

        // ---- state plumbing ----

        /// <summary>The single entry point for a state change: pose is a pure function
        /// of the new state and is reapplied unconditionally, and ground truth goes out
        /// on the same call. Nothing else is allowed to touch the pose.</summary>
        void SetState(State next)
        {
            _state = next;
            ApplyPose(next);
            PublishGroundTruth();
        }

        void ReturnToWander(bool resetTrigger)
        {
            ReturnToNavMesh();
            if (resetTrigger) ResetTrigger(); // a sniff must not postpone the next routine
            ResetSniffTimer();
            SetState(State.Wandering);
            _wander.Resume();
        }

        /// <summary>True only when the agent is genuinely drivable and has closed on its
        /// destination. remainingDistance on a disabled agent logs an error every frame,
        /// and the agent is disabled for the whole time the cat is on a box.</summary>
        bool ArrivedAtDestination() =>
            _navAgent.enabled && _navAgent.isOnNavMesh && _navAgent.HasArrived();

        /// <summary>How close to the validated staging point counts as "standing beside
        /// the box" and therefore safe to step up from.</summary>
        const float MountArrivalTolerance = 0.5f;

        bool NearStagingPoint()
        {
            Vector3 d = transform.position - _stagingNavPos;
            d.y = 0f;
            return d.sqrMagnitude <= MountArrivalTolerance * MountArrivalTolerance;
        }

        bool TrySetDestination(Vector3 worldPoint, float sampleRadius = 2f)
        {
            if (!_navAgent.enabled || !_navAgent.isOnNavMesh) return false;
            if (!NavMesh.SamplePosition(worldPoint, out NavMeshHit hit, sampleRadius, NavMesh.AllAreas)) return false;
            return _navAgent.SetDestination(hit.position);
        }

        // ---- misbehavior (unchanged behavior and timing) ----

        void BeginMisbehavior()
        {
            _wander.Pause();
            SetState(State.WalkingToZone);
            _stateTimer = walkTimeoutSec;

            if (!TrySetDestination(ProblemZonePoint()))
                BeginSquat(); // degenerate scene: squat where it stands rather than hang
        }

        Vector3 ProblemZonePoint()
        {
            Vector3 center = problemZoneCenter != null ? problemZoneCenter.position : transform.position;
            Vector2 offset = UnityEngine.Random.insideUnitCircle * problemZoneRadius;
            return center + new Vector3(offset.x, 0f, offset.y);
        }

        void BeginSquat()
        {
            SetState(State.Squatting);
            _stateTimer = squatDurationSec;
        }

        // ---- correct litterbox use ----

        /// <summary>Walk to a box and use it properly. Returns false when no box has
        /// a reachable approach point, so callers can fall back instead of committing
        /// the cat to a routine it can't finish. A failure leaves the state machine
        /// and the pose untouched; it may have written _targetBox and the staging
        /// point first, which the next successful call overwrites.</summary>
        bool BeginBoxUse()
        {
            if (!TryPickBox(out Transform box, out Vector3 staging)) return false;

            _targetBox = box;
            _stagingNavPos = staging;
            _hasStaging = true;

            _wander.Pause();
            if (!_navAgent.SetDestination(staging))
            {
                _wander.Resume();
                return false;
            }

            SetState(State.WalkingToBox);
            _stateTimer = walkTimeoutSec;
            return true;
        }

        /// <summary>Picks a box whose approach point is a confirmed point on the mesh.
        /// The boxes are carved out of the NavMesh (NavMeshModifier area 1 = Not
        /// Walkable), so the nearest sample beside one is always real floor - which is
        /// exactly the point the cat has to be able to warp back onto later. Validating
        /// it here, before anything is committed, is what makes stranding impossible.</summary>
        bool TryPickBox(out Transform box, out Vector3 staging)
        {
            box = null;
            staging = Vector3.zero;
            if (litterboxes == null || litterboxes.Length == 0) return false;
            if (!_navAgent.enabled || !_navAgent.isOnNavMesh) return false;

            int start = UnityEngine.Random.Range(0, litterboxes.Length);
            for (int i = 0; i < litterboxes.Length; i++)
            {
                Transform candidate = litterboxes[(start + i) % litterboxes.Length];
                if (candidate == null) continue;

                Vector3 top = BoxTopCenter(candidate);
                Vector3 probe = new Vector3(top.x, transform.position.y, top.z)
                                + AwayFrom(top) * boxApproachDistance;

                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, boxApproachSampleRadius, NavMesh.AllAreas))
                {
                    box = candidate;
                    staging = hit.position;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Horizontal unit vector from <paramref name="worldPoint"/> back toward
        /// the cat, so it approaches a box from its own side of the room.</summary>
        Vector3 AwayFrom(Vector3 worldPoint)
        {
            Vector3 away = transform.position - worldPoint;
            away.y = 0f;
            return away.sqrMagnitude > 0.0001f ? away.normalized : Vector3.back;
        }

        /// <summary>Centre of a litterbox's top surface, from the union of its renderer
        /// bounds - the first child is the shell, but the litter sitting in it is the
        /// taller one, and the cat has to stand on top of both.</summary>
        static Vector3 BoxTopCenter(Transform box)
        {
            Renderer[] renderers = box.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Bounds b = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
                return new Vector3(b.center.x, b.max.y, b.center.z);
            }
            Vector3 p = box.position;
            return new Vector3(p.x, p.y + box.lossyScale.y * 0.5f, p.z);
        }

        /// <summary>Hand the transform over for the last step. The box is non-walkable,
        /// so no NavMeshAgent can ever path onto it - the step up has to be driven
        /// directly, with the agent switched off so it doesn't drag the cat back.</summary>
        void BeginBoxMount()
        {
            Vector3 top = BoxTopCenter(_targetBox);
            // Measured, not assumed: while on the mesh the pivot sits this far above
            // the mesh surface, so the same offset puts it on top of the box.
            float pivotLift = Mathf.Clamp(transform.position.y - _stagingNavPos.y, 0f, 1f);

            _groundReturnPos = transform.position;
            _boxStandPos = new Vector3(top.x, top.y + pivotLift + boxSurfaceLift, top.z);

            if (_navAgent.isOnNavMesh) _navAgent.ResetPath();
            _navAgent.enabled = false;
            _offMesh = true;

            StartHop(_groundReturnPos, _boxStandPos, transform.rotation, FacingBetween(_groundReturnPos, _boxStandPos));
            SetState(State.MountingBox);
        }

        void BeginBoxSquat()
        {
            SetState(State.UsingBox);
            _stateTimer = boxUseDurationSec;
        }

        void BeginBoxDismount()
        {
            StartHop(_boxStandPos, _groundReturnPos, transform.rotation, FacingBetween(_boxStandPos, _groundReturnPos));
            SetState(State.DismountingBox);
        }

        static Quaternion FacingBetween(Vector3 from, Vector3 to)
        {
            Vector3 face = to - from;
            face.y = 0f;
            return face.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(face) : Quaternion.identity;
        }

        void StartHop(Vector3 from, Vector3 to, Quaternion fromRot, Quaternion toRot)
        {
            _hopFrom = from;
            _hopTo = to;
            _hopFromRot = fromRot;
            _hopToRot = toRot;
            _hopT = 0f;
        }

        /// <summary>Advances the scripted step; true on the frame it lands. The arc term
        /// is sin-based so it is exactly zero at t=1 and the cat lands on _hopTo, not
        /// near it.</summary>
        bool StepHop()
        {
            _hopT += Time.deltaTime / Mathf.Max(0.05f, boxMountDurationSec);
            float t = Mathf.Clamp01(_hopT);
            Vector3 p = Vector3.Lerp(_hopFrom, _hopTo, t);
            p.y += Mathf.Sin(t * Mathf.PI) * boxHopHeight;
            transform.position = p;
            transform.rotation = Quaternion.Slerp(_hopFromRot, _hopToRot, t);
            return t >= 1f;
        }

        /// <summary>Puts the cat back on the floor and re-attaches the NavMeshAgent.
        /// Position first, then enable, then Warp: enabling an agent that is still
        /// standing on top of a carved-out box is what produces "failed to create
        /// agent because it is not close enough to the NavMesh". Safe to call from any
        /// state - it no-ops when the cat never left the mesh.</summary>
        void ReturnToNavMesh()
        {
            if (_offMesh)
            {
                transform.position = _groundReturnPos;
                _offMesh = false;
            }
            if (!_navAgent.enabled) _navAgent.enabled = true;

            // Keyed on the condition, not on the flag that caused it: whatever left
            // the agent off the mesh, every later call retries the recovery. An
            // agent left off-mesh is fatal and silent - WanderAgent.PickNewDestination
            // bails on !isOnNavMesh, and its HasArrived() never fires again to
            // re-pick, so the cat would freeze for the rest of the session.
            if (!_navAgent.isOnNavMesh)
            {
                bool recovered = _hasStaging && _navAgent.Warp(_stagingNavPos);
                if (!recovered &&
                    NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 4f, NavMesh.AllAreas))
                    recovered = _navAgent.Warp(hit.position); // was discarded, so a failed warp read as success

                // Say so. A stranded cat still runs its timers and still publishes
                // ground truth, so it can sit on top of a box squatting in place and
                // label itself "misbehavior" - a wrong label on correct behaviour is
                // the most damaging thing this file can emit, and until now it
                // happened without a single line in the console.
                if (!recovered)
                    Debug.LogWarning(name + ": could not return to the NavMesh at " + transform.position
                                     + " - it is stranded and its published behavior may not match what it "
                                     + "is doing. Recovery is retried on every later state change.");
            }

            // The staging point belongs to one box routine. ReturnToNavMesh is called
            // exactly when that routine ends or is cancelled, so forget it here -
            // otherwise a later recovery could warp the cat back to a box it walked
            // to minutes ago. _stagingNavPos itself is only read while _hasStaging is
            // set or inside the routine that just wrote it.
            _hasStaging = false;

            _navAgent.updateRotation = true;
        }

        // ---- sniffing ----

        void BeginSniff()
        {
            _wander.Pause();

            bool nearBox = HasAnyBox() && UnityEngine.Random.value < sniffNearBoxChance;
            Vector3 target = nearBox ? BoxSideSniffPoint() : FloorSniffPoint();

            if (TrySetDestination(target))
            {
                SetState(State.WalkingToSniff);
                _stateTimer = walkTimeoutSec;
            }
            else
            {
                BeginSniffHold(); // nothing reachable nearby: just sniff where it stands
            }
        }

        void BeginSniffHold()
        {
            SetState(State.Sniffing);
            _stateTimer = sniffDurationSec;
        }

        bool HasAnyBox()
        {
            if (litterboxes == null) return false;
            foreach (Transform t in litterboxes)
                if (t != null) return true;
            return false;
        }

        /// <summary>A point just outside a random box - beside it, never on it. This is
        /// the false-positive bait: from the camera it looks like the opening of a
        /// squat, and it is completely innocent.</summary>
        Vector3 BoxSideSniffPoint()
        {
            Transform box = null;
            int start = UnityEngine.Random.Range(0, litterboxes.Length);
            for (int i = 0; i < litterboxes.Length && box == null; i++)
                box = litterboxes[(start + i) % litterboxes.Length];
            if (box == null) return FloorSniffPoint();

            Vector3 top = BoxTopCenter(box);
            return new Vector3(top.x, transform.position.y, top.z) + AwayFrom(top) * sniffBoxStandoff;
        }

        Vector3 FloorSniffPoint()
        {
            Vector2 offset = UnityEngine.Random.insideUnitCircle * sniffRoamRadius;
            return transform.position + new Vector3(offset.x, 0f, offset.y);
        }

        // ---- pose ----

        static float PoseScaleY(State s, float squat, float sniff)
        {
            switch (s)
            {
                case State.Squatting:
                case State.UsingBox:
                    return squat;
                case State.Sniffing:
                    return sniff;
                default:
                    return 1f;
            }
        }

        /// <summary>Pose is a pure function of state, reapplied on every transition.
        /// Scale is always recomputed from the scale the cat was built with (never
        /// multiplied or divided by the previous value), and the sniff tilt restores to
        /// an absolute upright yaw captured before the tilt was applied - so no
        /// interleaving of transitions can leave the cat permanently squashed or
        /// permanently nose-down.</summary>
        void ApplyPose(State s)
        {
            float y = PoseScaleY(s, squatScaleY, sniffScaleY);
            transform.localScale = new Vector3(_baseScale.x, _baseScale.y * y, _baseScale.z);

            if (s == State.Sniffing)
            {
                if (!_tilted)
                {
                    _uprightYaw = transform.eulerAngles.y; // read once, never off an already-tilted rotation
                    _tilted = true;
                }
                if (_navAgent.enabled) _navAgent.updateRotation = false;
                transform.rotation = Quaternion.Euler(sniffPitchDeg, _uprightYaw, 0f);
            }
            else
            {
                if (_tilted)
                {
                    transform.rotation = Quaternion.Euler(0f, _uprightYaw, 0f);
                    _tilted = false;
                }
                if (_navAgent.enabled) _navAgent.updateRotation = true;
            }
        }

        // ---- flee ----

        /// <summary>Cancel whatever the cat is doing and sprint to the far corner.
        /// Works from every state, including from on top of a litterbox - the cat hops
        /// off, re-attaches to the mesh and runs.</summary>
        public void FleeFrom(Vector3 threatWorldPos)
        {
            if (_state == State.Fleeing) return;

            // Order matters: get back on the mesh before anything touches the agent,
            // because mid-mount the agent is switched off entirely.
            ReturnToNavMesh();
            // Pause explicitly. FleeFrom can now fire straight out of Wandering, where
            // WanderAgent was never paused - its next idle tick would otherwise pick a
            // fresh destination straight over the flee.
            _wander.Pause();

            SetState(State.Fleeing); // clears any squat/sniff pose first
            _stateTimer = walkTimeoutSec;
            _navAgent.speed = _normalSpeed * fleeSpeedMultiplier;

            Vector3 target = farCorner != null
                ? farCorner.position
                : transform.position + (transform.position - threatWorldPos).normalized * 5f;

            if (_navAgent.enabled && _navAgent.isOnNavMesh)
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
            ReturnToWander(true);
        }

        // ---- ground truth ----

        static string BehaviorFor(State s)
        {
            switch (s)
            {
                case State.Squatting: return "misbehavior";
                case State.Sniffing: return "sniff";
                case State.MountingBox:
                case State.UsingBox:
                case State.DismountingBox: return "box_use";
                case State.Fleeing: return "flee";
                default: return "wander"; // approach walks are exactly that: walking
            }
        }

        static bool InBoxFor(State s) =>
            s == State.MountingBox || s == State.UsingBox || s == State.DismountingBox;

        /// <summary>Publishes what the cat is actually doing, so detector precision and
        /// recall can be scored against it later. Best-effort by design: a missing
        /// bridge or a down broker must never throw or stall the sim.</summary>
        void PublishGroundTruth()
        {
            string behavior = BehaviorFor(_state);
            bool inBox = InBoxFor(_state);
            if (_gtPublished && behavior == _gtBehavior && inBox == _gtInBox) return;

            _gtPublished = true;
            _gtBehavior = behavior;
            _gtInBox = inBox;

            if (bridge == null) return;

            var msg = new CatGroundTruthMsg
            {
                ts = Iso8601Utc.Now(),
                cat = gameObject.name,
                behavior = behavior,
                in_box = inBox
            };
            bridge.Publish(groundTruthTopic, JsonUtility.ToJson(msg), MqttBridge.QosAtMostOnce);
        }

        // ---- inspect ----

        /// <summary>What a gate asserts against to prove a behavior actually happened.
        /// Position is here deliberately: "three cats exist" is a count, and a count
        /// cannot prove a cat walked to a litterbox. Anything a test needs to check has
        /// to be a field, or the assertion is unverifiable by construction.</summary>
        public string InspectJson()
        {
            Vector3 p = transform.position;
            var sb = new System.Text.StringBuilder();
            sb.Append('{');
            sb.Append("\"name\":").Append(SimControl.Quote(gameObject.name)).Append(',');
            sb.Append("\"type\":\"cat\",");
            // The raw state, because the derived fields cannot separate the box
            // states from each other. "squatting" is true for MountingBox and
            // DismountingBox (a deterrent has to be able to startle a cat mid-hop),
            // but the pose during those is full height - so for the ~30 frames of
            // each hop a gate reading "squatting" sees a squat that is not on screen.
            // Anything asserting on posture should read this field.
            sb.Append("\"state\":").Append(SimControl.Quote(_state.ToString())).Append(',');
            sb.Append("\"behavior\":").Append(SimControl.Quote(CurrentBehavior)).Append(',');
            sb.Append("\"in_box\":").Append(IsUsingBox ? "true" : "false").Append(',');
            sb.Append("\"squatting\":").Append(IsSquatting ? "true" : "false").Append(',');
            sb.Append("\"x\":").Append(SimControl.Fmt(p.x)).Append(',');
            sb.Append("\"y\":").Append(SimControl.Fmt(p.y)).Append(',');
            sb.Append("\"z\":").Append(SimControl.Fmt(p.z));
            sb.Append('}');
            return sb.ToString();
        }
    }

    /// <summary>catsentry/groundtruth payload. Sim-only and additive: it is not part of
    /// catsentry-v1 and renames nothing in it, so a detector that doesn't know the topic
    /// is unaffected. Field names are the wire format - don't rename them either.</summary>
    [Serializable]
    public class CatGroundTruthMsg
    {
        public string ts;
        public string cat;
        public string behavior; // "wander" | "sniff" | "box_use" | "misbehavior" | "flee"
        public bool in_box;
    }
}
