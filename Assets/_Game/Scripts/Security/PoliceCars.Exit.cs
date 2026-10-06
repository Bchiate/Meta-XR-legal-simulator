using HIS.Scenario;
using UnityEngine;
using IntruderT = HIS.Intruder.Intruder;

namespace HIS.Security
{
    /// <summary>
    /// Getting out of the vehicles, the car's half (Brett 2026-10-05: "have the flow of cops exiting look better" — the riders
    /// teleported from the seat to standing outside, every door swinging open at once 0.8 s after the stop). The rider's half is
    /// IntruderCarExit.cs (the Mixamo "Exiting Car" clip from his seat). Police and a Marshal night's hitmen alike, every body
    /// built by MakeCar / MakeTruck (a new CarLook gets it for free: it all keys off the doors, seats and exits those build):
    ///  • Turns (<see cref="GetOut"/>, <see cref="TurnAt"/>): once it has stopped the driver's comes first (0.15 s: his hand is on
    ///    the door ~0.5 s after the stop), the front passenger's 0.3 s later, a back-seat man's once the man whose door he shares
    ///    is clear of it; the truck's / van's box team one after another out of the back.
    ///  • Each door swings on its own when its rider's hand pushes it (<see cref="SwingDoor"/>: the clip's door moment), its clunk
    ///    on that swing (the hitmen's: DoorHeard). A rider shot dead in his seat before his turn never opens his: it stays shut
    ///    (as before). Nobody shuts them again: the police use them as cover.
    ///  • An empty vehicle (the bad-call show's cruisers, a test's) opens its doors on its own, in turn, as before.
    ///  • The plan (<see cref="TryGetExit"/>): which door and side, where his hips line up with the door's opening (the front seats
    ///    sit in it since 2026-10-05; a back-seat man slides up to it, by the side of the front seat's back, while his legs are still
    ///    in the footwell), where he ends up.
    /// Transform and float work only: nothing new drawn, no allocation after a vehicle's first stop.
    /// </summary>
    public partial class PoliceCars
    {
        partial class Car
        {
            /// <summary>Per door (<see cref="Doors"/> order): when it began to swing open (this call's clock), −1 shut; its swing (s).</summary>
            public float[] DoorAt, DoorFor;
            /// <summary>Per door: when the man whose own door it is was clear of it (the clock), −1 not yet — the back seat goes then.</summary>
            public float[] DoorClear;
        }

        // Turns, s after it stops (his clip starts then; his hand reaches the door ExitDoorTouch / his rate later).
        const float DriverTurn = 0.15f, PassengerTurn = 0.45f, BackSeatTurn = 0.75f, BackSeatGap = 0.1f, TurnCap = 6f;
        /// <summary>The box team: the first out at this after the stop, the rest this far apart; the second back leaf this after the first.</summary>
        const float CargoTurn = 0.6f, CargoStep = 0.55f, RearDoorsLag = 0.2f;
        /// <summary>An empty vehicle's doors: the first this long after the stop, the next this much later, each swinging this long.</summary>
        const float EmptyDoorFirst = 0.5f, EmptyDoorStep = 0.3f, EmptyDoorSwing = 0.7f;
        /// <summary>A side door is a seat's own when its opening is within this of him along the car (m): the front seats' (in their
        /// opening since 2026-10-05: 0; the back seat 0.58 m behind it — it was 0.6, from when the front seats sat 0.46 m behind it).</summary>
        const float OwnDoorGap = 0.3f;
        /// <summary>
        /// His hips line up this far inside the opening's rear edge (his thigh, ~0.15 m either side of them, clears it as his legs
        /// come out), and at least this far from its front (the dash).
        /// </summary>
        const float DoorInset = 0.15f, DoorFrontKeep = 0.35f;
        /// <summary>
        /// A back-seat man goes by the front seat's back on the door's side first, this far out from the middle (m): his shoulders
        /// (~0.23 m either side of him) just outside the seat back's outer edge (SeatBackOut 0.45 since 2026-10-05: it ran to 0.64,
        /// and he went through it), level with its middle — behind the B pillar from outside.
        /// </summary>
        const float BackSeatOutboard = 0.68f, SeatBackBehind = 0.24f;
        /// <summary>Where a front-seat man stands once out: this far out from the door's hinge line, this far inside its rear edge — the
        /// pocket between the open door and the body (the cruiser's: (∓1.6, 0.11), 0.47 m from the open leaf).</summary>
        const float StandOut = 0.665f, StandInset = 0.15f;
        /// <summary>No door on his side (a vehicle built with none: the trucks' cabs had none before their driver's door, 2026-10-05): out a little
        /// ahead of his seat; he stands this far out and ahead.</summary>
        const float DoorlessAhead = 0.2f, DoorlessStandOut = 0.52f, DoorlessStandAhead = 0.6f;
        /// <summary>The box team (m): standing ready in the back doorway's recess when its doors swing open (StandUpInTheBox: up
        /// from their seats behind the shut doors), on its floor (BoxDoorwayFloor) — the front two this far in from the back face
        /// (their guns short of the doors' inside), the third this far, between and behind them; each walks out to its lip, this far
        /// behind the face (over the rear step's edge), and steps down off it; he lands this far behind the face; lanes no wider
        /// than this.</summary>
        const float CargoFront = 0.55f, CargoSecond = 0.95f, CargoLip = 0.02f, CargoHop = 0.55f, CargoLane = 0.75f;

        /// <summary>How a rider gets out: a side door (the exit clip: the cars' front doors, the trucks' cab door), a side with no door (the clip), the back of the box.</summary>
        public enum ExitWay { Door, Side, Back }

        /// <summary>One rider's way out, in his vehicle's space (x right, y up, z forward; the road at y 0).</summary>
        public struct ExitPlan
        {
            public Transform Car;
            public ExitWay Way;
            /// <summary>Out to his right (the passenger's side: the mirrored clip).</summary>
            public bool Right;
            /// <summary>The door he opens (Doors index; the box team: the first back leaf), −1 none.</summary>
            public int Door;
            /// <summary>His seat (Board's: his root, the feet below the floor).</summary>
            public Vector3 Seat;
            /// <summary>
            /// Door/Side: where his root lines up with the opening before his legs come out (only x/z count); a back-seat man goes by
            /// <see cref="Via"/> first (<see cref="HasVia"/>). Back: the doorway's lip he steps down from; Via where he stands ready
            /// in the doorway's recess.
            /// </summary>
            public Vector3 Sill, Via;
            public bool HasVia;
            /// <summary>Back: where his hop lands (on the road).</summary>
            public Vector3 Land;
            /// <summary>Where he ends up standing (on the road): what <see cref="GetOut"/> gives as his exit.</summary>
            public Vector3 Stand;
        }

        /// <summary>
        /// How <paramref name="seat"/> of wave <paramref name="wave"/>'s vehicle gets out (<see cref="ExitPlan"/>). False: no such
        /// vehicle out, or no such seat.
        /// </summary>
        /// <param name="faction">The rider's own (<see cref="VehicleOf"/>); null: the run's now.</param>
        public static bool TryGetExit(int wave, int seat, out ExitPlan plan, SquadFaction? faction = null)
        {
            plan = default;
            var c = SentVehicle(wave, faction);
            return c != null && Instance.Plan(c, seat, out plan);
        }

        /// <summary>
        /// <paramref name="seat"/>'s hand is on his door (the exit clip's door moment): it swings open over <paramref name="seconds"/>
        /// (already swinging: nothing). The box team's first man opens both back leaves, the second <see cref="RearDoorsLag"/> later.
        /// </summary>
        public static void SwingDoor(int wave, int seat, float seconds, SquadFaction? faction = null)
        {
            var c = SentVehicle(wave, faction);
            if (c == null || !Instance.Plan(c, seat, out var p)) return;
            if (p.Way == ExitWay.Back)
            {
                int k = 0;
                bool shut = true;
                for (int i = 0; i < c.Doors.Count; i++) if (!IsSideDoor(c, i) && c.DoorAt != null && i < c.DoorAt.Length && c.DoorAt[i] >= 0f) shut = false;
                for (int i = 0; i < c.Doors.Count; i++) if (!IsSideDoor(c, i)) Instance.Swing(c, i, seconds, RearDoorsLag * k++);
                // As they start to open, the rest of the box team stand up ready in the doorway (still behind the shut leaves): the
                // doors show the team there, and each walks out at his turn (2026-10-05: one at a time out of the dark box's wall).
                if (shut)
                    for (int j = 1; j < c.Riders.Count; j++)
                        if (j != seat && c.Riders[j] != null) c.Riders[j].StandUpInTheBox();
                return;
            }
            if (p.Door >= 0) Instance.Swing(c, p.Door, seconds, 0f);
        }

        /// <summary><paramref name="seat"/> is out and clear of his door's line: a back-seat man sharing it may follow.</summary>
        public static void ClearedDoor(int wave, int seat, SquadFaction? faction = null)
        {
            var pc = Instance;
            var c = SentVehicle(wave, faction);
            if (c == null || !pc.Plan(c, seat, out var p) || p.Way != ExitWay.Door || p.Door < 0) return;
            if (c.DoorClear[p.Door] < 0f) c.DoorClear[p.Door] = pc._t;
        }

        /// <summary>Wave <paramref name="wave"/>'s vehicle, out this call (sent, built); null otherwise.</summary>
        static Car SentVehicle(int wave, SquadFaction? faction)
        {
            var pc = Instance;
            if (pc == null || !pc.Active) return null;
            int v = VehicleOf(wave, faction);
            if (v < 0 || v >= pc._cars.Count) return null;
            var c = pc._cars[v];
            return c.Sent && c.Root != null ? c : null;
        }

        void Swing(Car c, int door, float seconds, float after)
        {
            EnsureExitState(c);
            if (door < 0 || door >= c.DoorAt.Length || c.DoorAt[door] >= 0f) return;
            c.DoorAt[door] = _t + after;
            c.DoorFor[door] = Mathf.Max(0.1f, seconds);
        }

        /// <summary>
        /// Each door at its own swing (<see cref="SwingDoor"/>): shut until its rider's hand is on it — a rider shot dead in his seat
        /// before that never opens it (as before). An empty vehicle's open on their own, one after another (the bad-call show).
        /// The hitmen's clunk on each door's own swing (PoliceCars.Arrival.cs). <paramref name="sinceStop"/> &lt; 0: not stopped.
        /// </summary>
        void OpenDoors(Car c, float sinceStop)
        {
            if (c.Doors.Count == 0) return;
            EnsureExitState(c);
            bool anyone = false;
            for (int i = 0; i < c.Riders.Count && !anyone; i++) anyone = c.Riders[i] != null;
            for (int i = 0; i < c.Doors.Count; i++)
            {
                var (hinge, angle) = c.Doors[i];
                if (hinge == null) continue;
                if (!anyone && c.DoorAt[i] < 0f && sinceStop >= EmptyDoorFirst + EmptyDoorStep * i) { c.DoorAt[i] = _t; c.DoorFor[i] = EmptyDoorSwing; }
                float open = c.DoorAt[i] < 0f ? 0f : Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_t - c.DoorAt[i]) / c.DoorFor[i]));
                hinge.localRotation = Quaternion.Euler(0f, angle * open, 0f);
                if (open > 0f && anyone) DoorHeard(c, i, open);   // (the hitmen's only: a car door for each rider getting out)
            }
        }

        /// <summary>
        /// When <paramref name="seat"/>'s turn to get out comes (this call's clock): see the class summary. The hitmen's turns come
        /// once their lights have died (<see cref="LightsDie"/> after the stop: "a black SUV parks; its lights die", then the doors).
        /// </summary>
        float TurnAt(Car c, int seat)
        {
            float stop = StoppedAt(c) + (c.Hitman && !c.Wrecked ? LightsDie : 0f);
            if (c.Seats == null || seat < 0 || seat >= c.Seats.Length) return stop + PassengerTurn + 0.3f * Mathf.Max(0, seat - 1);
            if (c.Truck && seat >= 1) return stop + CargoTurn + CargoStep * (seat - 1);
            int door = SideDoor(c, seat, out bool own, out int owner);
            if (door < 0 || own) return stop + (seat == 0 ? DriverTurn : PassengerTurn + 0.3f * (seat - 1));
            // A back-seat man: after the man whose door it is (he's out of the way of it), or at once if there's nobody to wait for.
            EnsureExitState(c);
            if (c.DoorClear[door] >= 0f) return Mathf.Max(stop + BackSeatTurn, c.DoorClear[door] + BackSeatGap);
            var o = owner >= 0 && owner < c.Riders.Count ? c.Riders[owner] : null;
            bool wait = o != null && o.Current != IntruderT.State.Dead && o.Current != IntruderT.State.Gone;
            return stop + (wait ? TurnCap : BackSeatTurn);
        }

        /// <summary>The way out of <paramref name="seat"/> (<see cref="ExitPlan"/>), worked out from the doors, seats and exits as built.</summary>
        bool Plan(Car c, int seat, out ExitPlan p)
        {
            p = default;
            if (c.Root == null || c.Seats == null || c.Exits == null || c.Exits.Length == 0 || seat < 0 || seat >= c.Seats.Length) return false;
            EnsureExitState(c);
            var s = c.Seats[seat];
            p.Car = c.Root; p.Seat = s; p.Door = -1; p.Via = s; p.Sill = s;
            if (c.Truck && seat >= 1)
            {
                // The box team: up on his feet ready in the back doorway's recess (StandUpInTheBox, as its doors swing open), out at a
                // walk to its lip at his turn, a step down onto the road behind it, and on to the exit behind the truck as before.
                float back = BackFace(c), floor = BoxDoorwayFloor, x = Mathf.Clamp(s.x, -CargoLane, CargoLane);
                p.Way = ExitWay.Back;
                p.Via = new Vector3(x, floor, back + (seat <= 2 ? CargoFront : CargoSecond));
                p.Sill = new Vector3(x, floor, back - CargoLip);
                p.Land = new Vector3(x, 0f, back - CargoHop);
                p.Stand = c.Exits[(seat - 1) % c.Exits.Length];
                for (int i = 0; i < c.Doors.Count && p.Door < 0; i++) if (!IsSideDoor(c, i)) p.Door = i;
                return true;
            }
            float side = s.x < 0f ? -1f : 1f;
            int door = SideDoor(c, seat, out bool own, out int owner);
            if (door < 0)
            {
                // No door on his side (none built today; the trucks' cabs had none before 2026-10-05): out of the side as if there were one.
                p.Way = ExitWay.Side; p.Right = side > 0f;
                p.Sill = new Vector3(s.x, s.y, s.z + DoorlessAhead);
                p.Stand = new Vector3(side * (c.Half.x + DoorlessStandOut), 0f, s.z + DoorlessStandAhead);
                return true;
            }
            var hinge = c.Doors[door].Hinge;
            var leaf = door < c.DoorBoxes.Count ? c.DoorBoxes[door] : new Bounds(new Vector3(0f, 0f, -0.5f), new Vector3(0.05f, 0.6f, 1f));
            float dside = hinge.localPosition.x < 0f ? -1f : 1f;
            float rear = hinge.localPosition.z + leaf.min.z, front = hinge.localPosition.z + leaf.max.z;
            p.Way = ExitWay.Door; p.Door = door; p.Right = dside > 0f;
            // Lined up with the opening's rear part, on the line of the front seat beside it (a back-seat man takes the front seat's
            // place once its man is out, going by the side of its back first). The front seats sit in their opening (SeatZ): no slide.
            var line = own || owner < 0 ? s : c.Seats[owner];
            float x0 = own ? s.x : dside * Mathf.Abs(line.x);
            p.Sill = new Vector3(x0, s.y, Mathf.Clamp(line.z, rear + DoorInset, Mathf.Max(rear + DoorInset, front - DoorFrontKeep)));
            if (!own)
            {
                p.HasVia = true;
                p.Via = new Vector3(dside * BackSeatOutboard, s.y, line.z - SeatBackBehind);
            }
            if (own) p.Stand = new Vector3(dside * (Mathf.Abs(hinge.localPosition.x) + StandOut), 0f, rear + StandInset);
            else { var e = c.Exits[seat % c.Exits.Length]; p.Stand = new Vector3(dside * Mathf.Abs(e.x), 0f, e.z); }
            return true;
        }

        /// <summary>
        /// The side door <paramref name="seat"/> goes out of: on his side, beside him (<paramref name="own"/>: the front seats'), else
        /// the nearest on his side (a back seat: the front one, after its man — <paramref name="owner"/>), or the other side's when the
        /// man in that front seat died there (he'd climb over him). −1: none on his side.
        /// </summary>
        int SideDoor(Car c, int seat, out bool own, out int owner)
        {
            own = false; owner = -1;
            var s = c.Seats[seat];
            float side = s.x < 0f ? -1f : 1f;
            int door = NearestSideDoor(c, side, s.z, out float gap);
            if (door < 0) return -1;
            if (gap <= OwnDoorGap) { own = true; return door; }
            owner = OwnerOf(c, door);
            if (owner >= 0 && DeadInSeat(c, owner))
            {
                int other = NearestSideDoor(c, -side, s.z, out _);
                int otherOwner = other >= 0 ? OwnerOf(c, other) : -1;
                if (other >= 0 && !(otherOwner >= 0 && DeadInSeat(c, otherOwner))) { door = other; owner = otherOwner; }
            }
            return door;
        }

        /// <summary>The side door on <paramref name="side"/> whose opening is nearest <paramref name="z"/> along the car; its gap (m, 0 = beside it).</summary>
        static int NearestSideDoor(Car c, float side, float z, out float gap)
        {
            int best = -1; gap = float.MaxValue;
            for (int i = 0; i < c.Doors.Count; i++)
            {
                var h = c.Doors[i].Hinge;
                if (h == null || i >= c.DoorBoxes.Count || !IsSideDoor(c, i) || (h.localPosition.x < 0f ? -1f : 1f) != side) continue;
                var b = c.DoorBoxes[i];
                float z0 = h.localPosition.z + b.min.z, z1 = h.localPosition.z + b.max.z;
                float g = z < z0 ? z0 - z : z > z1 ? z - z1 : 0f;
                if (g < gap) { gap = g; best = i; }
            }
            return best;
        }

        /// <summary>The seat whose own door <paramref name="door"/> is (−1: none).</summary>
        static int OwnerOf(Car c, int door)
        {
            var h = c.Doors[door].Hinge;
            if (h == null || c.Seats == null) return -1;
            float side = h.localPosition.x < 0f ? -1f : 1f;
            for (int j = 0; j < c.Seats.Length; j++)
            {
                if (c.Truck && j >= 1) continue;
                var s = c.Seats[j];
                if ((s.x < 0f ? -1f : 1f) != side) continue;
                if (NearestSideDoor(c, side, s.z, out float gap) == door && gap <= OwnDoorGap) return j;
            }
            return -1;
        }

        /// <summary>The man in <paramref name="seat"/> was shot dead there (in it, or half out of it).</summary>
        static bool DeadInSeat(Car c, int seat)
        {
            var r = seat < c.Riders.Count ? c.Riders[seat] : null;
            return r != null && r.Current == IntruderT.State.Dead && r.Riding;
        }

        /// <summary>A side door (its leaf runs along the car), not a back one (across it: the truck's).</summary>
        static bool IsSideDoor(Car c, int i)
        {
            if (i >= c.DoorBoxes.Count) return true;
            var b = c.DoorBoxes[i];
            return b.size.z >= b.size.x;
        }

        /// <summary>The back face (car z) the box team gets out of: the back doors' hinge line (−3.0 on the truck and van).</summary>
        static float BackFace(Car c)
        {
            float z = float.MaxValue;
            for (int i = 0; i < c.Doors.Count; i++) if (c.Doors[i].Hinge != null && !IsSideDoor(c, i)) z = Mathf.Min(z, c.Doors[i].Hinge.localPosition.z);
            return z < float.MaxValue ? z : -c.Half.y + 0.35f;
        }

        static void EnsureExitState(Car c)
        {
            int n = c.Doors.Count;
            if (c.DoorAt != null && c.DoorAt.Length == n) return;
            c.DoorAt = new float[n]; c.DoorFor = new float[n]; c.DoorClear = new float[n];
            ResetExits(c);
        }

        /// <summary>Every door shut and nobody out of one yet (a new send, the call cleared: from ResetArrival).</summary>
        static void ResetExits(Car c)
        {
            if (c.DoorAt == null) return;
            for (int i = 0; i < c.DoorAt.Length; i++) { c.DoorAt[i] = -1f; c.DoorFor[i] = EmptyDoorSwing; c.DoorClear[i] = -1f; }
            for (int i = 0; i < c.Doors.Count; i++) if (c.Doors[i].Hinge != null) c.Doors[i].Hinge.localRotation = Quaternion.identity;
        }

        // ───────────────────────────── tests ─────────────────────────────

        /// <summary>Tests: vehicle <paramref name="v"/>'s door count.</summary>
        public int DoorCount(int v) => v >= 0 && v < _cars.Count ? _cars[v].Doors.Count : 0;

        /// <summary>Tests: when vehicle <paramref name="v"/>'s door <paramref name="door"/> began to swing (its <see cref="Clock"/>), −1 shut; how far open (0–1).</summary>
        public float DoorSwungAt(int v, int door, out float open)
        {
            open = 0f;
            if (v < 0 || v >= _cars.Count) return -1f;
            var c = _cars[v];
            if (c.DoorAt == null || door < 0 || door >= c.DoorAt.Length || c.DoorAt[door] < 0f) return -1f;
            open = Mathf.Clamp01((_t - c.DoorAt[door]) / c.DoorFor[door]);
            return c.DoorAt[door];
        }

        /// <summary>Tests: vehicle <paramref name="v"/>'s horn has blared this call (its driver dead in his seat).</summary>
        public bool HornBlown(int v) => v >= 0 && v < _cars.Count && _cars[v].HornUntil >= 0f;

        /// <summary>Tests: when vehicle <paramref name="v"/> stopped (its clock), −1 not yet.</summary>
        public float StopClock(int v) => v >= 0 && v < _cars.Count && Stopped(_cars[v]) ? StoppedAt(_cars[v]) : -1f;

        /// <summary>Tests: <paramref name="world"/> is inside vehicle <paramref name="v"/>'s shell (a body collider: not glass, not the light bar).</summary>
        public bool InsideBody(int v, Vector3 world)
        {
            if (v < 0 || v >= _cars.Count || _cars[v].Root == null) return false;
            foreach (var b in _cars[v].Root.GetComponentsInChildren<VehicleBody>())
            {
                if (b.Part != BodyPart || !b.TryGetComponent<BoxCollider>(out var box)) continue;
                var l = box.transform.InverseTransformPoint(world) - box.center;
                var h = box.size * 0.5f;
                if (Mathf.Abs(l.x) < h.x && Mathf.Abs(l.y) < h.y && Mathf.Abs(l.z) < h.z) return true;
            }
            return false;
        }
    }
}
