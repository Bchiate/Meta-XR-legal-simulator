using System;
using System.Collections;
using HIS.Core;
using HIS.Scenario;
using HIS.Weapons;
using UnityEngine;
using UnityEngine.AI;

namespace HIS.Intruder
{
    /// <summary>
    /// Deputy Ortiz IN PERSON for the Marshal night's handover (Brett 0.1.57: "I thought ortiz was just apart of the story intro
    /// and tutorial which seemed to be missing"; the lead's design A). He is one more Intruder (the bone hitboxes, the voice,
    /// the caption and the visitors' walk off the porch come for free); his brain is his own, driven by
    /// Scenario/MarshalBriefing:
    ///   - placed IN THE ROOM before the night's card (IntruderSpawner.SpawnHandoverDeputy), standing beside the front door on
    ///     its latch side, out of the leaf's sweep (<see cref="HandoverStandSpot"/>), dressed as the deputy (DressAsDeputy: the
    ///     windbreaker, the U.S. MARSHAL print, the badge), no weapon out, posed before his first frame is drawn (no T-pose);
    ///   - idle, turning to face the player (the rig has no head look-at: the body turns); he says the briefing's lines from his
    ///     own head (<see cref="HandoverSay"/>: his deputy voice, positional, captioned "Ortiz") — no radio;
    ///   - told to go (<see cref="HandoverLeave"/>): to the door, opening it himself if it's shut (from its latch side, clear of
    ///     the leaf), out over the doorway link, onto the porch, off past the porch camera's edge (the visitors' WalkOffPorch),
    ///     then gone and removed (Vanish, Destroy). Shut on him before he's out, he opens it again and goes; never stuck — no
    ///     way out at all, he's gone where he stands. With <c>pullShut</c> (the short handover, a skip) he pulls it to behind
    ///     him; in the weapons drill the player shuts it ("Lock it behind me.");
    ///   - practice-safe for his whole life (<see cref="HandoverPracticeHit"/>): a round, a blade or a fist does nothing to him
    ///     but "Whoa! Watch it!" (at most every 3 s) — no wound, no damage, no force reported, nothing judged.
    /// He is never the spawner's Active nor the director's Intruder (the night's rounds, the judge and the commands never see
    /// him). Every line of this file is behind <see cref="IsHandoverDeputy"/>: no other man ever runs it.
    /// </summary>
    public partial class Intruder
    {
        /// <summary>"Whoa! Watch it!" (a practice hit on him: VoiceLines, his deputy voice).</summary>
        public const string HandoverWatchItKey = "vo.deputy.handover.watchit";
        /// <summary>"Whoa! Watch it!" at most this often (a shotgun's pellets, a slash's re-hits: one complaint).</summary>
        const float HandoverWatchItGap = 3f;
        /// <summary>Out past the door's plane by this much, off the doorway link: through the doorway (the "Lock it behind me." beat:
        /// the leaf can swing shut behind him from here, Intruder.InDoorwayOf).</summary>
        const float HandoverThroughAt = 0.45f;

        static Intruder s_handover;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetHandoverStatics() => s_handover = null; // domain reload off

        /// <summary>Tonight's handover deputy (Ortiz in the room), or null: none, or he has gone and been removed.</summary>
        public static Intruder HandoverDeputy => s_handover != null ? s_handover : null; // (Unity's null: a destroyed one reads as none)

        bool _handover, _handoverLeave, _handoverOpenIfShut, _handoverPull, _handoverHeadNoted;
        float _nextHandoverWatchIt = -100f;

        /// <summary>This is Ortiz in the room for the handover (not a visitor, not Ricky, not a squad member).</summary>
        public bool IsHandoverDeputy => _handover;
        /// <summary>He has been told to go (<see cref="HandoverLeave"/>) and is on his way out (or gone).</summary>
        public bool HandoverLeaving => _handover && _handoverLeave;
        /// <summary>Through the doorway: off the doorway link and <see cref="HandoverThroughAt"/> m out past the door's plane (or gone).</summary>
        public bool HandoverOut => _handover && (Current == State.Gone || (!OnDoorwayLink && OutFromDoor(BodyPoint) > HandoverThroughAt));
        /// <summary>
        /// On his way off the porch, out past the door's plane (A6, Brett 0.1.57): the night's room clear after the handover keeps
        /// him (GameSession.ClearWorld's keepProtectee), so he walks off the porch camera's edge and goes by himself instead of
        /// popping off the feed mid-walk. Still in the room, he goes with the clear.
        /// </summary>
        public bool HandoverWalkingOff => _handover && _handoverLeave && Current != State.Dead && Current != State.Gone && OutFromDoor(BodyPoint) > 0.2f;
        /// <summary>He opened the front door himself on his way out (a shut door: the short handover, a skip, or shut on him).</summary>
        public bool HandoverOpenedDoor { get; private set; }
        /// <summary>Practice hits on him that did nothing (tests, the log).</summary>
        public int HandoverPracticeHits { get; private set; }
        /// <summary>His head (world), for the label naming him and his voice; his root + 1.65 m without a rig.</summary>
        public Vector3 HandoverHeadPoint => _hasAnim && _anim.Head != null ? _anim.Head.position : transform.position + Vector3.up * 1.65f;
        /// <summary>Called every frame while he exists (MarshalBriefing's watchdog: a handover whose coroutine was stopped cleans up).</summary>
        public Action HandoverWatchdog;

        /// <summary>
        /// Make this freshly spawned intruder Ortiz for the handover (IntruderSpawner.SpawnHandoverDeputy): standing on
        /// <paramref name="spot"/> facing <paramref name="facing"/>, the agent on the room NavMesh (he walks out through the
        /// front door's link later), dressed as the deputy and posed before this frame renders. <paramref name="outsidePoint"/>/
        /// <paramref name="insidePoint"/> are the front door's (his way out: the apron at the outside point).
        /// </summary>
        public void InitializeHandover(ScenarioDef def, Vector3 spot, Vector3 facing, float floorY, Vector3 outsidePoint, Vector3 insidePoint, int agentTypeId, Transform player)
        {
            _handover = true;
            s_handover = this;
            HideUntilPlaced();                               // (not drawn until he's posed on his spot: no T-pose flash)
            DoorIndex = 0;                                   // the front door: his way out
            _outsidePoint = outsidePoint; _insidePoint = insidePoint;
            _floorY = floorY;
            _player = player;
            Configure(def);                                  // a sober Innocent with his own voice (the caption "Ortiz"): the calm stand
            foreach (var w in _weapons) if (w != null) w.Show(false);
            _weapon = null;                                  // no weapon out (his holster is DressAsDeputy's prop)
            spot.y = floorY;
            transform.SetPositionAndRotation(spot, Quaternion.LookRotation(Flat(facing), Vector3.up));
            _prevPos = spot;
            _agent.agentTypeID = agentTypeId;
            _agent.enabled = true;
            if (_agent.isOnNavMesh) _agent.Warp(spot);
            _agent.speed = GaitSpeed;
            SetState(State.Idle);
            SetVisible(true);
            if (_hasAnim) { _anim.SnapGait(_gait); _anim.Evaluate(); }   // standing, this frame (the props go on off this pose)
            DressAsDeputy();
            ShowPlaced();
            StopAllCoroutines();
            StartCoroutine(Guarded(HandoverBrain(), HandoverBrainFailed, "Ortiz"));
            StartCoroutine(HandoverWatch());
            Debug.Log($"[HIS] Ortiz (#{Id}): in the room for the handover at {spot:F2} facing {Flat(facing).normalized:F2}, {_gait}" +
                      $"{(_agent.isOnNavMesh ? "" : " (NOT on the NavMesh: he can't walk out — he'll be gone where he stands)")}");
        }

        /// <summary>
        /// He says <paramref name="key"/> in person, from his head (his deputy voice, positional; captioned "Ortiz"). Returns the
        /// line's length in seconds (0: no take yet — the caption still shows).
        /// </summary>
        public float HandoverSay(string key)
        {
            if (!_handover || key == null || Current is State.Dead or State.Gone) return 0f;
            Say(key);
            Debug.Log($"[HIS] Ortiz (#{Id}, in person): \"{VoiceLines.Text(key)}\" from his head at {HandoverHeadPoint:F2}");
            return Sfx.Length(key);
        }

        /// <summary>
        /// Go now: out through the front door and off the porch (see the class summary). <paramref name="openIfShut"/>: a shut
        /// door he opens himself (always true today — he's never left stuck at a shut door). <paramref name="pullShut"/>: he
        /// pulls it to behind him once he's out (any door but one the player is holding or that was kicked in). Once.
        /// </summary>
        public void HandoverLeave(bool openIfShut, bool pullShut)
        {
            if (!_handover || _handoverLeave || Current is State.Dead or State.Gone) return;
            _handoverLeave = true;
            _handoverOpenIfShut = openIfShut;
            _handoverPull = pullShut;
            Debug.Log($"[HIS] Ortiz (#{Id}): told to go ({(pullShut ? "he pulls the door to behind him" : "the player gets the door")})");
        }

        /// <summary>Already on his way out (the drill's door step), and now he pulls the door to behind him after all (a skip).</summary>
        public void HandoverPullBehind()
        {
            if (!_handover || _handoverPull) return;
            _handoverPull = true;
            Debug.Log($"[HIS] Ortiz (#{Id}): pulls the door to behind him after all (the check was skipped)");
        }

        /// <summary>
        /// Every hit on any of his hitboxes (IntruderProtectee.PracticeHit, from Hitbox.ApplyDamage): nothing happens to him — no
        /// wound, no blood, no damage, no force reported, nothing judged — and he says so ("Whoa! Watch it!", at most every 3 s).
        /// </summary>
        bool HandoverPracticeHit(in DamageInfo info)
        {
            if (Current == State.Dead) return false;         // (he can't die: never)
            HandoverPracticeHits++;
            if (Time.time < _nextHandoverWatchIt) return true;
            _nextHandoverWatchIt = Time.time + HandoverWatchItGap;
            Debug.Log($"[HIS] Ortiz: a practice {info.Type} hit him — no harm, nothing judged (\"{VoiceLines.Text(HandoverWatchItKey)}\")");
            Say(HandoverWatchItKey);
            return true;
        }

        // ───────────────────────────── his brain ─────────────────────────────

        /// <summary>Standing, facing you, until he's told to go; then the walk out (<see cref="HandoverWalkOut"/>).</summary>
        IEnumerator HandoverBrain()
        {
            if (!_handoverHeadNoted) { _handoverHeadNoted = true; Debug.Log("[HIS] Ortiz: the rig has no head look-at — his body turns to face you"); }
            while (!_handoverLeave)
            {
                if (Time.deltaTime > 0f && !MovementLocked) FaceTowards(PlayerOnFloor(), 3f);
                yield return null;
            }
            yield return HandoverWalkOut();
        }

        /// <summary>Every frame while he exists: the briefing's watchdog (its own frame stamp says whether its coroutine still runs).</summary>
        IEnumerator HandoverWatch()
        {
            while (true)
            {
                HandoverWatchdog?.Invoke();
                yield return null;
            }
        }

        void HandoverBrainFailed(Exception e)
        {
            Debug.LogWarning($"[HIS] Ortiz: his brain stopped ({e.GetType().Name}) — gone where he stands");
            HandoverGoneNow();
        }

        /// <summary>
        /// Out through the front door (the visitors' way: the doorway link to the apron, then WalkOffPorch past the camera's edge),
        /// opening it himself if it's shut, and again if it's shut on him before he's out; pulled to behind him when asked. Gone
        /// and removed at the end. No way out (no NavMesh under him, no link, blocked): gone where he stands, hidden for good.
        /// </summary>
        IEnumerator HandoverWalkOut()
        {
            var door = Door;
            SetState(State.Fleeing);                         // (the leaving state every visitor uses: HideOncePastTheDoor watches it)
            if (_hasAnim) _anim.SetSpeedOverride(null);
            if (!_agent.enabled) _agent.enabled = true;
            if (!_agent.isOnNavMesh && NavMesh.SamplePosition(transform.position, out var near, 1f, NavMesh.AllAreas)) _agent.Warp(near.position); // (a re-bake under him)
            var nav = Nav;
            var exit = _outsidePoint;
            bool way = _agent.enabled && _agent.isOnNavMesh && nav != null && nav.Ready && nav.OnApron(_outsidePoint, out exit);
            if (!way)
            {
                Debug.LogWarning($"[HIS] Ortiz: no way out ({(nav == null ? "no doorway link" : !nav.Ready ? "the link isn't built" : !_agent.isOnNavMesh ? "not on the NavMesh" : "no apron")}) — gone where he stands");
                HandoverGoneNow();
                yield break;
            }
            if (door != null && !door.IsOpen && _handoverOpenIfShut) yield return HandoverOpenDoor(door);
            if (Current is State.Dead or State.Gone) yield break;
            // A leaf the player holds part-open: a way through beside it if there is one; else he takes it and swings it wide.
            if (door != null && door.IsOpen && !nav.Reconnect() && door.MakeWay()) { yield return new WaitForSeconds(0.4f); nav.Reconnect(); }
            StartCoroutine(HideOncePastTheDoor());           // one switch to the outside's layer, on the step (as every visitor)
            if (door != null) StartCoroutine(HandoverPullShut(door));   // (it pulls only while _handoverPull: a skip mid-walk sets it)
            Debug.Log($"[HIS] Ortiz (#{Id}): walks out through the front door");
            // Shut on him before he's out (the drill: you swung it shut too soon): he opens it again himself and goes on.
            yield return PathTo(exit, () => GaitSpeed,
                () => door != null && !door.IsOpen && _handoverOpenIfShut && !OnDoorwayLink && OutFromDoor(BodyPoint) < -0.1f ? HandoverOpenDoor(door) : null);
            if (Current is State.Dead or State.Gone) yield break;
            // Boxed in by the practice target's NavMesh hole and the swung leaf's (PracticeTarget.PlanSpot keeps them a walkway
            // apart; the integrator 0.1.58 saw them meet once): its hole out, and once more — past its edge, never gone where he stands.
            var board = MarshalBriefing.Target;
            if (_walkBlocked && board != null && board.Carving)
            {
                Debug.LogWarning($"[HIS] Ortiz: boxed in at {transform.position:F2} (the door {(door != null ? $"{door.OpenAngle:F0}°" : "none")} open, the target at {board.transform.position:F2}) — its hole out of the NavMesh, he goes past it");
                board.StopCarving();
                yield return null;                           // (the carve comes out at the next NavMesh update)
                nav.Reconnect();
                yield return PathTo(exit, () => GaitSpeed,
                    () => door != null && !door.IsOpen && _handoverOpenIfShut && !OnDoorwayLink && OutFromDoor(BodyPoint) < -0.1f ? HandoverOpenDoor(door) : null);
                if (Current is State.Dead or State.Gone) yield break;
            }
            if (_walkBlocked || !nav.OnApron(transform.position, out _))
            {
                Debug.LogWarning("[HIS] Ortiz: blocked on his way out — gone where he stands");
                HandoverGoneNow();
                yield break;
            }
            if (_hasAnim) _anim.SetSpeedOverride(0f);
            if (_agent.enabled) _agent.enabled = false;
            if (WalksOffPorch) yield return WalkOffPorch(run: false);   // off the porch, past the camera's edge (IntruderVisitors)
            if (Current is State.Dead or State.Gone) yield break;
            // (A short porch: he's off it before the leaf could swing to. Shut now, before he's gone.)
            if (_handoverPull && door != null && door.IsOpen && !door.HeldByPlayer && !door.Breached && !AnyInDoorway(door))
            { door.Close(0.9f); Debug.Log("[HIS] Ortiz: pulls the door to behind him"); }
            HandoverGoneNow();
        }

        /// <summary>
        /// The front door, from inside: to its latch side, clear of the leaf's sweep (<see cref="HandoverHandleSpot"/>), a beat
        /// facing the handle, and it swings open (OpenGently: no door clip — the knock and the voice did the identifying). The
        /// doorway link is re-picked once it's open (the open leaf is carved out of the room NavMesh).
        /// </summary>
        IEnumerator HandoverOpenDoor(HIS.Room.VirtualDoor door)
        {
            if (door == null || door.IsOpen) yield break;
            var at = HandoverHandleSpot(door);
            if (_agent.enabled && _agent.isOnNavMesh && FlatDistance(transform.position, at) > 0.2f) yield return PathTo(at, () => GaitSpeed, null);
            if (Current is State.Dead or State.Gone) yield break;
            StopHere();
            for (float t = 0f; t < 0.4f && !door.IsOpen; t += Time.deltaTime) { FaceTowards(door.HandleWorld, 8f); yield return null; }
            if (!door.IsOpen)
            {
                door.OpenGently(1.0f, 80f);
                HandoverOpenedDoor = true;
                Debug.Log("[HIS] Ortiz: opens the front door himself");
            }
            for (float t = 0f; t < 1.3f && door.State == HIS.Room.VirtualDoor.DoorState.Opening; t += Time.deltaTime) yield return null;
            var nav = Nav;
            if (nav != null) nav.Reconnect();
        }

        /// <summary>
        /// Where he stands to open the front door: on its latch side 0.3 m past the latch jamb and 0.35 m into the room — never
        /// in the leaf's sweep (it swings into the room on the hinge side) — on the room NavMesh; else where he is.
        /// </summary>
        Vector3 HandoverHandleSpot(HIS.Room.VirtualDoor door)
        {
            float half = HalfWidth(door);
            var p = door.Threshold + Flat(door.HingeToLatch).normalized * (half + 0.3f) + Flat(door.Inward).normalized * 0.35f;
            p.y = _floorY;
            return NavMesh.SamplePosition(p, out var hit, 0.3f, NavMesh.AllAreas) && !OutTheDoor(hit.position) && SweepGap(door, hit.position) > 0.2f
                ? hit.position : transform.position;
        }

        /// <summary>The short handover / a skip (pullShut, or HandoverPullBehind): once he's out, the leaf swung to behind him (not while
        /// the player holds it). In the drill it only watches: the player shuts it ("Lock it behind me.").</summary>
        IEnumerator HandoverPullShut(HIS.Room.VirtualDoor door)
        {
            while (door != null && Current is not (State.Dead or State.Gone))
            {
                bool outside = OutFromDoor(BodyPoint) > PullDoorAt;
                if (outside && !door.IsOpen) yield break;    // shut already (the player did it)
                if (_handoverPull && outside && door.State == HIS.Room.VirtualDoor.DoorState.Open && !door.HeldByPlayer && !door.Breached && !AnyInDoorway(door))
                {
                    door.Close(0.9f);
                    Debug.Log("[HIS] Ortiz: pulls the door to behind him");
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>Gone for good: hidden at once (Vanish), removed a frame later. The briefing reads him as none from then on.</summary>
        void HandoverGoneNow()
        {
            if (_hasAnim) _anim.SetSpeedOverride(0f);
            if (_agent.enabled && _agent.isOnNavMesh && !_agent.isOnOffMeshLink) { _agent.ResetPath(); _agent.velocity = Vector3.zero; }
            if (Current != State.Gone) SetState(State.Gone);
            Vanish();
            Debug.Log($"[HIS] Ortiz (#{Id}): gone — removed");
            Destroy(gameObject);
        }

        // ───────────────────────────── where he stands ─────────────────────────────

        /// <summary>Half the front door's opening (m): its hinge line to its middle, on the floor.</summary>
        static float HalfWidth(HIS.Room.VirtualDoor door)
        {
            var h = door.HingeWorld - door.Threshold; h.y = 0f;
            return Mathf.Max(0.3f, h.magnitude);
        }

        /// <summary>
        /// How far <paramref name="p"/> (flat) is from the floor the leaf sweeps as it swings into the room: the quarter disc round
        /// its hinge, the leaf's width in radius, from shut (along the wall toward the latch) to its swing stop. 0: inside it.
        /// </summary>
        public static float SweepGap(HIS.Room.VirtualDoor door, Vector3 p)
        {
            if (door == null) return 99f;
            var x = Flat(door.HingeToLatch).normalized; var z = Flat(door.Inward).normalized;
            var v = p - door.HingeWorld;
            float px = Vector3.Dot(v, x), pz = Vector3.Dot(v, z);
            float w = HalfWidth(door) * 2f, stop = Mathf.Clamp(door.MaxOpenAngle, 0f, 180f) * Mathf.Deg2Rad;
            float ang = Mathf.Atan2(pz, px), r = Mathf.Sqrt(px * px + pz * pz);
            if (ang >= 0f && ang <= stop) return Mathf.Max(0f, r - w);
            return Mathf.Min(SegmentGap(px, pz, w, 0f), SegmentGap(px, pz, w * Mathf.Cos(stop), w * Mathf.Sin(stop)));
        }

        static float SegmentGap(float px, float pz, float ex, float ez)
        {
            float t = Mathf.Clamp01((px * ex + pz * ez) / Mathf.Max(1e-6f, ex * ex + ez * ez));
            float dx = px - ex * t, dz = pz - ez * t;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Ricky closer than this (flat, m) rules a stand spot out (A2).</summary>
        const float HandoverRickyGap = 0.8f;
        /// <summary>The player closer than this (flat, m) rules a stand spot out: never spawned in him.</summary>
        const float HandoverPlayerGap = 0.7f;
        /// <summary>His body's radius plus a margin (m): this clear of the leaf's sweep.</summary>
        const float HandoverLeafGap = 0.3f;

        /// <summary>
        /// Ortiz's stand spot for the handover (the lead's design A2, Brett 0.1.57), first fit wins, each on the room NavMesh
        /// (never on a doorway's apron), clear of the leaf's sweep (<see cref="SweepGap"/> ≥ 0.3 m), ≥ 0.8 m from Ricky and
        /// ≥ 0.7 m from the player, and — with a practice target (<paramref name="target"/>: the drill) — never between the player
        /// and it nor behind it as the player sees it (PracticeTarget.Crowds: ≥ 25° off the line from <paramref name="eye"/>, and
        /// where the board would stand with him there, never onto Ricky):
        ///   1. the LATCH side (the leaf swings into the room on the hinge side): 0.35–0.6 m off the front door's wall, 0.9–1.6 m
        ///      from the door's centre along it (Brett's door hinges on the corner side: this is the open side);
        ///   2. the hinge side, beyond the leaf's sweep + 0.3 m;
        ///   3. any NavMesh point ≥ 1 m from the door (rings round it, into the room);
        ///   4. (only if none of those fits) the same again without the target's 25° (logged: never seen yet).
        /// <paramref name="why"/>: which, and how it measured (the log line).
        /// </summary>
        public static bool HandoverStandSpot(HIS.Room.VirtualDoor door, Vector3 eye, Vector3? ricky, bool target, out Vector3 spot, out string why)
        {
            spot = default; why = "no front door";
            if (door == null) return false;
            var hl = Flat(door.HingeToLatch).normalized; var inw = Flat(door.Inward).normalized;
            var sill = door.Threshold;
            float half = HalfWidth(door), w = half * 2f;
            float overhang = Mathf.Max(0f, -Mathf.Cos(Mathf.Clamp(door.MaxOpenAngle, 0f, 180f) * Mathf.Deg2Rad)) * w;
            float[] offs = { 0.45f, 0.35f, 0.55f, 0.6f };
            float[] latchAlong = { 1.0f, 1.2f, 0.9f, 1.1f, 1.3f, 1.4f, 1.5f, 1.6f };
            for (int pass = 0; pass < 2; pass++)
            {
                bool strict = pass == 0;
                // 1. The latch side.
                foreach (float a in latchAlong)
                    foreach (float o in offs)
                        if (Fits(sill + hl * a + inw * o, false, strict, out spot, out var info))
                        { why = $"the latch side, {a:F1} m along the wall from the door's centre, {o:F2} m off it ({info})"; return true; }
                // 2. The hinge side, beyond the leaf's sweep + 0.3 m.
                for (int k = 0; k < 4; k++)
                {
                    float a = half + overhang + HandoverLeafGap + 0.3f + k * 0.2f;
                    foreach (float o in offs)
                        if (Fits(sill - hl * a + inw * o, false, strict, out spot, out var info))
                        { why = $"the hinge side, {a:F1} m along the wall from the door's centre, {o:F2} m off it ({info})"; return true; }
                }
                // 3. Anywhere on the NavMesh ≥ 1 m from the door, nearest first.
                foreach (float r in new[] { 1.2f, 1.5f, 1.8f, 2.2f, 2.6f, 3.0f })
                    for (int s = 0; s <= 6; s++)
                        for (int sign = 1; sign >= -1; sign -= 2)
                        {
                            if (s == 0 && sign < 0) continue;
                            var dir = Quaternion.AngleAxis(sign * s * 15f, Vector3.up) * inw;
                            if (Fits(sill + dir * r, true, strict, out spot, out var info))
                            { why = $"{r:F1} m into the room from the door, {sign * s * 15f:+0;-0;0}° off straight in ({info})"; return true; }
                        }
                if (target) Debug.LogWarning("[HIS] Ortiz: no stand spot clear of the target's line from the eye — trying without it");
                else break;                                  // (no target: pass 2 would try the same spots again)
            }
            why = "no NavMesh spot by the door fits";
            return false;

            bool Fits(Vector3 c, bool ring, bool strict, out Vector3 at, out string info)
            {
                at = default; info = null;
                c.y = sill.y;
                if (!NavMesh.SamplePosition(c, out var hit, 0.3f, NavMesh.AllAreas)) return false;
                var p = hit.position;
                if (FlatDistance(p, c) > 0.15f || OutTheDoor(p)) return false;            // snapped elsewhere, or on an apron
                if (Vector3.Dot(p - sill, inw) < 0.3f) return false;                    // in the room, off the door's wall
                float leaf = SweepGap(door, p);
                if (leaf < HandoverLeafGap) return false;
                if (ring && FlatDistance(p, sill) < 1f) return false;
                float rickyAt = ricky.HasValue ? FlatDistance(p, ricky.Value) : 99f;
                if (rickyAt < HandoverRickyGap) return false;
                if (FlatDistance(p, eye) < HandoverPlayerGap) return false;
                string board = "no target";
                if (target)
                {
                    var planned = HIS.Room.PracticeTarget.PlanSpot(door, eye, ricky, p, out _);
                    if (!planned.HasValue) return false;
                    if (ricky.HasValue && FlatDistance(planned.Value, ricky.Value) < 1f) return false;   // (it would step onto Ricky)
                    var toBoard = planned.Value - eye; toBoard.y = 0f; var toHim = p - eye; toHim.y = 0f;
                    float angle = toBoard.sqrMagnitude > 1e-4f && toHim.sqrMagnitude > 1e-4f ? Vector3.Angle(toBoard, toHim) : 0f;
                    if (strict && HIS.Room.PracticeTarget.Crowds(planned.Value, eye, p)) return false;
                    if (!strict && FlatDistance(planned.Value, p) < 0.8f) return false;
                    board = $"{angle:F0}° off the line to the target from the eye{(strict ? "" : " (the 25° rule relaxed)")}";
                }
                at = p;
                info = $"clear of the leaf by {leaf:F2} m, {(ricky.HasValue ? $"{rickyAt:F1} m from Ricky" : "no Ricky")}, {board}";
                return true;
            }
        }
    }
}
