using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace HIS.UI
{
    /// <summary>
    /// One world-space message panel for things the player must read in any build: pause, "scan your room", etc.
    /// Not head-locked: it's placed in front of the player and lazily re-centres when they look away (Meta comfort guidance).
    /// Several systems can post at once; the highest priority message shows. Runs on unscaled time so it works while paused.
    /// </summary>
    public class Notice : MonoBehaviour
    {
        public static Notice Instance { get; private set; }

        [SerializeField] TextMeshPro text;
        [SerializeField] Transform backing;           // unit quad scaled to the text
        [SerializeField] float distance = 1.1f;
        [SerializeField] float minDistance = 0.55f;
        [SerializeField] float recenterAngle = 35f;
        [SerializeField] float followSpeed = 4f;
        [SerializeField] Vector2 padding = new(0.06f, 0.045f);

        struct Entry { public string Message; public int Priority; }
        readonly Dictionary<string, Entry> _entries = new();
        string _shownKey;
        Transform _head;
        bool _placed;
        Vector3 _targetPos;
        Quaternion _targetRot;

        void Awake()
        {
            Instance = this;
            SetVisible(false);
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        /// <summary>Post or replace the message for <paramref name="key"/>. Higher priority wins when several are posted.</summary>
        public static void Show(string key, string message, int priority = 0)
        {
            if (Instance == null) { Debug.Log($"[HIS] Notice ({key}): {message}"); return; }
            Instance._entries[key] = new Entry { Message = message, Priority = priority };
            Instance.Refresh();
        }

        /// <summary>The key of the message on the panel now (highest priority), or null (tests, tools).</summary>
        public static string ShownKey => Instance != null ? Instance._shownKey : null;
        /// <summary>The message text posted under <paramref name="key"/>, shown or not; null if none.</summary>
        public static string Posted(string key) => Instance != null && Instance._entries.TryGetValue(key, out var e) ? e.Message : null;

        public static void Hide(string key)
        {
            if (Instance == null || !Instance._entries.Remove(key)) return;
            Instance.Refresh();
        }

        void Refresh()
        {
            string bestKey = null; var best = default(Entry);
            foreach (var kv in _entries)
                if (bestKey == null || kv.Value.Priority > best.Priority) { bestKey = kv.Key; best = kv.Value; }

            if (bestKey == null) { _shownKey = null; SetVisible(false); return; }
            bool wasHidden = _shownKey == null;
            _shownKey = bestKey;
            SetVisible(true); // TMP only builds geometry for active objects; measure after activating
            text.text = best.Message;
            text.ForceMeshUpdate(true);
            // Text bounds are in the text object's local space; the text is scaled (TMP renders best at font size ~1-10).
            var s = text.transform.localScale.x;
            var b = text.textBounds;
            bool valid = b.size.x > 0f && b.size.y > 0f && b.size.x < 1000f && b.size.y < 1000f;
            var size = valid ? b.size * s : new Vector3(0.6f, 0.15f, 0f);
            var centre = text.transform.localPosition + (valid ? b.center * s : Vector3.zero);
            if (backing != null)
            {
                backing.localScale = new Vector3(size.x + padding.x * 2f, size.y + padding.y * 2f, 1f);
                backing.localPosition = new Vector3(centre.x, centre.y, 0.003f);
            }
            SetVisible(true);
            if (wasHidden) _placed = false; // a fresh notice appears right in front
        }

        void SetVisible(bool on)
        {
            if (text != null) text.gameObject.SetActive(on);
            if (backing != null) backing.gameObject.SetActive(on);
        }

        void LateUpdate()
        {
            if (_shownKey == null) return;
            if (_head == null) { var cam = Camera.main; if (cam == null) return; _head = cam.transform; }

            var fwd = _head.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();

            var toPanel = transform.position - _head.position;
            float heightError = Mathf.Abs(toPanel.y + 0.05f);
            toPanel.y = 0;
            // Off to the side, too high/low (e.g. placed on the first frame while the head was still at the floor origin),
            // or much nearer/farther than intended because the player walked.
            bool offCentre = toPanel.sqrMagnitude < 1e-4f || Vector3.Angle(fwd, toPanel) > recenterAngle ||
                             heightError > 0.25f || Mathf.Abs(toPanel.magnitude - distance) > 0.5f;
            if (!_placed || offCentre)
            {
                // Don't bury the panel in a real wall, past a door's frame or behind an open leaf: stay in front of it, even
                // closer than minDistance if the player is facing it up close (Subtitles.RoomSideDistance).
                float d = Subtitles.RoomSideDistance(_head.position, fwd, distance, minDistance, Subtitles.PlacementMask());
                _targetPos = _head.position + fwd * d + Vector3.down * 0.05f;
                _targetRot = Quaternion.LookRotation(fwd, Vector3.up);
                if (!_placed) { transform.SetPositionAndRotation(_targetPos, _targetRot); _placed = true; }
            }
            float k = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            transform.SetPositionAndRotation(Vector3.Lerp(transform.position, _targetPos, k), Quaternion.Slerp(transform.rotation, _targetRot, k));
        }
    }
}
