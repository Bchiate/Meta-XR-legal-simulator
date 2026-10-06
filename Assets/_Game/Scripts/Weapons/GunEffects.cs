using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>
    /// Muzzle flash (light + flash quad), smoke wisp and ejected shell casing for a Firearm. Built procedurally so it needs
    /// no art; each piece is pooled. Occlusion mode "None" on the flash (it must never be clipped by the depth map).
    /// </summary>
    [RequireComponent(typeof(Firearm))]
    public class GunEffects : MonoBehaviour
    {
        [SerializeField] Transform muzzle;
        [SerializeField] Transform ejectionPort;
        [SerializeField] Vector3 ejectDirectionLocal = new(1f, 0.6f, -0.2f); // right-up-back
        [SerializeField] float ejectSpeed = 2.2f;
        [SerializeField] float flashSeconds = 0.045f;
        [SerializeField] int smokePuffs = 0; // Brett: no muzzle smoke
        [SerializeField] float flashScale = 1f;
        [SerializeField] Color casingColor = new(0.85f, 0.7f, 0.3f); // brass; red for 12-gauge hulls
        [SerializeField] float casingSize = 0.022f;
        [Tooltip("Pump/bolt guns eject when racked, not when fired.")]
        [SerializeField] bool ejectOnFire = true;

        Light _light;
        Transform _flash;
        ParticleSystem _smoke;
        ParticleSystem _shells;
        Firearm _gun;

        void Awake()
        {
            _gun = GetComponent<Firearm>();
            if (muzzle == null) muzzle = transform.Find("Model/Muzzle") ?? transform;
            if (ejectionPort == null) ejectionPort = transform.Find("Model/Slide") ?? muzzle;

            var lightGo = new GameObject("MuzzleLight"); lightGo.transform.SetParent(muzzle, false);
            lightGo.transform.localPosition = new Vector3(0, 0, 0.04f);
            _light = lightGo.AddComponent<Light>();
            _light.type = LightType.Point; _light.color = new Color(1f, 0.75f, 0.4f); _light.intensity = 0f; _light.range = 2.5f;
            _light.shadows = LightShadows.None;
            _light.enabled = false; // off between shots: a 0-intensity light may still count as a per-pixel light (GPU heat)

            _flash = MakeFlashQuad(muzzle);
            _smoke = MakeSmoke();
            _shells = MakeShells();
        }

        public void OnFired()
        {
            _light.intensity = 6f; _light.enabled = true;
            _flash.gameObject.SetActive(true);
            _flash.localRotation = Quaternion.Euler(0, 0, Random.Range(0f, 360f));
            _flash.localScale = Vector3.one * Random.Range(0.10f, 0.16f) * flashScale;
            CancelInvoke(nameof(EndFlash)); Invoke(nameof(EndFlash), flashSeconds);
            if (smokePuffs > 0) _smoke.Emit(smokePuffs);
            if (ejectOnFire) EjectShell();
        }

        void EndFlash() { _light.intensity = 0f; _light.enabled = false; _flash.gameObject.SetActive(false); }

        public void EjectShell()
        {
            var dir = ejectionPort.TransformDirection(ejectDirectionLocal.normalized);
            var p = new ParticleSystem.EmitParams
            {
                position = ejectionPort.position + ejectionPort.right * 0.015f,
                velocity = dir * ejectSpeed + Random.insideUnitSphere * 0.4f,
                startLifetime = 1.6f, startSize = casingSize, startColor = casingColor,
                rotation = Random.Range(0f, 360f), angularVelocity = Random.Range(-720f, 720f),
            };
            _shells.Emit(p, 1);
        }

        /// <summary>The flash quad on a muzzle (hidden; show it for ~45 ms per shot). Shared with the intruder's pistol (IntruderGunFx).</summary>
        public static Transform MakeFlashQuad(Transform muzzle)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "MuzzleFlash"; DestroyImmediate(go.GetComponent<Collider>()); // (immediate: also built in edit mode by PoseSnapshots)
            go.transform.SetParent(muzzle, false);
            go.transform.localPosition = new Vector3(0, 0, 0.03f);
            var mr = go.GetComponent<MeshRenderer>();
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Universal Render Pipeline/Unlit");
            var mat = new Material(shader) { color = new Color(1f, 0.85f, 0.5f, 0.9f) };
            mat.SetFloat("_Surface", 1); mat.SetFloat("_Blend", 1); mat.renderQueue = 3100; // additive-ish, transparent
            mat.SetFloat("_ZWrite", 0); mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            // _Surface alone doesn't change the blend state (the material inspector would): without these the quad drew opaque —
            // a solid orange square instead of the star (PoseSnapshots Ctl_GunTellFire). Additive: flash = star alpha × colour.
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha); mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One); mat.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            mat.mainTexture = FlashTexture;
            mr.sharedMaterial = mat; mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.SetActive(false);
            return go.transform;
        }

        static Texture2D _flashTex;
        static Texture2D FlashTexture
        {
            get
            {
                if (_flashTex != null) return _flashTex;
                int n = 64; _flashTex = new Texture2D(n, n, TextureFormat.RGBA32, false);
                for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float u = (x - n / 2f) / (n / 2f), v = (y - n / 2f) / (n / 2f);
                    float r = Mathf.Sqrt(u * u + v * v); float ang = Mathf.Atan2(v, u);
                    float star = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(ang * 3f)); // 6-point star
                    float a = Mathf.Clamp01(1.4f * (star - r)) * Mathf.Clamp01(1.2f - r);
                    _flashTex.SetPixel(x, y, new Color(1f, 0.9f, 0.6f, a * a));
                }
                _flashTex.Apply(); return _flashTex;
            }
        }

        ParticleSystem MakeSmoke()
        {
            var go = new GameObject("MuzzleSmoke"); go.transform.SetParent(muzzle, false);
            var ps = go.AddComponent<ParticleSystem>(); ps.Stop();
            var main = ps.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.6f, 1.1f); main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.09f); main.startColor = new Color(0.8f, 0.8f, 0.8f, 0.35f);
            main.gravityModifier = -0.05f; main.maxParticles = 32;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 12f; shape.radius = 0.005f;
            var sol = ps.sizeOverLifetime; sol.enabled = true; sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 0.5f, 1, 2.2f));
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient(); g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) }, new[] { new GradientAlphaKey(0.35f, 0), new GradientAlphaKey(0f, 1) });
            col.color = g;
            var r = go.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = SoftParticleMaterial; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        ParticleSystem MakeShells()
        {
            var go = new GameObject("ShellCasings"); go.transform.SetParent(null, true);
            var ps = go.AddComponent<ParticleSystem>(); ps.Stop();
            var main = ps.main; main.playOnAwake = false; main.loop = false; main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1f; main.maxParticles = 64; main.startLifetime = 1.6f;
            var em = ps.emission; em.enabled = false;
            var coll = ps.collision; coll.enabled = true; coll.type = ParticleSystemCollisionType.World; coll.bounce = 0.35f; coll.lifetimeLoss = 0.1f; coll.enableDynamicColliders = false;
            coll.sendCollisionMessages = true;
            var gun = GetComponent<Firearm>();
            go.AddComponent<CasingSounds>().Key = gun != null && gun.SfxKey == "shotgun" ? "casing.shell" : "casing.brass";
            var r = go.GetComponent<ParticleSystemRenderer>(); r.renderMode = ParticleSystemRenderMode.Mesh; r.mesh = ShellMesh; r.alignment = ParticleSystemRenderSpace.Local;
            r.enableGPUInstancing = false; // (see ConfettiBurst: the instanced variant went without its instance buffer on the Quest)
            var shader = Shader.Find("Universal Render Pipeline/Particles/Lit") ?? Shader.Find("Universal Render Pipeline/Lit");
            r.sharedMaterial = new Material(shader) { color = new Color(0.85f, 0.7f, 0.3f) };
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return ps;
        }

        static Mesh _shellMesh;
        static Mesh ShellMesh
        {
            get
            {
                if (_shellMesh != null) return _shellMesh;
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _shellMesh = Instantiate(tmp.GetComponent<MeshFilter>().sharedMesh);
                var v = _shellMesh.vertices; for (int i = 0; i < v.Length; i++) v[i] = new Vector3(v[i].x * 0.35f, v[i].y, v[i].z * 0.35f); // 9x19-ish proportions at 0.022 size
                _shellMesh.vertices = v; _shellMesh.RecalculateBounds();
                Destroy(tmp); return _shellMesh;
            }
        }

        static Material _softMat;
        static Material SoftParticleMaterial
        {
            get
            {
                if (_softMat != null) return _softMat;
                var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Universal Render Pipeline/Unlit");
                _softMat = new Material(shader); _softMat.SetFloat("_Surface", 1); _softMat.SetFloat("_ZWrite", 0); _softMat.renderQueue = 3050;
                _softMat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                int n = 32; var t = new Texture2D(n, n, TextureFormat.RGBA32, false);
                for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) { float u = (x - n / 2f) / (n / 2f), v = (y - n / 2f) / (n / 2f); float a = Mathf.Clamp01(1f - Mathf.Sqrt(u * u + v * v)); t.SetPixel(x, y, new Color(1, 1, 1, a * a)); }
                t.Apply(); _softMat.mainTexture = t;
                return _softMat;
            }
        }
    }

    /// <summary>Clink where an ejected casing actually hits the floor or a table (particle collision events), quieter per bounce.</summary>
    public class CasingSounds : MonoBehaviour
    {
        public string Key = "casing.brass";
        ParticleSystem _ps;
        readonly System.Collections.Generic.List<ParticleCollisionEvent> _events = new();

        void Awake() => _ps = GetComponent<ParticleSystem>();

        void OnParticleCollision(GameObject other)
        {
            int n = _ps.GetCollisionEvents(other, _events);
            for (int i = 0; i < n; i++)
            {
                float speed = _events[i].velocity.magnitude;
                if (speed < 0.4f) continue; // resting contact, not a bounce
                HIS.Core.Sfx.Play(Key, _events[i].intersection, Mathf.Clamp01(speed / 3f));
            }
        }
    }
}
