using System;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// One Touch controller: grab/release nearby Grabbables with the grip, forward trigger/buttons to the held weapon,
    /// and provide haptics. Lives under the OVRCameraRig hand anchor.
    /// </summary>
    public class HandController : MonoBehaviour
    {
        [SerializeField] OVRInput.Controller controller = OVRInput.Controller.RTouch;
        [SerializeField] Transform anchor;                // where a held object attaches (defaults to this transform)
        [SerializeField] LayerMask grabMask = ~0;
        [SerializeField] float gripThreshold = 0.55f;
        [SerializeField] float releaseThreshold = 0.35f;
        [Tooltip("OpenXR grip pose points down the handle (~60° below the aim direction). Held weapons snap to an anchor pitched up by this much.")]
        [SerializeField] float aimPitchOffset = -20f;
        [Tooltip("Grab assist: the nearest grabbable within this distance of the hand snaps into it.")]
        [SerializeField] float grabAssistRadius = 0.3f;
        [Tooltip("Distance grab: point the hand at a grabbable within this range and squeeze grip to pull it in.")]
        [SerializeField] float distanceGrabRange = 3.5f;
        [SerializeField] float distanceGrabRadius = 0.2f;
        [Tooltip("Grab buffer: a squeeze that starts before the hand is in reach (closing the hand on the way to a holster) still " +
                 "grabs the first weapon that comes within reach this long after it. After that the hand stays a fist until it opens.")]
        [SerializeField] float grabBuffer = 0.35f;
        [Tooltip("Door handle: the squeeze takes it only from a hand slower than this (m/s). Faster is a draw sweeping past the " +
                 "lever toward the hip holster (same height), not a reach for the door.")]
        [SerializeField] float doorGrabMaxSpeed = 0.7f;
        [Header("Fists")]
        [SerializeField] float punchMinSpeed = 2.6f;
        [SerializeField] float punchDamage = 12f;
        [SerializeField] float punchRadius = 0.09f;
        [SerializeField] float punchCooldown = 0.35f;

        public float AimPitchOffset => aimPitchOffset;

        /// <summary>
        /// The web of the hand (between thumb and index) in the controller anchor's frame, ±1 cm (research/16 §3.1: Meta's anchor
        /// sits inside the Touch Plus head, level with the trigger; the web wraps the handle 4 cm behind it). Held weapons put
        /// their GripPoint — the web point on the weapon (tang) — here, so the gun sits high in the hand, not at the controller head.
        /// </summary>
        public static readonly Vector3 WebPoint = new(0f, -0.003f, -0.040f);

        /// <summary>AimAnchor = the web point, pitched by the tuned offset, then the player's trims (GripTrim; inward yaw/roll
        /// mirrored per hand). The trims pivot about the web of the hand, not the controller head.</summary>
        public static void ConfigureAimAnchor(Transform aim, float pitch, bool left, Vector3 trim)
        {
            float s = left ? 1f : -1f; // inward = toward the midline: the right hand yaws/rolls left (−), the left hand right (+)
            aim.localPosition = WebPoint;
            aim.localRotation = Quaternion.Euler(pitch + trim.x, 0f, 0f) * Quaternion.Euler(0f, s * trim.y, s * trim.z);
        }

        public OVRInput.Controller Controller => controller;
        public bool IsLeft => controller == OVRInput.Controller.LTouch;
        public Transform Anchor => anchor != null ? anchor : _aimAnchor != null ? _aimAnchor : transform;
        Transform _aimAnchor;
        public Grabbable Held { get; private set; }
        /// <summary>Holding nothing, but steadying the other hand's weapon (the shotgun's fore-end).</summary>
        public WeaponGrip Supporting { get; private set; }
        /// <summary>
        /// On a door's handle (research/25: open the door yourself). Like the support grip, a hold that is never
        /// <see cref="Held"/>, so aim, the police stance and "armed" never see it.
        /// </summary>
        public bool OnDoor => _door != null;
        HIS.Room.VirtualDoor _door, _doorHover;
        bool _doorWasOpen;
        public float Trigger { get; private set; }
        public bool TriggerDown { get; private set; }
        public bool TriggerUp { get; private set; }
        public bool PrimaryButtonDown { get; private set; }   // A / X
        public bool PrimaryButton { get; private set; }       // A / X held (yell commands: hold + flick the stick)
        public bool PrimaryButtonUp { get; private set; }
        public bool SecondaryButtonDown { get; private set; } // B / Y
        public bool SecondaryButton { get; private set; }     // B / Y held
        public bool ThumbstickClickDown { get; private set; }
        public Vector2 Thumbstick { get; private set; }
        public Vector3 Velocity { get; private set; }
        public Vector3 AngularVelocity { get; private set; }

        public event Action<Grabbable> GrabbedObject;
        public event Action<Grabbable> ReleasedObject;

        readonly Collider[] _overlap = new Collider[32];
        GripHand _gripHand;
        HandController _other;
        Renderer[] _controllerRenderers;
        bool _controllerHidden;

        void Awake()
        {
            if (anchor == null)
            {
                var go = new GameObject("AimAnchor");
                go.transform.SetParent(transform, false);
                _aimAnchor = go.transform;
                ApplyTrim();
            }
            _gripHand = GetComponent<GripHand>();
        }

        void OnEnable() { GripTrim.Changed += ApplyTrim; HIS.Core.GamePause.PausedChanged += ShowWebMarker; }
        void ApplyTrim() { if (_aimAnchor != null) ConfigureAimAnchor(_aimAnchor, aimPitchOffset, IsLeft, GripTrim.Current); }

        void Start()
        {
            foreach (var h in FindObjectsByType<HandController>(FindObjectsInactive.Exclude)) if (h != this) _other = h;
            // The Touch Plus model Meta draws on this anchor (OVRControllerPrefab, a sibling under the same anchor). Its helper
            // toggles the model GameObjects every frame, so hide it with the renderers instead.
            var helper = transform.parent != null ? transform.parent.GetComponentInChildren<OVRControllerHelper>(true) : null;
            _controllerRenderers = helper != null ? helper.GetComponentsInChildren<Renderer>(true) : new Renderer[0];
        }

        /// <summary>Holding a weapon (or steadying one): the virtual hand is on it, so the controller model goes.</summary>
        void SyncControllerModel()
        {
            bool hide = Held != null || Supporting != null;
            if (hide == _controllerHidden || _controllerRenderers == null) return;
            _controllerHidden = hide;
            foreach (var r in _controllerRenderers) if (r != null) r.enabled = !hide;
        }
        bool _gripHeld, _pulling;
        float _grabBufferLeft; // > 0: squeezed with nothing in hand, still taking the first weapon that comes within reach
        Vector3 _lastPos; Quaternion _lastRot;

        void Update()
        {
            // Before the early-outs below: the gun it steadies left the other hand (dropped, holstered, swapped) → the support
            // hand lets go even while paused or untracked, and the controller model follows Held/Supporting on every frame.
            if (Supporting != null && (_other == null || _other.Held == null || _other.Held.gameObject != Supporting.gameObject)) EndSupport();
            // The door was taken out of the hand (a kick, he shoved it wide, the round reset) or its rig rebuilt (a re-pick).
            if ((object)_door != null && (_door == null || !_door.IsHeldBy(this))) EndDoor(taken: true);
            SyncControllerModel();

            // Paused or a panel owns the controllers: no grab/fire/punch. Keep the velocity baseline fresh so the first
            // frame after resuming doesn't read a huge jump (dt is 0 while paused) as a punch or a throw.
            if (!HIS.Core.InputGate.GameplayActive || Time.deltaTime <= 0f) // also the resume frame: dt is still 0
            {
                _lastPos = Anchor.position; _lastRot = Anchor.rotation;
                Velocity = AngularVelocity = Vector3.zero;
                if (_reticle != null) _reticle.Hide();
                return;
            }

            ReadInput();

            // No grabbing until this controller is actually tracked (on resume the anchors sit at the origin for a few frames).
            // _gripHeld is left as it was, so a grip released during the dropout still releases the weapon once tracking returns.
            // Untracked frames only refresh the velocity baseline, so the snap back to the real pose isn't read as a throw.
            bool tracked = OVRInput.IsControllerConnected(controller) && OVRInput.GetControllerPositionTracked(controller);
            if (!tracked)
            {
                _lastPos = Anchor.position; _lastRot = Anchor.rotation;
                Velocity = AngularVelocity = Vector3.zero;
                return;
            }
            TrackVelocity();

            GripInput(OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller), Time.deltaTime);
            if (Supporting != null && !SupportStillValid()) EndSupport();

            if (Held != null)
            {
                var weapon = Held.GetComponent<WeaponBase>();
                if (weapon != null) weapon.HandUpdate(this);
                if (_reticle != null) _reticle.Hide();
            }
            else if (Supporting != null || OnDoor) { if (_reticle != null) _reticle.Hide(); } // (on the door: no punch when yanking it)
            else
            {
                // A closed fist past the grab buffer won't grab: no ring or "it's here" tick that a squeeze can't honour.
                if (_gripHeld && _grabBufferLeft <= 0f) { _hovered = null; if (_reticle != null) _reticle.Hide(); }
                else HoverTick();
                FistStrike();
            }
            SlotHint();
            SyncControllerModel();
        }

        /// <summary>
        /// Grip edge → support grip or grab; opening the hand lets go. The grab used to fire only on the rising edge: a squeeze
        /// that began a few cm before the hand reached the holster found nothing and silently did nothing until the hand opened
        /// and squeezed again ("sometimes you can't grab stuff"). Now a squeeze keeps looking (proximity only) for grabBuffer s.
        /// </summary>
        void GripInput(float grip, float dt)
        {
            if (!_gripHeld && grip >= gripThreshold)
            {
                _gripHeld = true;
                _grabBufferLeft = TrySupport() || TryGrab(true) ? 0f : grabBuffer;
            }
            else if (_gripHeld && grip <= releaseThreshold) { _gripHeld = false; _grabBufferLeft = 0f; EndSupport(); EndDoor(taken: false); ForceRelease(); }
            else if (_door != null) DriveDoor(dt);
            else if (_grabBufferLeft > 0f)
            {
                if (Held != null || Supporting != null || _pulling) _grabBufferLeft = 0f;
                else if (TrySupport() || TryGrab(false)) _grabBufferLeft = 0f;
                else if ((_grabBufferLeft -= dt) <= 0f) LogGrabMiss();
            }
        }

        // Support hand (research/16 §3.4 step 6): the free hand squeezing its grip at the other hand's shotgun fore-end takes
        // its own pose there. Visual for now: aim stays with the holding hand (a support hand dragging aim is a known pitfall).
        bool TrySupport()
        {
            if (Held != null || _other == null || _other.Held == null) return false;
            if (!_other.Held.TryGetComponent<WeaponGrip>(out var wg) || wg.SupportFrame == null || wg.SupportPose == null) return false;
            if (Vector3.Distance(wg.SupportPoint(IsLeft), transform.TransformPoint(WebPoint)) > wg.SupportRadius) return false;
            Supporting = wg;
            if (_gripHand != null) _gripHand.Attach(wg, true);
            Haptic(0.3f, 0.05f);
            return true;
        }

        bool SupportStillValid() =>
            _gripHeld && _other != null && _other.Held != null && _other.Held.gameObject == Supporting.gameObject &&
            Vector3.Distance(Supporting.SupportPoint(IsLeft), transform.TransformPoint(WebPoint)) <= Supporting.SupportRadius * 1.6f;

        void EndSupport()
        {
            if (Supporting == null) return;
            Supporting = null;
            if (_gripHand != null) _gripHand.Detach();
        }

        // ───────────── the door handle (research/25; Brett 2026-10-01: "a way to open the door yourself") ─────────────
        // An empty hand squeezing grip at a door's room-side lever takes it; the leaf follows the hand round the hinge
        // (VirtualDoor.DriveHandle). Weapons win: a hand holding one never gets here (TryGrab refuses), a weapon in reach
        // (a holster) is taken first, and only the squeeze itself, from a hand that has slowed at the lever, takes the door —
        // never the grab buffer or a hand sweeping past it on the way to the hip (review 2026-10-01: an early squeeze for the
        // holster took the handle and the draw pulled the door open). The door beats the distance pull (reaching for the
        // handle while pointing at a racked gun).

        /// <summary>The nearest door handle a hand can take (<see cref="HIS.Room.VirtualDoor.HandleFree"/>) within its reach of the hand, not one the other hand has.</summary>
        HIS.Room.VirtualDoor DoorInReach()
        {
            HIS.Room.VirtualDoor best = null; float bestDist = float.MaxValue;
            var p = Anchor.position;
            foreach (var d in HIS.Room.VirtualDoor.All)
            {
                if (d == null || !d.HandleFree || (d.HeldByPlayer && !d.IsHeldBy(this))) continue;
                float dist = d.GrabDistance(p);
                if (dist <= d.GrabReach && dist < bestDist) { best = d; bestDist = dist; }
            }
            return best;
        }

        bool TryDoor()
        {
            var d = DoorInReach();
            if (d == null) return false;
            if (Velocity.magnitude > doorGrabMaxSpeed)
            {
                if (Debug.isDebugBuild) Debug.Log($"[HIS] {(IsLeft ? "L" : "R")} squeeze at the {d.Label} handle passed over (hand moving {Velocity.magnitude:F1} m/s: a draw, not a reach)");
                return false;
            }
            if (!d.GrabHandle(this, Anchor.position)) return false;
            _door = d;
            _doorWasOpen = d.IsOpen;
            _hovered = null; _doorHover = null;
            Haptic(0.3f, 0.04f);
            Debug.Log($"[HIS] {(IsLeft ? "L" : "R")} hand on the {d.Label} handle ({(d.IsOpen ? $"open {d.OpenAngle:F0}°" : "shut")})");
            return true;
        }

        /// <summary>The leaf follows the hand; a pulse as it unlatches, a firmer one as it clicks shut.</summary>
        void DriveDoor(float dt)
        {
            _door.DriveHandle(this, Anchor.position, dt);
            if (_door.IsOpen == _doorWasOpen) return;
            _doorWasOpen = _door.IsOpen;
            if (_doorWasOpen) Haptic(0.5f, 0.06f); else Haptic(0.7f, 0.08f);
        }

        /// <summary>Off the handle: let go (it carries on or stays: VirtualDoor.ReleaseHandle), or <paramref name="taken"/> out of the hand (a bump).</summary>
        void EndDoor(bool taken)
        {
            if ((object)_door == null) return;
            var d = _door; _door = null;
            if (d != null) d.ReleaseHandle(this);
            if (taken) Haptic(0.8f, 0.1f);
        }

        float _nextPunch;
        readonly Collider[] _punchHits = new Collider[8];
        void FistStrike()
        {
            if (Time.time < _nextPunch || Velocity.magnitude < punchMinSpeed) return;
            int n = Physics.OverlapSphereNonAlloc(Anchor.position, punchRadius, _punchHits, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var target = _punchHits[i].GetComponentInParent<IDamageable>();
                if (target == null || target is IInanimate) continue;   // flesh only (not a car or the ground outside)
                var info = new DamageInfo
                {
                    Amount = punchDamage * Mathf.Clamp(Velocity.magnitude / 4f, 0.7f, 1.6f), Type = DamageType.Blunt,
                    Point = _punchHits[i].ClosestPoint(Anchor.position), Normal = -Velocity.normalized, Direction = Velocity.normalized,
                    Penetration = 0f, Source = gameObject,
                };
                target.ApplyDamage(info);
                HIS.Core.Sfx.Play("flesh.punch", info.Point, 0.9f);
                _nextPunch = Time.time + punchCooldown;
                Haptic(0.8f, 0.1f);
                break;
            }
        }

        // Light tick when a grabbable comes into reach; a ring on whatever the hand is pointing at further away.
        Grabbable _hovered;
        Holster _slotHint;
        Grabbable _hintFor;

        /// <summary>Holding a weapon over its slot: one tick on entry — "let go here and it holsters".</summary>
        void SlotHint()
        {
            Holster h = null;
            var g = Held != null && Held.GetComponent<WeaponBase>() != null ? Held : null;
            if (g != null)
            {
                var p = g.GripPoint.position;
                // 5 cm exit hysteresis (research/15): a gun held low by the side doesn't buzz on the capture edge.
                bool stay = g == _hintFor && _slotHint != null && _slotHint.Accepts(g) &&
                            Vector3.Distance(p, _slotHint.transform.position) <= _slotHint.DockRadius + 0.05f;
                h = stay ? _slotHint : Holster.FindNearest(p, g);
            }
            // A fresh grab (a draw starts inside the slot it just left) only seeds the state: no tick over the grab pulse.
            if (g != _hintFor) { _hintFor = g; _slotHint = h; return; }
            if (h != _slotHint) { if (h != null) Haptic(0.3f, 0.04f); _slotHint = h; }
        }
        GrabReticle _reticle;
        void HoverTick()
        {
            var g = FindGrabbable();
            if (g != _hovered)
            {
                _hovered = g;
                if (g != null) Haptic(g.CurrentHolster != null ? 0.3f : 0.15f, 0.03f); // holstered: a firmer "it's here"
            }
            // A door handle in reach (nothing to grab nearer): its lever lights up and a firm tick, once on the way in, so the
            // squeeze comes before the controller meets the real door. Not to a hand already squeezing (the grab buffer, a
            // draw on its way to the hip): it can't take the handle, so no promise that it can.
            var door = g == null && !_gripHeld ? DoorInReach() : null;
            if (door != null) door.HoverHandle();
            if (door != _doorHover) { _doorHover = door; if (door != null) Haptic(0.3f, 0.03f); }
            _reticle ??= GrabReticle.Create((IsLeft ? "Left" : "Right") + "GrabReticle");
            Grabbable far = g;
            if (far == null && door == null && Physics.SphereCast(Anchor.position, distanceGrabRadius, Anchor.forward, out var hit, distanceGrabRange, grabMask, QueryTriggerInteraction.Collide)) // (the handle beats a distance pull)
            {
                var cand = hit.collider.GetComponentInParent<Grabbable>();
                if (cand != null && !cand.IsHeld) far = cand;
            }
            if (far != null)
            {
                float radius = Mathf.Clamp(Vector3.Distance(Anchor.position, far.GripPoint.position) * 0.05f, 0.06f, 0.16f);
                _reticle.Show(far.GripPoint, radius);
            }
            else _reticle.Hide();
        }

        void OnDisable() { _pulling = false; EndDoor(taken: false); GripTrim.Changed -= ApplyTrim; HIS.Core.GamePause.PausedChanged -= ShowWebMarker; if (_reticle != null) _reticle.Hide(); StopHaptic(); }

        // Dev builds, while paused: a 1 cm red ball on the controller's web point (research/16 §3.4 step 3). It should sit in
        // the real web of the hand (between thumb and index) seen through passthrough; if not, WebPoint needs moving.
        GameObject _webMarker;
        void ShowWebMarker(bool paused)
        {
            if (!Debug.isDebugBuild) return;
            if (_webMarker == null && paused)
            {
                _webMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _webMarker.name = "WebPointMarker"; Destroy(_webMarker.GetComponent<Collider>());
                _webMarker.transform.SetParent(transform, false);
                _webMarker.transform.localPosition = WebPoint; _webMarker.transform.localScale = Vector3.one * 0.01f;
                var sh = Shader.Find("Universal Render Pipeline/Unlit");
                if (sh != null) { var m = new Material(sh); m.color = Color.red; if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.red); _webMarker.GetComponent<Renderer>().sharedMaterial = m; }
            }
            if (_webMarker != null) _webMarker.SetActive(paused);
        }

        Grabbable FindGrabbable()
        {
            int n = Physics.OverlapSphereNonAlloc(Anchor.position, grabAssistRadius, _overlap, grabMask, QueryTriggerInteraction.Collide);
            Grabbable best = null; float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var g = _overlap[i].GetComponentInParent<Grabbable>();
                if (g == null || g.IsHeld) continue;
                float d = g.GrabDistance(Anchor.position); // grip point, or anywhere along its grab zone (e.g. a shotgun stock)
                if (d <= grabAssistRadius && d < bestDist) { best = g; bestDist = d; }
            }
            return best;
        }

        void ReadInput()
        {
            Trigger = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller);
            TriggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, controller);
            TriggerUp = OVRInput.GetUp(OVRInput.Button.PrimaryIndexTrigger, controller);
            PrimaryButtonDown = OVRInput.GetDown(OVRInput.Button.One, controller);
            PrimaryButton = OVRInput.Get(OVRInput.Button.One, controller);
            PrimaryButtonUp = OVRInput.GetUp(OVRInput.Button.One, controller);
            SecondaryButtonDown = OVRInput.GetDown(OVRInput.Button.Two, controller);
            SecondaryButton = OVRInput.Get(OVRInput.Button.Two, controller);
            ThumbstickClickDown = OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, controller);
            Thumbstick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, controller);
        }

        void TrackVelocity()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            Velocity = (Anchor.position - _lastPos) / dt;
            var delta = Anchor.rotation * Quaternion.Inverse(_lastRot);
            delta.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f) angle -= 360f;
            AngularVelocity = axis * (angle * Mathf.Deg2Rad / dt);
            _lastPos = Anchor.position; _lastRot = Anchor.rotation;
        }

        /// <summary>
        /// Grab the nearest grabbable in reach, else — the squeeze itself (<paramref name="allowDistance"/>), not the grab
        /// buffer, which only ever takes weapons — a door handle in reach, else start pulling one in along the aim.
        /// </summary>
        bool TryGrab(bool allowDistance = true)
        {
            if (Held != null || _pulling || _door != null) return false;
            var best = FindGrabbable();
            if (best == null)
            {
                if (!allowDistance) return false;
                if (TryDoor()) return true;
                // Distance grab: nothing in reach, so look down the hand's aim direction for one to pull in.
                if (Physics.SphereCast(Anchor.position, distanceGrabRadius, Anchor.forward, out var hit, distanceGrabRange, grabMask, QueryTriggerInteraction.Collide))
                {
                    var far = hit.collider.GetComponentInParent<Grabbable>();
                    if (far != null && !far.IsHeld) { StartCoroutine(PullIn(far)); return true; }
                }
                return false;
            }
            if (Debug.isDebugBuild)
                Debug.Log($"[HIS] {(IsLeft ? "L" : "R")} grabbed {best.name}{(best.CurrentHolster != null ? " from " + best.CurrentHolster.name : "")} " +
                          $"at {best.GrabDistance(Anchor.position):F2} m{(allowDistance ? "" : " (grab buffer)")}");
            Held = best;
            best.OnGrab(this);
            AttachHand(best);
            Haptic(0.6f, 0.08f);
            GrabbedObject?.Invoke(best);
            return true;
        }

        /// <summary>Dev builds: a squeeze that found nothing — was the weapon just out of reach, or not where the hand went?</summary>
        void LogGrabMiss()
        {
            if (!Debug.isDebugBuild) return;
            int n = Physics.OverlapSphereNonAlloc(Anchor.position, 0.8f, _overlap, grabMask, QueryTriggerInteraction.Collide);
            Grabbable best = null; float bestDist = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var g = _overlap[i].GetComponentInParent<Grabbable>();
                if (g == null || g.IsHeld) continue;
                float d = g.GrabDistance(Anchor.position);
                if (d < bestDist) { best = g; bestDist = d; }
            }
            Debug.Log($"[HIS] {(IsLeft ? "L" : "R")} grab missed at {Anchor.position:F2}: " +
                      (best != null ? $"nearest {best.name} {bestDist:F2} m{(best.CurrentHolster != null ? " in " + best.CurrentHolster.name : "")}" : "nothing within 0.8 m"));
        }

        void AttachHand(Grabbable g)
        {
            if (_gripHand == null) return;
            if (g.TryGetComponent<WeaponGrip>(out var wg)) _gripHand.Attach(wg, false); else _gripHand.Detach();
        }

        System.Collections.IEnumerator PullIn(Grabbable g)
        {
            _pulling = true; // cleared on every exit below, and in OnDisable (a deactivated hand drops its coroutines)
            g.BeginPull();
            var start = g.transform.position; var startRot = g.transform.rotation;
            float t = 0, dur = 0.22f;
            Haptic(0.3f, 0.08f);
            while (t < dur && g != null && !g.IsHeld)
            {
                t += Time.deltaTime; float k = Mathf.SmoothStep(0, 1, t / dur);
                g.transform.position = Vector3.Lerp(start, Anchor.position, k);
                g.transform.rotation = Quaternion.Slerp(startRot, Anchor.rotation, k);
                yield return null;
            }
            _pulling = false;
            if (g == null || g.IsHeld) yield break;
            if (Held != null || !_gripHeld) { g.EndPull(); Debug.Log($"[HIS] pull of {g.name} cancelled"); yield break; } // let go mid-flight
            Debug.Log($"[HIS] {(IsLeft ? "L" : "R")} distance-grabbed {g.name}");
            Held = g;
            g.OnGrab(this);
            AttachHand(g);
            Haptic(0.6f, 0.08f);
            GrabbedObject?.Invoke(g);
        }

        public void ForceRelease()
        {
            if (Held == null) return;
            var g = Held; Held = null;
            if (_gripHand != null) _gripHand.Detach();
            Debug.Log($"[HIS] {(IsLeft ? "L" : "R")} released {g.name} v={Velocity.magnitude:F1}");
            g.OnRelease(this, Velocity, AngularVelocity);
            ReleasedObject?.Invoke(g);
        }

        /// <summary>Short vibration pulse (amplitude 0..1, seconds).</summary>
        public void Haptic(float amplitude, float duration, float frequency = 0.8f)
        {
            OVRInput.SetControllerVibration(frequency, Mathf.Clamp01(amplitude), controller);
            CancelInvoke(nameof(StopHaptic));
            Invoke(nameof(StopHaptic), duration);
        }

        public void HapticLight() => Haptic(0.35f, 0.05f);
        public void HapticHeavy() => Haptic(1f, 0.18f, 0.5f);

        void StopHaptic() => OVRInput.SetControllerVibration(0, 0, controller);
    }
}
