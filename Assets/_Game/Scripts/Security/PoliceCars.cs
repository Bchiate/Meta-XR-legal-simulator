using System.Collections.Generic;
using HIS.Outcome;
using HIS.Room;
using HIS.Scenario;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// Bad call → two police cars pull up on the front door's camera (Brett 0.1.22: "when the police come for bad call maybe
    /// you can have a couple cop cars pull up on the camera"). On <see cref="OutcomeShow.Started"/> with <see cref="Tier.Bad"/>:
    /// a black-and-white cruiser comes in from the right (far lane, swings across), a second from the left 1.4 s later; both
    /// slow and stop at the curb angled toward the house, headlights on, light bars flashing; the first one's driver door
    /// opens. They stay until <see cref="OutcomeShow.Cleared"/> (skip, next round, reset), then vanish.
    /// Feed only: layer 11, children of the front <see cref="PorchSet"/> and switched with it (<see cref="PorchSet.AddLive"/>).
    /// The light bars light the porch through the porch shader's _FlashA/_FlashB terms (no realtime lights anywhere): red
    /// reads bright on a night IR camera and blue dim, so the picture pulses with each car's L/R cycle (2 Hz, under the
    /// 2.5 Hz cap). Cars are procedural low-poly in the porch material (2026-10-05: lofted bodies, pillars, real wheels, lamps,
    /// livery — ~2,300 triangles a cruiser, ~2,000 a truck, no new renderer or material: PoliceCars.Looks.cs).
    /// While they're out the front camera stays live (<see cref="SecurityCamera.KeepLive"/>) and its feed goes big.
    /// The police standoff sends one vehicle per wave (Brett 0.1.26: "for first wave of 2 maybe have one cop car pull up second
    /// wave another car and 3rd wave maybe we add swat truck"): wave 1 the first cruiser, wave 2 the second, wave 3 on the SWAT
    /// truck (dark, boxy, rear doors); each wave's officers get out of theirs (<see cref="TryGetOfficerStart"/>).
    /// A US Marshal night (research/27 §2, §6 step 6) runs the same wave machine with hitmen, who arrive in five more vehicles
    /// (Brett 0.1.57: five waves, one vehicle each), built after the police ones on the night's first send
    /// (<see cref="BuildHitmanCars"/>; never on a police call) and dropped on leaving Marshal mode: wave 1 a grey sedan, wave 2
    /// a dark red sedan, wave 3 a black SUV, wave 4 a charcoal SUV, wave 5 a white van (<see cref="VehicleFor(int)"/> reads
    /// <see cref="PoliceResponse.Faction"/>). No light bar, no push bar, no strobe on the porch; the cars' lights die once
    /// parked and the van never has any. They park along the street on the doorbell's wide side, well off the doorway's line
    /// (<see cref="HitmanSide"/>), each in a spot of its own, and stay parked through the night's later waves (and the visitors
    /// between them: <see cref="OnShowCleared"/>): the feed shows them, the open doorway from far fewer places in the room than
    /// the police cars in front of the path (GPU, research/27 §6).
    /// </summary>
    public partial class PoliceCars : MonoBehaviour
    {
        public static PoliceCars Instance { get; private set; }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Instance = null;

        /// <summary>Full L/R light-bar cycle (Hz): the red side, a gap, the blue side, a gap.</summary>
        public const float BarHz = 2f;
        public const float RedFlash = 3f, BlueFlash = 1f, FlashRange = 8f, CarFill = 0.09f;

        public bool Active { get; private set; }
        /// <summary>Both cars have stopped (and the door has had time to open).</summary>
        public bool Arrived => Active && _t >= ArriveTime;
        public float ArriveTime { get { float t = 0f; foreach (var c in _cars) if (c.Sent) t = Mathf.Max(t, c.StartAt + c.Delay + c.Duration + (c.Doors.Count > 0 ? 1.6f : 0f)); return t; } }
        /// <summary>The police vehicles' mean triangles (every mesh of main's three; logged on each send).</summary>
        public int Triangles { get; private set; }
        /// <summary>How many vehicles are built: main's three, and the hitmen's once a Marshal night has sent one.</summary>
        public int VehicleCount => _cars.Count;

        /// <summary>
        /// Tests and the look renders (Brett 2026-10-05: "just make sure it wont be too heavy on the quest"): vehicle
        /// <paramref name="v"/>'s triangles (every mesh under it: body, doors, glass, lit parts, lenses, glows — bullet marks
        /// too if it carries any), its renderers that draw something and the distinct materials they draw with. False: no such
        /// vehicle built. (Walks its hierarchy: test code, not per frame.)
        /// </summary>
        public bool VehicleBudget(int v, out string name, out int tris, out int renderers, out int materials, out bool truck, out bool police)
        {
            name = null; tris = renderers = materials = 0; truck = police = false;
            if (v < 0 || v >= _cars.Count || _cars[v].Root == null) return false;
            var c = _cars[v];
            name = c.Root.name; truck = c.Truck; police = !c.Hitman;
            var mats = new List<Material>();
            foreach (var r in c.Root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || mf.sharedMesh.vertexCount == 0) continue;   // (an empty marks mesh draws nothing)
                renderers++;
                tris += (int)(mf.sharedMesh.GetIndexCount(0) / 3);
                foreach (var m in r.sharedMaterials) if (m != null && !mats.Contains(m)) mats.Add(m);
            }
            materials = mats.Count;
            return true;
        }

        public PorchSet Porch => _porch;
        /// <summary>The roots of the vehicles sent this call (porch children).</summary>
        public IEnumerable<Transform> CarRoots { get { foreach (var c in _cars) if (c.Root != null && c.Sent) yield return c.Root; } }
        public int ActiveCars { get { int n = 0; foreach (var c in _cars) if (c.Root != null && c.Root.gameObject.activeSelf) n++; return n; } }

        partial class Car   // (+ the doors' swing state: PoliceCars.Exit.cs)
        {
            public Transform Root, LensRed, LensBlue, GlowRed, GlowBlue;
            public readonly List<Transform> Facing = new();
            /// <summary>Doors that swing open once it's stopped: hinge, open angle (yaw, degrees).</summary>
            public readonly List<(Transform Hinge, float Angle)> Doors = new();
            /// <summary>Each door's leaf in its hinge's space: a bullet hole there rides the door when it swings open.</summary>
            public readonly List<Bounds> DoorBoxes = new();
            /// <summary>Where officers 0, 1, 2… step out (car space).</summary>
            public Vector3[] Exits;
            public Vector3 P0, P1, P2, P3;
            public float Delay, Duration, Phase;
            /// <summary>When it was sent (the clock), or −1: not sent yet this call.</summary>
            public float StartAt = -1f;
            public bool Sent => StartAt >= 0f;
            public Vector3 RedLocal, BlueLocal;
            // ── shootable (PoliceCars.Combat.cs) ──
            public bool Truck;
            /// <summary>Where riders sit (car space): driver, passenger, back seat — the truck: its driver, then the cargo box.</summary>
            public Vector3[] Seats;
            /// <summary>Riders by seat (null: empty / on foot).</summary>
            public readonly List<HIS.Intruder.Intruder> Riders = new();
            /// <summary>Half size (x, z) of its footprint, for walkers going round it and cars crashing into it.</summary>
            public Vector2 Half;
            /// <summary>Its glass, pane by pane (windshield, rear, left, right; the truck: windshield, left, right).</summary>
            public readonly List<Pane> Panes = new();
            /// <summary>Its bullet marks: one mesh per thing they stick to (body, a door, a pane).</summary>
            public readonly List<Decals> Decals = new();
            /// <summary>Where a shot-up hood smokes from (car space).</summary>
            public Vector3 Hood;
            public int BodyHits;
            public bool BarBroken, Smoking;
            public float SmokeAcc;
            // Crash: the driver shot dead while it was still moving — it rolls on, veers, hits the curb / another car.
            public bool Crashed, CrashStopped, OverCurb;
            /// <summary>It has hit something (or run out of road): the engine's dead, it only slows from here. When the crash began.</summary>
            public bool Wrecked;
            public float CrashAt;
            public float CrashStoppedAt, CrashYawRate, CrashTurned, CrashSpeed, CrashJolt, HornUntil = -1f;
            public Vector3 CrashPos, CrashDir;
            public int Horn;
            // More dramatic (Brett 0.1.43): a hop over the curb, a bounce back and a spin off the hit, the front crumpled; and
            // a vehicle it slams into gets shoved (any car: parked ones too). All transform/vertex work, no GPU cost.
            public float Hop, HopVel, ReboundLeft, SpinLeft, SpinRate;
            public Vector3 Shove, ShoveVel;
            public float ShoveYaw, ShoveYawVel;
            public Vector3[] BodyRest;
            public Mesh BodyMesh;
            // ── a Marshal night's hitman vehicles (research/27 §6 step 6) ──
            /// <summary>One of the hitmen's (the two sedans, the two SUVs, the van): parked on the doorbell's wide side (<see cref="AimHitman"/>).</summary>
            public bool Hitman;
            /// <summary>Its engine's pitch as it pulls up (a hitman vehicle's: each its own, the sedans highest, the van lowest).</summary>
            public float EnginePitch = 1f;
            /// <summary>It has a light bar (the police's). One without counts as <see cref="BarBroken"/> from the start: dark, no strobe.</summary>
            public bool HasBar = true;
            /// <summary>Grey of the paint chips a hit knocks off (Puff): the cruiser's black and white averaged, the truck's dark paint, …</summary>
            public float Chips = 0.34f;
            /// <summary>
            /// Lights that die once it has parked (the sedan's and SUV's head and tail glows, the headlights' pool, the lit lamp
            /// faces). Null: the police's, lit all the time as before — or the van's, which has none.
            /// </summary>
            public List<Transform> Lamps;
            public bool LampsOn = true;
            /// <summary>Its drive in as built: for the left side of the street (porch x &lt; 0); mirrored when the wide side is the right.</summary>
            public Vector3 From, To;
            public float FromYaw, ToYaw;
            /// <summary>Which side of the path it parked on this call (porch x sign; 0 = the police's fixed spots).</summary>
            public float Side;
            // ── a hitman vehicle's arrival cues (PoliceCars.Arrival.cs; Brett 0.1.56) ──
            /// <summary>Its engine's Sfx handle while it pulls up (0: none).</summary>
            public int Engine;
            /// <summary>Where its engine is heard from (a child of its root, moved toward it from the front door each frame).</summary>
            public Transform EngineAt;
            /// <summary>The doorbell camera's VEHICLE alert was raised for it this send.</summary>
            public bool Spotted;
            /// <summary>Bit per door whose clunk has played this send.</summary>
            public int DoorsHeard;
        }

        /// <summary>
        /// Index of the first hitman vehicle (research/27 §6 step 6): 3 the grey sedan, 4 the dark red sedan, 5 the black SUV,
        /// 6 the charcoal SUV, 7 the white van — one per wave (<see cref="HitmanTier.Waves"/>), named as the tier's
        /// <see cref="HitmanTier.Vehicle"/>.
        /// </summary>
        public const int HitmanVehicles = 3;

        /// <summary>
        /// The vehicle a standoff wave arrives in: 0 = the first cruiser, 1 = the second, 2 = the SWAT truck — or, when the wave
        /// machine is sending a Marshal night's hitmen (<see cref="PoliceResponse.Faction"/>), 3–7 = wave 1–5's own (the grey
        /// sedan, the red sedan, the black SUV, the charcoal SUV, the van). <see cref="Board"/>, <see cref="GetOut"/>,
        /// <see cref="TryGetOfficerStart"/> and <see cref="WaveCarCrashed"/> all key off it.
        /// </summary>
        public static int VehicleFor(int wave)
        {
            var pr = PoliceResponse.Instance;
            return VehicleFor(wave, pr != null ? pr.Faction : SquadFaction.Police);
        }

        /// <summary>
        /// The vehicle wave <paramref name="wave"/> of <paramref name="faction"/> arrives in: the police's later waves reuse the
        /// third (the truck), exactly as before; the hitmen's one each, past wave 5 the van.
        /// </summary>
        public static int VehicleFor(int wave, SquadFaction faction) =>
            faction == SquadFaction.Hitmen ? HitmanVehicles + Mathf.Clamp(wave, 1, HitmanTier.Waves) - 1
                                           : (wave <= 1 ? 0 : wave == 2 ? 1 : 2);

        /// <summary>
        /// The caller's own faction when it gives one (an officer's: his car is his faction's whatever the last run left
        /// <see cref="PoliceResponse.Faction"/> at — a police test after a Marshal night in the same Play session), else
        /// <see cref="VehicleFor(int)"/>'s (the run's now, as before).
        /// </summary>
        static int VehicleOf(int wave, SquadFaction? faction) => faction.HasValue ? VehicleFor(wave, faction.Value) : VehicleFor(wave);

        readonly List<Car> _cars = new();
        readonly List<Mesh> _meshes = new();
        PorchSet _porch;
        Material _carMat; // the porch material with a little fill light: white doors must read between flashes
        Material _glassMat;
        const float GlassAlpha = 0.4f;
        float _t;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnEnable()
        {
            OutcomeShow.Started += OnShow;
            OutcomeShow.Cleared += OnShowCleared;
            HIS.Scenario.PoliceResponse.WaveCalled += OnWave;
            HIS.Scenario.PoliceResponse.Ended += OnRunEnded;
        }

        void OnDisable()
        {
            OutcomeShow.Started -= OnShow;
            OutcomeShow.Cleared -= OnShowCleared;
            HIS.Scenario.PoliceResponse.WaveCalled -= OnWave;
            HIS.Scenario.PoliceResponse.Ended -= OnRunEnded;
        }

        /// <summary>
        /// The police standoff (Brett 0.1.25) calls a wave: its vehicle pulls up (one per wave; later waves reuse the truck). A
        /// hitman wave called after a visitor's banner: the clear that banner held off is moot, the night goes on (<see cref="OnShowCleared"/>).
        /// </summary>
        void OnWave(int wave)
        {
            _clearOwed = false;
            Send(VehicleFor(wave));
        }

        /// <summary>A show was cleared while a Marshal night's waves were still on: its End is owed until the run ends (or the next wave).</summary>
        bool _clearOwed;

        /// <summary>
        /// The show was cleared (skip, next round, reset): the cars go (<see cref="End"/>) — except while a Marshal night's hitman
        /// run is still on with its vehicles out (Brett 0.1.57: the pizza courier and Sal come BETWEEN the waves, and a visitor
        /// left alone gets his banner, whose clear would have swept the parked sedan and SUV off the street mid-night and reset
        /// their drive-ins). Then they stay parked, and the clear is owed: a reset or a stop that ends the run while it's owed
        /// ends them too (<see cref="OnRunEnded"/>: hold-Menu clears the show BEFORE it cancels the run), and the next wave's
        /// call settles it (the night went on). A police run, and every clear with no run on, is exactly as before.
        /// </summary>
        void OnShowCleared()
        {
            if (HitmenOut())
            {
                if (!_clearOwed) Debug.Log("[HIS] PoliceCars: the show cleared mid-night — the hitmen's vehicles stay parked (the waves go on)");
                _clearOwed = true;
                return;
            }
            End();
        }

        /// <summary>A Marshal night's hitman run is on (PoliceResponse active, Faction Hitmen) and one of its vehicles is out.</summary>
        bool HitmenOut()
        {
            if (!Active) return false;
            var pr = PoliceResponse.Instance;
            if (pr == null || !pr.Active || pr.Faction != SquadFaction.Hitmen) return false;
            foreach (var c in _cars) if (c.Hitman && c.Sent) return true;
            return false;
        }

        /// <summary>
        /// A run is over (PoliceResponse.Ended: its end, or Cancel). With a clear owed (<see cref="OnShowCleared"/>: a reset or
        /// a stop mid-night), the cars go now. Otherwise nothing: a night's own end keeps them under its card, as before (the
        /// 2:00 AM cut and the next clear take them), and a booking mid-night shows them with no cruisers rolling in.
        /// </summary>
        void OnRunEnded()
        {
            if (!_clearOwed) return;
            _clearOwed = false;
            Debug.Log("[HIS] PoliceCars: the hitmen's run is over — the clear it held off now");
            End();
        }

        /// <summary>
        /// The wave's vehicle was shot to a stop on its way in (its driver killed while it moved: <see cref="Car.Crashed"/>).
        /// Its riders then all come to the front door; nobody goes round the back (Brett 0.1.42).
        /// </summary>
        /// <param name="faction">The asking man's own (<see cref="VehicleOf"/>); null: the run's now.</param>
        public static bool WaveCarCrashed(int wave, SquadFaction? faction = null)
        {
            var pc = Instance;
            if (pc == null || !pc.Active) return false;
            int v = VehicleOf(wave, faction);
            return v < pc._cars.Count && pc._cars[v].Crashed;
        }

        /// <summary>
        /// Where officer <paramref name="index"/> of <paramref name="wave"/> gets out: by his wave's vehicle (the cruiser's driver,
        /// passenger, then behind; the truck's rear doors), on the porch set's floor. False until it has stopped.
        /// </summary>
        /// <param name="faction">The asking man's own (<see cref="VehicleOf"/>); null: the run's now.</param>
        public static bool TryGetOfficerStart(int wave, int index, out Vector3 at, SquadFaction? faction = null)
        {
            at = default;
            var pc = Instance;
            if (pc == null || !pc.Active || pc._porch == null) return false;
            int v = VehicleOf(wave, faction);
            if (v >= pc._cars.Count) return false;
            var c = pc._cars[v];
            if (c.Root == null || !c.Sent || !pc.Stopped(c)) return false;
            var e = c.Exits[index % c.Exits.Length] + new Vector3(0f, 0f, -1.0f * (index / c.Exits.Length));
            at = c.Root.TransformPoint(e);
            at.y = pc._porch.FloorY;
            return true;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DropCars();
        }

        void OnShow(Tier tier, float length)
        {
            if (tier == Tier.Bad && !Active) Begin(); // (a standoff's booking finds them already there: no second drive-in)
        }

        /// <summary>Send both cruisers in (a bad-call show with no standoff; also a test hook). False without a front porch.</summary>
        public bool Begin() => Send(0) && Send(1);

        /// <summary>
        /// Send vehicle <paramref name="v"/> in (0/1 the cruisers, 2 the SWAT truck; 3–7 the hitmen's grey sedan, red sedan, black
        /// SUV, charcoal SUV and van, parked on the doorbell's wide side); already out: nothing. False without a porch.
        /// </summary>
        public bool Send(int v)
        {
            var front = VirtualDoor.Instance;
            var porch = front != null ? PorchSet.For(front) : null;
            if (porch == null) { Debug.Log("[HIS] PoliceCars: no front porch, no cars"); return false; }
            try
            {
                if (porch != _porch || _cars.Count == 0 || _cars[0].Root == null) Build(porch);
                // A Marshal night's first send: the hitmen's five now (never on a police call — BuildHitmanCars).
                if (v >= HitmanVehicles && _cars.Count <= HitmanVehicles) BuildHitmanCars();
                if (v < 0 || v >= _cars.Count) return false;
                if (!Active)
                {
                    _t = 0f;
                    Active = true;
                    foreach (var o in _cars) { o.StartAt = -1f; ResetCombat(o); ResetArrival(o); o.Root.gameObject.SetActive(false); }
                    var mon = SecurityMonitor.Instance;
                    if (mon != null) mon.ShowFeed(front);
                    var cam = SecurityCamera.For(front);
                    if (cam != null) cam.KeepLive(0.5f);
                }
                var c = _cars[v];
                if (c.Sent) return true;
                if (c.Hitman) AimHitman(c);              // the doorbell's wide side, off the doorway's line (research/27 §6)
                c.StartAt = _t;
                c.Root.gameObject.SetActive(true);
                if (c.Lamps != null) SetLamps(c, true); // headlights on for the drive in; they die once it's parked
                Pose(c, _t);
                if (c.Hitman) BeginArrival(c, v);       // its engine pulling up (PoliceCars.Arrival.cs)
                Debug.Log($"[HIS] PoliceCars: {c.Root.name} pulling up on the {front.Label} camera ({ActiveCars} out, {Triangles} tris a car)" +
                          (c.Hitman ? $", parking {(c.Side < 0f ? "left" : "right")} of the path at x {c.Side * Mathf.Abs(c.To.x):F1} m" : ""));
                return true;
            }
            catch (System.Exception e) { Debug.LogError($"[HIS] PoliceCars: {e}"); Active = false; return false; }
        }

        /// <summary>The show is over: the cars vanish, the porch goes dark again.</summary>
        public void End()
        {
            if (!Active) return;
            Active = false;
            foreach (var c in _cars) { c.StartAt = -1f; ResetCombat(c); ResetArrival(c); if (c.Root != null) c.Root.gameObject.SetActive(false); }
            SetFlash(Vector4.zero, Vector4.zero);
            Shader.SetGlobalVector(FlashColA, Vector4.zero); Shader.SetGlobalVector(FlashColB, Vector4.zero);
            Debug.Log("[HIS] PoliceCars: cleared");
        }

        /// <summary>Seconds since the cars were sent in (pauses with the game).</summary>
        public float Clock => _t;

        /// <summary>Tests: jump the drive-in ahead.</summary>
        public void Advance(float seconds)
        {
            // In steps: a crash is integrated, not posed from the clock.
            for (float left = seconds; left > 1e-4f; left -= 0.05f) { _t += Mathf.Min(0.05f, left); if (Active) Step(); }
        }

        void Update()
        {
            if (!Active) return;
            if (_porch == null || _cars.Count == 0 || _cars[0].Root == null) { Active = false; return; }
            _t += Time.deltaTime; // pauses with the game
            Step();
            var cam = _porch.Door != null ? SecurityCamera.For(_porch.Door) : null;
            if (cam != null)
            {
                // Live while anything out front moves; once every vehicle out is a hitman's, parked and still (HitmenStill:
                // 0.1.58), one fresh frame of them and the feed holds it — minutes of 12 Hz renders saved through a Marshal
                // night's lulls and visitors. A police car out: live every frame, as before.
                bool still = HitmenStill();
                if (!still) cam.KeepLive(0.3f);
                else if (!_wasStill) cam.Refresh();
                _wasStill = still;
            }
        }

        bool _wasStill;
        /// <summary>Tests: this frame every vehicle out was a hitman's, parked and still — the feed wasn't kept live for them.</summary>
        public bool FeedHeldForCars => Active && _wasStill;

        void LateUpdate()
        {
            if (Active) FaceCamera();
        }

        /// <summary>Glow sprites face the lens — all of them, lit or not, so a strobe half that just came on is never edge-on.</summary>
        void FaceCamera()
        {
            var cam = _porch != null && _porch.Door != null ? SecurityCamera.For(_porch.Door) : null;
            if (cam == null) return;
            var eye = cam.transform.position;
            foreach (var c in _cars)
            {
                if (c.Hitman && !c.Sent) continue;   // (a hitman vehicle not out: hidden, nothing to face; the police's as main)
                foreach (var g in c.Facing)
                    if (g != null) g.rotation = Quaternion.LookRotation(eye - g.position, Vector3.up);
            }
        }

        void Step()
        {
            StepCombat();                        // dead drivers: crashes, horns (PoliceCars.Combat.cs)
            Vector4 a = Vector4.zero, b = Vector4.zero, ca = Vector4.zero, cb = Vector4.zero;
            int lit = 0;
            for (int i = _cars.Count - 1; i >= 0; i--)   // (the porch takes two light bars: the latest vehicles')
            {
                var c = _cars[i];
                if (!c.Sent) continue;
                Pose(c, _t);
                if (c.Lamps != null) StepLamps(c);
                StepArrival(c);                      // a hitman vehicle's engine and the camera's VEHICLE alert (PoliceCars.Arrival.cs)
                // Shot out (0.1.41), or a car with no bar at all (the hitmen's, research/27): dark, and it never lights the porch —
                // a Marshal night's waves leave _FlashA/B and _HIS_FlashColA/B at zero.
                if (c.BarBroken)
                {
                    Show(c.LensRed, false); Show(c.GlowRed, false);
                    Show(c.LensBlue, false); Show(c.GlowBlue, false);
                    continue;
                }
                // Light bar: red on for 40% of the cycle, a gap, blue for 40%, a gap.
                float ph = Mathf.Repeat((_t + c.Phase) * BarHz, 1f);
                bool red = ph < 0.4f, blue = ph >= 0.5f && ph < 0.9f;
                c.LensRed.gameObject.SetActive(red); c.GlowRed.gameObject.SetActive(red);
                c.LensBlue.gameObject.SetActive(blue); c.GlowBlue.gameObject.SetActive(blue);
                var at = c.Root.TransformPoint(red ? c.RedLocal : c.BlueLocal);
                var v = new Vector4(at.x, at.y, at.z, red ? RedFlash : blue ? BlueFlash : 0f);
                // The headset's colour look (through an open doorway): the phase's colour, the same punch for red and blue
                // (the feed's IR sees red much brighter; the eye doesn't).
                var col = v.w > 0f ? (red ? RedLight : BlueLight) * (EyeFlash / v.w) : Vector4.zero;
                if (lit == 0) { a = v; ca = col; } else if (lit == 1) { b = v; cb = col; }
                lit++;
            }
            SetFlash(a, b);
            Shader.SetGlobalVector(FlashColA, ca); Shader.SetGlobalVector(FlashColB, cb);
            FaceCamera();
        }

        static readonly int FlashColA = Shader.PropertyToID("_HIS_FlashColA"), FlashColB = Shader.PropertyToID("_HIS_FlashColB");
        static readonly Vector4 RedLight = new(1f, 0.08f, 0.06f, 0f), BlueLight = new(0.12f, 0.3f, 1f, 0f);
        const float EyeFlash = 1.7f;

        void SetFlash(Vector4 a, Vector4 b)
        {
            Flash(_porch != null ? _porch.SetMaterial : null, a, b);
            Flash(_carMat, a, b);
        }

        static void Flash(Material m, Vector4 a, Vector4 b)
        {
            if (m == null) return;
            m.SetVector("_FlashA", a); m.SetVector("_FlashB", b); m.SetFloat("_FlashRange", FlashRange);
        }

        /// <summary>A light-bar part on or off; a car built without a bar has none (null).</summary>
        static void Show(Transform t, bool on) { if (t != null) t.gameObject.SetActive(on); }

        // ── a Marshal night's hitman vehicles (research/27 §2, §6 step 6) ──

        /// <summary>Parked this long, the hitmen's lights die ("a black SUV parks; its lights die"), just before the doors open.</summary>
        const float LightsDie = 0.7f;

        /// <summary>
        /// Head and tail lights and the headlights' pool on while it drives in, dark from <see cref="LightsDie"/> after it stops
        /// (every tier; the van has none). The pool is a 3.4 × 7.5 m additive quad on the road: off, it costs the headset
        /// nothing for the rest of the night. A wreck's lamps go at the impact (they'd float before a crumpled nose).
        /// </summary>
        void StepLamps(Car c)
        {
            bool on = !c.Wrecked && !(Stopped(c) && _t >= StoppedAt(c) + LightsDie);
            if (on != c.LampsOn) SetLamps(c, on);
        }

        static void SetLamps(Car c, bool on)
        {
            c.LampsOn = on;
            foreach (var t in c.Lamps) if (t != null) t.gameObject.SetActive(on);
        }

        /// <summary>
        /// The side of the street (porch x sign) the hitmen park on: the doorbell's wide side. The doorbell hangs on the latch
        /// side turned 15° toward the step, so its 120° picture reaches 75° off the door's axis on the hinge side and only 45°
        /// on the latch side. On the wide side the vehicles park 36–51° off the door's axis and still sit 38–51° off the
        /// camera's (its half-width is 60°): in the picture, but past the edge of what an open doorway shows from most of the
        /// room. Of a 0.2 m grid of standing spots in Brett's room (door #10 or #14 as the front, the room either side) and in
        /// plain 4 × 4 / 5 × 4 m rooms, 2–50% (mean 25%) see any corner of the sedan through the front doorway, 2–46% (22%)
        /// the SUV, 0–34% (15%) the van — against 3–58% (29–36%) for research/27's first spots (x −7 / +7.5 / −11, the van
        /// partly out of the picture on a latch-side camera). The side doorway, if it's on the same wall: none of them over its
        /// backyard fence but the van's roof. (A top-down sweep of the rooms and the doorbell's frustum; research/27 §6 step 12
        /// measures the headset.) Falls back to the left.
        /// </summary>
        float HitmanSide()
        {
            if (_porch == null) return -1f;
            float latch = _porch.transform.InverseTransformPoint(_porch.CameraPosition).x;   // the doorbell's side of the door
            return latch >= 0f ? -1f : 1f;
        }

        /// <summary>Its drive in, as built (the left side) or mirrored to the right (<see cref="HitmanSide"/>): in from that end of the street.</summary>
        void AimHitman(Car c)
        {
            c.Side = HitmanSide();
            var m = new Vector3(-c.Side, 1f, 1f);   // (built for side −1: unchanged there)
            Curve(c, Vector3.Scale(c.From, m), -c.Side * c.FromYaw, Vector3.Scale(c.To, m), -c.Side * c.ToYaw);
        }

        /// <summary>Along its curve with an ease-out (fast in, braking to the stop); the door swings open after.</summary>
        void Pose(Car c, float t)
        {
            if (c.Crashed) { PoseCrashed(c); return; }
            t -= Mathf.Max(0f, c.StartAt);   // its own drive-in clock
            float u = Mathf.Clamp01((t - c.Delay) / c.Duration);
            float s = 1f - (1f - u) * (1f - u);
            var p = Bezier(c.P0, c.P1, c.P2, c.P3, s);
            var d = BezierTangent(c.P0, c.P1, c.P2, c.P3, Mathf.Clamp(s, 0.001f, 0.999f));
            d.y = 0f;
            c.Root.localPosition = p + c.Shove;
            if (d.sqrMagnitude > 1e-6f) c.Root.localRotation = Quaternion.LookRotation(d.normalized, Vector3.up);
            // A small nose dip as it brakes to the stop; the rock of being hit (a crashing car slammed into it).
            float brake = u > 0.75f && u < 1f ? Mathf.Sin((u - 0.75f) / 0.25f * Mathf.PI) : 0f;
            float j = c.CrashJolt, wob = Mathf.Sin(_t * 27f) * j;
            c.Root.localRotation = Quaternion.Euler(0f, c.ShoveYaw, 0f) * c.Root.localRotation * Quaternion.Euler(brake * 1.2f + 1.2f * wob, 0f, 2f * wob);
            OpenDoors(c, t - c.Delay - c.Duration);
        }

        // (OpenDoors — each door on its own rider's swing, Brett 2026-10-05 — is in PoliceCars.Exit.cs.)

        static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float s)
        {
            float r = 1f - s;
            return r * r * r * a + 3f * r * r * s * b + 3f * r * s * s * c + s * s * s * d;
        }

        static Vector3 BezierTangent(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float s)
        {
            float r = 1f - s;
            return 3f * r * r * (b - a) + 6f * r * s * (c - b) + 3f * s * s * (d - c);
        }

        // ───────────────────────────── building ─────────────────────────────

        void DropCars()
        {
            foreach (var c in _cars) { ResetArrival(c); if (c.Root != null) Destroy(c.Root.gameObject); }
            _cars.Clear();
            DropFx();
            foreach (var m in _meshes) if (m != null) Destroy(m);
            _meshes.Clear();
            if (_carMat != null) Destroy(_carMat);
            if (_glassMat != null) Destroy(_glassMat);
            _carMat = null; _glassMat = null;
        }

        void Build(PorchSet porch)
        {
            DropCars();
            _porch = porch;
            _carMat = new Material(porch.SetMaterial) { name = "PoliceCar" };
            _carMat.SetFloat("_Ambient", CarFill);
            // The windows: tinted, see-through (Brett 0.1.33: the riders — the driver you can shoot — show through them).
            _glassMat = new Material(_carMat) { name = "PoliceCarGlass", renderQueue = 3000 };
            _glassMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One); // the shader premultiplies by _Alpha: eye-buffer alpha stays 1 (no passthrough through the glass)
            _glassMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _glassMat.SetFloat("_ZWrite", 0f);
            _glassMat.SetFloat("_Alpha", GlassAlpha);
            // Porch space: +z out to the street (road 9.35–16 m, near lane ~11 m), +x to the right looking out.
            // From well outside the 120° doorbell view (±60 m): one from the left in the near lane to the right of the path, then
            // one from the right in the far lane (clear of the first) to the left; both nose over the curb toward the house.
            // (~3.5 s in, research/22 §4.1: they're answering a shooting; both drivers get out.)
            _cars.Add(MakeCar("Police A", new Vector3(60f, 0f, 14.1f), -90f, new Vector3(-2.4f, 0f, 10.3f), -135f, 0.6f, 3.3f, 0f, CarLook.Cruiser));
            _cars.Add(MakeCar("Police B", new Vector3(-60f, 0f, 11.3f), 90f, new Vector3(4.2f, 0f, 10.6f), 130f, 0f, 3.1f, 0.19f, CarLook.Cruiser));
            // Wave 3: the SWAT truck, in from the left along the near lane, parked along the curb left of the first cruiser.
            _cars.Add(MakeTruck("SWAT truck", new Vector3(-60f, 0f, 11.8f), 90f, new Vector3(-8.8f, 0f, 11.4f), 95f, 0.3f, 4.2f, 0.37f, TruckLook.Swat));
            // (The hitmen's three come later, on a Marshal night's first send: BuildHitmanCars. A police call builds only these.)
            Triangles = 0;
            foreach (var m in _meshes) if (m != null) Triangles += (int)(m.GetIndexCount(0) / 3);
            Triangles /= Mathf.Max(1, _cars.Count);
            EnsureFx();   // the hit effects' particle systems (PoliceCars.Combat.cs), made now: no hitch on the first hit
        }

        /// <summary>
        /// A Marshal night's hitmen (research/27 §2, §6 step 6; Brett 0.1.57: five waves): one vehicle per wave at
        /// <see cref="HitmanVehicles"/>…+4, named as the tier's <see cref="HitmanTier.Vehicle"/>, built the first time one is sent
        /// (<see cref="Send"/>), not in <see cref="Build"/>: a police call's first send builds main's three and nothing more (the
        /// build frame, the porch's live renderers, FaceCamera and the combat resets all stay as main's). Each reads by its
        /// brightness and shape on the night feed — the grey sedan, the dark red one (darker on the IR picture, red through an
        /// open doorway), the black SUV, the charcoal one, the white van. Built for the left side (mirrored to the right when the
        /// doorbell's wide side is there: AimHitman), all in from that end of the street, so none crosses the doorway's line on
        /// its way in, and each parks short of the ones already there (a later wave's spot is never past an earlier one's in the
        /// same lane: its drive in is clear of every parked vehicle, so a clean arrival never meets the crash or shove code):
        ///   near lane (z ≈ 11): the grey sedan nearest the path (x −11, as before), the red sedan behind it (−17.4), the van
        ///     behind that (−24.65); far lane (z ≈ 14.4): the black SUV (−14.5, as before), the charcoal one behind it (−20.9).
        ///   Each nose ~1.2–1.35 m off the tail ahead (at their 92–97° parking yaw). The nearest corner of any of them is the grey
        ///   sedan's, 36° off the door's axis as before; the others 38° (the SUV) to 60° (the van) — farther along the street,
        ///   fewer places in the room see them through the doorway. The van, now ~27 m out, is the brightest and the biggest:
        ///   all of it stays inside the doorbell's picture (its tail ~56° off the camera's axis of 60°).
        /// Materials shared (the car fill and the glass); meshes each its own (a crash crumples its own body). ~500 tris a car,
        /// logged. <see cref="Triangles"/> stays the police cars' (SecurityTests' budget check).
        /// </summary>
        void BuildHitmanCars()
        {
            var tris = new List<string>(HitmanTier.Waves);
            void Add(int wave, System.Func<string, Car> make, float enginePitch)
            {
                int before = _meshes.Count;
                var c = Hitman(make(HitmanTier.For(wave).Vehicle));
                c.EnginePitch = enginePitch;
                _cars.Add(c);
                int n = 0;
                for (int i = before; i < _meshes.Count; i++) if (_meshes[i] != null) n += (int)(_meshes[i].GetIndexCount(0) / 3);
                tris.Add($"{c.Root.name} {n}");
            }
            // (Built in wave order: vehicle 3 + w − 1 is wave w's, as VehicleFor and each part's VehicleBody index expect.)
            Add(1, n => MakeCar(n, new Vector3(-60f, 0f, 11.3f), 90f, new Vector3(-11f, 0f, 11f), 97f, 0.3f, 3.6f, 0f, CarLook.Sedan), 1.05f);
            Add(2, n => MakeCar(n, new Vector3(-60f, 0f, 11.3f), 90f, new Vector3(-17.4f, 0f, 11f), 97f, 0.3f, 3.6f, 0f, CarLook.RedSedan), 1.0f);
            Add(3, n => MakeCar(n, new Vector3(-60f, 0f, 14.5f), 90f, new Vector3(-14.5f, 0f, 14.4f), 92f, 0.3f, 3.6f, 0f, CarLook.Suv), 0.92f);
            Add(4, n => MakeCar(n, new Vector3(-60f, 0f, 14.5f), 90f, new Vector3(-20.9f, 0f, 14.4f), 92f, 0.3f, 3.6f, 0f, CarLook.CharcoalSuv), 0.88f);
            Add(5, n => MakeTruck(n, new Vector3(-60f, 0f, 11.4f), 90f, new Vector3(-24.65f, 0f, 11.2f), 95f, 0.3f, 3.8f, 0f, TruckLook.Van), 0.85f);
            Debug.Log($"[HIS] PoliceCars: the hitmen's {HitmanTier.Waves} vehicles built (tris: {string.Join(", ", tris)})");
        }

        /// <summary>
        /// Leaving Marshal mode (ScenarioDirector.SetMode): if the hitmen's vehicles were built, every vehicle goes (as a porch
        /// change does) and the next send rebuilds main's three — police play after a Marshal night carries none of the
        /// hitmen's (Brett 2026-10-03: the normal game as main). Nothing when they were never built.
        /// </summary>
        public void DropHitmanCars()
        {
            if (_cars.Count <= HitmanVehicles) return;
            End();
            DropCars();
            Debug.Log("[HIS] PoliceCars: the hitmen's vehicles dropped (out of Marshal mode)");
        }

        Car NewVehicle(string name, Vector3 from, float fromYaw, Vector3 to, float toYaw, float delay, float duration, float phase)
        {
            var c = new Car { Delay = delay, Duration = duration, Phase = phase };
            var root = new GameObject(name) { layer = PorchSet.OutsideLayer }.transform;
            root.SetParent(_porch.transform, false);
            c.Root = root;
            c.From = from; c.FromYaw = fromYaw; c.To = to; c.ToYaw = toYaw;
            Curve(c, from, fromYaw, to, toYaw);
            return c;
        }

        /// <summary>Its drive in: out along its heading from <paramref name="from"/>, lined up 5.5 m short of where it stops.</summary>
        static void Curve(Car c, Vector3 from, float fromYaw, Vector3 to, float toYaw)
        {
            var f0 = Quaternion.Euler(0f, fromYaw, 0f) * Vector3.forward; var f1 = Quaternion.Euler(0f, toYaw, 0f) * Vector3.forward;
            float dist = Vector3.Distance(from, to);
            c.P0 = from; c.P1 = from + f0 * dist * 0.5f; c.P2 = to - f1 * 5.5f; c.P3 = to;
        }

        /// <summary>Marks a vehicle as one of a Marshal night's hitmen's (parked by <see cref="AimHitman"/>).</summary>
        static Car Hitman(Car c) { c.Hitman = true; return c; }

        Car MakeCar(string name, Vector3 from, float fromYaw, Vector3 to, float toYaw, float delay, float duration, float phase, in CarLook look)
        {
            var c = NewVehicle(name, from, fromYaw, to, toYaw, delay, duration, phase);
            var root = c.Root;
            float lift = look.Lift;
            var up = new Vector3(0f, lift, 0f);   // the SUV: body, doors, glass, lamps and seats all ride this much higher
            // Out of the driver's door, the passenger's, then behind the car.
            c.Exits = new[] { new Vector3(-1.6f, 0f, 0.5f), new Vector3(1.6f, 0f, 0.5f), new Vector3(-1.6f, 0f, -0.9f) };
            // Seated (0.1.50): heads under the roof, feet in the underbody between the wheels — raised with a taller body.
            c.Seats = new[] { new Vector3(-0.4f, SeatY + lift, SeatZ), new Vector3(0.4f, SeatY + lift, SeatZ), new Vector3(-0.3f, RearSeatY + lift, RearSeatZ) };
            c.Half = new Vector2(0.93f, 2.5f);
            c.Hood = new Vector3(0f, 0.9f + lift, 1.75f);
            c.Chips = look.Chips;
            c.HasBar = look.Police;
            c.BarBroken = !c.HasBar;
            // What rounds hit: the glass (they go through it: a crack, then whoever's behind; each pane its own collider, see
            // GreenhousePanes), the body (a hole, sparks, a ping) and the light bar (it breaks: 0.1.41).
            // (0.1.41: nose, doors and tail apart — one box over it all floated a hole 9 cm over the hood.)
            Surface(c, "Body", new Vector3(0f, 0.615f, -0.075f) + up, new Vector3(1.86f, 0.67f, 2.15f));   // the doors' section, to the belt line
            Surface(c, "Nose", new Vector3(0f, 0.56f, 1.79f) + up, new Vector3(1.86f, 0.6f, 1.58f));       // hood 0.86, push bar
            if (look.LongRoof)   // the SUV's tail runs up to the belt line under its long glass
                Surface(c, "Tail", new Vector3(0f, 0.605f, -1.85f) + up, new Vector3(1.86f, 0.69f, 1.4f));
            else
                Surface(c, "Tail", new Vector3(0f, 0.58f, -1.85f) + up, new Vector3(1.86f, 0.64f, 1.4f));
            float roofBack = look.LongRoof ? LongRoofBack : -0.95f;
            Surface(c, "Roof", new Vector3(0f, 1.48f, (roofBack + 0.45f) * 0.5f) + up, new Vector3(1.46f, 0.1f, 0.45f - roofBack));
            // A raised body leaves room for a round under it: the underbody (between the wheels, under the doors) stops it too.
            if (lift > 0f) Surface(c, "Under", new Vector3(0f, (0.04f + 0.26f + lift) * 0.5f, -0.01f), new Vector3(1.4f, 0.22f + lift, 2.34f));
            if (look.Police) Surface(c, "LightBar", new Vector3(0f, 1.575f, -0.12f), new Vector3(1.32f, 0.13f, 0.42f), LightBarPart);

            // Body, cabin, pillars and roof, wheels, lamps (the police's lit ones are emissive in the same mesh), the police kit;
            // the glass apart. All of it from the look (PoliceCars.Looks.cs, Brett 2026-10-05): a new look gets every part.
            var k = new MeshKit();
            CarBody(k, look, look.Police ? UnitNumber(_cars.Count) : null);
            Part(root, "Body", k, _carMat, readable: !look.Police);   // (a crash crumples the hitmen's; the police's as before: see Part)
            GreenhousePanes(c, 0.07f, lift, look.LongRoof);
            // The front doors (driver left, x = -0.935; passenger right): hinged at their front edges, white panels over dark
            // doorways in the body; both swing open once it stops (two officers get out).
            foreach (float side in new[] { -1f, 1f })
            {
                var hinge = new GameObject(side < 0f ? "DoorHinge" : "DoorHingeR") { layer = PorchSet.OutsideLayer }.transform;
                hinge.SetParent(root, false);
                hinge.localPosition = new Vector3(0.935f * side, 0f, 0.98f);
                var dk = new MeshKit();
                CarDoor(dk, look, side);   // skin, belt moulding, window frame, mirror, handle (and POLICE): PoliceCars.Looks.cs
                Part(hinge, "Door", dk, _carMat);
                // Its window (0.1.41: see-through glass, was a dark slab): part of that side's pane — it cracks, and shatters
                // with the side glass behind it; it swings open with the door, cracks and all. (2026-10-05: in the side glass's
                // plane, raked at the front to the A pillar: DoorGlass.)
                DoorGlass(side, lift, out var ga, out var gb, out var gc, out var gd);
                AddPane(c, hinge, side < 0f ? "DoorGlassL" : "DoorGlassR", side < 0f ? 2 : 3, ga, gb, gc, gd, new Vector3(side, 0f, 0f), 0.07f);
                c.Doors.Add((hinge, 62f * -side)); // each swings out on its own side
                var leaf = new Bounds();   // the skin's leaf (CarDoor's chamfered skin is inside it; mirrored for the right)
                leaf.SetMinMax(new Vector3(side < 0f ? -0.05f : 0f, 0.3f + lift, -1.02f), new Vector3(side < 0f ? 0f : 0.05f, 0.95f + lift, 0f));
                c.DoorBoxes.Add(leaf);
            }

            if (look.Police)
            {
                // Light bar: the lit lens halves (emissive) and their glows, toggled by the strobe.
                // (Red reads bright on a night IR camera, blue dim: a big white bloom on one side, a smaller one on the other.)
                c.RedLocal = new Vector3(-0.34f, 1.6f, -0.12f); c.BlueLocal = new Vector3(0.34f, 1.6f, -0.12f);
                c.LensRed = Lens(root, "LensRed", -1f, 1.535f, 1.64f, -0.3f, 0.06f);   // (over the bar's unlit segments: LightBar)
                c.LensBlue = Lens(root, "LensBlue", 1f, 1.535f, 1.64f, -0.3f, 0.06f);
                c.GlowRed = Glow(c, root, "GlowRed", c.RedLocal + new Vector3(0f, 0.02f, 0f), 1.9f, 1f, 0.8f);  // + a hot core: it blooms
                c.GlowBlue = Glow(c, root, "GlowBlue", c.BlueLocal + new Vector3(0f, 0.02f, 0f), 1.2f, 0.6f, 0.5f);
            }
            else c.RedLocal = c.BlueLocal = new Vector3(0f, 1.5f + lift, -0.12f);   // (no bar: only a crash's bar burst would read it, and that's skipped)
            // Headlights, their pools on the road ahead, tail lights. The police's stay lit; the hitmen's go dark once parked
            // (StepLamps), lamp faces and all — so theirs are a part of their own over unlit lenses in the body.
            var lamps = look.Police ? null : new List<Transform>();
            if (lamps != null)
            {
                var lit = new MeshKit();
                LampFaces(lit, up, 0.003f, LitHeadCol, LitTailCol);   // (over the body's unlit lenses, the same shapes)
                lamps.Add(Part(root, "Lamps", lit, _carMat));
            }
            // (The head halos 0.35 m toward the lens: clear of the bumper's end and the push bar. The tail halos red: the warm lamp
            // tint the glow gets in the headset turned grey ones peach.)
            AddLamp(lamps, Glow(c, root, "HeadGlowL", new Vector3(-0.68f, 0.68f, 2.56f) + up, 0.6f, 0.7f, toward: HeadGlowToward));
            AddLamp(lamps, Glow(c, root, "HeadGlowR", new Vector3(0.68f, 0.68f, 2.56f) + up, 0.6f, 0.7f, toward: HeadGlowToward));
            AddLamp(lamps, Glow(c, root, "TailGlowL", new Vector3(-0.7f, 0.74f, -2.52f) + up, 0.2f, TailGlowCol, toward: 0.05f));
            AddLamp(lamps, Glow(c, root, "TailGlowR", new Vector3(0.7f, 0.74f, -2.52f) + up, 0.2f, TailGlowCol, toward: 0.05f));
            var pool = new MeshKit();
            pool.GlowQuad(new Vector3(-1.7f, 0.035f, 2.4f), new Vector3(0f, 0f, 7.5f), new Vector3(3.4f, 0f, 0f), new Color(0.3f, 0.3f, 0.3f));
            AddLamp(lamps, Part(root, "HeadlightPool", pool, _porch.GlowMaterial));
            c.Lamps = lamps;

            root.gameObject.SetActive(false);
            return c;
        }

        static void AddLamp(List<Transform> lamps, Transform t) { if (lamps != null && t != null) lamps.Add(t); }

        /// <summary>How far toward the lens the head lamps' halos sit (m); the tail lamps' halo colour (red, ~0.09 luma on the IR feed).</summary>
        const float HeadGlowToward = 0.35f;
        static readonly Color TailGlowCol = new(0.34f, 0.02f, 0.012f);

        /// <summary>The SUV's roof (and glass) run back to here (car z) instead of the cruiser's −0.95: no trunk, a near-upright tailgate.</summary>
        const float LongRoofBack = -2.28f, LongBeltBack = -2.4f;

        /// <summary>
        /// The SWAT truck (wave 3): a dark armoured box truck, 6.4 × 2.3 m — cab with a raked windshield, a tall cargo box with
        /// a light stripe, a heavy push bar, a light bar on the cab roof, and double doors at the back that swing open: the
        /// team gets out behind it; the driver's cab door swings open as he climbs out (2026-10-05). The hitmen's white van (wave 5
        /// of a Marshal night) is the same body in white with none of the police kit and no lights at all; the same seats (the
        /// driver at the wheel, the rest in the box).
        /// </summary>
        Car MakeTruck(string name, Vector3 from, float fromYaw, Vector3 to, float toYaw, float delay, float duration, float phase, in TruckLook look)
        {
            var c = NewVehicle(name, from, fromYaw, to, toYaw, delay, duration, phase);
            var root = c.Root;
            c.Exits = new[] { new Vector3(-0.7f, 0f, -4.1f), new Vector3(0f, 0f, -4.4f), new Vector3(0.7f, 0f, -4.1f) };
            c.Truck = true;
            // The driver in the cab (seen through its windshield); the team in the closed cargo box, out of the back.
            c.Seats = new[] { new Vector3(-0.5f, 0.4f, 1.6f), new Vector3(-0.45f, 0.35f, -1.2f), new Vector3(0.45f, 0.35f, -1.2f), new Vector3(0f, 0.35f, -2.2f) }; // seated (0.1.48 knelt: y 0.78 / 0.5)
            c.Half = new Vector2(1.18f, 3.35f);
            c.Hood = new Vector3(0f, 1.3f, 3.05f);
            c.Chips = look.Chips;
            c.HasBar = look.Police;
            c.BarBroken = !c.HasBar;
            Surface(c, "Cab", new Vector3(0f, 0.85f, 2.3f), new Vector3(2.2f, 0.8f, 1.85f));
            Surface(c, "Box", new Vector3(0f, 1.57f, -0.78f), new Vector3(2.3f, 2.25f, 4.45f));
            Surface(c, "CabRoof", new Vector3(0f, 2.14f, 1.9f), new Vector3(2.2f, 0.08f, 0.9f));
            if (look.Police) Surface(c, "LightBar", new Vector3(0f, 2.26f, 1.95f), new Vector3(1.4f, 0.14f, 0.42f), LightBarPart);
            else Surface(c, "Under", new Vector3(0f, 0.255f, 0.1f), new Vector3(1.66f, 0.39f, 6.1f));   // its underbody (below)
            const float glass = 0.05f;
            // The cab, its glass round the driver, the cargo box, the kit: all from the look (PoliceCars.Looks.cs TruckBody,
            // Brett 2026-10-05); the glass apart.
            var k = new MeshKit();
            TruckBody(k, look);
            // The cab's glass, pane by pane (0.1.41: each cracks, and shatters, on its own): windshield, left, right. The left (the
            // driver's) is his door's window since 2026-10-05: it rides the cab door (below).
            AddPane(c, root, "Windshield", 0, new Vector3(-1.05f, 1.25f, 3.2f), new Vector3(1.05f, 1.25f, 3.2f), new Vector3(1.0f, 2.1f, 2.3f),
                    new Vector3(-1.0f, 2.1f, 2.3f), new Vector3(0f, 0.7f, 1f), glass);
            AddPane(c, root, "SideGlassR", 2, new Vector3(1.1f, 1.28f, 1.52f), new Vector3(1.1f, 1.28f, 2.25f),
                    new Vector3(1.1f, 2.08f, 2.25f), new Vector3(1.1f, 2.08f, 1.52f), Vector3.right, glass);
            Part(root, "Body", k, _carMat, readable: !look.Police);   // (the van crumples; the SWAT truck as before: see Part)
            // The rear doors: two leaves hinged at the back corners, swinging out and round.
            foreach (float side in new[] { -1f, 1f })
            {
                var hinge = new GameObject(side < 0f ? "RearDoorL" : "RearDoorR") { layer = PorchSet.OutsideLayer }.transform;
                hinge.SetParent(root, false);
                hinge.localPosition = new Vector3(1.12f * side, 0f, -3.0f);
                var dk = new MeshKit();
                float x0 = side < 0f ? 0f : -1.1f, x1 = side < 0f ? 1.1f : 0f;
                TruckDoor(dk, look, side);   // the leaf, its panel seam, small window, hinges, handle (SW | AT): PoliceCars.Looks.cs
                Part(hinge, "Door", dk, _carMat);
                c.Doors.Add((hinge, side < 0f ? 105f : -105f));
                var leaf = new Bounds(); leaf.SetMinMax(new Vector3(x0, 0.55f, -0.05f), new Vector3(x1, 2.62f, 0f));   // the leaf (TruckDoor's skin)
                c.DoorBoxes.Add(leaf);
            }
            // The driver's cab door (2026-10-05: he climbed out through the cab's side, it had none): hinged at its front edge, its
            // window the cab's left glass (pane 1: it cracks and shatters as before, and swings open with it). Door 2, after the
            // back doors (0, 1: the tests and the box team's SwingDoor go by those); the exit code finds it as the driver's own
            // side door (PoliceCars.Exit.cs SideDoor) and swings it as his hand pushes it.
            {
                const float side = -1f;
                var hinge = new GameObject("CabDoor") { layer = PorchSet.OutsideLayer }.transform;
                hinge.SetParent(root, false);
                hinge.localPosition = CabDoorHinge(side);
                var dk = new MeshKit();
                TruckCabDoor(dk, look, side);   // PoliceCars.Looks.cs
                Part(hinge, "Door", dk, _carMat);
                CabDoorGlass(side, out var ga, out var gb, out var gc, out var gd);
                AddPane(c, hinge, "SideGlassL", 1, ga, gb, gc, gd, Vector3.left, glass);
                c.Doors.Add((hinge, CabDoorAngle * -side));
                var leaf = new Bounds();   // its leaf (TruckCabDoor's skin, the hinge on its inner face): along the cab's side
                leaf.SetMinMax(new Vector3(-(CabDoorOut - CabDoorIn), CabDoorFoot, CabDoorRear - CabDoorHingeZ), new Vector3(0f, CabDoorTop, 0f));
                c.DoorBoxes.Add(leaf);
            }
            c.RedLocal = new Vector3(-0.34f, 2.28f, 1.95f); c.BlueLocal = new Vector3(0.34f, 2.28f, 1.95f);
            if (!look.Police) { root.gameObject.SetActive(false); return c; }   // the van: no bar, no lamps, no glows at all
            // The light bar's lit halves and glows; headlights and their pool; tail lights (on the rear step since 2026-10-05:
            // they were behind the closed rear doors).
            c.LensRed = Lens(root, "LensRed", -1f, 2.215f, 2.318f, 1.76f, 2.14f);
            c.LensBlue = Lens(root, "LensBlue", 1f, 2.215f, 2.318f, 1.76f, 2.14f);
            c.GlowRed = Glow(c, root, "GlowRed", c.RedLocal + new Vector3(0f, 0.02f, 0f), 1.9f, 1f, 0.8f);
            c.GlowBlue = Glow(c, root, "GlowBlue", c.BlueLocal + new Vector3(0f, 0.02f, 0f), 1.2f, 0.6f, 0.5f);
            Glow(c, root, "HeadGlowL", new Vector3(-0.78f, 0.83f, 3.22f), 0.7f, 0.7f, toward: HeadGlowToward + 0.1f);   // (past the push bar, 3.46)
            Glow(c, root, "HeadGlowR", new Vector3(0.78f, 0.83f, 3.22f), 0.7f, 0.7f, toward: HeadGlowToward + 0.1f);
            Glow(c, root, "TailGlowL", new Vector3(-0.85f, 0.39f, -3.19f), 0.22f, TailGlowCol, toward: 0.05f);
            Glow(c, root, "TailGlowR", new Vector3(0.85f, 0.39f, -3.19f), 0.22f, TailGlowCol, toward: 0.05f);
            var pool = new MeshKit();
            pool.GlowQuad(new Vector3(-1.9f, 0.035f, 3.1f), new Vector3(0f, 0f, 8f), new Vector3(3.8f, 0f, 0f), new Color(0.3f, 0.3f, 0.3f));
            Part(root, "HeadlightPool", pool, _porch.GlowMaterial);
            root.gameObject.SetActive(false);
            return c;
        }

        /// <summary>
        /// A light bar half's lit lenses (<paramref name="sign"/> −1 the red, +1 the blue), over the unlit segments
        /// <see cref="LightBar"/> built in the body between <paramref name="y0"/>–<paramref name="y1"/> and
        /// <paramref name="z0"/>–<paramref name="z1"/> (Brett 2026-10-05: segmented, red and blue; were one white box each).
        /// </summary>
        Transform Lens(Transform root, string name, float sign, float y0, float y1, float z0, float z1)
        {
            var k = new MeshKit();
            LensSegments(k, sign, y0, y1, z0, z1);
            var t = Part(root, name, k, _porch.SetMaterial);
            t.gameObject.SetActive(false);
            return t;
        }

        Transform Glow(Car c, Transform root, string name, Vector3 local, float radius, float grey, float core = 0f, float toward = 0f) =>
            Glow(c, root, name, local, radius, new Color(grey, grey, grey), core, toward);

        /// <param name="toward">
        /// The sprite sits this far (m) toward the lens from its lamp — in the mesh, along the +z FaceCamera turns to the lens, so no
        /// per-frame cost. The head lamps' halos (2026-10-05): at the lamp, half of a 0.6 m sprite turned to a camera off to the side
        /// went behind the bumper's end and the wrapped lamp corner — a hard black block in the halo.
        /// </param>
        Transform Glow(Car c, Transform root, string name, Vector3 local, float radius, Color col, float core = 0f, float toward = 0f)
        {
            var k = new MeshKit();
            k.GlowQuad(new Vector3(-radius, -radius, toward), new Vector3(2f * radius, 0f, 0f), new Vector3(0f, 2f * radius, 0f), col); // faces +z: turned to the lens
            if (core > 0f) k.GlowQuad(new Vector3(-core, -core, toward - 0.01f), new Vector3(2f * core, 0f, 0f), new Vector3(0f, 2f * core, 0f), Color.white);
            var t = Part(root, name, k, _porch.GlowMaterial);
            t.localPosition = local;
            c.Facing.Add(t);
            return t;
        }

        /// <param name="readable">
        /// Keep the mesh's vertices on the CPU (a vehicle's body: a crash's Crumple bends them). A ToMesh mesh is upload-only:
        /// every crash since 0.1.43 logged "Not allowed to access vertices on mesh 'Police A Body'" (with a stack trace, on the
        /// crash frame and at every reset) and the front never caved in. Only the hitmen's bodies are readable for now
        /// (research/27: the police must not change in the Marshal work): theirs go dark at the impact (StepLamps), while a
        /// police car's lit head glows would float ~0.3 m in front of a crumpled nose. Fixing the police crumple = readable for
        /// them too, plus hiding their head glows on a crash.
        /// </param>
        Transform Part(Transform parent, string name, MeshKit k, Material mat, bool readable = false)
        {
            var go = new GameObject(name) { layer = PorchSet.OutsideLayer };
            go.transform.SetParent(parent, false);
            Mesh mesh;
            if (readable) { mesh = new Mesh { name = $"{parent.name} {name}" }; k.Apply(mesh); }
            else mesh = k.ToMesh($"{parent.name} {name}");
            _meshes.Add(mesh);
            LiveRenderer(go, mesh, mat);
            return go.transform;
        }

        /// <summary>A plain renderer for <paramref name="mesh"/> on <paramref name="go"/>, switched with the porch set (<see cref="PorchSet.AddLive"/>).</summary>
        void LiveRenderer(GameObject go, Mesh mesh, Material mat)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.allowOcclusionWhenDynamic = false;
            _porch.AddLive(r);
        }

        /// <summary>
        /// The cabin's glass: a prism from the belt line (z -1.35…1.05) to the roof (z -0.95…0.42), narrowing a little — four
        /// panes, each its own part and collider (0.1.41: a pane cracks, and shatters, on its own): windshield 0, rear 1, left 2,
        /// right 3. The side glass's collider covers only its rear 10–57%: the front door's window (the same pane) is in front
        /// of the rest. <paramref name="lift"/> raises it with a taller body; <paramref name="longRoof"/> runs it back over the
        /// SUV's cargo space to a near-upright tailgate glass (its side glass's collider then the rear 2–70%, the same stretch
        /// behind the door's window).
        /// </summary>
        void GreenhousePanes(Car c, float glass, float lift = 0f, bool longRoof = false)
        {
            float belt = 0.95f + lift, top = 1.42f + lift, back = longRoof ? LongBeltBack : -1.35f, roofBack = longRoof ? LongRoofBack : -0.95f;
            Vector3 b0 = new(-0.8f, belt, back), b1 = new(0.8f, belt, back), b2 = new(0.8f, belt, 1.05f), b3 = new(-0.8f, belt, 1.05f);
            Vector3 t0 = new(-0.72f, top, roofBack), t1 = new(0.72f, top, roofBack), t2 = new(0.72f, top, 0.42f), t3 = new(-0.72f, top, 0.42f);
            // The door's window starts at car z 0.03: the side glass's collider stops there, a share of the belt line's length.
            float u0 = longRoof ? 0.02f : 0.1f, u1 = longRoof ? (0.03f - back) / (1.05f - back) : 0.575f;
            AddPane(c, c.Root, "Windshield", 0, b3, b2, t2, t3, new Vector3(0f, 0.6f, 1f), glass);
            AddPane(c, c.Root, "RearWindow", 1, b0, b1, t1, t0, new Vector3(0f, longRoof ? 0.15f : 0.6f, -1f), glass);
            AddPane(c, c.Root, "SideGlassL", 2, b0, b3, t3, t0, Vector3.left, glass, u0, u1);
            AddPane(c, c.Root, "SideGlassR", 3, b1, b2, t2, t1, Vector3.right, glass, u0, u1);
        }

        static void Face(MeshKit k, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 col, Vector3 outward)
        {
            TriOut(k, a, b, c, col, outward);
            TriOut(k, a, c, d, col, outward);
        }

        static void TriOut(MeshKit k, Vector3 a, Vector3 b, Vector3 c, Color32 col, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) (b, c) = (c, b);
            k.Tri(a, b, c, col);
        }

        // ───────────────────────────── looks ─────────────────────────────

        /// <summary>
        /// How a car-bodied vehicle is dressed: the police cruiser, or a Marshal night's hitman sedans and SUVs (research/27 §2:
        /// each reads by brightness and shape on the night feed — grey sedan, dark red sedan, black SUV, charcoal SUV, and the
        /// white van; the car fill, <see cref="CarFill"/>, is all that lights them once their lamps die, with no light bar
        /// flashing on them).
        /// </summary>
        readonly struct CarLook
        {
            /// <summary>Body grey (one paint all over); below 0: the cruiser's black nose and tail, white doors and roof.</summary>
            public readonly float Paint;
            /// <summary>Bumpers (and the SUV's roof rails).</summary>
            public readonly float Trim;
            /// <summary>Paint chips' grey when a round hits it (Puff).</summary>
            public readonly float Chips;
            /// <summary>Everything above the wheels this much higher (body, doors, glass, lamps, seats), on wheels of radius <see cref="WheelR"/>.</summary>
            public readonly float Lift, WheelR;
            /// <summary>The police kit: light bar, push bar, lamps lit the whole call.</summary>
            public readonly bool Police;
            /// <summary>The roof and glass run back over the cargo space to a near-upright tailgate (the SUV), not the cruiser's trunk.</summary>
            public readonly bool LongRoof;
            /// <summary>
            /// The paint's colour: <see cref="Paint"/>'s grey × this (white: plain grey, every look before 0.1.58 — their vertex
            /// colours unchanged). The red sedan's: the night feed shows its luma (a dark car), the headset's colour look red.
            /// </summary>
            public readonly Color Tint;

            CarLook(float paint, float trim, float chips, float lift, float wheelR, bool police, bool longRoof, Color? tint = null)
            {
                Paint = paint; Trim = trim; Chips = chips; Lift = lift; WheelR = wheelR; Police = police; LongRoof = longRoof;
                Tint = tint ?? Color.white;
            }

            /// <summary>The paint at <paramref name="grey"/> in this look's colour (a vertex colour, plain pattern).</summary>
            public Color32 PaintAt(float grey) => Tinted(grey, Tint);

            /// <summary>The black-and-white (unchanged since 0.1.41).</summary>
            public static readonly CarLook Cruiser = new(-1f, 0.14f, 0.34f, 0f, 0.34f, true, false);
            /// <summary>Wave 1, street muscle: a plain grey sedan (unchanged).</summary>
            public static readonly CarLook Sedan = new(0.45f, 0.14f, 0.4f, 0f, 0.34f, false, false);
            /// <summary>
            /// Wave 3, professionals (wave 2 before 0.1.58): a black SUV, 0.25 m taller on 0.42 m wheels (roof 1.72 m), its seats raised with the body —
            /// heads still under the roof, feet in the underbody. Does it read on the feed with no bar flashing on it? Its paint
            /// under the car fill comes to ~0.011 (+ ~0.01 of IR at 15 m), the asphalt round it ~0.004, the sky's town glow behind
            /// it 0.064: a dark shape, lighter than the road and darker than the sky, and the light trim (bumpers, roof rails,
            /// ~0.03) outlines it. The grey sedan comes to ~0.04, the white van ~0.07: black, grey, white.
            /// </summary>
            public static readonly CarLook Suv = new(0.12f, 0.35f, 0.16f, 0.25f, 0.42f, false, true);
            /// <summary>
            /// Wave 2, the street crew (Brett 0.1.57): the sedan's body in a dark red, (0.42, 0.07, 0.06) — on the IR feed its luma
            /// is ~0.14 against the grey sedan's 0.45 and the black SUV's 0.12 (a dark sedan), through an open doorway it's red.
            /// </summary>
            public static readonly CarLook RedSedan = new(0.42f, 0.14f, 0.22f, 0f, 0.34f, false, false, new Color(1f, 0.17f, 0.14f));
            /// <summary>Wave 4, the heavier pros (Brett 0.1.57): the SUV's body in charcoal grey (0.26: between the black SUV and the grey sedan).</summary>
            public static readonly CarLook CharcoalSuv = new(0.26f, 0.35f, 0.28f, 0.25f, 0.42f, false, true);
        }

        /// <summary>
        /// <paramref name="grey"/> × <paramref name="tint"/> as a plain vertex colour. White: exactly <see cref="MeshKit.Grey"/>
        /// (the same bytes as every float-grey box and quad), so the police cars and the first three hitman looks are unchanged.
        /// </summary>
        static Color32 Tinted(float grey, Color tint)
        {
            if (tint == Color.white) return MeshKit.Grey(grey);
            var c = (Color32)new Color(grey * tint.r, grey * tint.g, grey * tint.b);
            c.a = 0;   // MeshKit's pattern id in alpha: 0 = plain
            return c;
        }

        /// <summary>How a truck-bodied vehicle is dressed: the SWAT truck, or the hitmen's white van (research/27 wave 3).</summary>
        readonly struct TruckLook
        {
            /// <summary>Cab, roof and doors grey; the cargo box is that × <see cref="BoxShade"/>.</summary>
            public readonly float Paint, BoxShade;
            /// <summary>Paint chips' grey when a round hits it (Puff).</summary>
            public readonly float Chips;
            /// <summary>The police kit: light bar, push bar, side stripes, lamps lit the whole call. Without it: no lamps at all.</summary>
            public readonly bool Police;
            /// <summary>A dark underbody between the wheels hiding the seated riders' feet under the cab and the box.</summary>
            public readonly bool Underbody;

            TruckLook(float paint, float boxShade, float chips, bool police, bool underbody)
            {
                Paint = paint; BoxShade = boxShade; Chips = chips; Police = police; Underbody = underbody;
            }

            /// <summary>The dark armoured SWAT truck (unchanged).</summary>
            public static readonly TruckLook Swat = new(0.08f, 0.9f, 0.16f, true, false);
            /// <summary>Wave 5, operators (wave 3 before 0.1.58): a white van, no lights — the brightest of the five on the feed.</summary>
            public static readonly TruckLook Van = new(0.8f, 0.95f, 0.5f, false, true);
        }
    }
}
