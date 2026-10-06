using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using HIS.Core;
using Meta.XR.EnvironmentDepth;
using Meta.XR.MRUtilityKit;
using UnityEngine;
using UnityEngine.AI;

namespace HIS.Room
{
    /// <summary>
    /// The door the intruder comes through, built over the scanned DOOR_FRAME (Brett, 2026-09-22: "a box over the door frame
    /// so I can't see if the real door is open or closed; they come from a black box").
    ///  • A dark box behind the frame hides the real door (a depth mask stops the real door from occluding it), so the real
    ///    door can be open or closed. Real people/pets in front of the doorway still occlude everything (all parts use the
    ///    depth-occluded shader).
    ///  • Trim casing hides scan error; a solid panel leaf with a lever handle is visible from room load.
    ///  • Knock = light tremble, pound = judder, kick = the leaf pops in a few degrees and springs back, breach = the latch side
    ///    splinters off and the leaf swings violently open on its hinges (bounce + settle). It stays open until Close/Reset.
    ///  • Rounds go through the leaf (Brett 0.1.8), closed or open: <see cref="RaycastLeaf"/> + <see cref="BulletHole"/> (holes
    ///    ride the leaf until ResetDoor), and sounds from behind it come through muffled (<see cref="PlayThrough"/>).
    ///  • Draw order: the void box draws after the door (queue 2010). Where real depth half-occludes the leaf (soft occlusion
    ///    at its edges, outside the depth mask at an angle), the leaf blends with the passthrough, never with a black box
    ///    that happened to draw first (Brett 0.1.8: "viewed at an angle it starts glitching black"). The doorway's stencil mark
    ///    (the box and the porch are seen only through it) is drawn only while the leaf is off its seat: shut, the leaf's 3–8 mm
    ///    gaps showed the box as black lines that crawled with the head (Brett 0.1.56: "the edges flicker black").
    ///  • Hinge side: like a real door, at the corner when both sides swing clear (Brett 0.1.30), measured on the room outline
    ///    (RoomGeometry: whole walls, no door holes) once the room has settled, and again after every room change — a re-scan put
    ///    his frame 9 cm from the corner with another frame's hole in the corner wall, and the old physics probe saw neither
    ///    (0.1.55/0.1.56: "the door start opening the wrong way again"). The pause menu's "Door hinge: flip" overrides it,
    ///    saved per room (<see cref="FlipHinge"/>).
    /// Local frame: the anchor's plane is XY; the room side is along ±Z (sign detected from the room's inward direction).
    /// </summary>
    public class VirtualDoor : MonoBehaviour
    {
        /// <summary>The front door (<see cref="All"/>[0]): where the security monitor hangs, and every single-door caller.</summary>
        public static VirtualDoor Instance { get; private set; }
        /// <summary>Every door rig in the room: [0] the front door, then the second door the player confirmed (Brett 0.1.20).</summary>
        public static readonly List<VirtualDoor> All = new();
        /// <summary>Raised after a rig is built and registered in <see cref="All"/> (the front door first).</summary>
        public static event System.Action<VirtualDoor> Built;
        /// <summary>The door the latest intruder came to (the key light aims at it); the front door until one comes.</summary>
        public static VirtualDoor Current { get => _current != null ? _current : Instance; set => _current = value; }
        static VirtualDoor _current;
        /// <summary>Position in <see cref="All"/>: 0 = the front door.</summary>
        public int Index { get; private set; }
        public bool IsFront => Index == 0;
        /// <summary>The camera feed's caption for this door.</summary>
        public string Label => IsFront ? "FRONT DOOR" : "SIDE DOOR";
        /// <summary>World centre of the opening, on the wall plane.</summary>
        public Vector3 Center => transform.TransformPoint(new Vector3(_plane.center.x, _plane.center.y, 0f));
        /// <summary>World point on the threshold (bottom middle of the opening, on the wall plane).</summary>
        public Vector3 Threshold => transform.TransformPoint(new Vector3(_plane.center.x, _plane.yMin, 0f));
        /// <summary>Out of the room, horizontal.</summary>
        public Vector3 Outward => -Inward;

        /// <summary>Someone rang at this door (the security monitor plays the chime and brings up its feed).</summary>
        public static event System.Action<VirtualDoor> DoorbellRung;

        /// <summary>The doorbell: the monitor's chime and feed if there is one, else a plain chime inside the door.</summary>
        public void RingDoorbell()
        {
            if (DoorbellRung != null) { DoorbellRung(this); return; }
            if (Sfx.Has("door.bell")) Sfx.Play("door.bell", Center + Inward * 0.3f, 0.8f);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Instance = null; _current = null; All.Clear(); Built = null; DoorbellRung = null; LeafShot = null; }

        public enum DoorState { Closed, Opening, Open }
        public DoorState State { get; private set; } = DoorState.Closed;
        public bool IsOpen => State != DoorState.Closed;
        public float OpenAngle => Mathf.Abs(_angle);
        /// <summary>World-space unit vector pointing from the door into the room.</summary>
        public Vector3 Inward => transform.TransformDirection(new Vector3(0, 0, _roomSign)).normalized;
        /// <summary>World-space unit vector along the wall from the hinge side to the latch side (the open leaf's face toward the doorway).</summary>
        public Vector3 HingeToLatch => transform.TransformDirection(new Vector3(_hingeAtMin ? 1f : -1f, 0, 0)).normalized;
        /// <summary>The scanned opening in this door's local XY plane (x across it, yMin = the sill); the leaf's room face is at z = 0.</summary>
        public Rect Opening => _plane;

        /// <summary>
        /// Does the segment <paramref name="a"/>→<paramref name="b"/> cross the wall plane inside the doorway (shrunk by
        /// <paramref name="margin"/> at the sides and the top)? The scanned walls' colliders face into the room only (one-sided
        /// MRUK meshes, back faces not hit), so a ray from outside passes through their backs: anything out there that sees or
        /// shoots someone inside has to do it through here (0.1.48 review: officers on the path fired through solid wall).
        /// </summary>
        public bool SegmentThroughOpening(Vector3 a, Vector3 b, float margin = 0.1f)
        {
            var la = transform.InverseTransformPoint(a); var lb = transform.InverseTransformPoint(b);
            if ((la.z > 0f) == (lb.z > 0f) || Mathf.Abs(la.z - lb.z) < 1e-5f) return false;
            var p = Vector3.Lerp(la, lb, la.z / (la.z - lb.z));
            return p.x > _plane.xMin + margin && p.x < _plane.xMax - margin && p.y > _plane.yMin && p.y < _plane.yMax - margin;
        }
        /// <summary>The slab's depth behind the frame plane (the gap under it is this deep).</summary>
        public const float LeafDepth = LeafThickness;

        const float VoidDepth = 1.8f;      // how far the dark box extends behind the frame (intruder waits ~1.25 m back)
        const float LeafThickness = 0.04f;
        const float HandleHeight = 0.95f;  // above the floor

        Rect _plane;
        float _roomSign = 1f;
        bool _hingeAtMin = true;
        float _angle, _angularVel, _stopAngle = 95f;
        Transform _hinge, _leaf, _latchChunk, _root, _handle;
        Vector3 _leafRestLocal, _chunkRestLocal;
        Quaternion _chunkRestRot;
        ParticleSystem _splinters, _dust;
        MeshFilter _depthMask;
        MeshRenderer _portalMask; // HIS/DoorPortalMask over the opening: on only while the leaf is off its seat (UpdatePortal)
        Materials _mats;
        Coroutine _motion;
        NavMeshObstacle _leafObstacle;
        int _ambience; // night outside, heard through the open doorway
        BoxCollider _leafCollider;
        Vector2 _leafSize;
        Material _voidMat, _woodMat, _holeMat; // per-door copies: the void with its later render queue, raw wood + dark core for holes

        public struct Materials
        {
            public Material Leaf, Trim, Void, Metal, Portal;
            /// <summary>The same set with the lit ones (leaf, trim, metal) copied, the copies added to <paramref name="into"/>.</summary>
            public Materials OwnCopies(List<Material> into)
            {
                Material Copy(Material m) { if (m == null) return null; var c = new Material(m); into.Add(c); return c; }
                return new Materials { Leaf = Copy(Leaf), Trim = Copy(Trim), Metal = Copy(Metal), Void = Void, Portal = Portal };
            }
        }

        readonly List<Material> _own = new(); // this rig's lit materials (its copies, the hole wood): its key light

        static readonly int FillColorId = Shader.PropertyToID("_FillColor"), FillDirId = Shader.PropertyToID("_FillDir");

        /// <summary>
        /// This door's own key light (HIS/OcclusionLitPBR's fill; black = off): DoorKeyLight gives it to every door but the one
        /// the scene's light is aimed at (Brett 0.1.38: "the second door turned grey" once the police came to the front door —
        /// the doors are on facing walls, and the light then came from behind the side door's face).
        /// </summary>
        public void SetFill(Color color, Vector3 towardLight)
        {
            var dir = new Vector4(towardLight.x, towardLight.y, towardLight.z, 0f);
            foreach (var m in _own)
                if (m != null) { m.SetColor(FillColorId, color); m.SetVector(FillDirId, dir); }
        }

        public static VirtualDoor Build(Transform doorAnchor, Rect plane, Vector3 inward, Materials mats, int index = 0)
        {
            var go = new GameObject("VirtualDoor");
            go.transform.SetParent(doorAnchor, false);
            var d = go.AddComponent<VirtualDoor>();
            d.Index = index;
            if (index == 0) Instance = d;
            All.RemoveAll(x => x == null || x.Index == index);
            All.Add(d);
            All.Sort((a, b) => a.Index.CompareTo(b.Index));
            d._plane = plane;
            d._mats = mats.OwnCopies(d._own); // its own leaf/trim/metal, so its key light (SetFill) is its own
            d._roomSign = Vector3.Dot(doorAnchor.forward, inward) >= 0f ? 1f : -1f;
            d.BuildGeometry();
            Built?.Invoke(d);
            return d;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            All.Remove(this);
            var depth = FindAnyObjectByType<EnvironmentDepthManager>();
            if (depth != null && _depthMask != null) depth.MaskMeshFilters.Remove(_depthMask);
            if (_voidMat != null) Destroy(_voidMat);
            if (_woodMat != null) Destroy(_woodMat);
            if (_holeMat != null) Destroy(_holeMat);
            foreach (var m in _own) if (m != null && m != _woodMat && m != _holeMat) Destroy(m);
        }

        // ---------------------------------------------------------------- geometry

        void BuildGeometry()
        {
            _root = transform;
            float R = _roomSign, w = _plane.width, h = _plane.height;
            float xMin = _plane.xMin, xMax = _plane.xMax, yMin = _plane.yMin, yMax = _plane.yMax;
            var c = new Vector2(_plane.center.x, _plane.center.y);
            const float cw = 0.06f, cd = 0.05f; // trim casing width / depth

            // The void draws after everything else in the door (queue 2010 > the leaf/trim's 2000): a black wall that drew
            // first under a half-occluded leaf edge showed through it as a black strip. Rim wood for the bullet holes.
            _voidMat = _mats.Void != null ? new Material(_mats.Void) { renderQueue = 2010 } : null;
            _woodMat = _mats.Leaf != null ? new Material(_mats.Leaf) { color = new Color(0.66f, 0.52f, 0.36f) } : null;
            // A bullet hole's dark core: the leaf's own shader, not the void's (that one only draws through the open doorway).
            _holeMat = _mats.Leaf != null ? new Material(_mats.Leaf) { color = new Color(0.012f, 0.012f, 0.016f) } : null;
            if (_woodMat != null) _own.Add(_woodMat);   // (the splintered wood in a bullet hole is lit like the leaf)
            if (_holeMat != null) _own.Add(_holeMat);

            // 1) The dark box behind the frame: five inward-facing walls.
            var voidRoot = new GameObject("Void").transform; voidRoot.SetParent(_root, false);
            Quad(voidRoot, "Back", new Vector3(c.x, c.y, -R * VoidDepth), new Vector2(w + 0.02f, h + 0.02f), new Vector3(0, 0, R), _voidMat);
            Quad(voidRoot, "Left", new Vector3(xMin - 0.01f, c.y, -R * VoidDepth * 0.5f), new Vector2(VoidDepth, h + 0.02f), Vector3.right, _voidMat);
            Quad(voidRoot, "Right", new Vector3(xMax + 0.01f, c.y, -R * VoidDepth * 0.5f), new Vector2(VoidDepth, h + 0.02f), Vector3.left, _voidMat);
            Quad(voidRoot, "Top", new Vector3(c.x, yMax + 0.01f, -R * VoidDepth * 0.5f), new Vector2(w + 0.02f, VoidDepth), Vector3.down, _voidMat);
            Quad(voidRoot, "Floor", new Vector3(c.x, yMin - 0.005f, -R * VoidDepth * 0.5f), new Vector2(w + 0.02f, VoidDepth), Vector3.up, _voidMat);
            // The box is seen only THROUGH the opening (HIS/DoorVoid draws where HIS/DoorPortalMask marked the stencil): its
            // side walls no longer show as black bars beside the frame at an angle (Brett 0.1.17: "the black and stuff at
            // angles"). 2 mm inside the frame plane, behind the closed leaf's face — and drawn only while the leaf is off its
            // seat (UpdatePortal): shut, the leaf's 3–8 mm gaps at the jambs, sill and head still showed the box as black lines.
            if (_mats.Portal != null)
            {
                _portalMask = Quad(voidRoot, "PortalMask", new Vector3(c.x, c.y, -R * 0.002f), new Vector2(w, h), new Vector3(0, 0, R), _mats.Portal);
                _portalMask.enabled = false; // (built shut)
            }

            // 2) Depth mask 3 cm into the room: the real door (at the frame plane) no longer occludes the box, but anything real
            //    in front of the doorway still does. It covers the casing too, not just the opening: 3 cm in front of the leaf,
            //    an opening-sized mask misses a strip along the leaf's far edge as soon as you look at the door at an angle, and
            //    the real jamb there half-hid the leaf (flicker). The casing margin covers angles up to ~60°. And a 10 cm skirt
            //    below the sill: soft occlusion blends each pixel with its depth neighbours, so without it the porch ground just
            //    past the threshold picked up the real floor in front of the door and showed a thin strip of the real door's
            //    bottom (in daylight, the bright gap under it). Below the floor it only masks real floor within ~10% of the
            //    door plane's distance, where nothing virtual needs the real floor to hide it. A jamb within 20 cm of a corner gets
            //    its side widened to just past the corner while the leaf is shut (FitToRoom): the real corner's depth is the noisiest.
            var maskGo = new GameObject("DepthMask"); maskGo.transform.SetParent(_root, false);
            const float skirt = 0.10f;
            float maskH = h + cw + 0.01f + skirt;
            maskGo.transform.localPosition = new Vector3(c.x, yMin - skirt + maskH * 0.5f, R * 0.03f);
            maskGo.transform.localRotation = Quaternion.LookRotation(new Vector3(0, 0, -R));
            maskGo.transform.localScale = new Vector3(w + 2f * cw + 0.02f, maskH, 1f);
            _depthMask = maskGo.AddComponent<MeshFilter>();
            _depthMask.sharedMesh = QuadMesh();
            SizeDepthMask(); // (the casing margin each side until the room says otherwise)
            var depth = FindAnyObjectByType<EnvironmentDepthManager>();
            if (depth != null && !depth.MaskMeshFilters.Contains(_depthMask)) depth.MaskMeshFilters.Add(_depthMask);

            // 3) Trim casing (6 cm wide, 1.5 cm proud of the wall) hides scan error around the opening.
            float cz = R * (0.015f - cd * 0.5f);
            Box(_root, "Casing_Head", new Vector3(c.x, yMax + cw * 0.5f, cz), new Vector3(w + cw * 2f, cw, cd), _mats.Trim);
            // Hinge side first so the latch side (split around the latch chunk) is known.
            _hingeAtMin = true;
            BuildJambs(cz, cw, cd);

            // 4) Hinge + leaf with two raised panels and a lever handle on the room side.
            _hinge = new GameObject("Hinge").transform; _hinge.SetParent(_root, false);
            _leaf = new GameObject("Leaf").transform; _leaf.SetParent(_hinge, false);
            float lw = w - 0.006f, lh = h - 0.012f;
            _leafSize = new Vector2(lw, lh);
            Box(_leaf, "Slab", Vector3.zero, new Vector3(lw, lh, LeafThickness), _mats.Leaf);
            // The open leaf sticks ~0.9 m into the room: carve it out of the NavMesh so intruders walk around it instead of
            // through it (Brett, 2026-09-23). Only while it rests open; closed, it sits in the doorway the entry walks through.
            _leafObstacle = _leaf.gameObject.AddComponent<NavMeshObstacle>();
            _leafObstacle.shape = NavMeshObstacleShape.Box;
            _leafObstacle.center = Vector3.zero;
            // Padded 10 cm each way: the carve alone left NavMesh 15 cm from the leaf, so a burglar's loot spot (or a path) there
            // put half his body through it (ScenarioTests' doorway watch).
            _leafObstacle.size = new Vector3(lw + 0.2f, lh, 0.28f);
            _leafObstacle.carving = true;
            _leafObstacle.carveOnlyStationary = true;
            _leafObstacle.enabled = false;
            // Same footprint as a physics collider on the Door layer, so falls/clearance checks see the open leaf too.
            _leafCollider = _leaf.gameObject.AddComponent<BoxCollider>();
            _leafCollider.size = new Vector3(lw, lh, LeafThickness);
            _leafCollider.enabled = false;
            int doorLayer = LayerMask.NameToLayer("Door");
            if (doorLayer >= 0) _leaf.gameObject.layer = doorLayer;
            float faceZ = R * (LeafThickness * 0.5f + 0.002f);
            Box(_leaf, "PanelUpper", new Vector3(0, lh * 0.2f, faceZ), new Vector3(lw * 0.62f, lh * 0.34f, 0.004f), _mats.Leaf);
            Box(_leaf, "PanelLower", new Vector3(0, -lh * 0.25f, faceZ), new Vector3(lw * 0.62f, lh * 0.3f, 0.004f), _mats.Leaf);
            var handle = new GameObject("Handle").transform; handle.SetParent(_leaf, false); handle.name = "Handle";
            _handle = handle;
            Box(handle, "Rose", new Vector3(0, 0, R * 0.008f), new Vector3(0.055f, 0.055f, 0.012f), _mats.Metal);
            Box(handle, "Neck", new Vector3(0, 0, R * 0.03f), new Vector3(0.018f, 0.018f, 0.04f), _mats.Metal);
            Box(handle, "Lever", new Vector3(0, 0, R * 0.052f), new Vector3(0.12f, 0.02f, 0.02f), _mats.Metal);
            Layout();

            _splinters = BuildParticles("Splinters", new Color(0.55f, 0.42f, 0.28f), true);
            _dust = BuildParticles("Dust", new Color(0.7f, 0.68f, 0.64f, 0.6f), false);
            BuildThroughSources();

            // Against the room outline at once (flush with its wall, the depth mask to a near corner): no jump 1.5 s in.
            var geo = Geo;
            if (FindDoorWall(geo, out var wall))
                FitToRoom(true, wall, CornerGap(geo, wall, true, out _), CornerGap(geo, wall, false, out _));
            // Pick the hinge side once the room has settled (the furniture rays need the colliders EffectMesh builds on the same
            // scene-loaded event; a re-send in progress holds it).
            RechooseHinge("built", InitialSettle);
        }

        void BuildJambs(float cz, float cw, float cd)
        {
            float R = _roomSign, h = _plane.height, yMin = _plane.yMin, yMax = _plane.yMax;
            foreach (var t in new List<Transform>(GetComponentsInChildren<Transform>()))
                if (t != null && t.parent == _root && t.name.StartsWith("Jamb")) Destroy(t.gameObject);
            if (_latchChunk != null) Destroy(_latchChunk.gameObject);

            float hingeX = _hingeAtMin ? _plane.xMin - cw * 0.5f : _plane.xMax + cw * 0.5f;
            float latchX = _hingeAtMin ? _plane.xMax + cw * 0.5f : _plane.xMin - cw * 0.5f;
            Box(_root, "Jamb_Hinge", new Vector3(hingeX, (yMin + yMax) * 0.5f, cz), new Vector3(cw, h, cd), _mats.Trim);
            // Latch side in three pieces: the middle piece (at handle height) tears off on the breach.
            float handleY = yMin + HandleHeight, chunkH = 0.36f;
            float lowTop = handleY - chunkH * 0.5f, highBottom = handleY + chunkH * 0.5f;
            Box(_root, "Jamb_LatchLow", new Vector3(latchX, (yMin + lowTop) * 0.5f, cz), new Vector3(cw, lowTop - yMin, cd), _mats.Trim);
            Box(_root, "Jamb_LatchHigh", new Vector3(latchX, (highBottom + yMax) * 0.5f, cz), new Vector3(cw, yMax - highBottom, cd), _mats.Trim);
            _latchChunk = Box(_root, "Jamb_LatchChunk", new Vector3(latchX, handleY, cz), new Vector3(cw, chunkH, cd), _mats.Trim);
            _chunkRestLocal = _latchChunk.localPosition; _chunkRestRot = _latchChunk.localRotation;
        }

        /// <summary>Places hinge, leaf and handle for the current hinge side.</summary>
        void Layout()
        {
            float R = _roomSign, w = _plane.width, h = _plane.height;
            float lw = w - 0.006f, lh = h - 0.012f;
            float hingeX = _hingeAtMin ? _plane.xMin + 0.003f : _plane.xMax - 0.003f;
            _hinge.localPosition = new Vector3(hingeX, _plane.yMin, 0f);
            _hinge.localRotation = Quaternion.identity;
            float dir = _hingeAtMin ? 1f : -1f; // leaf extends from the hinge toward the latch
            // Front face flush with the frame plane: the slab sits just behind it.
            _leafRestLocal = new Vector3(dir * lw * 0.5f, 0.004f + lh * 0.5f, -R * LeafThickness * 0.5f);
            _leaf.localPosition = _leafRestLocal;
            var handle = _leaf.Find("Handle");
            handle.localPosition = new Vector3(dir * (lw * 0.5f - 0.065f), HandleHeight - (0.004f + lh * 0.5f), R * LeafThickness * 0.5f);
            // Lever points back toward the hinge, like a real lever handle.
            var lever = handle.Find("Lever"); lever.localPosition = new Vector3(-dir * 0.05f, 0, R * 0.052f);
            _angle = 0f; _angularVel = 0f;
        }

        // ---------------------------------------------------------------- the hinge side

        const float InitialSettle = 1.5f, RecheckSettle = 0.5f, MaxSettleWait = 5f; // unscaled seconds
        Coroutine _hingeRoutine;
        float _hingeWait;                        // seconds left before the check (unscaled; a pause or an app switch counts 0.1 s at most)
        string _hingeWhy = "built";
        bool? _pendingHinge;                     // the side to take once the leaf is shut and still (a re-pick, or the flip row, while open)
        float _clearMin = 95f, _clearMax = 95f;  // the last check's swing clearance with the hinge on each side (°)

        /// <summary>Hinge checks done (tests): ~1.5 s after the build, then after every room change (re-send, walls rebuilt).</summary>
        public int HingeChecks { get; private set; }
        /// <summary>A hinge check is waiting for the room to settle.</summary>
        public bool HingeCheckPending => _hingeRoutine != null;
        /// <summary>A change of hinge side is waiting for the leaf to be shut and still (open, held, kicked in or swinging).</summary>
        public bool HingeChangePending => _pendingHinge.HasValue;
        /// <summary>The leaf hangs on the side the player saved for this room (the pause menu's "Door hinge: flip").</summary>
        public bool HingeSaved { get; private set; }
        /// <summary>The leaf hangs on the opening's min-x jamb (door space); false: the max-x jamb.</summary>
        public bool HingeAtMin => _hingeAtMin;

        void OnDisable() => _hingeRoutine = null; // (its coroutine stopped with it; a later RechooseHinge starts a new one)

        /// <summary>
        /// Measure the hinge side again once the room has settled: <paramref name="settle"/> s of unscaled time (an app pause or a
        /// switch away counts as 0.1 s at most — 0.1.56's check fired 82 ms after a resume, in the middle of the device's re-send)
        /// and no DoorHoles rebuild pending. Called again before it ran, it waits for the later of the two. RoomScanner calls it
        /// for a rig it keeps through a re-sent room, DoorHoles after it rebuilt the walls. A shut, still, unheld leaf changes
        /// side at once; otherwise once it next is (never under a hand, mid-swing or kicked in).
        /// </summary>
        public void RechooseHinge(string why, float settle = RecheckSettle)
        {
            if (!isActiveAndEnabled || _hinge == null) return;
            _hingeWhy = why;
            if (_hingeRoutine != null) { _hingeWait = Mathf.Max(_hingeWait, settle); return; }
            _hingeWait = settle;
            _hingeRoutine = StartCoroutine(HingeWhenSettled());
        }

        IEnumerator HingeWhenSettled()
        {
            float held = 0f; // waiting on a pending wall rebuild after the settle time (capped: never stuck on it)
            while (_hingeWait > 0f || (DoorHoles.Instance != null && DoorHoles.Instance.Pending && held < MaxSettleWait))
            {
                yield return null;
                float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
                if (_hingeWait > 0f) _hingeWait -= dt; else held += dt;
            }
            _hingeRoutine = null;
            ChooseHinge(_hingeWhy);
        }

        void ChooseHinge(string why)
        {
            HingeChecks++;
            var geo = Geo;
            bool outline = FindDoorWall(geo, out var wall);
            float cornerMin = WallAlong(geo, outline, wall, true, out string whatMin), cornerMax = WallAlong(geo, outline, wall, false, out string whatMax);
            FitToRoom(outline, wall, cornerMin, cornerMax); // (before the sweep: a rig moved flush swings from its new place)
            float minSide = MaxClearAngle(true, cornerMin, outline ? wall.Index : -1), maxSide = MaxClearAngle(false, cornerMax, outline ? wall.Index : -1);
            _clearMin = minSide; _clearMax = maxSide;
            bool hingeAtMin = minSide >= maxSide;
            // Both swing clear: hang it like a real door, hinged on the side nearer the corner, so the open leaf folds back
            // toward that wall instead of standing out into the room (Brett 0.1.30: "can we have the door open the other way").
            if (Mathf.Abs(minSide - maxSide) < 15f && Mathf.Min(minSide, maxSide) >= 80f && Mathf.Abs(cornerMin - cornerMax) > 0.2f)
                hingeAtMin = cornerMin < cornerMax;
            string src = outline ? $"room outline, frame {wall.Depth * 100f:+0.0;-0.0} cm off the wall face" : "wall probe (no room outline)";
            if (cornerMin >= NoCorner && cornerMax >= NoCorner)
                Debug.LogWarning($"[HIS] Door: no corner found on either side of the {Label} ({src}): hinged by its swing clearance alone");
            string how = "auto";
            HingeSaved = SavedHinge(out bool saved);
            if (HingeSaved) { hingeAtMin = saved; how = "your side for this room"; }
            bool now = SetHingeSide(hingeAtMin);
            string seen = whatMin.Length > 0 || whatMax.Length > 0 ? $": {(whatMin.Length > 0 ? whatMin : "-")} / {(whatMax.Length > 0 ? whatMax : "-")}" : "";
            Debug.Log($"[HIS] Door: hinge on {(hingeAtMin ? "min" : "max")} side ({how}{(now ? "" : ", once the leaf is shut")}), swing stop {StopFor(hingeAtMin):F0}° " +
                      $"(clearance {minSide:F0}°/{maxSide:F0}°, walls {cornerMin:F2}/{cornerMax:F2} m along the wall{seen}; {src}) [{Label}, {why}]");
        }

        /// <summary>
        /// Hang the leaf from that side (true: the opening's min-x jamb), at once when it is shut, still and nobody has the handle,
        /// else as soon as it is (LateUpdate). Not saved: <see cref="FlipHinge"/> (the pause menu) saves; tests set a side with it.
        /// True when it took effect now.
        /// </summary>
        public bool SetHingeSide(bool hingeAtMin)
        {
            if (_hinge == null) return false;
            if (hingeAtMin == _hingeAtMin) { _pendingHinge = null; _stopAngle = StopFor(_hingeAtMin); return true; }
            if (!CanRehang) { _pendingHinge = hingeAtMin; return false; }
            _pendingHinge = null;
            HangOn(hingeAtMin);
            return true;
        }

        /// <summary>
        /// The pause menu's "Door hinge: flip" (Brett 0.1.56: "the door start opening the wrong way again"): the leaf hangs from its
        /// other jamb — at once if it is shut, else once it next is — and that side is saved for this room (PlayerPrefs, the door
        /// picker's per-room key + ".hinge": the front door's anchor and the hinge jamb in that anchor's own space), so it beats the
        /// auto pick on every load of the room: a re-send, a restart, a fresh rig on the same frame. Another room, another frame or
        /// the side door is picked automatically. Returns the new side.
        /// </summary>
        public bool FlipHinge()
        {
            bool to = !(_pendingHinge ?? _hingeAtMin);
            HingeSaved = SaveHinge(to);
            bool now = SetHingeSide(to);
            Debug.Log($"[HIS] Door: hinge flipped to the {(to ? "min" : "max")} side from the pause menu{(now ? "" : " (once the leaf is shut)")}, " +
                      $"{(HingeSaved ? $"saved for this room ({HingePrefKey(GameRoom)})" : "not saved (no room)")} [{Label}]");
            return to;
        }

        float StopFor(bool hingeAtMin) => Mathf.Clamp(hingeAtMin ? _clearMin : _clearMax, 60f, 100f);

        /// <summary>Shut, latched still and free: the leaf may change jambs without anyone seeing it jump.</summary>
        bool CanRehang => _hinge != null && State == DoorState.Closed && _holder == null && !Breached && !_scripted && Mathf.Abs(_angle) < 0.01f;

        void HangOn(bool hingeAtMin)
        {
            _hingeAtMin = hingeAtMin;
            _stopAngle = StopFor(hingeAtMin); // the swing stop is the hinged side's own clearance (not the larger of the two)
            const float cw = 0.06f, cd = 0.05f;
            BuildJambs(_roomSign * (0.015f - cd * 0.5f), cw, cd);
            Layout();
            UpdatePortal();
        }

        // ── saved per room (the flip row): the front door's own

        const string HingeSuffix = ".hinge";
        const float SavedHingeReach = 0.35f; // a saved hinge point this near one of the front door's jambs is its side

        /// <summary>The PlayerPrefs key of the front door's hinge side saved for <paramref name="room"/>: the door picker's per-room key + ".hinge".</summary>
        public static string HingePrefKey(MRUKRoom room) => room != null ? DoorPicker.PrefKey(room) + HingeSuffix : null;

        /// <summary>Forget the hinge side saved for <paramref name="room"/> (DoorPicker: another door was picked as the front door).</summary>
        public static void ForgetHinge(MRUKRoom room)
        {
            string key = HingePrefKey(room);
            if (key == null || !PlayerPrefs.HasKey(key)) return;
            PlayerPrefs.DeleteKey(key);
            Debug.Log("[HIS] Door: the saved hinge side is forgotten (another front door was picked)");
        }

        static MRUKRoom GameRoom => RoomScanner.Instance != null ? RoomScanner.Instance.Room : null;

        /// <summary>The scanned frame this rig hangs on (its parent; FitToRoom moves the rig, never the anchor).</summary>
        MRUKAnchor FrameAnchor => transform.parent != null ? transform.parent.GetComponent<MRUKAnchor>() : null;

        Vector3 JambAtFloor(bool atMin) => FramePoint(atMin ? _plane.xMin : _plane.xMax, _plane.yMin, 0f);

        /// <summary>
        /// Saves the front door's hinge jamb as "its anchor's UUID | the jamb in that anchor's own space | the jamb in world space".
        /// The anchor's space moves with the scan, so the side survives a restart. (The room's own space doesn't: MRUKRoom's
        /// transform stays the identity — the room entity has no pose — so it is world space, which changes on every launch; the
        /// 0.1.57 review found the flip lost on each one.) The world spot is only the fallback for a re-scan in the same session
        /// that put the frame under a new anchor, as DoorPicker's own.
        /// </summary>
        bool SaveHinge(bool atMin)
        {
            var room = GameRoom; var anchor = FrameAnchor;
            if (!IsFront || room == null || anchor == null || _hinge == null) return false;
            WriteHinge(room, anchor, JambAtFloor(atMin));
            return true;
        }

        static void WriteHinge(MRUKRoom room, MRUKAnchor anchor, Vector3 jamb)
        {
            PlayerPrefs.SetString(HingePrefKey(room), anchor.Anchor.Uuid.ToString("N") + "|" + V3(anchor.transform.InverseTransformPoint(jamb)) + "|" + V3(jamb));
            PlayerPrefs.Save();
        }

        /// <summary>
        /// The side saved for this room — the front door only — when the saved spot is one of its jambs: through its own anchor
        /// (same UUID), else, the frame re-scanned under a new anchor, by the world spot (then saved again under the new anchor).
        /// A saved side that names another anchor of the room (another frame), an older build's room-space point, or a spot that
        /// is no jamb of this door: false (the automatic pick).
        /// </summary>
        bool SavedHinge(out bool atMin)
        {
            atMin = _hingeAtMin;
            var room = GameRoom; var anchor = FrameAnchor;
            if (!IsFront || room == null || anchor == null) return false;
            var parts = PlayerPrefs.GetString(HingePrefKey(room), "").Split('|');
            if (parts.Length != 3 || !System.Guid.TryParse(parts[0], out var id) || !TryV3(parts[1], out var local) || !TryV3(parts[2], out var world)) return false;
            bool own = id == anchor.Anchor.Uuid;
            if (!own && NamesAnotherAnchor(room, id, anchor)) return false;
            var spot = own ? anchor.transform.TransformPoint(local) : world;
            float dMin = Vector3.Distance(spot, JambAtFloor(true)), dMax = Vector3.Distance(spot, JambAtFloor(false));
            if (Mathf.Min(dMin, dMax) > SavedHingeReach) return false;
            atMin = dMin < dMax;
            if (!own)
            {
                WriteHinge(room, anchor, JambAtFloor(atMin)); // under the frame's new anchor from now on
                Debug.Log($"[HIS] Door: the saved hinge side found by its spot (the frame has a new anchor), saved again under it [{Label}]");
            }
            return true;
        }

        static bool NamesAnotherAnchor(MRUKRoom room, System.Guid id, MRUKAnchor mine)
        {
            foreach (var a in room.Anchors) if (a != null && a != mine && a.Anchor.Uuid == id) return true;
            return false;
        }

        static string V3(Vector3 v) => string.Join(",", F3(v.x), F3(v.y), F3(v.z));
        static bool TryV3(string s, out Vector3 v)
        {
            v = default;
            var c = s.Split(',');
            if (c.Length != 3 || !TryF(c[0], out float x) || !TryF(c[1], out float y) || !TryF(c[2], out float z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        static string F3(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
        static bool TryF(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

        // ---------------------------------------------------------------- the door in the room outline

        const float NoCorner = 4.99f;                            // (5 m: nothing found)
        static readonly float[] CornerInsets = { 0.05f, 0.25f }; // how far into the room the lines along the wall run
        static readonly float[] ProbeDepths = { 0.15f, 0.4f, 0.8f }, ProbeHeights = { 0.5f, 1.2f }, LeafRayHeights = { 0.4f, 1.0f, 1.7f };

        static RoomGeometry Geo => RoomScanner.Instance != null ? RoomScanner.Instance.Geometry : null;

        /// <summary>A point in the scanned frame's own plane (door space as built), wherever <see cref="FitToRoom"/> moved the rig.</summary>
        Vector3 FramePoint(float x, float y, float z) => transform.TransformPoint(new Vector3(x, y, z - _roomSign * _wallShift));

        /// <summary>This door in the room outline (flat, world XZ): its wall, along it, into the room, and the frame plane's depth off the wall's face (+ = in the room).</summary>
        struct DoorWall { public int Index; public Vector2 A, U, Along, In, Center; public float Length, Depth; }

        /// <summary>The outline's wall the opening is in: parallel to it within 10°, its middle within 15 cm of the wall's line and on its stretch.</summary>
        bool FindDoorWall(RoomGeometry geo, out DoorWall w)
        {
            w = default;
            if (geo == null || geo.Walls.Count == 0 || _plane.width <= 0f) return false;
            var c = RoomGeometry.Flat(FramePoint(_plane.center.x, 0f, 0f));
            var along = RoomGeometry.Flat(transform.TransformDirection(Vector3.right));
            var inward = RoomGeometry.Flat(Inward);
            if (along.sqrMagnitude < 1e-6f || inward.sqrMagnitude < 1e-6f) return false;
            along.Normalize(); inward.Normalize();
            float best = 0.15f; bool found = false;
            for (int i = 0; i < geo.Walls.Count; i++)
            {
                var s = geo.Walls[i];
                var d = s.B - s.A; float len = d.magnitude;
                if (len < 0.1f) continue;
                var u = d / len;
                if (Mathf.Abs(Vector2.Dot(u, along)) < 0.985f) continue;
                float t = Vector2.Dot(c - s.A, u);
                if (t < -0.05f || t > len + 0.05f) continue;
                var foot = s.A + u * t;
                float off = (c - foot).magnitude;
                if (off >= best) continue;
                best = off; found = true;
                w = new DoorWall { Index = i, A = s.A, U = u, Length = len, Along = along, In = inward, Center = c, Depth = Vector2.Dot(c - foot, inward) };
            }
            return found;
        }

        /// <summary>From the min or max jamb along the wall to the corner (m): the room outline when it holds the door, else the physics probe.</summary>
        float WallAlong(RoomGeometry geo, bool outline, in DoorWall wall, bool fromMin, out string what) =>
            outline ? CornerGap(geo, wall, fromMin, out what) : WallAlongProbe(fromMin, out what);

        /// <summary>
        /// From the min or max jamb along the wall to the room's corner, m (negative: the frame runs into the corner wall; 5: none
        /// within 5 m). The first other wall a line just inside the room meets — run from the opening's middle, so a frame that
        /// overlaps the corner wall by a few cm still finds it — or this wall's own end where the next wall turns into the room.
        /// Whole walls: another door's hole in the corner wall hides nothing (0.1.55: the old Room-layer probe started 10 cm past a
        /// jamb 9 cm from the corner, i.e. behind the corner wall, and looked through a 1.65 m frame's hole: "walls 5.0/5.0").
        /// </summary>
        float CornerGap(RoomGeometry geo, in DoorWall w, bool fromMin, out string what)
        {
            what = "";
            float half = _plane.width * 0.5f, best = 5f;
            var dir = fromMin ? -w.Along : w.Along;
            foreach (float inset in CornerInsets)
            {
                var o = w.Center + w.In * inset;
                for (int i = 0; i < geo.Walls.Count; i++)
                {
                    var s = geo.Walls[i];
                    var d = s.B - s.A;
                    if (i == w.Index || d.sqrMagnitude < 1e-4f || Mathf.Abs(Vector2.Dot(d.normalized, w.U)) > 0.985f) continue; // its own wall, or one in line
                    if (!RoomGeometry.RaySegment(o, dir, s.A, s.B, out float t) || t < half - 0.1f) continue;                 // (not one across the opening)
                    if (t - half < best) { best = t - half; what = s.Invisible ? "the scan's edge" : "a wall"; }
                }
            }
            bool toB = Vector2.Dot(dir, w.U) > 0f;
            float tJamb = Vector2.Dot(w.Center - w.A, w.U) + (toB ? half : -half);
            float toEnd = toB ? w.Length - tJamb : tJamb;
            if (toEnd < best - 0.005f && TurnsIntoRoom(geo, toB ? w.A + w.U * w.Length : w.A, w)) { best = toEnd; what = "the wall's end"; }
            return Mathf.Max(best, -0.1f);
        }

        /// <summary>Another wall starts within 20 cm of this wall's end and runs on into the room from it: an inside corner.</summary>
        static bool TurnsIntoRoom(RoomGeometry geo, Vector2 end, in DoorWall w)
        {
            for (int i = 0; i < geo.Walls.Count; i++)
            {
                if (i == w.Index) continue;
                var s = geo.Walls[i];
                var d = s.B - s.A;
                if (d.sqrMagnitude < 1e-4f || Mathf.Abs(Vector2.Dot(d.normalized, w.U)) > 0.94f) continue; // in line (within 20°): no corner
                bool aNear = (s.A - end).sqrMagnitude <= (s.B - end).sqrMagnitude;
                Vector2 near = aNear ? s.A : s.B, far = aNear ? s.B : s.A;
                if ((near - end).sqrMagnitude > 0.04f) continue;
                if (Vector2.Dot(far - near, w.In) > 0.1f) return true;
            }
            return false;
        }

        /// <summary>
        /// The corner by physics, for a door the room outline doesn't hold: Room-layer rays along the wall from 2 cm inside the
        /// opening, 0.15 / 0.4 / 0.8 m into the room at two heights, the nearest hit (small ones count: a corner wall right at the
        /// jamb, or one with another door's hole further along it, still shows). 5: none within 5 m.
        /// </summary>
        float WallAlongProbe(bool fromMin, out string what)
        {
            what = "";
            int mask = LayerMask.GetMask("Room");
            if (mask == 0) return 5f;
            const float inside = 0.02f;
            float x = fromMin ? _plane.xMin + inside : _plane.xMax - inside, best = 5f;
            var d = transform.TransformDirection(new Vector3(fromMin ? -1f : 1f, 0f, 0f)).normalized;
            foreach (float depth in ProbeDepths)
                foreach (float y in ProbeHeights)
                {
                    var from = FramePoint(x, _plane.yMin + y, _roomSign * depth);
                    foreach (var hit in Physics.RaycastAll(from, d, 5f + inside, mask, QueryTriggerInteraction.Ignore))
                        if (hit.distance > 0.005f && hit.distance - inside < best) { best = hit.distance - inside; what = hit.collider.name; }
                }
            return best;
        }

        /// <summary>
        /// How far (degrees) a leaf hinged on that side swings into the room before it meets a wall of the room outline (whole
        /// walls: the hole another door's frame cut in the corner wall is no gap, 0.1.55) or real furniture (Room-layer rays at
        /// three heights). <paramref name="cornerGap"/>: that jamb to its corner; a frame scanned into the corner wall swings from
        /// 1 cm inside the room, or its leaf would "hit" the wall it hangs beside at every angle. <paramref name="ownWall"/>: the
        /// outline wall the door is in (skipped).
        /// </summary>
        float MaxClearAngle(bool hingeAtMin, float cornerGap, int ownWall)
        {
            int mask = LayerMask.GetMask("Room");
            var geo = Geo;
            bool outline = geo != null && geo.Walls.Count > 0;
            if (mask == 0 && !outline) return 95f;
            float w = _plane.width, dir = hingeAtMin ? 1f : -1f;
            float hingeX = (hingeAtMin ? _plane.xMin : _plane.xMax) + dir * Mathf.Max(0f, 0.01f - cornerGap);
            var hingeFlat = RoomGeometry.Flat(transform.TransformPoint(new Vector3(hingeX, _plane.yMin, _roomSign * 0.03f)));
            for (float a = 10f; a <= 100f; a += 10f)
            {
                float signedA = (hingeAtMin ? -_roomSign : _roomSign) * a;
                var leafDirLocal = Quaternion.Euler(0, signedA, 0) * new Vector3(dir, 0, 0);
                var dirWorld = transform.TransformDirection(leafDirLocal).normalized;
                if (outline)
                {
                    var tip = hingeFlat + RoomGeometry.Flat(dirWorld).normalized * (w * 0.95f);
                    if (geo.CrossesWall(hingeFlat, tip, ownWall)) return a - 10f;
                }
                if (mask == 0) continue;
                foreach (float y in LeafRayHeights)
                {
                    var from = transform.TransformPoint(new Vector3(hingeX, _plane.yMin + y, _roomSign * 0.03f));
                    if (Physics.Raycast(from, dirWorld, w * 0.95f, mask, QueryTriggerInteraction.Ignore)) return a - 10f;
                }
            }
            return 100f;
        }

        // ---------------------------------------------------------------- fit to the room, the doorway's stencil mark

        const float MaskMargin = 0.07f;   // the depth mask past each jamb: the 6 cm casing + 1 cm (covers angles up to ~60°)
        const float FlushMax = 0.10f;     // a frame further than this behind its wall's face is left where it was scanned
        float _wallShift;                 // the rig moved this far into the room (m), flush with its wall's face
        float _maskMarginMin = MaskMargin, _maskMarginMax = MaskMargin; // the shut leaf's depth-mask margins (to just past a near corner)
        bool _maskAtCasing;               // the leaf is off its seat: the mask is back to the casing margin each side

        /// <summary>
        /// The rig against the room outline. (1) A frame scanned behind its wall's face (Brett 0.1.56, on the re-scan: "the doors a
        /// little deeper in") is brought forward flush with it, up to 10 cm; one on or in front of the face stays put. (2) A jamb
        /// within 20 cm of a corner gets the depth mask widened to 2 cm past the corner while the leaf is shut: the casing margin
        /// alone ended ~2 cm short of the corner on the re-scan, where the real wall's depth is noisiest (the 0.1.8 "real jamb
        /// half-hid the leaf" flicker). Past the casing that strip is bare real wall, and the mask stops it hiding anything virtual
        /// behind it (EnvironmentDepthManager's 10% mask bias: the corner wall's own face too). Shut, nothing out there is drawn
        /// (the outside is seen only through the open doorway); open, a man beside the doorway would show through the wall, so
        /// the mask is back to the casing margin until the leaf is shut again (UpdatePortal).
        /// </summary>
        void FitToRoom(bool hasWall, in DoorWall wall, float cornerMin, float cornerMax)
        {
            float shift = hasWall && wall.Depth < -0.005f && wall.Depth > -FlushMax ? -wall.Depth : 0f;
            if (Mathf.Abs(shift - _wallShift) > 0.002f)
            {
                _wallShift = shift;
                transform.localPosition = new Vector3(0f, 0f, _roomSign * _wallShift);
                Debug.Log($"[HIS] Door: {Label} frame {(shift > 0f ? $"{shift * 100f:F1} cm behind its wall's face: moved flush" : "back on its scanned plane")}");
            }
            float mMin = cornerMin < 0.2f ? Mathf.Max(MaskMargin, cornerMin + 0.02f) : MaskMargin;
            float mMax = cornerMax < 0.2f ? Mathf.Max(MaskMargin, cornerMax + 0.02f) : MaskMargin;
            if (Mathf.Abs(mMin - _maskMarginMin) > 0.001f || Mathf.Abs(mMax - _maskMarginMax) > 0.001f)
            {
                _maskMarginMin = mMin; _maskMarginMax = mMax;
                SizeDepthMask();
                Debug.Log($"[HIS] Door: {Label} depth mask {mMin * 100f:F0}/{mMax * 100f:F0} cm past the jambs while shut, {MaskMargin * 100f:F0} cm open (corners {cornerMin:F2}/{cornerMax:F2} m)");
            }
        }

        /// <summary>
        /// The depth mask's width: the opening plus each side's margin — the casing, or while the leaf is shut, to just past a near
        /// corner (<see cref="FitToRoom"/>).
        /// </summary>
        void SizeDepthMask()
        {
            if (_depthMask == null) return;
            var m = DepthMaskMargins;
            var t = _depthMask.transform;
            float x0 = _plane.xMin - m.x, x1 = _plane.xMax + m.y;
            t.localPosition = new Vector3((x0 + x1) * 0.5f, t.localPosition.y, t.localPosition.z);
            t.localScale = new Vector3(x1 - x0, t.localScale.y, 1f);
        }

        /// <summary>How far the depth mask reaches past the min / max jamb now (m; tests): the casing margin while the leaf is off its seat.</summary>
        public Vector2 DepthMaskMargins => _maskAtCasing ? new Vector2(MaskMargin, MaskMargin) : new Vector2(_maskMarginMin, _maskMarginMax);
        /// <summary>How far the rig was moved into the room to sit flush with its wall's face (m; 0: on its scanned plane).</summary>
        public float WallShift => _wallShift;

        /// <summary>
        /// The doorway's stencil mark is drawn only while the leaf is off its seat (open, opening, kicked in, popped by a kick):
        /// shut, the 3–8 mm gaps between the leaf and the opening let the black box (HIS/DoorVoid) draw thin black lines that crawled
        /// with every head movement (Brett 0.1.56: "the edges flicker black"). Open, the box and the porch show through it as before.
        /// The depth mask follows the same seat: widened to a near corner only while shut (FitToRoom), the casing margin otherwise.
        /// </summary>
        void UpdatePortal()
        {
            bool on = State != DoorState.Closed || Mathf.Abs(_angle) > 0.3f;
            if (on != _maskAtCasing) { _maskAtCasing = on; SizeDepthMask(); }
            if (_portalMask == null) return;
            if (_portalMask.enabled != on) _portalMask.enabled = on;
        }

        /// <summary>The doorway's stencil mark is drawn now (tests): only while the leaf is off its seat.</summary>
        public bool PortalMarked => _portalMask != null && _portalMask.enabled;

        // ---------------------------------------------------------------- behaviour

        /// <summary>Knuckles on the door: a faint tremble (knocking never looks like forcing).</summary>
        public void Knock(float strength = 1f)
        {
            Restart(Tremble(0.0006f * strength, 0.15f));
            Sfx.Play("door.knock", LatchWorld(), Mathf.Clamp01(0.6f + 0.3f * strength));
        }

        /// <summary>Fist pounding: the leaf judders in its frame and dust falls from the head casing.</summary>
        public void Pound(float strength = 1f)
        {
            Restart(Tremble(0.0025f * strength, 0.3f));
            EmitDust(4);
        }

        /// <summary>A kick that the latch survives: the leaf pops in a few degrees and springs back, splinters at the latch.</summary>
        public void KickImpact(float strength = 1f)
        {
            if (State != DoorState.Closed) return;
            DropHold();                           // a hand holding it shut is knocked off it
            Restart(KickSpring(3.5f * strength));
            EmitAtLatch(_splinters, Mathf.RoundToInt(6 * strength));
            EmitDust(8);
            Sfx.Play("door.kick", LatchWorld(), 1f);
        }

        /// <summary>The latch fails: the chunk of frame tears off and the leaf slams open on its hinges.</summary>
        public void Breach(float degreesPerSecond = 650f)
        {
            if (State != DoorState.Closed) return;
            DropHold();
            Breached = true;
            State = DoorState.Opening;
            _leaf.localPosition = _leafRestLocal; // a kick/thump still ringing out
            DetachLatchChunk();
            EmitAtLatch(_splinters, 30);
            EmitDust(16);
            Sfx.Play("door.burst", LatchWorld(), 1f);
            _angularVel = degreesPerSecond;
            Restart(SwingOpen());
        }

        /// <summary>Someone opens it normally (unlocked door, family member with a key).</summary>
        public void OpenGently(float seconds = 1.6f, float degrees = 80f)
        {
            if (State != DoorState.Closed) return;
            DropHold();
            State = DoorState.Opening;
            SetObstacle(false);
            Sfx.Play("door.open", LatchWorld(), 0.8f);
            Restart(Ease(0f, Mathf.Min(degrees, _stopAngle), seconds, DoorState.Open));
            _scripted = true;
        }

        /// <summary>
        /// Swing shut (animated). Leaves the latch chunk off if it was breached, but the handle works again once it's shut:
        /// Sandbox only resets the door on death, and the next man finds it shut (research/25).
        /// </summary>
        public void Close(float seconds = 0.8f)
        {
            if (State == DoorState.Closed) return;
            DropHold();
            OpenedByPlayer = false;
            SetObstacle(false);
            Sfx.Play("door.close", LatchWorld(), 0.7f);
            Restart(Ease(OpenAngle, 0f, seconds, DoorState.Closed));
            _scripted = true;
        }

        /// <summary>Closed, intact and visible — for a new round/session.</summary>
        public void ResetDoor()
        {
            if (_motion != null) StopCoroutine(_motion);
            _motion = null;
            DropHold();
            Breached = OpenedByPlayer = _scripted = false;
            State = DoorState.Closed;
            SetObstacle(false);
            _angle = 0f; _angularVel = 0f;
            ApplyAngle();
            _leaf.localPosition = _leafRestLocal;
            TintHandle(false);
            RestoreLatchChunk();
            ClearHoles();
            foreach (var src in _thru) if (src != null) src.Stop();
            StopVoiceThrough();
        }

        /// <summary>A body dropping against the closed leaf (the man behind it was shot): a heavy judder, dust, a dull thud.</summary>
        /// <param name="lowpassHz">&gt; 0: the thud heard through the leaf (a body on the far side), dull.</param>
        public void Thump(float strength = 1f, float lowpassHz = 0f)
        {
            if (State != DoorState.Closed) return;
            Restart(Tremble(0.004f * strength, 0.4f));
            EmitDust(6);
            PlayThrough("door.pound", transform.TransformPoint(new Vector3(_plane.center.x, _plane.yMin + 0.9f, 0f)), strength, 0.7f, lowpassHz);
        }

        // ---------------------------------------------------------------- your hand on the handle (research/25)
        // Brett 2026-10-01: "adding a way to open the door yourself before someone breaks in". An empty hand squeezes grip
        // at the room-side lever (HandController.TryDoor) and the leaf follows it round the hinge. DoorState is unchanged:
        // Closed = latched at 0°; a leaf the hand moves is always Open, never Opening (ExteriorDoor's kick dust and burst
        // splinters key off those two), and Opening stays scripted (OpenGently, Breach).

        /// <summary>The latch was kicked out (<see cref="Breach"/>): no working handle until it's shut again (Close) or reset.</summary>
        public bool Breached { get; private set; }
        /// <summary>
        /// The player unlatched it by hand and it hasn't latched since: whoever is at it sees him (Intruder.WatchDoor), and an
        /// entry through it isn't forced (the judge's PC 198.5 presumption needs the door kicked in).
        /// </summary>
        public bool OpenedByPlayer { get; private set; }
        /// <summary>A hand has the handle (one hand at a time).</summary>
        public bool HeldByPlayer => _holder != null;
        /// <summary>This hand has the handle; false once scripted motion took it (a kick, he shoves it wide, the round resets).</summary>
        public bool IsHeldBy(Component hand) => hand != null && _holder == hand;
        /// <summary>How far it swings into the room before real furniture/walls (the stop measured when the room loaded).</summary>
        public float MaxOpenAngle => _stopAngle;
        /// <summary>The hinge line at the floor, world space.</summary>
        public Vector3 HingeWorld => _hinge.position;
        /// <summary>The room-side lever handle (its rose on the leaf's face), world space.</summary>
        public Vector3 HandleWorld => _handle.position;
        /// <summary>
        /// How far a hand may be from the grab strip and still take it. Shut, a generous 25 cm: the squeeze comes before the
        /// controller meets the real door or its real handle, which can sit right where the virtual lever is.
        /// </summary>
        public float GrabReach => State == DoorState.Closed ? 0.25f : 0.2f;

        const float GrabStripLow = 0.80f, GrabStripHigh = 1.15f, GrabStripIn = 0.065f; // above the floor; in from the free edge
        const float UnlatchAngle = 4f, LatchAngle = 2f, AutoCloseAngle = 8f;           // degrees
        const float HandMaxSpeed = 400f, CoastMinSpeed = 30f, CoastFriction = 2f;     // °/s, °/s, 1/s

        Component _holder;          // the HandController on the handle
        float _grabOffset;          // leaf angle minus the hand's angle round the hinge at the grab: the leaf doesn't jump
        float _handAngle, _heldVel; // the hand's unwrapped angle; the leaf's speed while held (°/s), carried on at release
        bool _scripted;             // a scripted ease is moving the leaf (OpenGently, Close, MakeWay): no hand, no second MakeWay

        bool SomeoneInDoorway => HIS.Intruder.Intruder.AnyInDoorway(this);

        /// <summary>
        /// From <paramref name="p"/> to the grab strip: a 35 cm vertical line through the lever (0.80–1.15 m up, 6.5 cm in from
        /// the free edge, mid-thickness of the leaf), so either face works, shut or open (open, it's the free edge you take).
        /// </summary>
        public float GrabDistance(Vector3 p)
        {
            if (_leaf == null) return float.MaxValue;
            float dir = _hingeAtMin ? 1f : -1f, centreY = 0.004f + _leafSize.y * 0.5f; // leaf space: its middle, mid-thickness
            float x = dir * (_leafSize.x * 0.5f - GrabStripIn);
            float y = Mathf.Clamp(_leaf.InverseTransformPoint(p).y, GrabStripLow - centreY, GrabStripHigh - centreY);
            return Vector3.Distance(p, _leaf.TransformPoint(new Vector3(x, y, 0f)));
        }

        /// <summary>
        /// A hand may take the handle: the latch isn't kicked out and no scripted swing is moving the leaf (a key or the
        /// burglar's pry easing it open, Close, MakeWay). Caught mid-swing, it was an open door nobody let him through: pushed
        /// shut, the man still hidden behind it walked in through the closed leaf, unseen, and every round at him was judged
        /// "fired through a closed door" (review 2026-10-01). Each such man is Entering (InDoorwayOf) before his ease ends.
        /// </summary>
        public bool HandleFree => !Breached && !_scripted && State != DoorState.Opening;

        /// <summary>
        /// A hand takes the handle at <paramref name="at"/>. Refused unless <see cref="HandleFree"/>, or when the other hand has
        /// it. Catches what the leaf was doing on its own (a coast after a let-go, a tremble, a kick still ringing out).
        /// </summary>
        public bool GrabHandle(Component hand, Vector3 at)
        {
            if (hand == null || _leaf == null || !HandleFree || (_holder != null && _holder != hand)) return false;
            if (_motion != null) { StopCoroutine(_motion); _motion = null; }
            _leaf.localPosition = _leafRestLocal;
            if (State == DoorState.Closed && _angle != 0f) { _angle = 0f; ApplyAngle(); }
            _holder = hand;
            _angularVel = _heldVel = 0f;
            _handAngle = HandAngle(at, _angle);
            _grabOffset = _angle - _handAngle;
            // Held, it keeps the shut leaf's 0.06: at 0.25 your own fingers on it (0-15 cm in front of it) only hid it once
            // they were 25% nearer, so the leaf and lever drew over your hand.
            SetLeafDepthBias(ClosedLeafDepthBias);
            TintHandle(true);
            if (State == DoorState.Closed && Sfx.Has("door.handle")) Sfx.Play("door.handle", LatchWorld(), 0.5f);
            return true;
        }

        /// <summary>
        /// The leaf follows the hand round the hinge (pull to open, push to shut), at most 400°/s so a tracking jump can't slam
        /// it, between shut and its swing stop. Shut, it stays latched until the hand has pulled it 4°; brought back under 2°
        /// it clicks shut. It never swings shut on someone in the doorway (Intruder.AnyInDoorway): then it only opens wider.
        /// Only the hand that holds it drives it (a hold dropped earlier this frame does nothing).
        /// </summary>
        public void DriveHandle(Component hand, Vector3 at, float dt)
        {
            if (hand == null || _holder != hand || dt <= 0f) return;
            _handAngle = HandAngle(at, _handAngle);
            float target = Mathf.Clamp(_handAngle + _grabOffset, 0f, _stopAngle);
            if (State == DoorState.Closed)
            {
                if (target < UnlatchAngle) return;                  // latched: the hand only works the lever
                Unlatch();
            }
            float next = Mathf.MoveTowards(_angle, target, HandMaxSpeed * dt);
            if (next < _angle && SomeoneInDoorway) next = _angle;
            _heldVel = Mathf.Lerp(_heldVel, (next - _angle) / dt, 0.5f);
            _angle = next;
            ApplyAngle();
            if (_angle < LatchAngle && target < LatchAngle) Latch("by hand");
        }

        /// <summary>
        /// The hand lets go. Still moving (over 30°/s), the leaf carries on round the hinge, slowing (no slam at the stop), and
        /// latches if it gets under 2° closing: a push shuts it from either face. Slow and under 8°, it swings to and latches.
        /// Otherwise it stays where it is. Never shut onto someone in the doorway.
        /// </summary>
        public void ReleaseHandle(Component hand)
        {
            if (hand == null || _holder != hand) return;
            DropHold();
            if (State == DoorState.Closed) return;
            if (Mathf.Abs(_heldVel) > CoastMinSpeed) Restart(Coast(_heldVel));
            else if (_angle < AutoCloseAngle) Restart(Coast(-60f));
        }

        /// <summary>
        /// Someone's coming through a door the player opened (or has hold of): the hold drops (he shoves it out of your hand)
        /// and a leaf left ajar swings to min(80°, the stop) over <paramref name="seconds"/>, so it can't block the doorway link.
        /// True when it moves (the caller waits for it). A kicked-in door, a shut one, one already wide: false.
        /// </summary>
        public bool MakeWay(float seconds = 0.35f)
        {
            if (!(OpenedByPlayer || HeldByPlayer) || Breached || State == DoorState.Closed || _scripted) return false;
            DropHold();
            float to = Mathf.Min(80f, _stopAngle);
            if (_angle >= to - 1f) return false;
            Restart(Ease(_angle, to, seconds, DoorState.Open));
            _scripted = true;
            return true;
        }

        /// <summary>
        /// An empty hand is in reach of the handle this frame (HandController): the lever lights up warm for a moment, so the
        /// squeeze comes before the controller meets the real door.
        /// </summary>
        public void HoverHandle() { _hoverUntil = Time.time + 0.15f; TintHandle(true); }

        float _hoverUntil;
        bool _tinted;
        Color _metalColor;
        float _metalMetallic;
        static readonly Color HandleLit = new(1f, 0.8f, 0.42f);
        static readonly int MetallicId = Shader.PropertyToID("_Metallic");

        void LateUpdate()
        {
            if (_tinted && _holder == null && Time.time > _hoverUntil) TintHandle(false);
            // A side picked (or flipped from the pause menu) while the leaf was off its seat: taken once it's shut and still.
            if (_pendingHinge.HasValue && CanRehang)
            {
                bool to = _pendingHinge.Value;
                _pendingHinge = null;
                HangOn(to);
                Debug.Log($"[HIS] Door: hinge moved to the {(to ? "min" : "max")} side now the leaf is shut [{Label}]");
            }
            UpdatePortal(); // (state changes without an angle change: opened by hand at 0°, latched, reset)
        }

        void TintHandle(bool on)
        {
            var m = _mats.Metal;
            if (on == _tinted || m == null) return;
            bool metallic = m.HasProperty(MetallicId);
            if (on) { _metalColor = m.color; if (metallic) _metalMetallic = m.GetFloat(MetallicId); }
            _tinted = on;
            m.color = on ? HandleLit : _metalColor;
            if (metallic) m.SetFloat(MetallicId, on ? 0.2f : _metalMetallic); // a warm glow, not just a tinted reflection
        }

        void Unlatch()
        {
            State = DoorState.Open;                 // before the angle moves, in the same frame
            OpenedByPlayer = true;
            SetObstacle(true);                      // night ambience, NavMesh carve, the Door-layer collider
            SetLeafDepthBias(ClosedLeafDepthBias);  // (held: see GrabHandle)
            Sfx.Play("door.open", LatchWorld(), 0.6f);
            Debug.Log($"[HIS] Door: opened by hand ({Label})");
        }

        void Latch(string how)
        {
            _angle = 0f; _angularVel = _heldVel = 0f;
            ApplyAngle();
            State = DoorState.Closed;
            OpenedByPlayer = false;
            SetObstacle(false);
            Sfx.Play("door.close", LatchWorld(), 0.7f);
            Debug.Log($"[HIS] Door: shut {how} ({Label})");
        }

        /// <summary>The hand comes off without moving the leaf (it let go, scripted motion took over, the round reset): open, its open-leaf depth bias comes back.</summary>
        void DropHold()
        {
            if (_holder == null) return;
            _holder = null;
            SetLeafDepthBias(State == DoorState.Open ? OpenLeafDepthBias : ClosedLeafDepthBias);
        }

        /// <summary>The hand's angle round the hinge (0 = along the shut leaf, + into the room), unwrapped near <paramref name="near"/>; unreadable within 15 cm of the hinge line.</summary>
        float HandAngle(Vector3 at, float near)
        {
            var v = at - _hinge.position;
            float x = Vector3.Dot(v, HingeToLatch), z = Vector3.Dot(v, Inward);
            if (x * x + z * z < 0.15f * 0.15f) return near;
            return near + Mathf.DeltaAngle(near, Mathf.Atan2(z, x) * Mathf.Rad2Deg);
        }

        /// <summary>The leaf carries on after the hand let go (<see cref="ReleaseHandle"/>): hinge friction, no bounce at the stop.</summary>
        IEnumerator Coast(float vel)
        {
            while (State == DoorState.Open)
            {
                float dt = Time.deltaTime;
                if (dt <= 0f) { yield return null; continue; } // paused
                vel -= vel * Mathf.Min(1f, CoastFriction * dt);
                if (vel < 0f && SomeoneInDoorway) break;     // stops dead short of him
                _angle = Mathf.Clamp(_angle + vel * dt, 0f, _stopAngle);
                ApplyAngle();
                if (vel < 0f && _angle < LatchAngle) { Latch("(swung to)"); break; }
                if (_angle >= _stopAngle || Mathf.Abs(vel) < 4f) break;
                yield return null;
            }
        }

        // ---------------------------------------------------------------- rounds through the leaf

        /// <summary>A ray through the leaf slab (closed, opening or open), entry and exit.</summary>
        public struct LeafHit
        {
            public float Distance, ExitDistance;  // along the ray, metres
            public Vector3 Point, Normal, ExitPoint;
            public Vector3 LocalIn, LocalInNormal, LocalOut, LocalOutNormal; // leaf space (for the holes)
        }

        /// <summary>
        /// The leaf has no bullet collider (its Door-layer collider exists only while it stands open, for bodies and pathing),
        /// so guns test the slab here: a ray/box test in leaf space. False when the ray starts inside the slab (a contact shot).
        /// </summary>
        public bool RaycastLeaf(Vector3 origin, Vector3 dir, float maxDistance, out LeafHit hit)
        {
            hit = default;
            if (_leaf == null) return false;
            var o = _leaf.InverseTransformPoint(origin);
            var d = _leaf.InverseTransformVector(dir); // t stays in world metres
            var half = new Vector3(_leafSize.x * 0.5f, _leafSize.y * 0.5f, LeafThickness * 0.5f);
            float tIn = float.NegativeInfinity, tOut = maxDistance;
            int axisIn = -1, axisOut = -1; float signIn = 0f, signOut = 0f;
            for (int a = 0; a < 3; a++)
            {
                if (Mathf.Abs(d[a]) < 1e-7f) { if (Mathf.Abs(o[a]) > half[a]) return false; continue; }
                float inv = 1f / d[a];
                float t1 = (-half[a] - o[a]) * inv, t2 = (half[a] - o[a]) * inv, n = -1f;
                if (t1 > t2) { (t1, t2) = (t2, t1); n = 1f; } // travelling -: in through the + face
                if (t1 > tIn) { tIn = t1; axisIn = a; signIn = n; }
                if (t2 < tOut) { tOut = t2; axisOut = a; signOut = -n; }
                if (tIn > tOut) return false;
            }
            if (axisIn < 0 || tIn <= 0f || tIn > maxDistance) return false;
            var nIn = Vector3.zero; nIn[axisIn] = signIn;
            var nOut = Vector3.zero; if (axisOut >= 0) nOut[axisOut] = signOut;
            hit.Distance = tIn; hit.ExitDistance = tOut;
            hit.LocalIn = o + d * tIn; hit.LocalOut = o + d * tOut;
            hit.LocalInNormal = nIn; hit.LocalOutNormal = nOut;
            hit.Point = origin + dir * tIn; hit.ExitPoint = origin + dir * tOut;
            hit.Normal = _leaf.TransformDirection(nIn);
            return true;
        }

        /// <summary>
        /// A round through the leaf: an entry hole (and a torn exit hole on the far face for a bullet), a small puff of
        /// splinters back toward the shooter, and a dull wooden crack.
        /// </summary>
        /// <summary>
        /// A round went through this leaf (after its own hole/splinters on our side): the door camera's porch mirrors it on
        /// the outside face — the exit hole, splinters blown out onto the porch (Brett 0.1.22: "see door splintering through
        /// camera"). Args: the door, the hit (world ExitPoint / leaf-space LocalOut), the round's direction, pellet or not.
        /// </summary>
        public static event System.Action<VirtualDoor, LeafHit, Vector3, bool> LeafShot;

        /// <summary>
        /// When the player last put a round through this leaf (Time.time; −100 = never), and from where: his muzzle. A hitman
        /// stacked behind it answers back along that line (research/27 §2, the blind volley) — never toward where he is now.
        /// </summary>
        public float LastInboundAt { get; private set; } = -100f;
        /// <summary>The player's muzzle when his round last went through this leaf (<see cref="LastInboundAt"/>).</summary>
        public Vector3 LastInboundFrom { get; private set; }

        /// <summary>A round through the leaf: its holes, the splinter puff, the crack (and the porch's mirror of it, LeafShot).</summary>
        /// <param name="shooter">The player's muzzle (Firearm): recorded as <see cref="LastInboundFrom"/>. Null for anyone else's round.</param>
        /// <param name="fromOutside">A round fired in from outside (a hitman's blind volley): the splinters blow off the room
        /// face, toward the room, where the player sees them (else they'd puff out on the far side of the leaf).</param>
        public void BulletHole(in LeafHit h, Vector3 dir, bool pellet, bool sound, Vector3? shooter = null, bool fromOutside = false)
        {
            if (shooter.HasValue) { LastInboundAt = Time.time; LastInboundFrom = shooter.Value; }
            LeafShot?.Invoke(this, h, dir, pellet);
            PlaceHole(h.LocalIn, h.LocalInNormal, pellet ? 0.009f : 0.011f);
            if (!pellet && h.LocalOutNormal != Vector3.zero) PlaceHole(h.LocalOut, h.LocalOutNormal, 0.022f);
            if (_splinters != null)
            {
                if (fromOutside) _splinters.transform.SetPositionAndRotation(h.ExitPoint, Quaternion.LookRotation(dir.normalized));
                else _splinters.transform.SetPositionAndRotation(h.Point, Quaternion.LookRotation((h.Normal - dir).normalized));
                _splinters.Emit(pellet ? 2 : 5);
            }
            if (sound) PlayThrough("impact.wall", h.Point, pellet ? 0.6f : 0.8f, 0.78f); // pitched down: wood, not plaster
        }

        const int MaxHoles = 40; // a shotgun blast puts 9 in; the oldest are reused
        readonly Transform[] _holes = new Transform[MaxHoles];
        int _holeNext;

        void PlaceHole(Vector3 localPos, Vector3 localNormal, float size)
        {
            var t = _holes[_holeNext];
            if (t == null) t = _holes[_holeNext] = MakeHole();
            _holeNext = (_holeNext + 1) % MaxHoles;
            // The raised panels stand 4 mm proud of the slab on the room side: a hole there sits on the panel's face.
            // 3 mm off the face: the eye buffer's depth is 16-bit (a step is 1.4 mm at 3 m, 2.4 mm at 4 m), so 0.8 mm
            // z-fought with the leaf from about 2.3 m out (the holes flickered).
            float lift = 0.003f + (OnPanel(localPos, localNormal) ? 0.004f : 0f);
            t.localPosition = localPos + localNormal * lift;
            var up = Mathf.Abs(localNormal.y) > 0.9f ? Vector3.forward : Vector3.up;
            t.localRotation = Quaternion.LookRotation(-localNormal, up) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            t.localScale = new Vector3(size, size, size);
            t.gameObject.SetActive(true);
        }

        bool OnPanel(Vector3 p, Vector3 n)
        {
            if (n.z * _roomSign < 0.9f) return false; // the panels are on the room-side face only
            float lw = _leafSize.x, lh = _leafSize.y;
            if (Mathf.Abs(p.x) > lw * 0.31f) return false;
            return Mathf.Abs(p.y - lh * 0.2f) < lh * 0.17f || Mathf.Abs(p.y + lh * 0.25f) < lh * 0.15f;
        }

        Transform MakeHole()
        {
            var go = new GameObject("Hole");
            go.transform.SetParent(_leaf, false);
            go.AddComponent<MeshFilter>().sharedMesh = HoleMesh();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = new[] { _woodMat != null ? _woodMat : _mats.Leaf, _holeMat != null ? _holeMat : _mats.Void };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            return go.transform;
        }

        void ClearHoles()
        {
            foreach (var t in _holes) if (t != null) t.gameObject.SetActive(false);
            _holeNext = 0;
        }

        /// <summary>
        /// The leaf's bullet-hole mesh (unit size, front face toward −Z: a torn ring, submesh 0, round a dark core, submesh 1),
        /// shared: the Marshal weapons check's cardboard target punches the same holes (Room/PracticeTarget).
        /// </summary>
        public static Mesh BulletHoleMesh => HoleMesh();

        // A bullet hole, unit size, front face toward -Z like Unity's quad: a jagged ring of torn wood (submesh 0) around
        // a dark core (submesh 1).
        static Mesh _holeMesh;
        static Mesh HoleMesh()
        {
            if (_holeMesh != null) return _holeMesh;
            const int n = 12;
            var v = new Vector3[1 + 2 * n];
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                float core = 0.42f + 0.06f * Mathf.Sin(i * 2.7f), rim = (i & 1) == 0 ? 1f : 0.68f + 0.1f * Mathf.Sin(i * 1.3f);
                v[1 + i] = new Vector3(Mathf.Cos(a) * core, Mathf.Sin(a) * core, 0f);
                v[1 + n + i] = new Vector3(Mathf.Cos(a) * rim, Mathf.Sin(a) * rim, 0f);
            }
            var ring = new int[n * 6]; var disc = new int[n * 3];
            for (int i = 0; i < n; i++)
            {
                int a = 1 + i, b = 1 + (i + 1) % n, A = 1 + n + i, B = 1 + n + (i + 1) % n;
                // Clockwise seen from -Z (Unity's front face).
                ring[i * 6] = a; ring[i * 6 + 1] = B; ring[i * 6 + 2] = A;
                ring[i * 6 + 3] = a; ring[i * 6 + 4] = b; ring[i * 6 + 5] = B;
                disc[i * 3] = 0; disc[i * 3 + 1] = b; disc[i * 3 + 2] = a;
            }
            var normals = new Vector3[v.Length]; var tangents = new Vector4[v.Length]; var uv = new Vector2[v.Length];
            for (int i = 0; i < v.Length; i++) { normals[i] = Vector3.back; tangents[i] = new Vector4(1f, 0f, 0f, 1f); uv[i] = new Vector2(0.5f, 0.5f); }
            _holeMesh = new Mesh { name = "BulletHole", vertices = v, normals = normals, tangents = tangents, uv = uv, subMeshCount = 2 };
            _holeMesh.SetTriangles(ring, 0);
            _holeMesh.SetTriangles(disc, 1);
            _holeMesh.RecalculateBounds();
            return _holeMesh;
        }

        // ---------------------------------------------------------------- sound through the leaf

        // Sfx has no per-play pitch or filter, so sounds off/through the leaf get three sources of their own (clips, mix
        // volume and distances still come from the SfxLibrary). They pause with the AudioListener like everything else.
        readonly AudioSource[] _thru = new AudioSource[3];
        readonly AudioLowPassFilter[] _thruLp = new AudioLowPassFilter[3];
        int _thruNext;
        // The man behind the door talking: a source of his own, so the wood cracks of the next rounds (and his grunts) on the
        // round robin can never cut a line off mid-word; the speaker stops it himself (a new line, his death).
        AudioSource _voiceSrc;
        AudioLowPassFilter _voiceLp;

        void BuildThroughSources()
        {
            for (int i = 0; i < _thru.Length; i++) _thru[i] = ThroughSource("ThroughSfx" + i, out _thruLp[i]);
            _voiceSrc = ThroughSource("ThroughVoice", out _voiceLp);
        }

        AudioSource ThroughSource(string name, out AudioLowPassFilter lp)
        {
            var go = new GameObject(name); go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false; src.spatialBlend = 1f; src.rolloffMode = AudioRolloffMode.Logarithmic;
            src.dopplerLevel = 0f; src.spread = 0f;
            lp = go.AddComponent<AudioLowPassFilter>(); lp.enabled = false;
            return src;
        }

        /// <summary>
        /// A library sound at <paramref name="position"/> with its own pitch and low-pass: a wood crack off the leaf, or the
        /// man behind it heard through the door (lowpassHz ~600–900 = muffled). Falls back to Sfx when the door has no sources.
        /// </summary>
        public void PlayThrough(string key, Vector3 position, float volume, float pitch = 1f, float lowpassHz = 0f)
        {
            var lib = Sfx.Instance != null ? Sfx.Instance.Library : null;
            var e = lib != null ? lib.Find(key) : null;
            var src = _thru[_thruNext];
            if (e == null || src == null) { Sfx.Play(key, position, volume); return; }
            var lp = _thruLp[_thruNext];
            if (PlayOn(src, lp, lib, e, position, volume, pitch, lowpassHz)) _thruNext = (_thruNext + 1) % _thru.Length;
        }

        /// <summary>
        /// His voice through the closed leaf, muffled, on the door's voice source (a new line replaces the old one).
        /// False when it can't play here (no source / unknown key / cooling down): the caller plays it the normal way.
        /// </summary>
        public bool PlayVoiceThrough(string key, Vector3 position, float volume, float lowpassHz)
        {
            var lib = Sfx.Instance != null ? Sfx.Instance.Library : null;
            var e = lib != null ? lib.Find(key) : null;
            return e != null && _voiceSrc != null && PlayOn(_voiceSrc, _voiceLp, lib, e, position, volume, 1f, lowpassHz);
        }

        /// <summary>Cut his line through the door (he died, or says something else).</summary>
        public void StopVoiceThrough() { if (_voiceSrc != null) _voiceSrc.Stop(); }

        bool PlayOn(AudioSource src, AudioLowPassFilter lp, SfxLibrary lib, SfxLibrary.Entry e, Vector3 position, float volume, float pitch, float lowpassHz)
        {
            float now = Time.unscaledTime;
            if (e.cooldown > 0f && now - e.lastPlayed < e.cooldown) return false; // same rule as Sfx: no machine-gun grunts
            e.lastPlayed = now;
            src.Stop();
            src.clip = lib.Pick(e);
            src.pitch = pitch * (1f + Random.Range(-e.pitchJitter, e.pitchJitter));
            src.volume = Mathf.Clamp01(volume * e.volume);
            src.minDistance = e.minDistance; src.maxDistance = e.maxDistance;
            src.priority = e.priority;
            if (e.lowpassHz > 0f) lowpassHz = lowpassHz > 0f ? Mathf.Min(lowpassHz, e.lowpassHz) : e.lowpassHz; // the key's own, as Sfx
            lp.enabled = lowpassHz > 0f;
            if (lowpassHz > 0f) lp.cutoffFrequency = lowpassHz;
            src.transform.position = position;
            src.Play();
            return true;
        }

        /// <summary>Backwards-compatible entry point: the old "burst on the final kick".</summary>
        public void Burst(Vector3 _) => Breach();

        void Restart(IEnumerator routine)
        {
            if (_motion != null) StopCoroutine(_motion);
            _scripted = false;   // whatever ease was running is over; the caller sets it again for its own
            _motion = StartCoroutine(routine);
        }

        IEnumerator Tremble(float amplitude, float seconds)
        {
            float t = 0;
            while (t < seconds)
            {
                t += Time.deltaTime; float k = 1f - t / seconds;
                _leaf.localPosition = _leafRestLocal + new Vector3(0, 0, _roomSign * amplitude * k * Mathf.Sin(t * 90f));
                yield return null;
            }
            _leaf.localPosition = _leafRestLocal;
        }

        // Damped spring: pops open by `peak` degrees and rings back to closed (ω ≈ 35 rad/s, ζ ≈ 0.25).
        IEnumerator KickSpring(float peak)
        {
            const float omega = 35f, zeta = 0.25f;
            float t = 0;
            while (t < 0.5f)
            {
                t += Time.deltaTime;
                _angle = peak * Mathf.Exp(-zeta * omega * t) * Mathf.Sin(omega * t);
                if (_angle < 0f) _angle *= 0.3f; // the frame stops it swinging outward
                ApplyAngle();
                _leaf.localPosition = _leafRestLocal + new Vector3(0, 0, _roomSign * 0.004f * Mathf.Exp(-12f * t));
                yield return null;
            }
            _angle = 0f; ApplyAngle(); _leaf.localPosition = _leafRestLocal;
        }

        IEnumerator SwingOpen()
        {
            // Hinge friction slows it; it slams into its stop, rebounds 10–15° and comes to rest there.
            // (The old rebound — 30% of ~500°/s with the same light friction — coasted ~100° back: the door shut itself
            // again and the intruder walked through a closed leaf. Brett, 2026-09-23.)
            const float swingFriction = 1.5f, reboundFriction = 7f, restitution = 0.2f, maxRebound = 110f;
            bool bounced = false;
            while (true)
            {
                float dt = Time.deltaTime;
                if (dt <= 0f) { yield return null; continue; } // paused
                _angularVel -= _angularVel * Mathf.Min(1f, (bounced ? reboundFriction : swingFriction) * dt);
                _angle += _angularVel * dt;
                if (_angle >= _stopAngle)
                {
                    _angle = _stopAngle;
                    if (!bounced)
                    {
                        bounced = true;
                        _angularVel = -Mathf.Min(_angularVel * restitution, maxRebound);
                        Sfx.Play("door.slam", LatchWorld(), 0.9f); EmitDust(6);
                    }
                    else _angularVel = 0f;
                }
                ApplyAngle();
                if (bounced && Mathf.Abs(_angularVel) < 4f) break;
                yield return null;
            }
            _angularVel = 0f;
            State = DoorState.Open;
            SetObstacle(true);
            Debug.Log($"[HIS] Door: breached, resting at {_angle:F0}° (stop {_stopAngle:F0}°)");
        }

        IEnumerator Ease(float from, float to, float seconds, DoorState end)
        {
            float t = 0;
            while (t < seconds) { t += Time.deltaTime; _angle = Mathf.Lerp(from, to, Mathf.SmoothStep(0, 1, t / seconds)); ApplyAngle(); yield return null; }
            _angle = to; ApplyAngle();
            State = end;
            SetObstacle(end == DoorState.Open);
            if (end == DoorState.Closed) Breached = false; // shut again (Close): its handle works again
            _scripted = false;
        }

        void SetObstacle(bool on)
        {
            // The outside comes in with the open door: faint night ambience from the doorway while it stands open.
            if (on && _ambience == 0) _ambience = Sfx.PlayLoop("amb.night", transform.TransformPoint(new Vector3(_plane.center.x, _plane.center.y, -_roomSign * 0.6f)), 1f, 2.5f);
            else if (!on && _ambience != 0) { Sfx.Stop(_ambience, 1.2f); _ambience = 0; }
            if (_leafObstacle != null) _leafObstacle.enabled = on;
            if (_leafCollider != null) _leafCollider.enabled = on;
            SetLeafDepthBias(on ? OpenLeafDepthBias : ClosedLeafDepthBias);
        }

        // The Depth API bias on the leaf (its slab, panels, handle, bullet holes). Shut, it sits 1-5 cm in front of the real
        // door: Meta's 0.06. Open, it stands out in the room beside the player where nothing real belongs: coats, a shoe rack or
        // the side wall within a few percent of its depth ate see-through patches into it right in the corner of his view
        // (Brett 0.1.40/0.1.41 near the open door). 0.25: a real hand or body still hides it once it's a quarter nearer.
        const float ClosedLeafDepthBias = 0.06f, OpenLeafDepthBias = 0.25f;
        static readonly int DepthBiasId = Shader.PropertyToID("_EnvironmentDepthBias");

        void SetLeafDepthBias(float bias)
        {
            if (_mats.Leaf != null) _mats.Leaf.SetFloat(DepthBiasId, bias);
            if (_mats.Metal != null) _mats.Metal.SetFloat(DepthBiasId, bias);
            if (_woodMat != null) _woodMat.SetFloat(DepthBiasId, bias);
            if (_holeMat != null) _holeMat.SetFloat(DepthBiasId, bias);
        }

        void ApplyAngle()
        {
            // Positive _angle swings the free edge into the room for either hinge side.
            float signed = (_hingeAtMin ? -_roomSign : _roomSign) * _angle;
            _hinge.localRotation = Quaternion.Euler(0, signed, 0);
            UpdatePortal();
        }

        void DetachLatchChunk()
        {
            if (_latchChunk == null) return;
            // Already torn off on a Marshal night (each wave's broken door swings shut and the next wave kicks it again,
            // research/27 Question 5): it stays where it lies on the floor, no second launch from there. Every other mode keeps
            // main's relaunch (Sandbox's next man; police kicking a breached door the player shut): Brett 2026-10-03, the
            // normal game as main.
            var director = HIS.Scenario.ScenarioDirector.Instance;
            if (_latchChunk.parent != _root && director != null && director.CurrentMode == HIS.Scenario.ScenarioDirector.Mode.Marshal) return;
            _latchChunk.SetParent(null, true);
            // TryGetComponent, not `??`: Unity's destroyed/missing-component fake null isn't C# null.
            if (!_latchChunk.TryGetComponent<BoxCollider>(out _)) _latchChunk.gameObject.AddComponent<BoxCollider>(); // lands on the room floor
            if (!_latchChunk.TryGetComponent<Rigidbody>(out var rb)) rb = _latchChunk.gameObject.AddComponent<Rigidbody>();
            rb.mass = 0.4f; rb.isKinematic = false; rb.useGravity = true;
            rb.AddForce((Inward * 2.2f + Vector3.up * 0.6f + Random.insideUnitSphere * 0.4f), ForceMode.VelocityChange);
            rb.AddTorque(Random.insideUnitSphere * 8f, ForceMode.VelocityChange);
        }

        void RestoreLatchChunk()
        {
            if (_latchChunk == null) return;
            if (_latchChunk.TryGetComponent<Rigidbody>(out var rb)) Destroy(rb);
            if (_latchChunk.TryGetComponent<BoxCollider>(out var col)) Destroy(col);
            _latchChunk.SetParent(_root, false);
            _latchChunk.localPosition = _chunkRestLocal; _latchChunk.localRotation = _chunkRestRot;
        }

        Vector3 LatchWorld()
        {
            float latchX = _hingeAtMin ? _plane.xMax : _plane.xMin;
            return transform.TransformPoint(new Vector3(latchX, _plane.yMin + HandleHeight, _roomSign * 0.02f));
        }

        void EmitAtLatch(ParticleSystem ps, int count)
        {
            if (ps == null || count <= 0) return;
            ps.transform.SetPositionAndRotation(LatchWorld(), Quaternion.LookRotation(Inward));
            ps.Emit(count);
        }

        void EmitDust(int count)
        {
            if (_dust == null) return;
            var top = transform.TransformPoint(new Vector3(_plane.center.x, _plane.yMax - 0.05f, _roomSign * 0.05f));
            _dust.transform.SetPositionAndRotation(top, Quaternion.LookRotation(Vector3.down));
            _dust.Emit(count);
        }

        // ---------------------------------------------------------------- building helpers

        Transform Box(Transform parent, string name, Vector3 localPos, Vector3 size, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go.transform;
        }

        // Unity's quad shows its front to a viewer looking along its +Z; `facing` is the direction its visible side points.
        MeshRenderer Quad(Transform parent, string name, Vector3 localPos, Vector2 size, Vector3 facing, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = name;
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var up = Mathf.Abs(Vector3.Dot(facing.normalized, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
            go.transform.localRotation = Quaternion.LookRotation(-facing, up);
            go.transform.localScale = new Vector3(size.x, size.y, 1f);
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return r;
        }

        static Mesh _quadMesh;
        static Mesh QuadMesh()
        {
            if (_quadMesh != null) return _quadMesh;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quadMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
            return _quadMesh;
        }

        ParticleSystem BuildParticles(string name, Color color, bool stretch)
        {
            var go = new GameObject(name); go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = stretch ? new ParticleSystem.MinMaxCurve(0.6f, 1.4f) : new ParticleSystem.MinMaxCurve(1.0f, 2.0f);
            main.startSpeed = stretch ? new ParticleSystem.MinMaxCurve(1.5f, 4.5f) : new ParticleSystem.MinMaxCurve(0.1f, 0.4f);
            main.startSize = stretch ? new ParticleSystem.MinMaxCurve(0.01f, 0.04f) : new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
            main.startColor = color;
            main.gravityModifier = stretch ? 1f : 0.05f;
            main.maxParticles = 80;
            main.startRotation = new ParticleSystem.MinMaxCurve(0, 6.28f);
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = stretch ? 35f : 60f; shape.radius = stretch ? 0.05f : 0.3f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            if (stretch) { r.renderMode = ParticleSystemRenderMode.Stretch; r.lengthScale = 2.5f; r.velocityScale = 0.02f; }
            // Dust: soft round puffs, alpha-blended over the passthrough and occluded like the room's other light FX. On
            // Particles/Unlit with its default (opaque) blend every mote was a hard grey square.
            var puff = stretch ? null : Shader.Find("HIS/OcclusionUnlitBias");
            if (puff != null)
            {
                var m = new Material(puff) { renderQueue = 3000 };
                m.SetColor("_BaseColor", color);
                m.SetTexture("_BaseMap", SoftDot);
                r.sharedMaterial = m;
            }
            else r.sharedMaterial = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Universal Render Pipeline/Unlit")) { color = color };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        static Texture2D s_softDot;
        static Texture2D SoftDot
        {
            get
            {
                if (s_softDot != null) return s_softDot;
                const int n = 32;
                s_softDot = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "HIS soft dot", wrapMode = TextureWrapMode.Clamp };
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float u = (x + 0.5f - n / 2f) / (n / 2f), v = (y + 0.5f - n / 2f) / (n / 2f);
                        float a = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v));
                        s_softDot.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                    }
                s_softDot.Apply(false, true);
                return s_softDot;
            }
        }
    }
}
