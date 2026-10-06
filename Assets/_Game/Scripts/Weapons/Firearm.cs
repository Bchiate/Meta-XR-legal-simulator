using HIS.Core;
using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// Firearm with real internal state: magazine/tube, chambered round, slide lock.
    /// Semi-auto: trigger fires if a round is chambered; empty → slide locks → B reloads and chambers.
    /// Pump (shotgun): after each shot the pump must cycle — tap B (or click the stick) to pump; hold B to feed shells.
    /// DA/SA (opt-in, the M9; research/16 §1.5): the first shot from a decocked hammer needs a long pull (the hammer rolls back
    /// with it); the slide recocks the hammer, so later shots break early (single action). Stick click with a round chambered
    /// and the hammer back = decocker (back to double action). Guns without it (Glock, shotgun) fire on the trigger button.
    /// Physical magazines / a two-handed pump are later passes; the state machine is already the real one.
    /// Rifle action (opt-in, the AR-15; Brett 2026-10-03): the bolt carrier snaps back and home on its own clock each shot and
    /// locks back on the last round, a dust cover springs open with the first shot, the charging handle racks an empty chamber,
    /// and the reload's magazine drops out of a vertical well and seats with a click (<see cref="ActionMotion.Rifle"/>).
    /// </summary>
    public class Firearm : WeaponBase
    {
        [Header("Ballistics")]
        [SerializeField] Transform muzzle;
        [SerializeField] float damage = 34f;
        [SerializeField] float range = 60f;
        [SerializeField] float spreadDegrees = 0.6f;
        [SerializeField] int pellets = 1;                 // >1 = shotgun
        [SerializeField] float roundsPerSecond = 6f;     // max fire rate for semi-auto
        [SerializeField] LayerMask hitMask = ~0;
        [Tooltip("Damage left after punching through the virtual door leaf: a bullet / a shotgun pellet.")]
        [SerializeField] float doorBulletEnergy = 0.75f, doorPelletEnergy = 0.5f;

        [Header("Action")]
        [Tooltip("Pump/bolt action: every shot needs a manual rack before the next.")]
        [SerializeField] bool pumpAction;
        [Tooltip("Tube-fed: B loads one shell at a time instead of swapping a magazine.")]
        [SerializeField] bool shellByShell;
        [SerializeField] float shellLoadSeconds = 0.55f;

        [Header("Double action / single action (hammer guns)")]
        [Tooltip("DA/SA trigger: fires on the analog pull crossing daBreak (hammer down) or saBreak (hammer cocked).")]
        [SerializeField] bool doubleAction;
        [Tooltip("Turns about its local X by hammerCockedDegrees when cocked (pivot on the hammer pin).")]
        [SerializeField] Transform hammer;
        [SerializeField] float hammerCockedDegrees = -60f;
        [Tooltip("The decocker lever (turns about local X while decocking).")]
        [SerializeField] Transform decocker;
        [SerializeField] float decockerDegrees = -50f;
        [Tooltip("Slide catch (turns about local X while the slide is locked back empty).")]
        [SerializeField] Transform slideCatch;
        [SerializeField] float slideCatchDegrees = 6f;
        [Tooltip("Analog trigger value that breaks the shot: double action (long, heavy) / single action (short, light).")]
        [SerializeField, Range(0.1f, 1f)] float daBreak = 0.85f, saBreak = 0.5f;
        [Tooltip("Let the trigger out below this before it can fire again (the reset).")]
        [SerializeField, Range(0f, 0.9f)] float triggerReset = 0.3f;
        [Tooltip("Single action: the trigger sits this far back at rest (0..1 of its travel) — the shorter pull shows.")]
        [SerializeField, Range(0f, 0.9f)] float saTriggerRest = 0.4f;
        [Tooltip("Pitch of this gun's pistol.* sounds (1 = as recorded).")]
        [SerializeField] float sfxPitch = 1f;

        [Header("Ammo")]
        [SerializeField] int magazineCapacity = 15;
        [SerializeField] int reserveMagazines = 4;
        [Tooltip("Sandbox/testing: never run out of spare magazines or shells.")]
        [SerializeField] bool unlimitedReserve = true;
        [SerializeField] float reloadSeconds = 1.6f;

        [Header("Feel")]
        [SerializeField] Transform slide;                 // moves back on fire / stays back when empty
        [SerializeField] float slideTravel = 0.03f;
        [SerializeField] float recoilKickDegrees = 4f;
        [SerializeField] float recoilRecover = 12f;
        [SerializeField] float recoilBack = 0.015f;       // metres the gun kicks back into the hand
        [SerializeField] ParticleSystem muzzleFlash;
        [SerializeField] AudioSource audioSource;
        [SerializeField] AudioClip shotClip, emptyClip, reloadClip, rackClip;

        [Header("Rifle action (opt-in: the AR-15; pistols and the shotgun leave these empty)")]
        [Tooltip("Turns about its local Z by dustCoverOpenDegrees the first time the bolt moves (pivot on its hinge pin); stays open.")]
        [SerializeField] Transform dustCover;
        [SerializeField] float dustCoverOpenDegrees = -170f;
        [Tooltip("Racks back along its local −Z (stick click on an empty chamber; a reload of an empty gun whose bolt isn't locked back).")]
        [SerializeField] Transform chargingHandle;
        [SerializeField] float chargingHandleTravel = 0.06f;
        [Tooltip("How the slide/bolt, magazine, charging handle and dust cover move (seconds, metres). New = the M9's (research/20).")]
        [SerializeField] ActionMotion motion = new();

        public void Configure(float dmg, int pelletCount, float spread, float rps, int capacity, int reserve, bool pump, bool shells, float travel, float kick, float back = 0.015f)
        {
            damage = dmg; pellets = pelletCount; spreadDegrees = spread; roundsPerSecond = rps; magazineCapacity = capacity; reserveMagazines = reserve;
            pumpAction = pump; shellByShell = shells; slideTravel = travel; recoilKickDegrees = kick; recoilBack = back;
        }

        /// <summary>Editor setup (MainSceneBuilder.BuildRiflePrefab): the AR-15's own moving parts, their motion, its reload length,
        /// how fast the kick settles and the pitch of its (borrowed pistol.*) sounds.</summary>
        public void ConfigureRifle(Transform dustCoverPart, float coverOpenDegrees, Transform chargingHandlePart, float handleTravel,
                                   ActionMotion how, float reload, float recover, float pitch)
        {
            dustCover = dustCoverPart; dustCoverOpenDegrees = coverOpenDegrees; chargingHandle = chargingHandlePart; chargingHandleTravel = handleTravel;
            motion = how; reloadSeconds = reload; recoilRecover = recover; sfxPitch = pitch;
        }

        public int RoundsInMag { get; private set; }
        public bool RoundChambered { get; private set; }
        public bool SlideLockedBack { get; private set; }
        public int ReserveMagazines => reserveMagazines;
        public bool IsReloading => _reloadUntil > Time.time;
        public bool DoubleAction => doubleAction;
        /// <summary>DA/SA guns: the hammer is back (the next pull is single action). Guns without a hammer: always false.</summary>
        public bool HammerCocked { get; private set; }
        /// <summary>Analog pull that breaks the next shot (DA/SA guns; others fire on the trigger button).</summary>
        public float BreakAt => HammerCocked ? saBreak : daBreak;
        /// <summary>Where the trigger sits at rest, 0..1 of its travel (single action: already back; WeaponGrip draws it).</summary>
        public float TriggerRest => doubleAction && HammerCocked ? saTriggerRest : 0f;
        public Transform Hammer => hammer;
        /// <summary>Where the hammer is drawn: 0 = down, 1 = fully cocked (a DA pull rolls it back part way).</summary>
        public float HammerTravel { get; private set; }
        public float HammerCockedDegrees => hammerCockedDegrees;
        public float SlideTravel => slideTravel;
        public Transform Slide => slide;
        // The DA/SA parts (renders pose them: HandSnapshots ACTION=1).
        public Transform Decocker => decocker;
        public float DecockerDegrees => decockerDegrees;
        public Transform SlideCatch => slideCatch;
        public float SlideCatchDegrees => slideCatchDegrees;
        public bool PumpAction => pumpAction;
        public bool ShellByShell => shellByShell;
        // The rifle parts (renders pose them: HandSnapshots ACTION=1 -hands ar15).
        public Transform DustCover => dustCover;
        public float DustCoverOpenDegrees => dustCoverOpenDegrees;
        public Transform ChargingHandle => chargingHandle;
        public float ChargingHandleTravel => chargingHandleTravel;
        public ActionMotion Motion => motion;
        /// <summary>The dust cover is open (it opens with the first shot or rack and stays open).</summary>
        public bool DustCoverOpen => _coverOpenAt >= 0f;

        /// <summary>Editor setup (M9Builder): the DA/SA trigger and its parts.</summary>
        public void ConfigureDoubleAction(Transform hammerPart, float cockedDegrees, Transform decockerPart, float decockDegrees,
                                          Transform catchPart, float catchDegrees, float da, float sa, float saRest, float pitch)
        {
            doubleAction = true; hammer = hammerPart; hammerCockedDegrees = cockedDegrees; decocker = decockerPart; decockerDegrees = decockDegrees;
            slideCatch = catchPart; slideCatchDegrees = catchDegrees; daBreak = da; saBreak = sa; saTriggerRest = saRest; sfxPitch = pitch;
        }

        float _nextShotTime, _reloadUntil, _recoil, _nextShellAt, _pumpTravel, _pull, _decockUntil;
        bool _racking, _spentInChamber, _hitFeedbackThisShot, _fleshSfxThisShot, _triggerArmed = true;
        int _wallSfxThisShot, _doorSfxThisShot;
        GunEffects _effects;
        Quaternion _modelRestRot, _hammerRest, _decockerRest, _catchRest, _coverRest;
        Vector3 _modelRestPos, _slideRest, _chargeRest;
        float _boltAt = -10f, _coverOpenAt = -1f, _charge; // rifle: the last shot's bolt stroke, the dust cover opening, the charging handle 0..1
        bool _boltPending; // rifle: fired, the bolt not drawn back yet (its stroke starts on the next drawn frame, whatever the frame rate)
        Transform _model;

        protected override void Awake()
        {
            base.Awake();
            RoundsInMag = magazineCapacity;
            RoundChambered = true;
            _model = transform.childCount > 0 ? transform.GetChild(0) : transform;
            _modelRestRot = _model.localRotation;
            _modelRestPos = _model.localPosition;
            if (slide != null) _slideRest = slide.localPosition;
            if (hammer != null) _hammerRest = hammer.localRotation;
            if (decocker != null) _decockerRest = decocker.localRotation;
            if (slideCatch != null) _catchRest = slideCatch.localRotation;
            if (dustCover != null) _coverRest = dustCover.localRotation;
            if (chargingHandle != null) _chargeRest = chargingHandle.localPosition;
            motion ??= new ActionMotion();
            _magazine = FindDeep(transform, "Magazine"); // the M9's own magazine group (M9Builder "Parts/Magazine"); the Glock's is welded in
            if (_magazine != null) _magRest = _magazine.localPosition;
            if (muzzle == null) muzzle = transform;
            int weaponLayer = LayerMask.NameToLayer("Weapon");
            if (weaponLayer >= 0) hitMask &= ~(1 << weaponLayer); // never shoot your own guns (other hand, holsters)
            int doorLayer = LayerMask.NameToLayer("Door");
            if (doorLayer >= 0) hitMask &= ~(1 << doorLayer);     // the open leaf's collider is for bodies/pathing; rounds use RaycastLeaf
        }

        // DA/SA: a drawn gun's trigger arms only once the finger is off it (no double-action shot straight out of the holster
        // with the trigger already squeezed); a let-go gun forgets the half pull (its hammer doesn't sit half back in the holster).
        protected override void OnGrabbed(HandController hand) { _pull = 0f; _triggerArmed = !doubleAction; }
        protected override void OnReleased(HandController hand) { _pull = 0f; _triggerArmed = true; }

        public override void HandUpdate(HandController hand)
        {
            if (doubleAction) SqueezeTrigger(hand.Trigger, hand);
            else if (hand.TriggerDown) TryFire(hand);
            if (shellByShell) ShellInput(hand);
            else if (hand.SecondaryButtonDown) Reload(hand);
            if (hand.ThumbstickClickDown) { if (doubleAction && RoundChambered && HammerCocked) Decock(hand); else Rack(hand); }
        }

        /// <summary>
        /// DA/SA trigger, fed the analog pull every frame (tests call it directly, hand = null). Decocked: the hammer rolls
        /// back with the pull and the shot breaks at <see cref="daBreak"/>. Cocked: it breaks at <see cref="saBreak"/>. After a
        /// break the trigger must come back out past <see cref="triggerReset"/>. The slide recocks the hammer on every shot.
        /// </summary>
        public void SqueezeTrigger(float pull, HandController hand)
        {
            _pull = Mathf.Clamp01(pull);
            if (!_triggerArmed) { if (_pull <= triggerReset) _triggerArmed = true; return; }
            if (_pull < BreakAt || IsReloading || Time.time < _nextShotTime || _decockUntil > Time.time) return;
            _triggerArmed = false;
            // Slide locked back: the disconnector holds the cocked hammer; the trigger goes dead with a click.
            if (SlideLockedBack || !RoundChambered) { EmptyClick(hand); HammerCocked = true; return; }
            HammerCocked = false; // the hammer falls…
            TryFire(hand);
            HammerCocked = true;  // …and the slide's recoil stroke cocks it again (it stays back, also when the slide locks)
        }

        // The decocker (M9: the slide-mounted safety pushed down): the hammer drops safely onto the firing-pin block; the next
        // shot is double action again.
        public void Decock(HandController hand)
        {
            HammerCocked = false;
            _decockUntil = Time.time + 0.25f;
            PlaySfx(".empty", transform.position, 0.6f);
            hand?.Haptic(0.3f, 0.05f);
            Log.V($"[HIS] {DisplayName} decocked");
        }

        // Tube-fed: tap B pumps when the chamber is empty and there's a shell to feed; otherwise B loads a shell.
        // Holding B keeps feeding shells.
        void ShellInput(HandController hand)
        {
            if (hand.SecondaryButtonDown)
            {
                if (pumpAction && !RoundChambered && RoundsInMag > 0) { Rack(hand); _nextShellAt = Time.time + 0.45f; }
                else { LoadShell(hand); _nextShellAt = Time.time + shellLoadSeconds; }
                return;
            }
            if (hand.SecondaryButton && Time.time >= _nextShellAt && !_racking)
            {
                LoadShell(hand);
                _nextShellAt = Time.time + shellLoadSeconds;
            }
        }

        void Update()
        {
            // Procedural recoil on the visual model only, pivoting at the grip (the weapon root) with a short rearward kick,
            // so the grip stays in the hand while the muzzle climbs.
            if (_recoil > 0.001f)
            {
                _recoil = Mathf.Lerp(_recoil, 0f, recoilRecover * Time.deltaTime);
                var rot = Quaternion.Euler(-_recoil, 0, 0);
                _model.localRotation = rot * _modelRestRot;
                _model.localPosition = rot * _modelRestPos + new Vector3(0, 0, -recoilBack * (_recoil / Mathf.Max(recoilKickDegrees, 0.01f)));
            }
            else if (_model.localPosition != _modelRestPos) { _model.localRotation = _modelRestRot; _model.localPosition = _modelRestPos; }
            if (hammer != null)
            {
                // Cocked: back. Decocked: rolls back with a double-action pull (and falls again if the pull is let out).
                float h = HammerCocked ? 1f : doubleAction && _triggerArmed ? Mathf.Clamp01(_pull / daBreak) : 0f;
                hammer.localRotation = _hammerRest * Quaternion.Euler(hammerCockedDegrees * h, 0f, 0f);
                HammerTravel = h;
            }
            if (decocker != null)
                decocker.localRotation = _decockerRest * Quaternion.Euler(_decockUntil > Time.time ? decockerDegrees : 0f, 0f, 0f);
            if (slideCatch != null)
                slideCatch.localRotation = _catchRest * Quaternion.Euler(SlideLockedBack ? slideCatchDegrees : 0f, 0f, 0f);
            if (slide != null)
            {
                // Pump guns: the fore-end only moves when the player racks it. Pistols: slide cycles with recoil / locks back empty,
                // and a reload's slide release slams it home over 40 ms (not one frame). Rifle: the carrier rides the charging handle
                // when it's racked, and cycles on its own clock per shot (BoltStroke) instead of the recoil curve.
                float release = Time.time - _releaseAt;
                float back = pumpAction ? _pumpTravel * slideTravel
                           : _charge > 0f ? _charge * slideTravel
                           : SlideLockedBack ? slideTravel
                           : release >= 0f && release < motion.SlideSlamSeconds ? slideTravel * (1f - release / motion.SlideSlamSeconds)
                           : motion.BoltBackSeconds > 0f ? BoltStroke() * slideTravel
                           : Mathf.Clamp01(_recoil / recoilKickDegrees) * slideTravel;
                slide.localPosition = _slideRest + new Vector3(0, 0, -back);
            }
            if (chargingHandle != null) chargingHandle.localPosition = _chargeRest + new Vector3(0f, 0f, -_charge * chargingHandleTravel);
            if (dustCover != null) dustCover.localRotation = _coverRest * Quaternion.Euler(0f, 0f, dustCoverOpenDegrees * CoverSwing());
            // The magazine rides out along its own axis (the grip's rake) during a reload.
            if (_magazine != null) _magazine.localPosition = _magRest + _magazine.localRotation * Vector3.down * _magOut;
        }

        /// <summary>
        /// Rifle bolt stroke, 0 (home) … 1 (back), from the last shot: fully back at once (the real rearward stroke takes ~10 ms,
        /// under a frame), held for BoltBackSeconds, then the buffer spring drives it home over BoltHomeSeconds, accelerating.
        /// </summary>
        float BoltStroke()
        {
            if (_boltPending) { _boltPending = false; _boltAt = Time.time; } // the frame after the shot always shows it fully back
            float t = Time.time - _boltAt;
            if (t < 0f) return 0f;
            if (t < motion.BoltBackSeconds) return 1f;
            float u = (t - motion.BoltBackSeconds) / Mathf.Max(motion.BoltHomeSeconds, 1e-4f);
            return u >= 1f ? 0f : 1f - u * u;
        }

        /// <summary>The dust cover, 0 = shut … 1 = open: flips open on its spring, slaps the receiver, bounces back ~7 % and settles.</summary>
        float CoverSwing()
        {
            if (_coverOpenAt < 0f) return 0f;
            float u = (Time.time - _coverOpenAt) / Mathf.Max(motion.CoverOpenSeconds, 1e-4f);
            if (u >= 1.6f) return 1f;
            if (u < 1f) return 1f - (1f - u) * (1f - u);
            return 1f - 0.07f * Mathf.Sin(Mathf.PI * (u - 1f) / 0.6f) * (1.6f - u) / 0.6f; // the bounce off the stop, dying out
        }

        /// <summary>The bolt moved (a shot, the charging handle): a shut dust cover pops open.</summary>
        void OpenCover()
        {
            if (dustCover == null || _coverOpenAt >= 0f) return;
            _coverOpenAt = Time.time;
            Debug.Log($"[HIS] {DisplayName}: dust cover open");
        }

        void TryFire(HandController hand)
        {
            if (IsReloading) return;
            if (!RoundChambered || Time.time < _nextShotTime)
            {
                if (!RoundChambered) EmptyClick(hand);
                return;
            }
            _nextShotTime = Time.time + 1f / roundsPerSecond;

            // Brett 2026-10-03: "shooting yourself in the head should automatically restart at any point". The muzzle at your own
            // head is the quick restart: the shot sounds, no round flies (nothing behind you gets hit), and the session starts over
            // the way hold-Menu does — any mode, any moment. No death, no gore. The round stays chambered for the new run.
            if (AtOwnHead())
            {
                if (muzzleFlash != null) muzzleFlash.Play(true);
                PlaySfx(".shot", muzzle.position, 1f, 0.04f);
                hand?.Haptic(1f, 0.09f, 1f);
                Debug.Log($"[HIS] {DisplayName}: fired at your own head — restart");
                FindAnyObjectByType<GameSession>()?.ResetSession(GameSession.SelfShotReason);
                return;
            }
            RoundChambered = false;

            // One shot = one feedback: a shotgun's pellets share a single flesh sound, hit haptic and at most two wall sounds.
            _hitFeedbackThisShot = false; _fleshSfxThisShot = false; _wallSfxThisShot = 0; _doorSfxThisShot = 0;
            _shotHits = 0; _shotThroughDoor = false; _shotWall = null; _shotWhat = null;
            // Where the bodies are NOW (Brett 0.1.40: "a lot of the shots at the cops werent hitting"): autoSyncTransforms is off,
            // so the hitboxes on animated bones sat where the last physics step left them — a frame or two behind a jogging
            // officer's limbs (a round passing through his arm missed it). Once per trigger pull, not per pellet.
            Physics.SyncTransforms();
            for (int i = 0; i < pellets; i++) FireRay();
            LogShot();

            // Cycle: semi-auto chambers the next round itself; pump-action waits for the player to rack.
            if (pumpAction) { RoundChambered = false; _spentInChamber = true; }
            else if (RoundsInMag > 0) { RoundsInMag--; RoundChambered = true; }
            else
            {
                SlideLockedBack = true;
                // The last round: the slide stays back with its own clack and a tick in the hand (research/20 option 4).
                if (!pumpAction) { PlaySfx(".slidelock", transform.position, 0.9f); hand?.Haptic(0.35f, 0.05f, 0.6f); }
            }

            _recoil = recoilKickDegrees;
            // A rifle's stroke starts on the next drawn frame — unless this round locked it back: the catch holds it there, and a
            // stroke left pending would fire after the bolt release's slam (a second slam on every empty reload).
            _boltPending = !SlideLockedBack;
            OpenCover();
            if (muzzleFlash != null) muzzleFlash.Play(true);
            _effects ??= GetComponent<GunEffects>();
            if (_effects != null) _effects.OnFired();
            Play(shotClip);
            PlaySfx(".shot", muzzle.position, 1f, 0.04f);
            Fired?.Invoke(muzzle.position);
            ShotsFired++;
            TotalShotsFired++;
            hand?.Haptic(1f, 0.09f, 1f);
            Log.V($"[HIS] {DisplayName} fired — mag {RoundsInMag}{(RoundChambered ? "+1" : "")}");
        }

        void EmptyClick(HandController hand) { Play(emptyClip); PlaySfx(".empty", muzzle.position, 0.8f); hand?.Haptic(0.15f, 0.04f); }

        /// <summary>This gun's sound (SfxKey + suffix), at its own pitch.</summary>
        void PlaySfx(string suffix, Vector3 at, float volume, float jitter = -1f)
        {
            int h = Sfx.Play(SfxKey + suffix, at, volume, jitter);
            if (h != 0 && sfxPitch != 1f) Sfx.ScalePitch(h, sfxPitch);
        }

        /// <summary>Rounds this gun has fired (tests; the Marshal weapons check counts its practice shots with it).</summary>
        public int ShotsFired { get; private set; }

        /// <summary>Magazine reloads started (tests; the Marshal weapons check's rifle step waits for one).</summary>
        public int Reloads { get; private set; }

        /// <summary>A full gun: the magazine (or the tube) plus one in the chamber.</summary>
        public int FullLoad => magazineCapacity + 1;

        /// <summary>
        /// The Marshal weapons check's practice ammo (research/26 §2 "practice ammo", research/27; Scenario/MarshalBriefing, the
        /// only caller): load exactly <paramref name="rounds"/>, the chambered one included (0 = empty, a pistol's slide locked
        /// back; at most <see cref="FullLoad"/>), so three practice shots lock the slide, and a full gun goes back after the
        /// check. Refused (false) mid-reload or mid-pump, which end with a loaded gun anyway. Nothing else changes.
        /// </summary>
        public bool LoadForPractice(int rounds)
        {
            if (IsReloading || _racking) return false;
            rounds = Mathf.Clamp(rounds, 0, FullLoad);
            RoundChambered = rounds > 0;
            RoundsInMag = Mathf.Max(0, rounds - 1);
            SlideLockedBack = rounds == 0 && !pumpAction;
            _spentInChamber = false;
            return true;
        }

        int _shotHits; bool _shotThroughDoor; string _shotWall, _shotWhat;
        Vector3 _lastRayDir;

        /// <summary>
        /// One line per shot in the headset's log (Core/LogFile): what it hit, or by how far it missed the nearest living man and
        /// what stopped it (Brett 0.1.31: "if im just missing thats okay" — now the log can tell).
        /// </summary>
        void LogShot()
        {
            if (_shotHits > 0) { Debug.Log($"[HIS] Shot {DisplayName}: {_shotHits}/{pellets} hit — {_shotWhat}{(_shotThroughDoor ? " (through the door)" : "")}{(_shotWall == "glass" ? " (through glass)" : "")}"); return; }
            var o = muzzle.position; var d = pellets == 1 && _lastRayDir != Vector3.zero ? _lastRayDir : muzzle.forward;
            HIS.Intruder.Intruder best = null; float bestOff = float.MaxValue, bestDist = 0f;
            foreach (var man in HIS.Intruder.Intruder.All)
            {
                if (man == null || man.Current is HIS.Intruder.Intruder.State.Dead or HIS.Intruder.Intruder.State.Gone) continue;
                var chest = man.transform.position + Vector3.up * 1.3f;
                float along = Vector3.Dot(chest - o, d);
                if (along < 0.3f) continue;
                float off = Vector3.Distance(o + d * along, chest);
                if (off < bestOff) { bestOff = off; bestDist = along; best = man; }
            }
            if (best == null || bestOff > 2f) return; // not aimed at anyone
            // Which way it went (Brett 0.1.40: "a lot of the shots at the cops werent hitting": is it the aim, or the hitboxes?).
            var pass = o + d * bestDist; var chestAt = best.transform.position + Vector3.up * 1.3f;
            float up = pass.y - chestAt.y, right = Vector3.Dot(pass - chestAt, Vector3.Cross(Vector3.up, d).normalized);
            Debug.Log($"[HIS] Shot {DisplayName}: missed #{best.Id} at {bestDist:F1} m by {bestOff:F2} m (from his chest: {up:+0.00;-0.00} up, {right:+0.00;-0.00} right; " +
                      $"{NearestHitbox(best, o, d)}){(_shotThroughDoor ? ", through the door" : "")}{(_shotWall != null ? $", stopped by {_shotWall}" : "")}");
        }

        /// <summary>The man's hitbox nearest the shot's line and how far off it passed ("off" if it was switched off).</summary>
        static string NearestHitbox(HIS.Intruder.Intruder man, Vector3 o, Vector3 d)
        {
            Collider best = null; float bestGap = float.MaxValue;
            foreach (var c in man.GetComponentsInChildren<Collider>(true))
            {
                if (c.GetComponentInParent<IDamageable>() == null) continue;
                var q = o + d * Mathf.Max(0f, Vector3.Dot(c.bounds.center - o, d));
                float gap = c.enabled ? Vector3.Distance(c.ClosestPoint(q), q) : Vector3.Distance(c.bounds.center, q);
                if (gap < bestGap) { bestGap = gap; best = c; }
            }
            return best == null ? "no hitboxes" : $"nearest {HitName(best)} {bestGap:F2} m{(best.enabled ? "" : " (off)")}";
        }

        /// <summary>A hitbox's name for the log: the bone, or "the bone (gear)" for an officer's cap/vest/belt.</summary>
        static string HitName(Collider c) =>
            c.name == "OfficerProp" && c.transform.parent != null ? $"{c.transform.parent.name} (gear)" : c.name;

        void FireRay()
        {
            // Spread as a cone around the barrel (muzzle space). The old world-axis Euler collapsed into a flat line
            // depending on which way the player faced.
            var dir = muzzle.forward;
            if (spreadDegrees > 0f)
            {
                var r = Random.insideUnitCircle * Mathf.Tan(spreadDegrees * Mathf.Deg2Rad);
                dir = (muzzle.rotation * new Vector3(r.x, r.y, 1f)).normalized;
            }

            var origin = muzzle.position;
            _lastRayDir = dir;   // the miss log measures the round that flew (the spread), not the aim line
            bool struck = Physics.Raycast(origin, dir, out var hit, range, hitMask, QueryTriggerInteraction.Ignore);
            // A wall mesh left uncut across a doorway (0.1.34: a room update rebuilt the walls without their door holes, and
            // every round stopped at the doorway; Room/DoorHoles) is no wall: the round carries on through the opening.
            if (struck && HIS.Room.DoorHoles.WallAcrossDoorway(hit.collider, hit.point))
            {
                float skipped = hit.distance + 0.004f;
                struck = skipped < range && Physics.Raycast(origin + dir * skipped, dir, out hit, range - skipped, hitMask, QueryTriggerInteraction.Ignore);
                if (struck) hit.distance += skipped; // from the muzzle, like the leaf's distances
            }
            // Through the virtual door leaf, closed or open (Brett 0.1.8): a hole, splinters and a crack on the leaf, less
            // punch behind it, and the round carries on into whoever is there (his hitboxes are on while he's at the door).
            float energy = 1f;
            // Either door (front or second): the nearest leaf on the ray.
            HIS.Room.VirtualDoor door = null; HIS.Room.VirtualDoor.LeafHit leaf = default;
            foreach (var d in HIS.Room.VirtualDoor.All)
                if (d != null && d.RaycastLeaf(origin, dir, struck ? hit.distance : range, out var h) && (door == null || h.Distance < leaf.Distance)) { door = d; leaf = h; }
            if (door != null)
            {
                _shotThroughDoor = true;
                door.BulletHole(in leaf, dir, pellets > 1, _doorSfxThisShot++ < 1, origin);   // (the muzzle: a stacked hitman answers along it, research/27)
                energy = pellets > 1 ? doorPelletEnergy : doorBulletEnergy;
                float left = range - leaf.ExitDistance;
                struck = left > 0f && Physics.Raycast(leaf.ExitPoint + dir * 0.002f, dir, out hit, left, hitMask, QueryTriggerInteraction.Ignore);
            }
            // Through car glass (IPierceable): the pane is marked and the round carries on into whoever is behind it.
            for (int pane = 0; struck && pane < 2 && hit.collider.TryGetComponent<IPierceable>(out var glass); pane++)
            {
                glass.Pierce(new DamageInfo { Amount = damage * energy, Type = DamageType.Bullet, Point = hit.point, Normal = hit.normal, Direction = dir, Penetration = energy, Source = gameObject });
                energy *= 0.85f;
                _shotWall ??= "glass";
                struck = Physics.Raycast(hit.point + dir * 0.004f, dir, out hit, range, hitMask, QueryTriggerInteraction.Ignore);
            }
            if (!struck) return;

            var target = hit.collider.GetComponentInParent<IDamageable>();
            var info = new DamageInfo
            {
                Amount = damage * energy, Type = DamageType.Bullet, Point = hit.point, Normal = hit.normal,
                Direction = dir, Penetration = energy, Source = gameObject,
            };
            if (target is IInanimate)
            {
                target.ApplyDamage(info);               // a car's body: its own hole and ping, no flesh
                _shotWall ??= hit.collider.name;
                return;
            }
            if (target != null)
            {
                _shotHits++;
                _shotWhat ??= $"{hit.collider.transform.root.name}/{HitName(hit.collider)} at {hit.distance:F1} m";
                float applied = target.ApplyDamage(info);
                // Hit confirmation: a second, delayed pulse; a heavy thump for a headshot-class hit. Once per shot.
                if (Hand != null && (!_hitFeedbackThisShot || applied >= 200f))
                    Hand.Invoke(applied >= 200f ? nameof(HandController.HapticHeavy) : nameof(HandController.HapticLight), 0.06f);
                _hitFeedbackThisShot = true;
                // Through the closed door the thwack stays on his side of it; his grunt comes through muffled (Intruder).
                if (!_fleshSfxThisShot && !(door != null && !door.IsOpen)) { Sfx.Play("flesh.bullet", hit.point, 0.9f); _fleshSfxThisShot = true; }
            }
            else { _shotWall ??= hit.collider.name; Impact.WorldHit(info, hit, _wallSfxThisShot++ < 2); }
        }

        void LoadShell(HandController hand)
        {
            if (_racking || (reserveMagazines <= 0 && !unlimitedReserve) || RoundsInMag >= magazineCapacity) return;
            if (!unlimitedReserve) reserveMagazines--; // reserve counts shells for tube-fed guns
            RoundsInMag++;
            Play(reloadClip);
            PlaySfx(".shellin", transform.position, 0.9f);
            hand?.Haptic(0.35f, 0.08f);
            Log.V($"[HIS] {DisplayName} shell loaded ({RoundsInMag}/{magazineCapacity}, {reserveMagazines} left)");
        }

        /// <summary>B: drop the magazine, seat a full one, the slide release chambers a round (tests call it, hand = null).</summary>
        public void Reload(HandController hand)
        {
            if (IsReloading) return;
            if (shellByShell) { LoadShell(hand); return; }
            if (reserveMagazines <= 0 && !unlimitedReserve) return;
            if (RoundsInMag == magazineCapacity && RoundChambered) return;
            if (!unlimitedReserve) reserveMagazines--;
            _reloadUntil = Time.time + reloadSeconds;
            Reloads++;
            RoundsInMag = magazineCapacity;
            Play(reloadClip);
            PlaySfx(".magout", transform.position, 0.9f);
            hand?.Haptic(0.4f, 0.12f);
            StartCoroutine(_magazine != null ? AnimatedReload(hand) : FinishReload(hand));
            Debug.Log($"[HIS] {DisplayName} reloading ({reserveMagazines} mags left)");
        }

        // Mag seats, then the slide release drops the slide and chambers a round (one B press = complete reload).
        System.Collections.IEnumerator FinishReload(HandController hand)
        {
            yield return new WaitForSeconds(reloadSeconds);
            if (!RoundChambered && RoundsInMag > 0)
            {
                RoundsInMag--;
                RoundChambered = true;
                SlideLockedBack = false;
                Play(rackClip);
                PlaySfx(".rack", transform.position, 0.9f);
                hand?.Haptic(0.5f, 0.06f);
            }
        }

        // ───────────────────────────── the animated reload (the M9's: research/20 option 1, Brett: "do the ... m9 reload"; the AR-15's) ─────────────────────────────

        Transform _magazine;
        Vector3 _magRest;
        float _magOut, _releaseAt = -10f;

        void Phase(string what) { if (motion.LogPhases) Debug.Log($"[HIS] {DisplayName} reload: {what}"); }

        /// <summary>
        /// One B press, you see it all (timings: <see cref="ActionMotion"/>): the empty mag slides out of the well and drops to your
        /// real floor (a loose copy, bouncing, gone after ~20 s), a fresh one comes up from under the well and slides home ("mag
        /// in"; the rifle's is pushed a hair past the catch and clicks back down), and if the gun was empty the slide catch / bolt
        /// catch drops and the slide or carrier slams forward — or, a rifle whose bolt wasn't locked back, the charging handle is
        /// racked. A round still chambered (a tactical reload) skips that last part.
        /// </summary>
        System.Collections.IEnumerator AnimatedReload(HandController hand)
        {
            float t0 = Time.time;
            while (Time.time - t0 < motion.MagOutSeconds) { _magOut = motion.MagOutTravel * (Time.time - t0) / motion.MagOutSeconds; yield return null; }
            _magOut = motion.MagOutTravel;
            DroppedMag.Spawn(_magazine, gameObject.layer, -_magazine.up * motion.DropSpeed, transform);
            _magazine.gameObject.SetActive(false);                 // the well is empty for a beat
            Phase("mag out (dropped)");
            while (Time.time - t0 < motion.MagInAt) yield return null;
            _magazine.gameObject.SetActive(true);                  // a fresh one, from under the well
            Phase("fresh mag in");
            float s0 = Time.time;
            bool seat = motion.SeatOvershoot > 0f;
            while (Time.time - s0 < motion.MagInSeconds)
            {
                float u = (Time.time - s0) / motion.MagInSeconds;
                // The M9's slides up at a constant speed; a seated rifle magazine comes up fast and slows into the well.
                _magOut = Mathf.LerpUnclamped(motion.MagInTravel, seat ? -motion.SeatOvershoot : 0f, seat ? 1f - (1f - u) * (1f - u) : u);
                yield return null;
            }
            PlaySfx(".magin", transform.position, 0.9f);
            hand?.Haptic(0.5f, 0.06f);
            if (seat)
            {
                // Pushed past the catch, it clicks back down onto it.
                float c0 = Time.time;
                while (Time.time - c0 < motion.SeatSeconds) { _magOut = -motion.SeatOvershoot * (1f - (Time.time - c0) / motion.SeatSeconds); yield return null; }
            }
            _magOut = 0f;
            Phase("seated");
            if (RoundChambered || RoundsInMag <= 0) { Phase(RoundChambered ? "done (a round was still chambered)" : "done"); yield break; }
            while (Time.time - t0 < motion.SlideReleaseAt) yield return null;
            if (!SlideLockedBack && chargingHandle != null) { Phase("charging handle (the bolt wasn't locked back)"); yield return ChargingHandleCycle(hand); yield break; }
            RoundsInMag--;
            RoundChambered = true;
            SlideLockedBack = false;
            _releaseAt = Time.time;
            Play(rackClip);
            PlaySfx(Sfx.Has(SfxKey + ".sliderelease") ? ".sliderelease" : ".rack", transform.position, 0.9f);
            hand?.Haptic(0.5f, 0.06f);
            Phase("bolt release: chambered");
        }

        /// <summary>
        /// The rifle's charging handle (timings: <see cref="ActionMotion"/>): pulled back — the carrier rides it and the dust cover
        /// pops — held a beat, let go: both slam home and strip a round off the magazine into the chamber.
        /// </summary>
        System.Collections.IEnumerator ChargingHandleCycle(HandController hand)
        {
            _racking = true;
            OpenCover();
            Play(rackClip);
            PlaySfx(".rack", transform.position, 0.9f);
            hand?.Haptic(0.45f, 0.05f);
            float t0 = Time.time;
            while (Time.time - t0 < motion.ChargeBackSeconds) { float u = (Time.time - t0) / motion.ChargeBackSeconds; _charge = u * (2f - u); yield return null; }
            _charge = 1f;
            float h0 = Time.time;
            while (Time.time - h0 < motion.ChargeHoldSeconds) yield return null;
            if (!RoundChambered && RoundsInMag > 0) { RoundsInMag--; RoundChambered = true; }
            SlideLockedBack = !RoundChambered;                     // let go on an empty magazine: the bolt catch holds it back again
            float r0 = Time.time;
            while (Time.time - r0 < motion.SlideSlamSeconds) { _charge = 1f - (Time.time - r0) / motion.SlideSlamSeconds; yield return null; }
            _charge = 0f;
            hand?.Haptic(0.5f, 0.05f);
            _racking = false;
            Debug.Log($"[HIS] {DisplayName}: charging handle racked — {(RoundChambered ? "chambered" : "empty, locked back")}");
        }

        /// <summary>Put away or dropped mid-reload / mid-rack: the mag is back in the well, the handle home, and the reload completes at once.</summary>
        void OnDisable()
        {
            if (chargingHandle != null && (_racking || _charge > 0f))
            {
                _racking = false; _charge = 0f;
                chargingHandle.localPosition = _chargeRest;
                if (!RoundChambered && RoundsInMag > 0) { RoundsInMag--; RoundChambered = true; SlideLockedBack = false; }
            }
            if (_magazine == null || (_magOut == 0f && _magazine.gameObject.activeSelf)) return;
            _magazine.gameObject.SetActive(true);
            _magOut = 0f;
            _magazine.localPosition = _magRest;
            if (!RoundChambered && RoundsInMag > 0) { RoundsInMag--; RoundChambered = true; SlideLockedBack = false; }
        }

        static Transform FindDeep(Transform t, string name)
        {
            foreach (Transform c in t)
            {
                if (c.name == name) return c;
                var f = FindDeep(c, name);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Prefix for SfxLibrary keys, e.g. "pistol" → pistol.shot / pistol.empty / pistol.magout / pistol.rack.</summary>
        public string SfxKey = "pistol";

        /// <summary>Muzzle point (aim sensing: is the player pointing this gun at someone?).</summary>
        public Transform Muzzle => muzzle;

        /// <summary>
        /// The muzzle at the player's own head: already inside it (under 12 cm from its centre), or within 45 cm and the barrel
        /// pointing into it (≤ 35° off) — the temple, under the chin, looking down the barrel. The head's centre sits ~9 cm behind
        /// the eyes. Aimed normally the muzzle is ~0.5 m out in front pointing away, so an ordinary shot never trips it.
        /// </summary>
        bool AtOwnHead()
        {
            var cam = Camera.main;
            if (cam == null || muzzle == null) return false;
            var head = cam.transform.position - cam.transform.forward * 0.09f;
            var to = head - muzzle.position;
            float d = to.magnitude;
            return d < 0.12f || (d < 0.45f && Vector3.Angle(muzzle.forward, to) < 35f);
        }

        /// <summary>Any player gun fired (muzzle position): intruders hear it — a burglar bolts, a surrenderer flinches.</summary>
        public static event System.Action<Vector3> Fired;

        /// <summary>Rounds fired by every player gun this session (read as a difference: the police dispatcher's "Shots fired"
        /// only when a round was fired during the round, never after a stabbing alone).</summary>
        public static int TotalShotsFired { get; private set; }

        void Rack(HandController hand)
        {
            if (IsReloading || _racking) return;
            if (RoundChambered) return; // already chambered: racking would eject a live round (skip for now)
            if (pumpAction) { StartCoroutine(PumpCycle(hand)); return; }
            if (RoundsInMag <= 0) { Play(emptyClip); return; }
            if (chargingHandle != null) { StartCoroutine(ChargingHandleCycle(hand)); return; } // the rifle: you see it racked
            RoundsInMag--;
            RoundChambered = true;
            SlideLockedBack = false;
            if (doubleAction) HammerCocked = true; // racking the slide cocks the hammer
            Play(rackClip);
            PlaySfx(".rack", transform.position, 0.9f);
            hand?.Haptic(0.5f, 0.06f);
        }

        // Pump stroke: fore-end back (the spent hull flies out of the receiver), then forward (the next shell chambers).
        System.Collections.IEnumerator PumpCycle(HandController hand)
        {
            _racking = true;
            Play(rackClip);
            PlaySfx(".rack", transform.position, 0.9f);
            hand?.Haptic(0.55f, 0.06f);
            const float stroke = 0.13f;
            float t = 0;
            while (t < stroke) { t += Time.deltaTime; _pumpTravel = Mathf.Clamp01(t / stroke); yield return null; }
            if (_spentInChamber) { _effects ??= GetComponent<GunEffects>(); _effects?.EjectShell(); _spentInChamber = false; }
            t = 0;
            while (t < stroke) { t += Time.deltaTime; _pumpTravel = 1f - Mathf.Clamp01(t / stroke); yield return null; }
            _pumpTravel = 0f;
            if (!RoundChambered && RoundsInMag > 0) { RoundsInMag--; RoundChambered = true; }
            hand?.Haptic(0.4f, 0.05f);
            _racking = false;
        }

        void Play(AudioClip clip) { if (audioSource != null && clip != null) audioSource.PlayOneShot(clip); }

        public override string Status =>
            $"{DisplayName} {RoundsInMag}{(RoundChambered ? "+1" : "")}/{magazineCapacity}  {(shellByShell ? "shells" : "mags")} {(unlimitedReserve ? "∞" : reserveMagazines.ToString())}" +
            (pumpAction && !RoundChambered && RoundsInMag > 0 ? "  [PUMP — press B]" : pumpAction && !RoundChambered ? "  [EMPTY — hold B to load]" : "") +
            (doubleAction ? (HammerCocked ? "  SA" : "  DA") : "") +
            (IsReloading ? "  [RELOADING]" : SlideLockedBack ? "  [EMPTY — press B]" : !RoundChambered && RoundsInMag == 0 && reserveMagazines == 0 && !unlimitedReserve ? "  [NO AMMO]" : "");
    }
}
