using UnityEngine;

namespace HIS.Weapons
{
    /// <summary>A pulsing ring drawn around whatever grabbable the hand is pointing at. One per hand, built at runtime.</summary>
    public class GrabReticle : MonoBehaviour
    {
        LineRenderer _ring;
        Transform _target;
        float _phase, _radius;

        public static GrabReticle Create(string name)
        {
            var go = new GameObject(name);
            var r = go.AddComponent<GrabReticle>();
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false; lr.loop = true; lr.positionCount = 32; lr.widthMultiplier = 0.008f;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; lr.receiveShadows = false;
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(shader); var c = new Color(0.3f, 0.9f, 1f); mat.color = c; if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", c);
            lr.material = mat;
            for (int i = 0; i < 32; i++) { float a = i / 32f * Mathf.PI * 2f; lr.SetPosition(i, new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0)); }
            r._ring = lr; go.SetActive(false);
            return r;
        }

        public void Show(Transform target, float radius)
        {
            _target = target;
            if (!gameObject.activeSelf) gameObject.SetActive(true);
            _radius = radius;
        }

        public void Hide() { _target = null; if (gameObject.activeSelf) gameObject.SetActive(false); }

        void Update()
        {
            if (_target == null) { Hide(); return; }
            var cam = Camera.main != null ? Camera.main.transform : null;
            transform.position = _target.position;
            if (cam != null) transform.rotation = Quaternion.LookRotation(transform.position - cam.position, Vector3.up);
            _phase += Time.deltaTime * 4f;
            float pulse = 1f + 0.12f * Mathf.Sin(_phase);
            transform.localScale = new Vector3(_radius * pulse, _radius * pulse, 1f);
        }
    }
}
