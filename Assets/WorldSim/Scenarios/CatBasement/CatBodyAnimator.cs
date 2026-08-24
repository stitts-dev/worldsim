using UnityEngine;

namespace WorldSim.CatBasement
{
    /// <summary>
    /// Procedural secondary motion for a cat: leg gait, body bob and lean, a
    /// follow-through tail, head and ear movement, idle micro-motion, and a
    /// distinct posture per behaviour class. There is no skeleton and no imported
    /// animation - the cat is primitives built by CatVisualBuilder, and this drives
    /// those child transforms directly.
    ///
    /// <b>Ownership, and why pose purity survives.</b> CatAgent owns the cat ROOT
    /// transform: it scales the root to squat, rotates the root to tilt, and moves
    /// the root to hop onto a box. This component owns the <c>Visual</c> subtree and
    /// nothing else. The two never write the same transform, so they cannot fight,
    /// and no animation can accumulate into the base transform CatAgent recomputes
    /// its pose from. Everything here is <c>rest + offset</c> against a rest pose
    /// captured once in Awake, recomputed absolutely every frame from the current
    /// behaviour - never integrated - so an offset is fully released the frame its
    /// state ends, exactly like CatAgent's own pose.
    ///
    /// <b>Determinism.</b>
    /// - No wall clock. <c>Time.deltaTime</c> only, accumulated into <see cref="_t"/>.
    ///   <c>Time.time</c> is deliberately avoided: it survives the scene reload that
    ///   /reset performs, and <c>_t</c> does not, so the sim's animation phase is
    ///   zero on frame zero of every run.
    /// - Frozen at <c>timeScale = 0</c>: deltaTime is 0, so <c>_t</c> and the gait
    ///   phase stand still and every offset re-evaluates to the same value. A
    ///   stepped frame advances exactly one <c>captureDeltaTime</c> worth.
    /// - No Animator, no coroutines, no particles.
    /// - <b>Zero draws from UnityEngine.Random.</b> This is a deliberate deviation
    ///   from "get randomness from the seeded stream", and it is the stricter
    ///   choice, not the looser one. CatAgent and WanderAgent draw their trigger
    ///   timings, zone points and box choices from that one shared stream. An ear
    ///   flick drawing from it a few times a second would shift every one of those
    ///   draws and move where the cats are at frame 120. Instead the "random"
    ///   timings come from <see cref="Hash01"/>, a pure integer hash of a per-cat
    ///   seed derived from the GameObject name: reproducible by construction,
    ///   independent of component execution order, and it perturbs nothing.
    ///
    /// <b>No skate.</b> The gait cycle is driven by distance travelled, not by time,
    /// so the feet keep pace with whatever speed the locomotion layer picks.
    ///
    /// <b>Composing with the locomotion layer.</b> Speed, acceleration and turn rate
    /// are measured from ROOT TRANSFORM DELTAS, never from NavMeshAgent and never
    /// from a specific locomotion component. That is deliberate: it means this works
    /// identically whether the root is being driven by a plain NavMeshAgent or by a
    /// hand-written controller that sets updateRotation = false and writes rotation
    /// itself, and it stays correct if that layer is retuned or replaced. Nothing
    /// here writes the root's position, rotation or scale, so there is no value for
    /// the two layers to fight over - the entire output of this component lands on
    /// children of Visual.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class CatBodyAnimator : MonoBehaviour
    {
        // ---- bindings (assigned by CatVisualBuilder.WireAnimator at build time) ----

        [Header("Rig (wired by the scene builder; do not hand-place)")]
        [Tooltip("The Visual root. Carries body bob, lean and pitch.")]
        public Transform visual;
        public Transform hipFL, hipFR, hipRL, hipRR;
        public Transform headPivot;
        public Transform earL, earR;
        public Transform eyeL, eyeR;
        [Tooltip("Tail spheres, base to tip. Solved as a chain in position space.")]
        public Transform[] tail;

        // ---- gait ----

        [Header("Leg gait (cycle driven by distance travelled, never by time)")]
        [Tooltip("Metres of ground covered per gait cycle at a standstill pace. Foot skate is what this number controls.")]
        public float strideLength = 0.28f;
        [Tooltip("Extra stride length per (m/s) of speed. Animals lengthen their stride rather than just spinning their legs faster, and without this a darting cat's legs blur.")]
        public float strideSpeedGain = 0.55f;
        [Tooltip("Peak fore/aft hip swing, degrees.")]
        public float legSwingDeg = 24f;
        [Tooltip("How far the hip lifts during the swing half of the cycle, metres. Keeps the planted foot planted.")]
        public float legLift = 0.012f;
        [Tooltip("Speed (m/s) at which the gait reaches full amplitude. Below it legs fade to rest so a stopping cat settles instead of freezing mid-step.")]
        public float gaitFullSpeed = 0.35f;
        [Tooltip("Above this speed the four-beat walk blends into a two-beat diagonal trot, which is what a hurrying cat actually does. Sits between the locomotion layer's walk and trot gaits.")]
        public float trotSpeed = 1.45f;
        [Tooltip("Set false to keep the four-beat walk at every speed.")]
        public bool blendToTrot = true;

        [Header("Body")]
        [Tooltip("Vertical bob amplitude at full gait, metres. Two bumps per gait cycle.")]
        public float bodyBob = 0.007f;
        [Tooltip("Degrees of roll per (rad/s) of turn rate. Positive leans INTO the turn. Flip the sign if the cats lean the wrong way.")]
        public float turnLeanDegPerRadSec = 7f;
        [Tooltip("Hard cap on roll, degrees.")]
        public float maxLeanDeg = 12f;
        [Tooltip("Degrees of nose-up pitch per (m/s^2) of forward acceleration. Haunches drop under acceleration, nose dips under braking.")]
        public float accelPitchDegPerMSS = 2.5f;
        [Tooltip("Hard cap on acceleration pitch, degrees.")]
        public float maxAccelPitchDeg = 6f;
        [Tooltip("Leg swing multiplier at full sprint. A bolting cat reaches further than a walking one.")]
        public float fastSwingBoost = 1.3f;

        [Header("Idle micro-motion (a perfectly still cat reads as a paused game)")]
        [Tooltip("Breathing rise and fall, metres.")]
        public float breathAmplitude = 0.0035f;
        [Tooltip("Breaths per second while calm. Roughly doubles while fleeing.")]
        public float breathHz = 0.45f;
        [Tooltip("Slow sideways weight shift while standing, metres.")]
        public float weightShift = 0.004f;

        [Header("Head")]
        [Tooltip("Degrees the head leads a turn, per (rad/s) of turn rate. This is what reads as 'looking where it is going'.")]
        public float headLeadDegPerRadSec = 22f;
        [Tooltip("Hard cap on head yaw from any source, degrees.")]
        public float maxHeadYawDeg = 46f;
        [Tooltip("How far the head scans left and right while idle, degrees.")]
        public float idleScanDeg = 20f;
        [Tooltip("Seconds between idle head-scan targets.")]
        public float idleScanIntervalSec = 3.5f;
        [Tooltip("Head tracking stiffness. Higher snaps, lower drifts.")]
        public float headResponse = 5f;

        [Header("Ears")]
        [Tooltip("How far back the ears flatten when fleeing, degrees. Negative is backward.")]
        public float earsBackDeg = -38f;
        [Tooltip("Forward-perked ear angle while alert (sniffing, investigating), degrees.")]
        public float earsAlertDeg = 12f;
        [Tooltip("Mean seconds between ear flicks. The exact instant inside each interval is hashed per cat, so no two cats flick together.")]
        public float earFlickIntervalSec = 4.5f;
        [Tooltip("Peak ear flick swivel, degrees.")]
        public float earFlickDeg = 26f;
        [Tooltip("Ear flick ring-down time, seconds.")]
        public float earFlickDecaySec = 0.22f;
        [Tooltip("Ear response stiffness.")]
        public float earResponse = 8f;

        [Header("Tail (a follow-through tail reads as alive cheaply)")]
        [Tooltip("Degrees the whole tail swings per (rad/s) of body turn rate, as lag. This is the follow-through.")]
        public float tailLagDegPerRadSec = 26f;
        [Tooltip("Hard cap on lag sweep, degrees per joint.")]
        public float maxTailLagDeg = 18f;
        [Tooltip("Seconds of lag between the body's yaw and the tail's. Larger = whippier.")]
        public float tailLagSmoothing = 0.35f;
        [Tooltip("Travelling-wave sway amplitude per joint, degrees.")]
        public float tailSwayDeg = 6f;
        [Tooltip("Sway cycles per second while idle.")]
        public float tailIdleSwayHz = 0.28f;
        [Tooltip("Phase lag of the wave between consecutive joints, in cycles. This is what makes the tip trail the base.")]
        public float tailWaveLagPerJoint = 0.13f;
        [Tooltip("Degrees per joint of upward carriage at carriage = +1 (tail raised).")]
        public float tailCarriageDeg = 9f;
        [Tooltip("Carriage response stiffness. Slow, because a tail changes carriage over about a second.")]
        public float tailResponse = 3.5f;

        [Header("Per-state posture")]
        [Tooltip("Rear hips splay outward this much during a floor squat, degrees. The wide-stance rear is the classic squat read from above.")]
        public float squatSplayDeg = 15f;
        [Tooltip("Front-paw digging rate while using a litterbox, cycles per second. Digging is the busiest thing any cat does and is what separates box_use from a still floor squat.")]
        public float digHz = 2.6f;
        [Tooltip("Front-hip swing amplitude while digging, degrees.")]
        public float digSwingDeg = 26f;
        [Tooltip("Head pitch while sniffing, degrees nose-down, on top of CatAgent's whole-body tilt.")]
        public float sniffHeadPitchDeg = 26f;
        [Tooltip("How far the head weaves side to side while sniffing, degrees. Investigating is a moving nose, not a parked one.")]
        public float sniffWeaveDeg = 17f;
        [Tooltip("Sniff head-weave rate, cycles per second.")]
        public float sniffWeaveHz = 0.75f;
        [Tooltip("Head pitch down toward the litter while using a box, degrees.")]
        public float boxUseHeadPitchDeg = 18f;
        [Tooltip("Body drops this far while fleeing, metres. A fleeing cat runs low.")]
        public float fleeCrouch = 0.012f;

        [Header("Blink (visible in the editor; ~2 px on the security feed)")]
        [Tooltip("Mean seconds between blinks.")]
        public float blinkIntervalSec = 5.5f;
        [Tooltip("Blink duration, seconds.")]
        public float blinkDurationSec = 0.13f;

        [Header("Safety")]
        [Tooltip("A single-frame root move larger than this is a teleport (box hop, NavMesh warp, scene reset), not travel. It must not spin the gait or spike the lean.")]
        public float teleportDistance = 0.25f;

        // ---- captured rest pose ----

        struct Rest
        {
            public Vector3 Pos;
            public Quaternion Rot;
            public Vector3 Scale;
        }

        Rest _visualRest;
        Rest[] _hipRest;
        Rest _headRest;
        Rest _earLRest, _earRRest;
        Vector3 _eyeLRest, _eyeRRest;

        Transform[] _hips;

        // Tail solved in position space: a base point plus the rest bone vectors
        // between consecutive spheres. Zero joint angles rebuild the rest positions
        // exactly, and segment lengths are preserved by construction, which a naive
        // per-sphere offset would not do.
        Vector3 _tailBase;
        Vector3[] _tailBones;

        // ---- motion state ----

        CatAgent _agent;
        int _seed;

        /// <summary>Accumulated sim time. Never Time.time - see the class comment.</summary>
        float _t;
        /// <summary>
        /// Accumulated gait cycles. Incremented by (distance travelled / current
        /// stride length), NOT stored as raw distance and divided later: stride
        /// lengthens with speed, and dividing an accumulated distance by a stride
        /// that has since changed would retroactively rewrite the whole past phase
        /// and make the legs jump. Incrementing the phase makes stride safe to vary.
        /// </summary>
        float _gaitCycle;

        Vector3 _lastPos;
        float _lastYaw;
        float _speed;
        float _accel;
        float _turnRate;      // rad/s, signed, positive = turning right
        float _tailYawLag;    // smoothed body yaw the tail chases
        float _trotBlend;     // 0 = four-beat walk, 1 = diagonal trot

        float _headYaw, _headPitch;
        float _earPitch, _earLSwivel, _earRSwivel;
        float _tailCarriage;

        bool _bound;

        // Four-beat lateral walk: left hind, left fore, right hind, right fore.
        // That is the footfall order a cat actually uses at walking pace.
        static readonly float[] WalkPhase = { 0.25f, 0.75f, 0.00f, 0.50f }; // FL, FR, RL, RR
        // Diagonal trot: the two diagonal pairs alternate.
        static readonly float[] TrotPhase = { 0.00f, 0.50f, 0.50f, 0.00f }; // FL, FR, RL, RR

        const int IdxFL = 0, IdxFR = 1, IdxRL = 2, IdxRR = 3;

        void Awake()
        {
            _agent = GetComponent<CatAgent>();
            _hips = new[] { hipFL, hipFR, hipRL, hipRR };

            if (!Validate()) return;

            _visualRest = Capture(visual);
            _hipRest = new Rest[4];
            for (int i = 0; i < 4; i++) _hipRest[i] = Capture(_hips[i]);
            _headRest = Capture(headPivot);
            _earLRest = Capture(earL);
            _earRRest = Capture(earR);
            if (eyeL != null) _eyeLRest = eyeL.localScale;
            if (eyeR != null) _eyeRRest = eyeR.localScale;

            _tailBase = tail[0].localPosition;
            _tailBones = new Vector3[tail.Length];
            for (int i = 1; i < tail.Length; i++)
                _tailBones[i] = tail[i].localPosition - tail[i - 1].localPosition;

            _seed = StableSeed(gameObject.name);
            _bound = true;
        }

        /// <summary>
        /// One warning, then switch off. An animator that NREs inside LateUpdate
        /// would log every frame for the life of the run and drown the harness log
        /// that a determinism failure has to be diagnosed from.
        /// </summary>
        bool Validate()
        {
            bool ok = visual != null && headPivot != null && earL != null && earR != null
                      && tail != null && tail.Length >= 2;
            for (int i = 0; i < 4 && ok; i++) ok = _hips[i] != null;
            for (int i = 0; ok && i < tail.Length; i++) ok = tail[i] != null;

            if (!ok)
            {
                Debug.LogWarning(name + ": CatBodyAnimator is not fully wired (rebuild the scene with " +
                                 "CatBasementSceneBuilder). Disabling so it cannot spam LateUpdate.");
                enabled = false;
            }
            return ok;
        }

        void OnEnable()
        {
            // Phase zero on frame zero of every run. /reset reloads the scene, so
            // this runs again and the animation starts from the same place - which
            // Time.time would not have done.
            _t = 0f;
            _gaitCycle = 0f;
            _lastPos = transform.position;
            _lastYaw = transform.eulerAngles.y;
            _tailYawLag = _lastYaw;
            _speed = _accel = _turnRate = _trotBlend = 0f;
            _headYaw = _headPitch = 0f;
            _earPitch = _earLSwivel = _earRSwivel = 0f;
            _tailCarriage = 0f;
        }

        void OnDisable()
        {
            // Leave the rig exactly as the builder made it. Anything else and a
            // disabled animator would strand a cat mid-stride with a leg in the air.
            if (_bound) RestoreRestPose();
        }

        void LateUpdate()
        {
            // LateUpdate, after CatAgent's Update has settled the root pose for this
            // frame, so what is animated is always the current state's pose.
            float dt = Time.deltaTime;

            // Paused. deltaTime is 0, every rate would divide by zero, and there is
            // nothing to advance anyway: last frame's offsets are still written to
            // the transforms and are what the capture sees. This early return is
            // what makes a captured frame hold still.
            if (dt <= 0f) return;

            _t += dt;
            SampleMotion(dt);

            Behaviour b = ReadBehaviour();

            ApplyLegs(b, dt);
            ApplyBody(b);
            ApplyHead(b, dt);
            ApplyEars(b, dt);
            ApplyTail(b, dt);
            ApplyBlink();
        }

        // ---- motion sampling (read-only; steering belongs to the locomotion layer) ----

        void SampleMotion(float dt)
        {
            Vector3 p = transform.position;
            Vector3 d = p - _lastPos;
            d.y = 0f;
            float step = d.magnitude;

            // A box hop, a NavMesh warp or a scene reset moves the root metres in one
            // frame. Charging that to the gait would spin the legs and slam the lean
            // over; it is not travel, so it contributes nothing.
            if (step > teleportDistance)
            {
                step = 0f;
                _speed = 0f;
            }
            _lastPos = p;

            float instantSpeed = step / dt;
            // Phase advances by however many strides that step was worth, at the
            // stride length appropriate to the speed the cat is going right now.
            _gaitCycle += step / CurrentStride();
            float prevSpeed = _speed;
            // Frame-rate independent exponential smoothing, so the look does not
            // change with captureDeltaTime.
            _speed = Mathf.Lerp(_speed, instantSpeed, Damp(12f, dt));
            _accel = Mathf.Lerp(_accel, (_speed - prevSpeed) / dt, Damp(6f, dt));

            float yaw = transform.eulerAngles.y;
            float yawDelta = Mathf.DeltaAngle(_lastYaw, yaw);
            _lastYaw = yaw;
            _turnRate = Mathf.Lerp(_turnRate, yawDelta * Mathf.Deg2Rad / dt, Damp(9f, dt));

            // The tail's yaw chases the body's with a lag; the gap between them is
            // the follow-through.
            _tailYawLag = Mathf.LerpAngle(_tailYawLag, yaw, Damp(1f / Mathf.Max(0.01f, tailLagSmoothing), dt));

            float trotTarget = blendToTrot && _speed > trotSpeed ? 1f : 0f;
            // Slow on purpose: the gait phase offsets shift as this blends, and a
            // fast blend would read as the legs jumping rather than changing gait.
            _trotBlend = Mathf.MoveTowards(_trotBlend, trotTarget, dt * 0.8f);
        }

        /// <summary>Stride length at the current speed. Never zero.</summary>
        float CurrentStride() => Mathf.Max(0.05f, strideLength + strideSpeedGain * _speed);

        /// <summary>
        /// How much gait to show. Fades in with speed so a stopping cat sets its
        /// feet down instead of freezing mid-step, and overdrives past a trot so a
        /// bolting cat reaches further than a strolling one.
        /// </summary>
        float GaitWeight()
        {
            float w = Mathf.Clamp01(_speed / Mathf.Max(0.01f, gaitFullSpeed));
            float fast = Mathf.Clamp01((_speed - trotSpeed) / 1.5f);
            return w * Mathf.Lerp(1f, fastSwingBoost, fast);
        }

        // ---- behaviour ----

        enum Behaviour { Wander, Sniff, Misbehavior, BoxUse, Flee }

        Behaviour ReadBehaviour()
        {
            if (_agent == null) return Behaviour.Wander;
            switch (_agent.CurrentBehavior)
            {
                case "sniff": return Behaviour.Sniff;
                case "misbehavior": return Behaviour.Misbehavior;
                case "box_use": return Behaviour.BoxUse;
                case "flee": return Behaviour.Flee;
                default: return Behaviour.Wander;
            }
        }

        // ---- legs ----

        void ApplyLegs(Behaviour b, float dt)
        {
            // Amplitude fades with speed rather than the phase freezing, so a cat
            // that stops puts its feet down instead of holding one in the air.
            float gait = GaitWeight();
            float cycle = _gaitCycle;

            // Digging: the front paws paddle at the litter while the cat is on a
            // box. This is the single loudest difference between box_use and a
            // floor squat, both of which otherwise sit still at 60% height.
            float dig = b == Behaviour.BoxUse ? 1f : 0f;
            float digAngle = Mathf.Sin(_t * digHz * Mathf.PI * 2f) * digSwingDeg * dig;

            // Floor squat: rear legs splay outward. A wide-stance rear is what a
            // squatting cat looks like from above, and it costs one rotation.
            float splay = b == Behaviour.Misbehavior ? squatSplayDeg : 0f;

            for (int i = 0; i < 4; i++)
            {
                float phase = Mathf.Lerp(WalkPhase[i], TrotPhase[i], _trotBlend);
                float a = (cycle + phase) * Mathf.PI * 2f;

                // Positive rotation about the hip's local X swings the foot
                // BACKWARD, so forward swing is negated.
                float swing = -Mathf.Sin(a) * legSwingDeg * gait;

                // Foot moves forward while cos > 0, so lift only then. Squaring the
                // cosine makes the lift zero with zero slope at the hand-off, which
                // is what stops the paw snapping down at the end of the swing.
                float c = Mathf.Cos(a);
                float lift = c > 0f ? c * c * legLift * gait : 0f;

                bool isFront = i == IdxFL || i == IdxFR;
                bool isRear = !isFront;

                if (isFront && dig > 0f)
                {
                    // The two front paws paddle in antiphase - one down as the other
                    // comes up, which is what digging looks like.
                    swing += i == IdxFL ? digAngle : -digAngle;
                    lift += Mathf.Max(0f, i == IdxFL ? digAngle : -digAngle) * 0.0004f;
                }

                float outward = isRear ? splay * (i == IdxRL ? -1f : 1f) : 0f;

                Transform hip = _hips[i];
                Rest rest = _hipRest[i];
                // Absolute, recomputed from rest every frame. Nothing integrates,
                // so nothing can drift or get stuck.
                hip.localPosition = rest.Pos + new Vector3(0f, lift, 0f);
                hip.localRotation = rest.Rot * Quaternion.Euler(swing, 0f, outward);
            }
        }

        // ---- body ----

        void ApplyBody(Behaviour b)
        {
            float gait = GaitWeight();

            // Two vertical bumps per gait cycle: the body rises as each diagonal
            // pair passes under it.
            float bob = Mathf.Sin(_gaitCycle * Mathf.PI * 4f) * bodyBob * gait;

            // Breathing runs at every speed, and faster when the cat is frightened.
            float breathRate = b == Behaviour.Flee ? breathHz * 2.2f : breathHz;
            float breath = Mathf.Sin(_t * breathRate * Mathf.PI * 2f) * breathAmplitude;

            // Weight shift only shows up when the cat is standing; walking covers it.
            float still = Mathf.Clamp01(1f - gait);
            float sway = Mathf.Sin(_t * 0.21f * Mathf.PI * 2f + Hash01(_seed, 7) * 6.283f) * weightShift * still;

            float crouch = b == Behaviour.Flee ? -fleeCrouch : 0f;

            // Positive roll about local Z raises the RIGHT side, and a cat leans
            // into a turn (right side down when turning right), so the sign is
            // negated. Flip turnLeanDegPerRadSec if this reads backwards.
            float lean = Mathf.Clamp(-_turnRate * turnLeanDegPerRadSec, -maxLeanDeg, maxLeanDeg) * gait;

            // Nose up under acceleration, nose down under braking. Positive X pitch
            // is nose-down, so acceleration is negated.
            float pitch = Mathf.Clamp(-_accel * accelPitchDegPerMSS, -maxAccelPitchDeg, maxAccelPitchDeg);

            visual.localPosition = _visualRest.Pos + new Vector3(sway, bob + breath + crouch, 0f);
            visual.localRotation = _visualRest.Rot * Quaternion.Euler(pitch, 0f, lean);
            // localScale is never written. CatAgent squats by scaling the cat ROOT,
            // and the one thing that must never happen is two components multiplying
            // into the same scale.
        }

        // ---- head ----

        void ApplyHead(Behaviour b, float dt)
        {
            float targetYaw;
            float targetPitch;

            switch (b)
            {
                case Behaviour.Sniff:
                    // Nose down AND weaving. A parked nose reads as a squat starting;
                    // a moving one reads as investigating, which is the whole point
                    // of the sniff class being distinguishable from misbehavior.
                    targetPitch = sniffHeadPitchDeg;
                    targetYaw = Mathf.Sin(_t * sniffWeaveHz * Mathf.PI * 2f) * sniffWeaveDeg;
                    break;

                case Behaviour.BoxUse:
                    // Looking down at the litter it is digging in.
                    targetPitch = boxUseHeadPitchDeg;
                    targetYaw = Mathf.Sin(_t * 0.5f * Mathf.PI * 2f) * 7f;
                    break;

                case Behaviour.Misbehavior:
                    // Level and near-still: a squatting cat holds its head up and
                    // watches the room. Deliberately the opposite of the sniff.
                    targetPitch = -3f;
                    targetYaw = Mathf.Sin(_t * 0.16f * Mathf.PI * 2f) * 6f;
                    break;

                case Behaviour.Flee:
                    // Level, and turned back toward whatever it is running from.
                    targetPitch = -4f;
                    targetYaw = ThreatYaw();
                    break;

                default:
                    // Leading the turn is what reads as "looking where it is going".
                    float lead = -_turnRate * headLeadDegPerRadSec;
                    // Standing still, the head scans instead. The scan target is
                    // hashed per interval, so the cats never scan in unison.
                    float scan = IdleScan() * (1f - Mathf.Clamp01(_speed / Mathf.Max(0.01f, gaitFullSpeed)));
                    targetYaw = lead + scan;
                    targetPitch = 0f;
                    break;
            }

            _headYaw = Mathf.Lerp(_headYaw, Mathf.Clamp(targetYaw, -maxHeadYawDeg, maxHeadYawDeg), Damp(headResponse, dt));
            _headPitch = Mathf.Lerp(_headPitch, targetPitch, Damp(headResponse, dt));

            // Positive X is nose-down (the same convention as CatAgent.sniffPitchDeg).
            headPivot.localRotation = _headRest.Rot * Quaternion.Euler(_headPitch, _headYaw, 0f);
            headPivot.localPosition = _headRest.Pos;
        }

        /// <summary>Head yaw that turns the muzzle back toward the last threat, so a
        /// fleeing cat looks over its shoulder. Zero when nothing has ever fired.</summary>
        float ThreatYaw()
        {
            if (_agent == null || !_agent.HasThreat) return 0f;
            Vector3 toThreat = _agent.LastThreatPosition - transform.position;
            toThreat.y = 0f;
            if (toThreat.sqrMagnitude < 0.0001f) return 0f;
            float rel = Mathf.DeltaAngle(transform.eulerAngles.y,
                Quaternion.LookRotation(toThreat).eulerAngles.y);
            // Only ever a glance, never a full head-around.
            return Mathf.Clamp(rel, -maxHeadYawDeg, maxHeadYawDeg) * 0.5f;
        }

        /// <summary>Piecewise-smooth idle head scan. One hashed target per interval,
        /// smoothstepped between them, so it wanders rather than ticking.</summary>
        float IdleScan()
        {
            float u = _t / Mathf.Max(0.1f, idleScanIntervalSec) + Hash01(_seed, 11) * 4f;
            int k = Mathf.FloorToInt(u);
            float f = u - k;
            float a = Hash01(_seed, k * 3 + 101) * 2f - 1f;
            float c = Hash01(_seed, (k + 1) * 3 + 101) * 2f - 1f;
            return Mathf.Lerp(a, c, f * f * (3f - 2f * f)) * idleScanDeg;
        }

        // ---- ears ----

        void ApplyEars(Behaviour b, float dt)
        {
            float targetPitch;
            switch (b)
            {
                // Ears flat back is the loudest single frightened-cat cue there is,
                // and it survives being 100 px wide.
                case Behaviour.Flee: targetPitch = earsBackDeg; break;
                case Behaviour.Sniff: targetPitch = earsAlertDeg; break;
                case Behaviour.BoxUse: targetPitch = earsAlertDeg * 0.3f; break;
                default: targetPitch = 0f; break;
            }

            _earPitch = Mathf.Lerp(_earPitch, targetPitch, Damp(earResponse, dt));

            // Flicks are suppressed while fleeing: the ears are pinned back and a
            // flick on top would read as a twitch, not fear.
            float flickGate = b == Behaviour.Flee ? 0f : 1f;
            float flick = EarFlick() * flickGate;
            // One ear at a time, which ear alternates on the hash. Both ears
            // flicking together looks mechanical.
            bool leftEar = flick >= 0f;
            _earLSwivel = Mathf.Lerp(_earLSwivel, leftEar ? Mathf.Abs(flick) : 0f, Damp(earResponse * 2f, dt));
            _earRSwivel = Mathf.Lerp(_earRSwivel, leftEar ? 0f : Mathf.Abs(flick), Damp(earResponse * 2f, dt));

            // Positive X tips the ear tip forward, negative flattens it back.
            // Composed onto the rest rotation, which already carries the ear's
            // outward splay - so the splay is preserved, not overwritten.
            earL.localRotation = _earLRest.Rot * Quaternion.Euler(_earPitch, 0f, -_earLSwivel);
            earR.localRotation = _earRRest.Rot * Quaternion.Euler(_earPitch, 0f, _earRSwivel);
        }

        /// <summary>
        /// A decaying ring-down at a hashed instant inside each interval. Sign picks
        /// the ear. Deterministic and completely independent of UnityEngine.Random,
        /// so a flick never shifts a CatAgent trigger draw.
        /// </summary>
        float EarFlick()
        {
            float interval = Mathf.Max(0.2f, earFlickIntervalSec);
            float u = _t / interval;
            int k = Mathf.FloorToInt(u);
            float when = Hash01(_seed, k * 5 + 3);
            float since = (u - k - when) * interval;
            if (since < 0f) return 0f;

            float amp = (0.55f + 0.45f * Hash01(_seed, k * 5 + 4)) * earFlickDeg;
            float sign = Hash01(_seed, k * 5 + 5) < 0.5f ? -1f : 1f;
            float decay = Mathf.Exp(-since / Mathf.Max(0.02f, earFlickDecaySec));
            return sign * amp * decay * Mathf.Sin(since * 18f);
        }

        // ---- tail ----

        void ApplyTail(Behaviour b, float dt)
        {
            float targetCarriage;
            float sway;
            float waveHz;

            switch (b)
            {
                case Behaviour.Misbehavior:
                    // Held out straight behind and dead still. Against box_use's
                    // curled, busy tail this is the difference between a long line
                    // and a short blob in the frame, which is a cue that survives at
                    // 100 px far better than any change in body shape does.
                    targetCarriage = -0.15f;
                    sway = 0.05f;
                    waveHz = 0.08f;
                    break;

                case Behaviour.BoxUse:
                    // Curled tight and up against the flank while it digs.
                    targetCarriage = 0.9f;
                    sway = 1.6f;
                    waveHz = 0.9f;
                    break;

                case Behaviour.Sniff:
                    // Low with a slow interested wave.
                    targetCarriage = -0.55f;
                    sway = 1.0f;
                    waveHz = 0.5f;
                    break;

                case Behaviour.Flee:
                    // Streamed straight out behind, low and rigid.
                    targetCarriage = -1f;
                    sway = 0.25f;
                    waveHz = 1.4f;
                    break;

                default:
                    // Walking cats carry the tail up; standing ones let it drift.
                    targetCarriage = Mathf.Lerp(0.15f, 0.75f, Mathf.Clamp01(_speed / Mathf.Max(0.01f, gaitFullSpeed)));
                    sway = 1f;
                    waveHz = tailIdleSwayHz;
                    break;
            }

            _tailCarriage = Mathf.Lerp(_tailCarriage, targetCarriage, Damp(tailResponse, dt));

            // Follow-through: the gap between where the body is pointing and where
            // the tail still thinks it is pointing. This is the whole trick.
            float lagAngle = Mathf.DeltaAngle(_tailYawLag, transform.eulerAngles.y);
            float lagSweep = Mathf.Clamp(lagAngle * tailLagDegPerRadSec * 0.02f, -maxTailLagDeg, maxTailLagDeg);

            // While walking the wave rides the gait so the tail and the feet agree;
            // standing still it falls back to its own slow idle rate.
            float gait = Mathf.Clamp01(_speed / Mathf.Max(0.01f, gaitFullSpeed));
            float wavePhase = Mathf.Lerp(_t * waveHz, _gaitCycle * 0.5f, gait);

            // Solve the chain in position space. Zero angles reproduce the rest
            // positions exactly, and every bone keeps its rest length - which a
            // per-sphere positional offset would not.
            Quaternion q = Quaternion.identity;
            Vector3 p = _tailBase;
            tail[0].localPosition = p;

            int joints = tail.Length - 1;
            for (int i = 1; i < tail.Length; i++)
            {
                // Gain ramps toward the tip, so the base stays put and the tip whips.
                float s = i / (float)joints;
                float gain = s * s;

                float wave = Mathf.Sin((wavePhase - i * tailWaveLagPerJoint) * Mathf.PI * 2f)
                             * tailSwayDeg * sway * gain;
                float yaw = wave + lagSweep * gain;
                // Positive X pitch on a bone pointing backwards raises the tail.
                float pitch = tailCarriageDeg * _tailCarriage;

                q *= Quaternion.Euler(pitch, yaw, 0f);
                p += q * _tailBones[i];
                tail[i].localPosition = p;
            }
        }

        // ---- blink ----

        void ApplyBlink()
        {
            if (eyeL == null || eyeR == null) return;

            float interval = Mathf.Max(0.5f, blinkIntervalSec);
            float u = _t / interval + Hash01(_seed, 23) * 3f;
            int k = Mathf.FloorToInt(u);
            float when = Hash01(_seed, k * 7 + 13);
            float since = (u - k - when) * interval;

            float shut = 0f;
            float dur = Mathf.Max(0.02f, blinkDurationSec);
            if (since >= 0f && since < dur) shut = Mathf.Sin(since / dur * Mathf.PI);

            // Squash the eye spheres vertically. Only Y, and always recomputed from
            // the rest scale, so a blink can never accumulate.
            float f = 1f - 0.9f * shut;
            eyeL.localScale = new Vector3(_eyeLRest.x, _eyeLRest.y * f, _eyeLRest.z);
            eyeR.localScale = new Vector3(_eyeRRest.x, _eyeRRest.y * f, _eyeRRest.z);
        }

        // ---- rest pose ----

        static Rest Capture(Transform t) => new Rest
        {
            Pos = t.localPosition,
            Rot = t.localRotation,
            Scale = t.localScale
        };

        void RestoreRestPose()
        {
            Write(visual, _visualRest);
            for (int i = 0; i < 4; i++) Write(_hips[i], _hipRest[i]);
            Write(headPivot, _headRest);
            Write(earL, _earLRest);
            Write(earR, _earRRest);
            if (eyeL != null) eyeL.localScale = _eyeLRest;
            if (eyeR != null) eyeR.localScale = _eyeRRest;

            Vector3 p = _tailBase;
            tail[0].localPosition = p;
            for (int i = 1; i < tail.Length; i++)
            {
                p += _tailBones[i];
                tail[i].localPosition = p;
            }
        }

        static void Write(Transform t, Rest r)
        {
            if (t == null) return;
            t.localPosition = r.Pos;
            t.localRotation = r.Rot;
            t.localScale = r.Scale;
        }

        // ---- deterministic helpers ----

        /// <summary>
        /// Frame-rate independent exponential smoothing factor. Lerping by a raw
        /// per-frame constant makes the look depend on captureDeltaTime, and the
        /// harness changes captureDeltaTime.
        /// </summary>
        static float Damp(float rate, float dt) => 1f - Mathf.Exp(-rate * dt);

        /// <summary>
        /// Deliberately not string.GetHashCode: that is randomised per process on
        /// some runtimes, which would make two runs of the same seed differ. FNV-1a
        /// over the name is stable everywhere, forever.
        /// </summary>
        static int StableSeed(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++)
                {
                    h ^= s[i];
                    h *= 16777619u;
                }
                return (int)(h & 0x7fffffff);
            }
        }

        /// <summary>
        /// Deterministic 0..1 from two integers. Not randomness: a pure function, so
        /// it needs no seeded stream, cannot be affected by component execution
        /// order, and does not shift anyone else's UnityEngine.Random draws.
        /// </summary>
        static float Hash01(int seed, int n)
        {
            unchecked
            {
                uint h = (uint)(seed * 374761393 + n * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xffffff) / 16777215f;
            }
        }
    }
}
