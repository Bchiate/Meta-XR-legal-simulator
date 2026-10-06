using HIS.Core;
using HIS.Room;
using UnityEngine;

namespace HIS.Security
{
    /// <summary>
    /// A Marshal night's hitman car arriving, heard and seen before the men are at the door (Brett 0.1.56: "the bad guys seemingly
    /// come from no where"; research/27 §2 had them silent but for one car-door clunk). Sound and one monitor label only — no
    /// light, no headlight sweep, nothing new drawn (GPU: research/27 §6):
    ///  • its engine pulling up ("car.arrive", synthesized: scripts/prepare_sfx.py synth_car_arrive), heard from the car's real
    ///    direction but never farther than <see cref="EngineReach"/> from the front door (out at the car, 11–21 m off, its rolloff
    ///    would leave it inaudible), from the call to the key off with its lights (<see cref="LightsDie"/> after the stop);
    ///  • a car door for each live rider as his door swings open ("outcome.cardoor"), through the front of the house (dull while
    ///    the front door is shut);
    ///  • the doorbell camera's alert the moment the car is in its picture: the monitor chimes and flashes "VEHICLE"
    ///    (<see cref="SecurityCamera.VehicleInView"/>).
    /// Ricky calls the car (Intruder.ProtecteeBrain) and the men's footsteps come up the path (IntruderBrains.TrackFacts). Only
    /// the hitmen's vehicles (<see cref="Car.Hitman"/>): a police call's cruisers and truck sound exactly as before.
    /// </summary>
    public partial class PoliceCars
    {
        /// <summary>The engine clip (SfxLibrary key).</summary>
        public const string EngineKey = "car.arrive";
        /// <summary>The engine is heard toward the car, at most this far from the front door's centre.</summary>
        const float EngineReach = 6f;
        /// <summary>A car door's clunk toward the car, at most this far from the front door's centre.</summary>
        const float CarDoorReach = 5f;
        /// <summary>
        /// A door's clunk plays once it's this far into its own swing (Brett 2026-10-05: each door swings when its rider pushes it,
        /// one after another — PoliceCars.Exit.cs; the 0.2-per-door stagger of the doors' one shared swing is gone with it).
        /// </summary>
        const float DoorHeardAt = 0.15f;
        /// <summary>The engine's fade as it's switched off with the lights (and on a wreck or a clear).</summary>
        const float EngineOffFade = 0.3f;

        /// <summary>The hitman vehicle <paramref name="c"/> (sent as vehicle <paramref name="v"/>) starts its drive in: the engine.</summary>
        void BeginArrival(Car c, int v)
        {
            ResetArrival(c);
            int wave = v - HitmanVehicles + 1;
            if (c.EngineAt == null)
            {
                c.EngineAt = new GameObject("Engine").transform;
                c.EngineAt.SetParent(c.Root, false);                   // (goes with the car when the cars are dropped)
            }
            PlaceEngine(c);
            c.Engine = Sfx.PlayFollow(EngineKey, c.EngineAt, 0.9f);
            // Each its own engine (Car.EnginePitch, BuildHitmanCars): the grey sedan a touch higher, the red one plain, the SUVs
            // lower, the van lowest.
            if (c.Engine != 0) Sfx.ScalePitch(c.Engine, c.EnginePitch);
            Debug.Log($"[HIS] PoliceCars: {c.Root.name} engine (wave {wave}){(c.Engine == 0 ? $" — not heard (no '{EngineKey}' clip)" : "")}");
        }

        /// <summary>Each frame for a sent vehicle: the engine follows the car and dies with its lights; the camera's VEHICLE alert.</summary>
        void StepArrival(Car c)
        {
            if (!c.Hitman) return;
            if (c.Engine != 0)
            {
                PlaceEngine(c);
                bool off = c.Wrecked || (Stopped(c) && _t >= StoppedAt(c) + LightsDie);
                if (off || !Sfx.IsPlaying(c.Engine)) StopEngine(c, EngineOffFade);
            }
            if (!c.Spotted && _porch != null && _porch.Door != null)
            {
                var cam = SecurityCamera.For(_porch.Door);
                if (cam != null && cam.Sees(c.Root.position + Vector3.up * 0.8f))
                {
                    c.Spotted = true;
                    cam.VehicleInView();
                }
            }
        }

        /// <summary>Door <paramref name="i"/> of hitman vehicle <paramref name="c"/> is <paramref name="open"/> (0–1) into its own swing: once, the clunk.</summary>
        void DoorHeard(Car c, int i, float open)
        {
            if (!c.Hitman || i >= 31 || (c.DoorsHeard & (1 << i)) != 0 || open < DoorHeardAt) return;
            c.DoorsHeard |= 1 << i;
            const string key = "outcome.cardoor";
            Debug.Log($"[HIS] PoliceCars: {c.Root.name} door {i}");
            if (!Sfx.Has(key)) return;
            var hinge = c.Doors[i].Hinge;
            var at = hinge != null ? hinge.position : c.Root.position;
            var door = _porch != null ? _porch.Door : VirtualDoor.Instance;
            if (door == null) { Sfx.Play(key, at, 1f); return; }
            door.PlayThrough(key, door.Center + Vector3.ClampMagnitude(at - door.Center, CarDoorReach), 1f, Random.Range(0.94f, 1.04f), door.IsOpen ? 0f : 2200f);
        }

        /// <summary>The engine's point: toward the car from the front door, no farther than <see cref="EngineReach"/>.</summary>
        void PlaceEngine(Car c)
        {
            if (c.EngineAt == null) return;
            var door = _porch != null ? _porch.Door : VirtualDoor.Instance;
            var car = c.Root.position + Vector3.up * 0.6f;
            c.EngineAt.position = door != null ? door.Center + Vector3.ClampMagnitude(car - door.Center, EngineReach) : car;
        }

        static void StopEngine(Car c, float fade)
        {
            if (c.Engine == 0) return;
            Sfx.Stop(c.Engine, fade);
            c.Engine = 0;
        }

        /// <summary>A vehicle's arrival cues off and forgotten (a new send, the call cleared, the cars dropped).</summary>
        static void ResetArrival(Car c)
        {
            StopEngine(c, EngineOffFade);
            c.Spotted = false;
            c.DoorsHeard = 0;
            ResetExits(c);   // every door shut, nobody out of one yet (PoliceCars.Exit.cs)
        }

        // ───────────────────────────── tests (ScenarioTests.CarCover: the five waves' vehicles) ─────────────────────────────

        /// <summary>Tests: one vehicle as it stands now (<see cref="VehicleNow"/>).</summary>
        public struct VehicleInfo
        {
            public string Name;
            /// <summary>A hitman's; out this call (sent and shown); stopped where it parks (or crashed to a stop); its driver shot on the way in.</summary>
            public bool Hitman, Out, Parked, Crashed;
            /// <summary>The doorbell camera's VEHICLE alert was raised for it this send; its head and tail lights are on (never: the van).</summary>
            public bool Spotted, LampsLit;
            /// <summary>Anything a crash did to it as it stood: shove (m and m/s), slew (° and °/s), the rock. 0: untouched.</summary>
            public float Shoved;
            /// <summary>Its footprint's four corners (world, on the porch floor), and its root (world; the transform: its colliders are under it).</summary>
            public Vector3[] Corners;
            public Vector3 Root;
            public Transform RootTransform;
            /// <summary>The side of the path it parked on (porch x sign; 0: the police's fixed spots).</summary>
            public float Side;
        }

        /// <summary>Tests: vehicle <paramref name="v"/> (VehicleFor's index) as it stands now. False: no such vehicle (not built).</summary>
        public bool VehicleNow(int v, out VehicleInfo info)
        {
            info = default;
            if (v < 0 || v >= _cars.Count || _cars[v].Root == null) return false;
            var c = _cars[v];
            info = new VehicleInfo
            {
                Name = c.Root.name, Hitman = c.Hitman, Out = Live(c), Parked = Stopped(c), Crashed = c.Crashed,
                Spotted = c.Spotted, LampsLit = c.Lamps != null && c.LampsOn,
                Shoved = c.Shove.magnitude + c.ShoveVel.magnitude + Mathf.Abs(c.ShoveYaw) + Mathf.Abs(c.ShoveYawVel) + c.CrashJolt,
                Corners = Corners(c, 0f), Root = c.Root.position, RootTransform = c.Root, Side = c.Side,
            };
            return true;
        }

        /// <summary>
        /// Tests: the name of another vehicle out now whose footprint overlaps vehicle <paramref name="v"/>'s where it is this
        /// moment (on its drive in, or parked), either way round (<see cref="Overlaps"/>, porch space); null: clear of them all.
        /// </summary>
        public string ClashWith(int v)
        {
            if (v < 0 || v >= _cars.Count) return null;
            var c = _cars[v];
            if (!Live(c)) return null;
            foreach (var o in _cars)
            {
                if (o == c || !Live(o)) continue;
                if (Overlaps(c, c.Root.localPosition, FlatForward(c), o) || Overlaps(o, o.Root.localPosition, FlatForward(o), c)) return o.Root.name;
            }
            return null;
        }

        /// <summary>Its heading on the porch floor (porch space).</summary>
        static Vector3 FlatForward(Car c)
        {
            var f = c.Root.localRotation * Vector3.forward;
            f.y = 0f;
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }
    }
}
