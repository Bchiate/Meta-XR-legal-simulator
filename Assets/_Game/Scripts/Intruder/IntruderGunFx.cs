using System.Collections;
using HIS.Core;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// The intruder's pistol going off: the player gun's flash quad (GunEffects.MakeFlashQuad, one per gun, reused every shot)
    /// for ~50 ms, the same pistol report, and a casing clinking off the floor to his right a moment later. No point light
    /// (Quest budget: one directional light) and no smoke (Brett).
    /// </summary>
    [RequireComponent(typeof(IntruderWeapon))]
    public class IntruderGunFx : MonoBehaviour
    {
        const float FlashSeconds = 0.05f;
        /// <summary>× the report's pitch (1 = the pistol). A wave 3 hitman's "SMG" fires at 0.85: a deeper crack (research/27 §2).</summary>
        public float Pitch { get; set; } = 1f;
        Transform _muzzle, _flash;
        Material _flashMat; // MakeFlashQuad makes one per gun: it goes with him (a new intruder per scenario)

        void Awake()
        {
            var w = GetComponent<IntruderWeapon>();
            _muzzle = w.Muzzle != null ? w.Muzzle : transform;
            _flash = MakeFlash(_muzzle);
            _flashMat = _flash.GetComponent<MeshRenderer>().sharedMaterial;
        }

        void OnDestroy() { if (_flashMat != null) Destroy(_flashMat); }

        /// <summary>
        /// The flash quad on his muzzle (also PoseSnapshots), drawn double-sided: the player gun's quad faces its shooter and the
        /// particle shader culls back faces, so from in front of HIS muzzle — where the player stands — it was invisible (render
        /// Ctl_GunTellFire, first pass).
        /// </summary>
        public static Transform MakeFlash(Transform muzzle)
        {
            var flash = HIS.Weapons.GunEffects.MakeFlashQuad(muzzle);
            flash.GetComponent<MeshRenderer>().sharedMaterial.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            return flash;
        }

        public void Fire()
        {
            _flash.gameObject.SetActive(true);
            _flash.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            _flash.localScale = Vector3.one * Random.Range(0.11f, 0.17f);
            CancelInvoke(nameof(EndFlash)); Invoke(nameof(EndFlash), FlashSeconds);
            int report = Sfx.Play("pistol.shot", _muzzle.position, 1f, 0.05f);
            if (report != 0 && Pitch != 1f) Sfx.ScalePitch(report, Pitch);
            StartCoroutine(Casing(_muzzle.position + _muzzle.right * 0.7f + Vector3.down * 1.2f));
        }

        void EndFlash() => _flash.gameObject.SetActive(false);

        IEnumerator Casing(Vector3 at)
        {
            yield return new WaitForSeconds(0.45f);
            Sfx.Play("casing.brass", at, 0.5f);
        }

        void OnDisable() { if (_flash != null) _flash.gameObject.SetActive(false); }
    }
}
