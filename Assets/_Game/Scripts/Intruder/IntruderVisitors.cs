using System.Collections;
using System.Collections.Generic;
using HIS.Core;
using HIS.Scenario;
using UnityEngine;
using UnityEngine.AI;
using Act = HIS.Intruder.IntruderAnimator.ActKind;
using Gait = HIS.Intruder.IntruderAnimator.Gait;

namespace HIS.Intruder
{
    /// <summary>
    /// A Marshal night's visitors, after 0.1.56 (Brett: "the ortiz dude could be anybody walking up if youre a us marshall he
    /// also just stands there for like a minute beofre he leaves when he levaes he like goes in and out then in then back out
    /// ... pizza delivery guy carrying a mail package just got stuck there even into a new round"). All of it is behind data only
    /// the Marshal visitor defs set (ScenarioDef.WalksOff / StaysFor / Carries / RadioAhead, a visitor's own voice), except
    /// <see cref="Vanish"/>, the one shared fix: a man who left is hidden for good (Scenarios mode too; police unchanged).
    ///   - a visit (<see cref="ScenarioDef.StaysFor"/>): his hello, a couple of steps in, a short beat facing you, one look round,
    ///     his leaving line, out the door — and off along the front of the house past the porch camera's edge
    ///     (<see cref="WalkOffPorch"/>), pulling the door to behind him if he opened it himself; then Gone, hidden, and the night
    ///     removes him. One "Get out" sends him at once.
    ///   - hurt by you: his hurt line and out (<see cref="HurtAndGone"/>), never the calm leaving line.
    ///   - Ortiz reads as a Marshal before the door opens: a U.S. MARSHAL print on his jacket, chest and back
    ///     (<see cref="AddMarshalPrint"/>), with the badge on his belt (the radio call and the knock are the director's and
    ///     KnockAndAnnounce's).
    ///   - the pizza courier: a red cap and a pizza box carried in both hands (IK), held out to you if you opened the door, his
    ///     line, set down on the step, and he walks off (<see cref="HandOverPizza"/>).
    /// GPU: the prints are two small textured panels on Ortiz and one on the box lid (no lights, a few hundred triangles).
    /// </summary>
    public partial class Intruder
    {
        // ───────────────────────────── gone is gone (shared) ─────────────────────────────

        bool _vanished;

        /// <summary>
        /// Gone for good (Brett 0.1.56: the man who left stood frozen in the doorway and on the door camera until the next round —
        /// in Scenarios mode too): every renderer off and back on its own layer, his wounds hidden, no hitboxes, anything still in
        /// his hands gone with him. Called at Gone by the leaving routines; <see cref="SetVisible"/> on a Gone man (not police)
        /// lands here too. The object stays until the round's clear (the director still reads him), just never drawn again.
        /// </summary>
        void Vanish()
        {
            _renderers ??= GetComponentsInChildren<Renderer>(true);
            _rendererLayers ??= System.Array.ConvertAll(_renderers, r => r != null ? r.gameObject.layer : 0);
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null || !r.transform.IsChildOf(transform)) continue;   // (a knife he dropped is no longer his)
                r.enabled = false;
                if (i < _rendererLayers.Length) r.gameObject.layer = _rendererLayers[i];
            }
            foreach (var r in GetComponentsInChildren<Renderer>(true)) if (r != null) r.enabled = false;   // (anything made after the cache)
            if (_wounds != null) { _wounds.ShowBodyWounds(false); _wounds.WoundsOnLayer(-1); }
            if (_carry != null) { if (_hasAnim) _anim.ClearCarry(); Destroy(_carry.gameObject); _carry = null; }
            if (_prop != null) DropProp(false);
            _visible = false;
            SetHittable(false);
            if (!_vanished) { _vanished = true; Debug.Log($"[HIS] Intruder #{Id}: gone — hidden for good"); }
        }

        // ───────────────────────────── a visit's way out ─────────────────────────────

        /// <summary>A Marshal night's visitor who walks off the porch, out of the camera's view, before he's gone (ScenarioDef.WalksOff).</summary>
        bool WalksOffPorch => _def != null && _def.WalksOff && PorchFor(Door) != null;

        /// <summary>He let himself in with his key (QuietEntry): on his way out he pulls the door to behind him.</summary>
        bool _openedDoorHimself;

        /// <summary>Past his door's plane by this much (out on the step): on the Outside layer, as the police backing out are.</summary>
        const float OutOfRoomAt = 0.5f;
        /// <summary>Out this far, a visitor who opened the door himself pulls it to behind him.</summary>
        const float PullDoorAt = 0.8f;

        /// <summary>Metres out past his door's plane (negative: in the room).</summary>
        float OutFromDoor(Vector3 p)
        {
            var d = Door;
            return d != null ? Vector3.Dot(p - d.Threshold, d.Outward) : 9f;
        }

        /// <summary>
        /// Leaving through the room (FleeAndLeave): once he is <see cref="OutOfRoomAt"/> past the door plane he's drawn as the
        /// outside is — through the open doorway and on the door camera — one switch, no in/out at the threshold. Ends with
        /// the flight (or at the switch).
        /// </summary>
        IEnumerator HideOncePastTheDoor()
        {
            while (Current == State.Fleeing)
            {
                if (_visible && OutFromDoor(BodyPoint) > OutOfRoomAt) { SetVisible(false); yield break; }
                yield return null;
            }
        }

        /// <summary>
        /// Off the porch along the front of the house, past the door camera's edge (PorchSet.OffLeft/OffRight, out of view for
        /// either latch side): the porch front first unless he's already past it (the line then clears the bushes), at his
        /// leaving pace, on the Outside layer once he's out the door. A visitor who let himself in pulls the door to behind
        /// him on the way (never one you opened, are holding, or that was broken in). No NavMesh out here: a straight walk.
        /// </summary>
        IEnumerator WalkOffPorch(bool run)
        {
            var door = Door;
            var porch = PorchFor(door);
            if (porch == null) yield break;
            if (_agent.enabled) _agent.enabled = false;
            if (_gait == Gait.Crouch) SetGait(Gait.Calm);             // (the courier, hit as he set the box down: up and away)
            var waypoints = new List<Vector3>(2);
            var path = porch.Approach;
            if (path != null && path.Length >= 2 && OutFromDoor(transform.position) < OutFromDoor(path[^2]) - 0.15f) waypoints.Add(path[^2]);
            waypoints.Add(Nearer(porch.OffLeft, porch.OffRight));
            bool pulled = !_openedDoorHimself;
            // Out on the step he's on the outside's layer; while the door stands open he can still be hit through it (a round
            // in his back is a bad call, and it lands), as he could in the room. Shut, he's out of reach, as any leaver.
            bool? reachable = null;
            float giveUp = Time.time + 25f;                           // (never a walk that can't end: gone where he is, hidden)
            Debug.Log($"[HIS] Intruder #{Id}: walks off the porch, out of the camera's view");
            foreach (var w in waypoints)
            {
                var to = w; to.y = transform.position.y;
                while (Current != State.Dead && FlatDistance(transform.position, to) > 0.05f && Time.time < giveUp)
                {
                    if (Time.deltaTime <= 0f) { yield return null; continue; }   // paused
                    float s = MovementLocked ? 0f : FleeSpeed(run);
                    if (_hasAnim) _anim.SetSpeedOverride(s);
                    FaceTowards(to, 8f);
                    transform.position = Vector3.MoveTowards(transform.position, to, s * Time.deltaTime);
                    float outBy = OutFromDoor(transform.position);
                    if (_visible && outBy > OutOfRoomAt) SetVisible(false);
                    if (!_visible)
                    {
                        bool open = door != null && door.IsOpen;
                        if (reachable != open) { reachable = open; SetHittable(open); }
                    }
                    if (!pulled && outBy > PullDoorAt) pulled = PullDoorTo(door);
                    yield return null;
                }
                if (Current == State.Dead) yield break;
            }
            if (_hasAnim) _anim.SetSpeedOverride(0f);
        }

        /// <summary>Swing his door shut behind him if it's still his to shut (true: done, or not his to shut).</summary>
        bool PullDoorTo(HIS.Room.VirtualDoor door)
        {
            if (door == null || !door.IsOpen || door.OpenedByPlayer || door.HeldByPlayer || door.Breached) return true;
            if (AnyInDoorway(door)) return false;           // someone (or a body) in the doorway: not on him; try again
            door.Close(0.9f);
            Debug.Log($"[HIS] Intruder #{Id}: pulls the door to behind him");
            return true;
        }

        // ───────────────────────────── a visit's stay ─────────────────────────────

        /// <summary>A Marshal night's visit (ScenarioDef.StaysFor): stands, faces you, leaves after a short beat — not Mike's wander.</summary>
        bool Visiting => _def != null && _def.StaysFor > 0f;

        /// <summary>A couple of steps in from where he came through: toward you, never closer than 1.8 m, on the room's NavMesh.</summary>
        Vector3 VisitSpot()
        {
            var from = transform.position;
            var to = PlayerOnFloor() - from; to.y = 0f;
            float step = Mathf.Clamp(to.magnitude - 1.8f, 0f, 1.0f);
            if (step < 0.2f) return from;
            var p = from + to.normalized * step;
            return NavMesh.SamplePosition(p, out var hit, 0.6f, NavMesh.AllAreas) && !OutTheDoor(hit.position) ? hit.position : from;
        }

        /// <summary>
        /// A Marshal night's visit (ScenarioDef.StaysFor; Brett 0.1.56: "he also just stands there for like a minute"), in place
        /// of drunk Mike's wander: his hello, a couple of steps in, he stands facing you, one look round the place, and once his
        /// beat (from the hello, or from who he said he was if you challenged him first) is over, his leaving line and out.
        /// Everything you say or do is answered as Mike's would be (who he is on a challenge, hands up under a gun, down on
        /// "get down"), except that one "Get out" sends a man who has already said who he is. Hurt: <see cref="HurtAndGone"/>.
        /// </summary>
        IEnumerator VisitBrain()
        {
            SetState(State.Idle);
            if (_heard.Count == 0 && AimedFor <= 0.3f && !GunshotRecently(1f) && !HurtVisitor)
            {
                Say(VoiceLines.Visitor(VisitorVoice, "hello"));
                GreetedAt = Time.time;
                if (KeyedVisitor) _introduced = true;
            }
            float stayFrom = Time.time, walkUntil = Time.time + 3f;
            var spot = VisitSpot();
            bool steppedIn = false, lookedRound = false;
            Debug.Log($"[HIS] Intruder #{Id}: visits ({_def.Name}, {_def.StaysFor:F0} s)");
            while (Current != State.Dead)
            {
                if (Time.deltaTime <= 0f) { yield return null; continue; }   // paused
                if (HurtVisitor) { yield return HurtAndGone(); yield break; }
                bool heard = TryHear(out var c);
                bool aimed = AimedFor > 0.3f, shot = GunshotRecently(1f);
                bool known = _identityStated || _introduced;
                if (heard && c == Command.GetOut && known) { yield return LeaveVisit(); yield break; }
                if (heard && c == Command.GetDown && known) { yield return GetDown(); continue; }
                if (heard && c == Command.ShowHands && known) RaiseHands();
                if (!_identityStated && (heard || aimed || shot))
                {
                    _identityStated = true; stayFrom = Time.time;
                    if (!steppedIn) { steppedIn = true; StopHere(); }
                    yield return Identify(gunOnHim: aimed || shot);
                    continue;
                }
                if (_identityStated && _posture == Posture.Standing && Upright(_gait) && (aimed || shot)) RaiseHands();
                // Down on your word a while (Mike's waits): up, and out with his line.
                if ((_posture == Posture.Prone && Time.time - _proneAt > 8f) || (_gait == Gait.Kneel && Time.time - _downAt > 10f))
                { yield return LeaveVisit(); yield break; }
                bool standing = _posture != Posture.Prone && _gait != Gait.Kneel && !LyingDown;
                bool talking = Sfx.IsPlaying(_voice);
                if (standing && !talking && Time.time - stayFrom > _def.StaysFor) { yield return LeaveVisit(); yield break; }
                // A couple of steps in, then he stands; once his hello is said, one look round the place.
                if (!steppedIn)
                {
                    bool near = FlatDistance(transform.position, spot) < 0.3f;
                    if (near || Time.time > walkUntil || !_agent.enabled || !_agent.isOnNavMesh) { steppedIn = true; StopHere(); }
                    else if (!MovementLocked) { _agent.speed = GaitSpeed; _agent.SetDestination(spot); }
                }
                else if (!lookedRound && !talking && standing && _posture == Posture.Standing && !_identityStated
                         && _def.StaysFor - (Time.time - stayFrom) > 2.5f)
                {
                    lookedRound = Gesture(Act.LookAround);
                }
                if (!LyingDown && !Gesturing && (steppedIn || _identityStated)) FaceTowards(PlayerOnFloor(), 3f);
                yield return null;
            }
        }

        /// <summary>His leaving line (his "leave" slot, as Mike's in-room one; Sal: "Fine. Tell him Sal came by."), a short beat facing you on it, then out (a hurt man never gets here).</summary>
        IEnumerator LeaveVisit()
        {
            CancelAct();
            StopHere();
            var key = VoiceLines.Visitor(VisitorVoice, "leave") ?? VoiceLines.Visitor(VisitorVoice, "door.leave");
            Say(key);
            float beat = Mathf.Clamp((key != null && Sfx.Has(key) ? Sfx.Length(key) : 1.6f) * 0.5f, 0.5f, 1.2f);
            for (float t = 0f; t < beat && Current != State.Dead && !HurtVisitor; t += Time.deltaTime)
            {
                if (!LyingDown) FaceTowards(PlayerOnFloor(), 4f);
                yield return null;
            }
            if (Current == State.Dead) yield break;
            if (HurtVisitor) { yield return HurtAndGone(); yield break; }
            Debug.Log($"[HIS] Intruder #{Id}: leaves ({_def?.Name})");
            yield return FleeAndLeave(run: false);
        }

        // ───────────────────────────── hurt by you ─────────────────────────────

        /// <summary>A Marshal night's visitor (sober, his own voice) the player has hurt — a round, the knife, a blow.</summary>
        bool HurtVisitor => _intent == Intent.Innocent && !IsDrunk && VisitorVoice != null && (_woundCount > 0 || Health < 99.5f);

        bool _hurtSaid;

        /// <summary>
        /// Hurt by you (Brett 0.1.56: stabbed Ortiz finished his visit and said "Two o'clock. Lock it behind me."): his hurt line
        /// (Ortiz "Agh! I'm a Marshal!", Sal "Ow! What the hell, man?!"), hands up, facing you — a Marshal night stops the round a
        /// beat after a bad call (the arrest: ScenarioDirector), so that is how he stands when it does. Nothing stopping it, he
        /// backs out as his legs allow. Never the calm leaving line, and no leave timer after a wound.
        /// </summary>
        IEnumerator HurtAndGone()
        {
            _identityStated = true;
            if (KeyedVisitor) _introduced = true;
            CancelAct();
            StopHere();
            if (!_hurtSaid) { _hurtSaid = true; Say(VoiceLines.Visitor(VisitorVoice, "shot")); }
            Debug.Log($"[HIS] Intruder #{Id}: hurt by you — hands up, then backs out");
            RaiseHands();
            for (float t = 0f; t < 2.5f && Current != State.Dead; t += Time.deltaTime)
            {
                if (!LyingDown) FaceTowards(PlayerOnFloor(), 4f);
                yield return null;
            }
            if (Current == State.Dead) yield break;
            yield return FleeAndLeave(run: true);
        }

        // ───────────────────────────── Ortiz: the U.S. MARSHAL print ─────────────────────────────

        static readonly Color32 PrintInk = new(240, 204, 72, 255), PrintBand = new(20, 27, 50, 255);
        static readonly string[] MarshalWords = { "U.S.", "MARSHAL" };

        /// <summary>
        /// Deputy Ortiz's jacket print (Brett 2026-10-04 (b): "he wears a U.S. MARSHAL jacket ... readable on the porch camera"):
        /// "U.S. / MARSHAL" in big block letters, raid-jacket yellow on a navy band, across his chest (he walks up the path facing
        /// the camera) and bigger across his back (in the room, walking out). Two panels curved round his chest, on his chest
        /// bone, into <paramref name="added"/> (the body's renderer cache: they switch layers and hide with him). No colliders:
        /// his chest hitbox is right under them.
        /// </summary>
        void AddMarshalPrint(List<Renderer> added, Animator animator)
        {
            var chest = animator != null && animator.isHuman
                ? (animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest)) : null;
            if (chest == null) return;
            var tex = MarshalPrintTexture();
            var fwd = Flat(transform.forward).normalized; var up = Vector3.up;
            var c = chest.position;
            // (The officer's badge sits 0.118 m out from this bone and the POLICE panel 0.112 m back: the jacket's surface.)
            // (Integrator, 0.1.56 renders: at 0.25 / 0.31 m the print was a smudge on the door feed even at the door, and the chest
            // one sat under the vest's chest pockets, only bits of yellow showing between them. Now the chest band is 0.32 m
            // wide (letters ~5 cm), 0.175 m out: over the pockets; the back 0.38 m (~6 cm), the most the torso takes.)
            added.Add(PrintedPanel("MarshalPrint (chest)", chest, c + up * 0.03f + fwd * 0.175f, Quaternion.LookRotation(-fwd, up), 0.32f, tex, 0.20f, 0.11f));
            added.Add(PrintedPanel("MarshalPrint (back)", chest, c + up * 0.05f - fwd * 0.121f, Quaternion.LookRotation(fwd, up), 0.38f, tex, 0.21f, 0.11f));
        }

        /// <summary>
        /// A printed panel <paramref name="width"/> m wide (height from the print's aspect), curved like a chest of half-width
        /// <paramref name="halfW"/> and half-depth <paramref name="halfD"/>, seen from its local −Z (as Unity's Quad), placed in the
        /// world then onto <paramref name="bone"/> keeping it (Mixamo bones can carry a scale).
        /// </summary>
        static Renderer PrintedPanel(string name, Transform bone, Vector3 worldPos, Quaternion worldRot, float width, Texture2D tex, float halfW, float halfD)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(worldPos, worldRot);
            go.transform.SetParent(bone, true);
            float height = width * tex.height / Mathf.Max(1f, tex.width);
            go.AddComponent<MeshFilter>().sharedMesh = CurvedPanel(width, height, halfW, halfD);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = PrintMaterial(tex);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        static readonly Dictionary<(float, float, float, float), Mesh> s_panels = new();

        /// <summary>A strip on the ellipse z = d(1 − √(1 − x²/a²)) (back toward the body at the edges), uv 0..1, facing −Z.</summary>
        static Mesh CurvedPanel(float w, float h, float a, float d)
        {
            var key = (w, h, a, d);
            if (s_panels.TryGetValue(key, out var cached) && cached != null) return cached;
            const int cols = 8;
            var v = new Vector3[(cols + 1) * 2]; var uv = new Vector2[v.Length]; var n = new Vector3[v.Length];
            for (int i = 0; i <= cols; i++)
            {
                float u = (float)i / cols, x = (u - 0.5f) * w;
                float k = Mathf.Clamp(x / a, -0.98f, 0.98f), root = Mathf.Sqrt(1f - k * k);
                float z = d * (1f - root);
                var normal = new Vector3(d * k / (a * root), 0f, -1f).normalized;   // (dz/dx, 0, −1): out of the body
                v[i * 2] = new Vector3(x, -h * 0.5f, z); v[i * 2 + 1] = new Vector3(x, h * 0.5f, z);
                uv[i * 2] = new Vector2(u, 0f); uv[i * 2 + 1] = new Vector2(u, 1f);
                n[i * 2] = n[i * 2 + 1] = normal;
            }
            var t = new int[cols * 6];
            for (int i = 0; i < cols; i++)
            {
                int bl = i * 2, tl = bl + 1, br = bl + 2, tr = bl + 3;
                // Clockwise seen from −Z, as Unity's Quad: (bl, tr, br), (tr, bl, tl).
                t[i * 6] = bl; t[i * 6 + 1] = tr; t[i * 6 + 2] = br;
                t[i * 6 + 3] = tr; t[i * 6 + 4] = bl; t[i * 6 + 5] = tl;
            }
            var m = new Mesh { name = $"PrintPanel {w:F2}x{h:F2}", vertices = v, uv = uv, normals = n, triangles = t };
            m.RecalculateBounds();
            m.RecalculateTangents();
            return s_panels[key] = m;
        }

        // ───────────────────────────── the pizza courier ─────────────────────────────

        /// <summary>The pizza box in his hands (null: set down, dropped, or he never had one).</summary>
        Transform _carry;
        const float PizzaSide = 0.40f, PizzaThick = 0.05f;
        static readonly Color CapRed = new(0.72f, 0.1f, 0.08f), Cardboard = new(0.86f, 0.84f, 0.78f);

        /// <summary>This visitor carries a pizza box (ScenarioDef.Carries; the Marshal night's courier only).</summary>
        bool CarriesPizza => _def != null && _def.Carries == VisitorProp.PizzaBox;

        /// <summary>
        /// The courier's look (Brett 0.1.56: "pizza delivery guy carrying a mail package"): a red ball cap (two primitives, off
        /// his eyes, on his head bone — a round through it is a head hit, as an officer's cap) and the pizza box carried flat in
        /// both hands. Called as he sets off up the path (CarryProp), so the cap is placed off his walking pose.
        /// </summary>
        void DressAsCourierWithPizza()
        {
            if (!_dressed)
            {
                _dressed = true;
                var animator = GetComponentInChildren<Animator>();
                SetVisible(_visible);                                 // the renderer cache first: the cap joins it with the body's layer
                int bodyLayer = _rendererLayers != null && _rendererLayers.Length > 0 ? _rendererLayers[0] : gameObject.layer;
                var added = new List<Renderer>();
                var head = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Head) : null;
                if (head != null)
                {
                    var fwd = Flat(transform.forward).normalized; var up = Vector3.up;
                    var rot = Quaternion.LookRotation(fwd, up);
                    var top = HeadTop(head);
                    Prop(added, head, PrimitiveType.Sphere, top - up * 0.03f - fwd * 0.02f, rot, new Vector3(0.2f, 0.095f, 0.225f), CapRed, 0f);          // crown
                    Prop(added, head, PrimitiveType.Cube, top - up * 0.07f + fwd * 0.115f, rot * Quaternion.Euler(10f, 0f, 0f), new Vector3(0.17f, 0.012f, 0.09f), CapRed, 0f); // brim
                }
                AddOfficerRenderers(added, bodyLayer);
                _hitboxes = null;                                     // the cap's colliders are part of his head hitbox now
                SetHittable(_hittable);
                Debug.Log($"[HIS] Intruder #{Id}: dressed as the courier (red cap, {added.Count} props)");
            }
            CarryPizzaBox();
        }

        /// <summary>
        /// The box: 40 × 5 × 40 cm, cardboard, its lid printed (a red pie and PIZZA, readable from in front of him), level at his
        /// waist a hand's length out, on his root (not a hand bone: it stays flat and square as he walks), both hands on its
        /// edges by IK (IntruderAnimator.SetCarry). On the Outside layer as the parcel was: he never crosses the door plane, and a
        /// box left on the step never draws through the wall. Dropped flat if he's hit or killed (<see cref="DropCarried"/>).
        /// </summary>
        void CarryPizzaBox()
        {
            if (_carry != null) return;
            int layer = LayerMask.NameToLayer("Outside");
            float hips = _hasAnim && _anim.Hips != null ? Mathf.Clamp(_anim.Hips.position.y - transform.position.y, 0.8f, 1.1f) : 0.95f;
            var root = new GameObject("Prop_PizzaBox").transform;
            root.SetPositionAndRotation(transform.position + Vector3.up * (hips + 0.08f) + Flat(transform.forward).normalized * 0.3f, transform.rotation);
            root.SetParent(transform, true);
            if (layer >= 0) root.gameObject.layer = layer;
            WorldPart(root, PrimitiveType.Cube, Vector3.zero, Quaternion.identity, new Vector3(PizzaSide, PizzaThick, PizzaSide), PropMaterial(Cardboard, 0f), layer);
            // The lid: a quad lying on the top face, its print's "up" toward him (upright for whoever he's facing).
            WorldPart(root, PrimitiveType.Quad, new Vector3(0f, PizzaThick * 0.5f + 0.0015f, 0f), Quaternion.Euler(90f, 180f, 0f),
                      new Vector3(PizzaSide - 0.015f, PizzaSide - 0.015f, 1f), PrintMaterial(PizzaLidTexture()), layer);
            _carry = root;
            if (_hasAnim) _anim.SetCarry(root, PizzaSide * 0.5f);
            Died -= DropCarriedOnDeath; Died += DropCarriedOnDeath;
            Debug.Log($"[HIS] Intruder #{Id}: carries a pizza box (camera tell)");
        }

        /// <summary>A primitive of <paramref name="worldSize"/> under <paramref name="parent"/> (its scale divided out), no collider, no shadows.</summary>
        static void WorldPart(Transform parent, PrimitiveType type, Vector3 localPos, Quaternion localRot, Vector3 worldSize, Material mat, int layer)
        {
            var go = GameObject.CreatePrimitive(type);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.SetLocalPositionAndRotation(localPos, localRot);
            float s = Mathf.Max(1e-4f, parent.lossyScale.x);
            go.transform.localScale = worldSize / s;
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            if (layer >= 0) go.layer = layer;
        }

        /// <summary>
        /// The hand-over (research/27 §1 "the box on the step"; the lead, 2026-10-04: "holds the box out, says his line, sets it
        /// on the step and walks off. No grab mechanic"). Door shut: his line through it, the box down on the step. The door
        /// opened on him: he turns to you, holds the box out, his line, a beat, then sets it down on the step all the same. A hit
        /// at any point is answered as at any door (AtTheDoor: he drops it and runs). About 5–8 s at the step.
        /// </summary>
        IEnumerator HandOverPizza(HIS.Room.VirtualDoor door)
        {
            bool face = !BehindDoor;                                  // the door was opened on him: to your face
            var key = VoiceLines.Visitor(VisitorVoice, "hello");
            if (face && _carry != null)
            {
                Debug.Log($"[HIS] Intruder #{Id}: holds the pizza out to you");
                var rest = _carry.localPosition;
                var offer = rest + new Vector3(0f, 0.08f, 0.14f) / Mathf.Max(1e-4f, transform.lossyScale.x);
                for (float t = 0f; t < 0.45f && Current != State.Dead && _carry != null; t += Time.deltaTime)
                {
                    FaceTowards(PlayerOnFloor(), 8f);
                    _carry.localPosition = Vector3.Lerp(rest, offer, Mathf.SmoothStep(0f, 1f, t / 0.45f));
                    yield return null;
                }
            }
            Say(key);
            float line = Mathf.Clamp(key != null && Sfx.Has(key) ? Sfx.Length(key) : 2.2f, 1.5f, 3f);
            yield return AtTheDoor(line + (face ? 0.8f : 0.2f));
            yield return SetPizzaDown(door);
            yield return AtTheDoor(0.4f);
        }

        /// <summary>
        /// Down on the step: he crouches (LowCrouchIdle, his hands on the box by IK), lowers it level onto the doormat ~0.4 m in
        /// front of him and at least 0.3 m outside the door plane, square to the door with its print readable from inside, lets
        /// go, and stands up. It stays there (the camera's "box on the step") until the room is next cleared.
        /// </summary>
        IEnumerator SetPizzaDown(HIS.Room.VirtualDoor door)
        {
            if (_carry == null) yield break;
            var fwd = Flat(transform.forward).normalized;
            var spot = transform.position + fwd * 0.42f;
            if (door != null)
            {
                float outBy = Vector3.Dot(spot - door.Threshold, door.Outward);
                if (outBy < 0.3f) spot += Flat(door.Outward).normalized * (0.3f - outBy);
            }
            spot.y = _floorY + PizzaThick * 0.5f + 0.002f;
            var rot = Quaternion.LookRotation(door != null ? Flat(door.Inward).normalized : -fwd, Vector3.up);
            bool crouch = _hasAnim && _anim.HasGait(Gait.Crouch) && Upright(_gait);
            if (crouch) SetGait(Gait.Crouch);
            var box = _carry;
            _carry = null;                                            // (from here it's the step's, whatever happens to him)
            box.SetParent(null, true);
            LeaveOnFloor(box);
            Vector3 p0 = box.position; Quaternion r0 = box.rotation;
            const float lower = 0.8f;
            for (float t = 0f; t < lower && Current != State.Dead && box != null; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / lower);
                box.SetPositionAndRotation(Vector3.Lerp(p0, spot, k), Quaternion.Slerp(r0, rot, k));
                if (k > 0.65f && _hasAnim) _anim.ClearCarry();
                yield return null;
            }
            if (box != null) box.SetPositionAndRotation(spot, rot);
            if (_hasAnim) _anim.ClearCarry();
            Debug.Log($"[HIS] Intruder #{Id}: sets the pizza down on the step");
            if (Current == State.Dead) yield break;
            yield return new WaitForSeconds(0.2f);
            if (crouch && Current != State.Dead) { SetGait(Gait.Calm); yield return new WaitForSeconds(0.6f); }
        }

        /// <summary>Hit or killed with the box in his hands: it drops flat to the floor where his hands were.</summary>
        void DropCarried()
        {
            if (_carry == null) return;
            var box = _carry;
            _carry = null;
            if (_hasAnim) _anim.ClearCarry();
            box.SetParent(null, true);
            var p = box.position; p.y = _floorY + PizzaThick * 0.5f + 0.002f;
            box.SetPositionAndRotation(p, Quaternion.Euler(0f, box.eulerAngles.y, 0f));
            LeaveOnFloor(box);
            Debug.Log($"[HIS] Intruder #{Id}: drops the pizza");
        }

        void DropCarriedOnDeath(Intruder _) => DropCarried();

        /// <summary>A prop left lying outside goes with the next clear of the room (as the parcel on the step).</summary>
        static void LeaveOnFloor(Transform box)
        {
            var go = box.gameObject;
            System.Action clear = null;
            clear = () => { if (go != null) Destroy(go); GameSession.WorldCleared -= clear; };
            GameSession.WorldCleared += clear;
        }

        // ───────────────────────────── printed textures (no font assets: a 5×7 block face) ─────────────────────────────

        static Texture2D s_marshalTex, s_pizzaTex;
        static readonly Dictionary<Texture2D, Material> s_printMats = new();

        /// <summary>Make the prints ahead of the first visitor (under the night's intro card, with the Marshal looks): no hitch later.</summary>
        public static void PrewarmVisitorPrints() { PrintMaterial(MarshalPrintTexture()); PrintMaterial(PizzaLidTexture()); }

        static Material PrintMaterial(Texture2D tex)
        {
            if (s_printMats.TryGetValue(tex, out var m) && m != null) return m;
            var sh = Shader.Find("HIS/OcclusionLitPBR") ?? Shader.Find("Universal Render Pipeline/Lit");
            m = new Material(sh) { name = tex.name + " (runtime)", color = Color.white, mainTexture = tex };
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.3f);
            if (m.HasProperty("_EnvironmentDepthBias")) m.SetFloat("_EnvironmentDepthBias", 0.06f); // (as the props: solid on the real floor)
            return s_printMats[tex] = m;
        }

        static Texture2D MarshalPrintTexture()
        {
            if (s_marshalTex != null) return s_marshalTex;
            return s_marshalTex = PrintText("U.S. MARSHAL print", MarshalWords, PrintInk, PrintBand, 8, 1);
        }

        /// <summary>The pizza box lid: cardboard, a red pie up top, PIZZA under it in dark red. 128 px square.</summary>
        static Texture2D PizzaLidTexture()
        {
            if (s_pizzaTex != null) return s_pizzaTex;
            const int N = 128;
            Color32 paper = new(222, 216, 200, 255), red = new(176, 34, 26, 255), ink = new(132, 22, 16, 255), crust = new(200, 120, 60, 255);
            var px = new Color32[N * N];
            for (int i = 0; i < px.Length; i++) px[i] = paper;
            // The pie (from the top: centre 50 px down, radius 33) with a lighter crust ring, edges anti-aliased.
            float cx = 63.5f, cy = N - 1 - 50f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    float outer = Mathf.Clamp01(33.5f - d), inner = Mathf.Clamp01(28.5f - d);
                    if (outer <= 0f) continue;
                    var c = Color32.Lerp(crust, red, inner);
                    px[y * N + x] = Color32.Lerp(px[y * N + x], c, outer);
                }
            DrawText(px, N, N, "PIZZA", ink, 3, (N / 3 - TextCells("PIZZA")) / 2, 92 / 3);
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { name = "Pizza lid print", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return s_pizzaTex = tex;
        }

        /// <summary>
        /// Block text, centred line under line (7-cell glyphs, 1 cell between letters, 2 between lines), <paramref name="px"/>
        /// pixels per cell, <paramref name="margin"/> cells of <paramref name="paper"/> all round. Mipmapped (the door camera sees
        /// it small), then made unreadable to free the CPU copy.
        /// </summary>
        static Texture2D PrintText(string name, string[] lines, Color32 ink, Color32 paper, int px, int margin)
        {
            int cols = 0;
            foreach (var l in lines) cols = Mathf.Max(cols, TextCells(l));
            int rows = lines.Length * 7 + (lines.Length - 1) * 2;
            int W = (cols + margin * 2) * px, H = (rows + margin * 2) * px;
            var pixels = new Color32[W * H];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = paper;
            for (int li = 0; li < lines.Length; li++)
                DrawText(pixels, W, H, lines[li], ink, px, margin + (cols - TextCells(lines[li])) / 2, margin + li * 9);
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 2 };
            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return tex;
        }

        /// <summary>One line of block text, its top-left cell at (<paramref name="cellX"/>, <paramref name="cellTop"/>) counted from the top.</summary>
        static void DrawText(Color32[] pixels, int W, int H, string text, Color32 ink, int px, int cellX, int cellTop)
        {
            foreach (var ch in text)
            {
                if (!Glyphs.TryGetValue(ch, out var g)) { cellX += 4; continue; }   // (a space)
                for (int r = 0; r < 7; r++)
                    for (int c = 0; c < g[r].Length; c++)
                        if (g[r][c] == '#')
                            for (int yy = 0; yy < px; yy++)
                            {
                                int y = H - 1 - ((cellTop + r) * px + yy);       // texture rows run bottom-up
                                if (y < 0 || y >= H) continue;
                                for (int xx = 0; xx < px; xx++)
                                {
                                    int x = (cellX + c) * px + xx;
                                    if (x >= 0 && x < W) pixels[y * W + x] = ink;
                                }
                            }
                cellX += g[0].Length + 1;
            }
        }

        /// <summary>Cells across a line (letters and the gaps between them).</summary>
        static int TextCells(string text)
        {
            int w = 0;
            foreach (var ch in text) w += (Glyphs.TryGetValue(ch, out var g) ? g[0].Length : 3) + 1;
            return Mathf.Max(0, w - 1);
        }

        /// <summary>The block face: only the letters the prints use. '#' = ink.</summary>
        static readonly Dictionary<char, string[]> Glyphs = new()
        {
            ['U'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
            ['S'] = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
            ['M'] = new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" },
            ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
            ['H'] = new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
            ['L'] = new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" },
            ['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
            ['I'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####" },
            ['Z'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####" },
            ['.'] = new[] { "..", "..", "..", "..", "..", "##", "##" },
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetVisitorStatics() { s_marshalTex = null; s_pizzaTex = null; s_printMats.Clear(); s_panels.Clear(); } // domain reload off
    }
}
