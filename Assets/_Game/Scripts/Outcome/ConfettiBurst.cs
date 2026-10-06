using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace HIS.Outcome
{
    /// <summary>
    /// World-locked confetti for a good call (research/11 §2 + corrections): two floor cannons in front of the player,
    /// aimed about 70° up and back over them, plus a gentle rain from under the ceiling.
    /// <list type="bullet">
    /// <item>Opaque instanced quads. Opaque confetti can't alpha-fade, so each piece shrinks away at the end of its life.
    ///   Cull Off, so pieces don't vanish when they flip edge-on.</item>
    /// <item>Collision is two infinite planes, the real floor height and the ceiling. That's the cheapest mode; only the floor
    ///   and ceiling are safe as infinite planes. The ceiling keeps fast pieces from flying through it.</item>
    /// <item>All three systems are built once and reused (emission off, <see cref="ParticleSystem.Emit(int)"/> on a
    ///   schedule), so a show allocates nothing. At most 150 particles per emitter, 450 in all.</item>
    /// </list>
    /// </summary>
    public class ConfettiBurst : MonoBehaviour
    {
        const int MaxPerEmitter = 150;
        const float CannonElevation = 70f;
        const float SecondVolleyAt = 0.12f;
        const float RainStart = 0.15f, RainSeconds = 1.6f;

        ParticleSystem _left, _right, _rain;
        Transform _floorPlane, _ceilingPlane;
        Material _material;
        bool _ready;

        bool _active, _secondVolleyDone;
        float _t, _rainRate, _rainAccum;
        int _secondVolley, _rainLeft;

        public bool Ready => _ready;

        /// <summary>
        /// Builds the emitters. <paramref name="source"/> should be a saved material asset on
        /// "Universal Render Pipeline/Particles/Unlit" (opaque). A runtime Shader.Find is only a fallback, because the
        /// shader can be stripped from builds.
        /// </summary>
        public void Init(Material source)
        {
            if (_ready) return;
            if (source != null) _material = new Material(source) { name = "Confetti (runtime)" };
            else
            {
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                if (shader == null) { Debug.LogWarning("[HIS] Confetti: no material and no URP particle shader; confetti disabled"); return; }
                Debug.LogWarning("[HIS] Confetti: no material assigned; using a runtime material (may render pink in builds)");
                _material = new Material(shader) { name = "Confetti (runtime)" };
            }
            if (_material.HasProperty("_Cull")) _material.SetFloat("_Cull", 0f); // render face: both

            _floorPlane = new GameObject("FloorPlane").transform;
            _floorPlane.SetParent(transform, false);
            _ceilingPlane = new GameObject("CeilingPlane").transform;
            _ceilingPlane.SetParent(transform, false);

            _left = Build("CannonLeft", cannon: true);
            _right = Build("CannonRight", cannon: true);
            _rain = Build("CeilingRain", cannon: false);
            _ready = true;
        }

        /// <summary>
        /// Fire the cannons now and start the ceiling rain. <paramref name="perCannon"/> and <paramref name="rain"/> are
        /// particle counts, each capped at 150. <paramref name="room"/> may be null; when present it keeps the cannons
        /// inside the scanned room.
        /// </summary>
        public void Fire(Vector3 head, Vector3 forward, float floorY, float ceilingY, MRUKRoom room, int perCannon, int rain)
        {
            if (!_ready) return;
            StopAll();

            var fwd = forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var right = Vector3.Cross(Vector3.up, fwd);

            // Planes: the normal is each transform's +Y, so the ceiling plane is flipped to face down into the room.
            _floorPlane.SetPositionAndRotation(new Vector3(head.x, floorY, head.z), Quaternion.identity);
            _ceilingPlane.SetPositionAndRotation(new Vector3(head.x, ceilingY, head.z), Quaternion.Euler(180f, 0f, 0f));

            PlaceCannon(_left, head, fwd, -right, floorY, room);
            PlaceCannon(_right, head, fwd, right, floorY, room);

            var rainPos = new Vector3(head.x, ceilingY - 0.2f, head.z) + fwd * 0.5f;
            _rain.transform.SetPositionAndRotation(rainPos, Quaternion.LookRotation(Vector3.down, fwd)); // box emits along its +Z

            perCannon = Mathf.Clamp(HIS.Core.PerfBudget.Confetti(perCannon), 0, MaxPerEmitter); // fewer when the headset runs warm
            int first = perCannon * 3 / 4;
            _secondVolley = perCannon - first;
            if (first > 0) { _left.Emit(first); _right.Emit(first); }

            _rainLeft = Mathf.Clamp(HIS.Core.PerfBudget.Confetti(rain), 0, MaxPerEmitter);
            _rainRate = _rainLeft / RainSeconds;
            _rainAccum = 0f;
            _t = 0f;
            _secondVolleyDone = _secondVolley == 0;
            _active = true;
        }

        /// <summary>Runs the second cannon volley and the rain. Called by <see cref="OutcomeShow"/> every frame.</summary>
        public void Tick(float dt)
        {
            if (!_active) return;
            _t += dt;
            if (!_secondVolleyDone && _t >= SecondVolleyAt)
            {
                _left.Emit(_secondVolley);
                _right.Emit(_secondVolley);
                _secondVolleyDone = true;
            }
            if (_rainLeft > 0 && _t >= RainStart)
            {
                _rainAccum += _rainRate * dt;
                int n = Mathf.Min((int)_rainAccum, _rainLeft);
                if (n > 0)
                {
                    _rain.Emit(n);
                    _rainLeft -= n;
                    _rainAccum -= n;
                }
            }
            if (_secondVolleyDone && _rainLeft <= 0) _active = false;
        }

        /// <summary>Remove every piece right now (session reset or a new show).</summary>
        public void StopAll()
        {
            _active = false;
            _rainLeft = 0;
            if (!_ready) return;
            _left.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _right.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            _rain.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        void PlaceCannon(ParticleSystem ps, Vector3 head, Vector3 fwd, Vector3 side, float floorY, MRUKRoom room)
        {
            var headFloor = new Vector3(head.x, floorY + 0.03f, head.z);
            var spot = headFloor + side * 1.1f + fwd * 1.0f;
            // Pull toward the player until the cannon is inside the scanned room (not firing from inside a wall).
            if (room != null)
                for (int i = 0; i < 4 && !room.IsPositionInRoom(spot, false); i++)
                    spot = Vector3.Lerp(headFloor, spot, 0.6f);

            var toPlayer = headFloor - spot; toPlayer.y = 0f;
            toPlayer = toPlayer.sqrMagnitude > 1e-4f ? toPlayer.normalized : -fwd;
            float elev = CannonElevation * Mathf.Deg2Rad;
            var dir = Vector3.up * Mathf.Sin(elev) + toPlayer * Mathf.Cos(elev);
            ps.transform.SetPositionAndRotation(spot, Quaternion.LookRotation(dir));
        }

        ParticleSystem Build(string name, bool cannon)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate; // world-locked: keep falling while the player looks away
            main.maxParticles = MaxPerEmitter;
            main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 7f);
            main.startSpeed = cannon ? new ParticleSystem.MinMaxCurve(3.5f, 6.5f) : new ParticleSystem.MinMaxCurve(0.1f, 0.3f);
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(0.014f, 0.022f);
            main.startSizeY = new ParticleSystem.MinMaxCurve(0.022f, 0.036f);
            main.startSizeZ = 1f;
            main.startRotation3D = true;
            main.startRotationX = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
            main.startRotationY = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
            main.startRotationZ = new ParticleSystem.MinMaxCurve(0f, 2f * Mathf.PI);
            main.startColor = new ParticleSystem.MinMaxGradient(Palette()) { mode = ParticleSystemGradientMode.RandomColor };
            main.gravityModifier = 0.15f;

            var em = ps.emission; em.enabled = false; // emitted on a schedule by Fire/Tick

            var shape = ps.shape;
            if (cannon)
            {
                shape.shapeType = ParticleSystemShapeType.Cone;
                shape.angle = 20f;
                shape.radius = 0.04f;
            }
            else
            {
                shape.shapeType = ParticleSystemShapeType.Box;
                shape.scale = new Vector3(1.5f, 1.5f, 0.05f);
            }

            var rot = ps.rotationOverLifetime;
            rot.enabled = true;
            rot.separateAxes = true;
            rot.x = new ParticleSystem.MinMaxCurve(-12f, 12f);
            rot.y = new ParticleSystem.MinMaxCurve(-12f, 12f);
            rot.z = new ParticleSystem.MinMaxCurve(-12f, 12f);

            // Air drag that grows with speed: the cannon burst stalls under the ceiling and the pieces flutter down.
            var drag = ps.limitVelocityOverLifetime;
            drag.enabled = true;
            drag.limit = 100f;
            drag.dampen = 0f;
            drag.drag = 1.2f;
            drag.multiplyDragByParticleVelocity = true;
            drag.multiplyDragByParticleSize = false;

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.35f;
            noise.frequency = 0.6f;
            noise.quality = ParticleSystemNoiseQuality.Low;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(0.85f, 1f), new Keyframe(1f, 0f)));

            var col = ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.Planes;
            col.AddPlane(_floorPlane);
            col.AddPlane(_ceilingPlane);
            col.dampen = 0.85f;
            col.bounce = 0.05f;
            col.lifetimeLoss = 0f;
            col.radiusScale = 0.5f;
            col.sendCollisionMessages = false;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            r.mesh = QuadMesh();
            r.alignment = ParticleSystemRenderSpace.World;
            // No GPU instancing: on the Quest (Vulkan, multiview) the instanced variant ran without its instance buffer — "Shader
            // requires a compute buffer unity_ParticleInstanceData, but none provided. Skipping draw calls" (0.1.48 log, at a Good
            // show, inside a run of kernel GPU hangs). ≤ 150 quads an emitter: the plain mesh-particle path is fine.
            r.enableGPUInstancing = false;
            r.sharedMaterial = _material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        // Gold, red, blue, green, white (research/11 §2), as fixed bands so RandomColor picks one of five.
        static Gradient Palette()
        {
            var g = new Gradient { mode = GradientMode.Fixed };
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color32(0xFF, 0xC8, 0x3D, 0xFF), 0.2f),
                    new GradientColorKey(new Color32(0xE6, 0x39, 0x46, 0xFF), 0.4f),
                    new GradientColorKey(new Color32(0x1D, 0x4E, 0xD8, 0xFF), 0.6f),
                    new GradientColorKey(new Color32(0x22, 0xC5, 0x5E, 0xFF), 0.8f),
                    new GradientColorKey(Color.white, 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        static Mesh _quad;
        static Mesh QuadMesh()
        {
            if (_quad != null) return _quad;
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _quad = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
            return _quad;
        }

        void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }
    }
}
