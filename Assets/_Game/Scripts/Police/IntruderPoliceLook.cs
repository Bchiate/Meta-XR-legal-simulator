using System.Collections.Generic;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// How an officer looks (research/22 §4.5 option A, the placeholder until a real police model is approved): Alex with his
    /// dark clothes recoloured police navy (a runtime copy of his one texture atlas: only the dark, neutral clothing pixels
    /// change, skin/eyes/hair keep theirs), a peaked patrol cap, a gold badge, a light "POLICE" panel across the back, a black
    /// duty belt with a holster and cuff pouch, a radio on the chest. Wave 3 on = SWAT: a helmet and a heavy vest instead of
    /// the cap, and the vest stops the first torso round (<see cref="Armored"/>, Hitbox.cs). Props are a handful of primitives
    /// (~1–2k triangles) parented to his bones, switched with his body (Outside layer on the path); their colliders count as
    /// the hitbox of their bone (a round through the cap or helmet is a head hit: Brett 0.1.40).
    /// A Marshal night's hitmen (research/27) are dressed by <see cref="DressAsHitman"/> instead: Alex's own clothes and one
    /// tell per wave (beanie / shirt front / black helmet, plate carrier, NVG and SMG), no light, badge or panel. The night's
    /// two friendlies wear Alex's clothes recoloured light by the same atlas pass (<see cref="ClothAtlas"/>), so neither reads
    /// as a hitman tier: Ricky in a light-grey sweatsuit (<see cref="DressAsProtectee"/>), Deputy Ortiz in a light windbreaker
    /// with a gold badge on his belt (<see cref="DressAsDeputy"/>).
    /// </summary>
    public partial class Intruder
    {
        /// <summary>SWAT (wave ≥ 3): the heavy vest turns his first torso/belly round into a wound (Hitbox.cs). An operator's
        /// plate carrier (research/27 §2; the hitmen's wave 5): his first chest round only rocks him (the brain's plate rule, Hitbox.cs).</summary>
        public bool Armored { get; private set; }
        /// <summary>
        /// A hitman's kit, by his tier's look (<see cref="DressAsHitman"/>): "beanie" (street, waves 1–2), "suit" (pros, waves
        /// 3–4), "operator" (wave 5: helmet, plate carrier, SMG). Null for everyone else.
        /// </summary>
        public string HitmanKit { get; private set; }
        /// <summary>He carries the operators' SMG over his pistol (<see cref="ShowSmg"/>).</summary>
        public bool HasSmg => _smg != null;

        bool _dressed;
        OfficerFlashlight _light;
        GameObject _taser;
        Renderer[] _pistolRenderers;
        /// <summary>An operator's SMG over his pistol (<see cref="ShowSmg"/>); null for everyone else.</summary>
        GameObject _smg;

        static readonly Color Navy = new(0.11f, 0.15f, 0.3f), Black = new(0.05f, 0.05f, 0.055f), Gold = new(0.8f, 0.62f, 0.2f),
                              Patch = new(0.82f, 0.82f, 0.78f), Olive = new(0.16f, 0.17f, 0.13f), TaserYellow = new(0.95f, 0.8f, 0.05f);
        /// <summary>A shade off the black kit, so the operator's pouches and NVG tubes read as shapes on it (research/27 §2).</summary>
        static readonly Color Charcoal = new(0.12f, 0.12f, 0.125f);

        /// <summary>The uniform, the props (cap or SWAT kit) and his weapon light. Once per officer.</summary>
        void DressAsOfficer(bool swat)
        {
            if (_dressed) return;
            _dressed = true;
            Armored = swat;
            var animator = GetComponentInChildren<Animator>();
            // The renderer cache (SetVisible) must exist before the props, so the props can be added to it with the body's layer.
            SetVisible(_visible);
            int bodyLayer = _rendererLayers != null && _rendererLayers.Length > 0 ? _rendererLayers[0] : gameObject.layer;
            RecolorUniform();
            var added = new List<Renderer>();
            if (animator != null && animator.isHuman)
            {
                var fwd = Flat(transform.forward).normalized; var up = Vector3.up; var right = Vector3.Cross(up, fwd);
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                var chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest);
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                var rot = Quaternion.LookRotation(fwd, up);
                if (head != null)
                {
                    // Brett 0.1.26: "the cops hats cover their faces" — the band sat down over the eyes. Now the cap rides on the
                    // skull: band bottom at the brow (~7 cm under the head top), peak just above the eyes, crown above the head.
                    var top = HeadTop(head);
                    if (swat)
                    {
                        // The shell sits back and high so it wraps the skull, not the face; the brim stops at the brow.
                        Prop(added, head, PrimitiveType.Sphere, top - up * 0.04f - fwd * 0.03f, rot, new Vector3(0.24f, 0.17f, 0.26f), Olive, 0.1f);
                        Prop(added, head, PrimitiveType.Cube, top - up * 0.075f + fwd * 0.085f, rot * Quaternion.Euler(-8f, 0f, 0f), new Vector3(0.19f, 0.03f, 0.04f), Olive, 0.1f);
                    }
                    else
                    {
                        // A peaked patrol cap: a band round the forehead, a flat crown a little wider than the head, the peak
                        // angled down at the brow, a gold badge on the band.
                        Prop(added, head, PrimitiveType.Cylinder, top - up * 0.025f - fwd * 0.03f, rot, new Vector3(0.198f, 0.09f, 0.228f), Navy, 0f);       // band
                        Prop(added, head, PrimitiveType.Cylinder, top - up * 0.062f - fwd * 0.03f, rot, new Vector3(0.203f, 0.014f, 0.233f), Black, 0.1f);    // hat band
                        Prop(added, head, PrimitiveType.Cylinder, top + up * 0.032f - fwd * 0.02f, rot, new Vector3(0.235f, 0.03f, 0.255f), Navy, 0f);        // crown (over the skull)
                        Prop(added, head, PrimitiveType.Cube, top - up * 0.072f + fwd * 0.1f, rot * Quaternion.Euler(18f, 0f, 0f), new Vector3(0.16f, 0.008f, 0.06f), Black, 0.1f); // peak
                        Prop(added, head, PrimitiveType.Cube, top - up * 0.03f + fwd * 0.086f, rot, new Vector3(0.032f, 0.036f, 0.006f), Gold, 0.9f);         // cap badge
                    }
                }
                if (chest != null)
                {
                    var c = chest.position;
                    if (swat)
                    {
                        Prop(added, chest, PrimitiveType.Cube, c - up * 0.06f + fwd * 0.01f, rot, new Vector3(0.37f, 0.4f, 0.28f), Olive, 0.05f);    // heavy vest
                        Prop(added, chest, PrimitiveType.Cube, c + up * 0.02f + fwd * 0.152f, rot, new Vector3(0.24f, 0.12f, 0.03f), Olive * 0.8f, 0.05f); // front pouches
                        Prop(added, chest, PrimitiveType.Cube, c + up * 0.06f - fwd * 0.142f, rot, new Vector3(0.26f, 0.065f, 0.006f), TaserYellow * 0.9f, 0f); // back panel
                    }
                    else
                    {
                        Prop(added, chest, PrimitiveType.Cube, c + up * 0.07f + fwd * 0.118f - right * 0.09f, rot, new Vector3(0.045f, 0.055f, 0.008f), Gold, 0.9f); // badge
                        Prop(added, chest, PrimitiveType.Cube, c + up * 0.05f + fwd * 0.115f + right * 0.09f, rot, new Vector3(0.045f, 0.075f, 0.03f), Black, 0.3f); // radio
                        Prop(added, chest, PrimitiveType.Cube, c + up * 0.06f - fwd * 0.112f, rot, new Vector3(0.24f, 0.055f, 0.006f), Patch, 0f);   // "POLICE" panel
                    }
                }
                if (hips != null)
                {
                    var h = hips.position + up * 0.06f;
                    Prop(added, hips, PrimitiveType.Cylinder, h, rot, new Vector3(0.345f, 0.03f, 0.25f), Black, 0.3f);                               // duty belt
                    Prop(added, hips, PrimitiveType.Cube, h - up * 0.06f + right * 0.175f, rot, new Vector3(0.05f, 0.16f, 0.1f), Black, 0.3f);     // holster
                    Prop(added, hips, PrimitiveType.Cube, h - fwd * 0.12f - right * 0.06f, rot, new Vector3(0.08f, 0.05f, 0.04f), Black, 0.3f);    // cuff pouch
                    Prop(added, hips, PrimitiveType.Cube, h + fwd * 0.12f - right * 0.1f, rot, new Vector3(0.05f, 0.06f, 0.04f), Black, 0.3f);     // mag pouch
                }
            }
            AddOfficerRenderers(added, bodyLayer);
            // The props' colliders are part of his hitboxes now (their bones carry the Hitbox): re-read the set, so they switch
            // on and off with the rest (SetHittable cached it before the props existed).
            _hitboxes = null;
            SetHittable(_hittable);
            // His weapon light: a pool of light on the real room where his barrel points (only while he's in view).
            var lightGo = new GameObject("WeaponLight");
            lightGo.transform.SetParent(transform, false);
            _light = lightGo.AddComponent<OfficerFlashlight>();
            _light.Muzzle = _weapon != null ? _weapon.Muzzle : null;
            Debug.Log($"[HIS] Intruder #{Id}: dressed as {(swat ? "SWAT" : "patrol")} ({added.Count} props)");
        }

        /// <summary>
        /// A Marshal night's hitman (research/27 §2 "How a tester can tell them apart", §6 step 4). Alex's own dark clothes, no
        /// navy recolour, and one tell per look (<see cref="HIS.Scenario.HitmanTier.Look"/>: Brett 0.1.57's five waves dress as three kinds of
        /// men — the wave number is only which car they came in) that reads on the night camera:
        ///   street (waves 1–2, the street muscle and the street crew): a black beanie;
        ///   pros (waves 3–4, the professionals and the heavier pro team): suits, so bare heads and a light shirt front on the
        ///     chest (a charcoal recolour would not show: <see cref="NavyAtlas"/>'s pass only touches cloth that is already dark,
        ///     so the street men and the pros would read the same);
        ///   operators (wave 5): the SWAT helmet in black with an NVG mount flipped up on its front, a black plate carrier with
        ///     pouches, an SMG over the pistol (<see cref="ShowSmg"/>), and the plate (<see cref="Armored"/>, the tier's Plated).
        /// No weapon light (OfficerFlashlight), badge, patch or POLICE panel. Once per hitman. The props go into the body's
        /// renderer cache and hitboxes exactly as <see cref="DressAsOfficer"/>'s do, so SetVisible's layer switching and
        /// HideUntilPlaced cover them.
        /// </summary>
        void DressAsHitman(int wave)
        {
            if (_dressed) return;
            _dressed = true;
            var tier = HIS.Scenario.HitmanTier.For(wave);
            Armored = tier.Plated;
            var animator = GetComponentInChildren<Animator>();
            // The renderer cache (SetVisible) must exist before the props, so the props can be added to it with the body's layer.
            SetVisible(_visible);
            int bodyLayer = _rendererLayers != null && _rendererLayers.Length > 0 ? _rendererLayers[0] : gameObject.layer;
            var added = new List<Renderer>();
            if (animator != null && animator.isHuman)
            {
                var fwd = Flat(transform.forward).normalized; var up = Vector3.up; var right = Vector3.Cross(up, fwd);
                var head = animator.GetBoneTransform(HumanBodyBones.Head);
                var chest = animator.GetBoneTransform(HumanBodyBones.UpperChest) ?? animator.GetBoneTransform(HumanBodyBones.Chest);
                var rot = Quaternion.LookRotation(fwd, up);
                if (head != null && tier.Look != HIS.Scenario.HitmanTier.LookPro)
                {
                    var top = HeadTop(head);
                    if (tier.Look >= HIS.Scenario.HitmanTier.LookOperator)
                    {
                        // The SWAT shell and brim (DressAsOfficer), black instead of olive.
                        Prop(added, head, PrimitiveType.Sphere, top - up * 0.04f - fwd * 0.03f, rot, new Vector3(0.24f, 0.17f, 0.26f), Black, 0.1f);
                        Prop(added, head, PrimitiveType.Cube, top - up * 0.075f + fwd * 0.085f, rot * Quaternion.Euler(-8f, 0f, 0f), new Vector3(0.19f, 0.03f, 0.04f), Black, 0.1f);
                        // The NVG mount (research/27 §6 step 4): a 6 × 4 × 4 cm box on the shell's front just above the brim, tipped
                        // back with the shell's curve, and the two tubes flipped up off it, leaning back over the brow and standing
                        // ~2 cm above the helmet line: the silhouette that says "operator" on the porch camera. Well above the face.
                        Prop(added, head, PrimitiveType.Cube, top - up * 0.03f + fwd * 0.115f, rot * Quaternion.Euler(-20f, 0f, 0f), new Vector3(0.06f, 0.04f, 0.04f), Black, 0.1f);
                        Prop(added, head, PrimitiveType.Cylinder, top + up * 0.022f + fwd * 0.097f - right * 0.02f, rot * Quaternion.Euler(-25f, 0f, 0f), new Vector3(0.032f, 0.07f, 0.032f), Charcoal, 0.3f);
                        Prop(added, head, PrimitiveType.Cylinder, top + up * 0.022f + fwd * 0.097f + right * 0.02f, rot * Quaternion.Euler(-25f, 0f, 0f), new Vector3(0.032f, 0.07f, 0.032f), Charcoal, 0.3f);
                    }
                    else
                    {
                        // A black knit beanie: the folded cuff at the patrol cap band's height and a touch wider (it rides on the
                        // skull, its bottom at the brow, never over the eyes: Brett 0.1.26), a soft dome over the crown.
                        Prop(added, head, PrimitiveType.Cylinder, top - up * 0.045f - fwd * 0.03f, rot, new Vector3(0.212f, 0.05f, 0.242f), Black, 0f);  // cuff
                        Prop(added, head, PrimitiveType.Sphere, top - up * 0.035f - fwd * 0.03f, rot, new Vector3(0.205f, 0.13f, 0.235f), Black, 0f);    // crown
                    }
                }
                if (chest != null && tier.Look >= HIS.Scenario.HitmanTier.LookPro)
                {
                    var c = chest.position;
                    if (tier.Look >= HIS.Scenario.HitmanTier.LookOperator)
                    {
                        // A plate carrier, not the SWAT vest's full length: chest only, from under the arms to the bottom of the
                        // ribs (the vest reaches c − 0.26 m, over the belly). Its collider counts as a torso hit (the plate), so
                        // the belly below it stays open: "head, belly or legs drop him" (research/27 §2).
                        Prop(added, chest, PrimitiveType.Cube, c + fwd * 0.01f, rot, new Vector3(0.37f, 0.28f, 0.28f), Black, 0.05f);                // carrier
                        Prop(added, chest, PrimitiveType.Cube, c - up * 0.065f + fwd * 0.155f, rot, new Vector3(0.24f, 0.11f, 0.03f), Charcoal, 0.3f); // mag pouches
                    }
                    else
                    {
                        // The suit's light shirt front between the lapels: the POLICE panel's prop turned round to his chest (the
                        // badge's depth), narrower and taller so it reads as a shirt, not a band; tipped back to follow the chest.
                        Prop(added, chest, PrimitiveType.Cube, c + up * 0.045f + fwd * 0.115f, rot * Quaternion.Euler(-10f, 0f, 0f), new Vector3(0.1f, 0.13f, 0.006f), Patch, 0f);
                    }
                }
            }
            AddOfficerRenderers(added, bodyLayer);
            int smgParts = tier.Look >= HIS.Scenario.HitmanTier.LookOperator ? ShowSmg(bodyLayer) : 0;
            HitmanKit = tier.Look >= HIS.Scenario.HitmanTier.LookOperator ? "operator" : tier.Look == HIS.Scenario.HitmanTier.LookPro ? "suit" : "beanie";
            // The props' colliders are part of his hitboxes now: re-read the set (SetHittable cached it before they existed).
            _hitboxes = null;
            SetHittable(_hittable);
            Debug.Log($"[HIS] Intruder #{Id}: dressed as hitman w{tier.Wave}, {tier.Name}, the {HitmanKit} look ({added.Count} props{(smgParts > 0 ? $", SMG over the pistol ({smgParts} parts)" : "")}{(Armored ? ", plated" : "")})");
        }

        /// <summary>
        /// The operators (wave 5): a compact SMG in his gun hand over the pistol, the <see cref="ShowTaser"/> trick made permanent. The pistol
        /// stays "in hand" (its muzzle ray, flash, aim pose, Show/Drop carry on, and the SMG's barrel ends at its muzzle), but its
        /// meshes are switched off for good and taken out of SetVisible's cache, which would switch them back on each time he
        /// comes into view. The SMG's parts join that cache instead (the porch camera shows it on the path) and have no
        /// colliders, as the pistol has none (IntruderBuilder strips them): a round passes the gun as before, and a dropped gun
        /// keeps the pistol's floor box. Returns the parts made (0: no gun).
        /// </summary>
        int ShowSmg(int fallbackLayer)
        {
            if (_weapon == null || _weapon.Kind != HIS.Scenario.WeaponKind.Gun || _smg != null) return 0;
            var muzzle = _weapon.Muzzle;
            var m = muzzle != null ? muzzle : _weapon.transform;
            // The pistol's own meshes, not the flash quad on its muzzle (IntruderGunFx): that still fires.
            var pistol = new List<Renderer>();
            foreach (var r in _weapon.GetComponentsInChildren<Renderer>(true))
                if (r != null && r.name != "MuzzleFlash" && (muzzle == null || !r.transform.IsChildOf(muzzle))) pistol.Add(r);
            int gunLayer = fallbackLayer;
            if (_renderers != null)
            {
                var rs = new List<Renderer>(_renderers.Length); var ls = new List<int>(_renderers.Length);
                for (int i = 0; i < _renderers.Length; i++)
                {
                    int layer = _rendererLayers != null && i < _rendererLayers.Length ? _rendererLayers[i] : fallbackLayer;
                    if (pistol.Contains(_renderers[i])) { gunLayer = layer; continue; }
                    rs.Add(_renderers[i]); ls.Add(layer);
                }
                _renderers = rs.ToArray(); _rendererLayers = ls.ToArray();
            }
            foreach (var r in pistol) { r.enabled = false; r.gameObject.layer = gunLayer; }
            var root = new GameObject("SMG").transform;
            root.SetParent(_weapon.transform, false);
            root.SetPositionAndRotation(m.position, m.rotation);
            var parts = new List<Renderer>();
            Vector3 p = m.position, f = m.forward, u = m.up; var q = m.rotation;
            // In the muzzle's frame, as the Taser: the receiver runs back from the barrel over his wrist (the length is the tell:
            // a pistol ends at the fist), the magazine hangs ahead of the grip raked forward, the grip under his fist.
            Prop(parts, root, PrimitiveType.Cube, p - f * 0.14f + u * 0.006f, q, new Vector3(0.04f, 0.06f, 0.23f), Black, 0.3f);                                    // receiver
            Prop(parts, root, PrimitiveType.Cylinder, p - f * 0.02f, q * Quaternion.Euler(90f, 0f, 0f), new Vector3(0.022f, 0.04f, 0.022f), Black, 0.3f);            // barrel (ends at the muzzle)
            Prop(parts, root, PrimitiveType.Cube, p - f * 0.065f - u * 0.08f, q * Quaternion.Euler(-10f, 0f, 0f), new Vector3(0.024f, 0.12f, 0.036f), Black, 0.3f);  // magazine
            Prop(parts, root, PrimitiveType.Cube, p - f * 0.13f - u * 0.06f, q * Quaternion.Euler(15f, 0f, 0f), new Vector3(0.03f, 0.09f, 0.04f), Black, 0.3f);      // grip
            // Immediate (as GunEffects.MakeFlashQuad): gone before SetHittable re-reads his colliders.
            foreach (var r in parts) { var col = r.GetComponent<Collider>(); if (col != null) DestroyImmediate(col); }
            AddOfficerRenderers(parts, gunLayer);
            _smg = root.gameObject;
            return parts.Count;
        }

        /// <summary>
        /// Ricky, the protectee (research/27 §3, Question 1's default): Alex's dark clothes turned a light-grey sweatsuit, so he
        /// never reads as one of the men coming for him (same face, but grey, crouched in the far corner and never armed).
        /// Unarmed, no props. Once.
        /// </summary>
        void DressAsProtectee()
        {
            if (_dressed) return;
            _dressed = true;
            int swapped = RecolorCloth(ClothLook.Sweats);
            Debug.Log($"[HIS] Intruder #{Id}: dressed as Ricky (light-grey sweats, {swapped} material(s))");
        }

        /// <summary>
        /// Deputy Ortiz (research/27 §6 step 4, the check-in and the 2 AM relief): a light windbreaker (Alex's clothes recoloured
        /// light tan, trousers included: the atlas pass can't tell them apart) and his badge where a plain-clothes deputy wears
        /// it, gold on a black belt, a holster on his right hip. He reads as neither hitman tier on the night camera (they keep
        /// Alex's dark clothes). No badge-to-camera pose (there's no clip): the knock, the voice and the key identify him. Call
        /// right after the spawn, as officers and hitmen are dressed (the props are placed off his spawn pose). Once.
        /// </summary>
        public void DressAsDeputy()
        {
            if (_dressed) return;
            _dressed = true;
            var animator = GetComponentInChildren<Animator>();
            // The renderer cache (SetVisible) must exist before the props, so the props can be added to it with the body's layer.
            SetVisible(_visible);
            int bodyLayer = _rendererLayers != null && _rendererLayers.Length > 0 ? _rendererLayers[0] : gameObject.layer;
            int swapped = RecolorCloth(ClothLook.Windbreaker);
            var added = new List<Renderer>();
            var hips = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (hips != null)
            {
                var fwd = Flat(transform.forward).normalized; var up = Vector3.up; var right = Vector3.Cross(up, fwd);
                var rot = Quaternion.LookRotation(fwd, up);
                var h = hips.position + up * 0.06f;
                // The officer's duty belt (DressAsOfficer), the badge clipped on its front left of the buckle (the cap badge's
                // gold and shine; on the belt's curve 6 cm off centre), the holster as the officer's.
                Prop(added, hips, PrimitiveType.Cylinder, h, rot, new Vector3(0.345f, 0.03f, 0.25f), Black, 0.3f);                                  // belt
                Prop(added, hips, PrimitiveType.Cube, h + fwd * 0.121f - right * 0.06f, rot, new Vector3(0.045f, 0.055f, 0.008f), Gold, 0.9f);       // badge
                Prop(added, hips, PrimitiveType.Cube, h - up * 0.06f + right * 0.175f, rot, new Vector3(0.05f, 0.16f, 0.1f), Black, 0.3f);           // holster
            }
            // U.S. MARSHAL across his chest and back (VISITORS, Brett 2026-10-04 (b): "he wears a U.S. MARSHAL jacket", readable on
            // the porch camera as he walks up; IntruderVisitors).
            AddMarshalPrint(added, animator);
            AddOfficerRenderers(added, bodyLayer);
            if (PlacementPending) HideUntilPlaced();              // (dressed on his hidden spawn frame: the props stay hidden with him)
            // The props' colliders are part of his hitboxes now: re-read the set (SetHittable cached it before they existed).
            _hitboxes = null;
            SetHittable(_hittable);
            Debug.Log($"[HIS] Intruder #{Id}: dressed as Deputy Ortiz (light windbreaker, {swapped} material(s), {added.Count} props)");
        }

        /// <summary>The top of his head: Mixamo's HeadTop_End if the rig has it, else ~0.19 m above the head bone.</summary>
        static Vector3 HeadTop(Transform head)
        {
            foreach (var t in head.GetComponentsInChildren<Transform>(true))
                if (t.name.EndsWith("HeadTop_End")) return t.position;
            return head.position + Vector3.up * 0.19f;
        }

        void Prop(List<Renderer> added, Transform bone, PrimitiveType type, Vector3 worldPos, Quaternion worldRot, Vector3 size, Color color, float metal)
        {
            var go = GameObject.CreatePrimitive(type);
            // Its collider stays (Brett 0.1.40: "a lot of the shots at the cops werent hitting"): a round through the cap or
            // helmet is a head hit, through the vest a torso hit, the belt the belly — whatever Hitbox its bone carries
            // (Firearm: GetComponentInParent<IDamageable>). What he looks like is what can be hit.
            go.name = "OfficerProp";
            // World size first, then onto the bone keeping it (Mixamo bones can carry a scale).
            go.transform.SetPositionAndRotation(worldPos, worldRot);
            // Unity's cylinder is 2 m tall at scale 1: halve y so `size` is the real height.
            go.transform.localScale = type == PrimitiveType.Cylinder ? new Vector3(size.x, size.y * 0.5f, size.z) : size;
            go.transform.SetParent(bone, true);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = PropMaterial(color, metal);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            added.Add(r);
        }

        /// <summary>Add renderers made after the body's cache (SetVisible) to it, with the layer they show on in the room.</summary>
        void AddOfficerRenderers(List<Renderer> extra, int layer)
        {
            if (extra.Count == 0) return;
            foreach (var r in extra) r.gameObject.layer = layer;
            if (_renderers == null) { SetVisible(_visible); return; } // (built now, props included)
            var rs = new List<Renderer>(_renderers); rs.AddRange(extra);
            var ls = new List<int>(_rendererLayers ?? new int[0]);
            while (ls.Count < _renderers.Length) ls.Add(layer);
            foreach (var _ in extra) ls.Add(layer);
            _renderers = rs.ToArray(); _rendererLayers = ls.ToArray();
            SetVisible(_visible);
        }

        // ───────────────────────────── the uniform ─────────────────────────────
        static readonly Dictionary<Material, Material> s_uniforms = new();

        /// <summary>His body materials swapped for navy-uniform copies (shared by every officer; made once per source material).</summary>
        void RecolorUniform()
        {
            int swapped = 0, made = 0;
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.GetComponentInParent<IntruderWeapon>() != null) continue;
                var mats = smr.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src == null) continue;
                    if (!s_uniforms.TryGetValue(src, out var u) || u == null) { s_uniforms[src] = u = MakeUniform(src); made++; }
                    if (u != null && u != src) { mats[i] = u; changed = true; swapped++; }
                }
                if (changed) smr.sharedMaterials = mats;
            }
            s_uniformReady = true; // (made here if the prewarm never ran: nothing left for it to do)
            // "all prewarmed" = this spawn only swapped references (no Blit/ReadPixels hitch); "made now" = it paid for them.
            if (!s_loggedUniform) { s_loggedUniform = true; Debug.Log($"[HIS] Officer uniform: {swapped} body material(s) swapped, {(made == 0 ? "all prewarmed" : $"{made} made now")} ({s_uniformInfo})"); }
        }

        /// <summary>The navy copies exist (prewarmed, or made by the first officer): <see cref="PrewarmUniform"/> has nothing to do.</summary>
        public static bool UniformReady => s_uniformReady;
        static bool s_uniformReady;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetUniformStatics() { s_uniformReady = false; s_loggedUniform = false; s_uniforms.Clear(); } // domain reload off

        /// <summary>
        /// Make the navy copies ahead of the first officer (HANDOFF 0.1.26: his spawn hitched 10–20 ms on NavyAtlas's Blit +
        /// ReadPixels + upload, right as the standoff began). PoliceResponse calls it once, at a quiet moment. The copies go in
        /// <see cref="s_uniforms"/>, keyed by the source material; every intruder is the spawner's one prefab and shares its
        /// body materials (nothing gives him his own instance), so the first real officer's <see cref="RecolorUniform"/> finds
        /// them and only swaps references. Sources: the undressed intruders in memory, the prefab asset included
        /// (FindObjectsOfTypeAll lists loaded assets too; the spawner's reference keeps it loaded). Weapons are skipped as
        /// RecolorUniform skips them. False = no intruder loaded yet (try again later).
        /// </summary>
        public static bool PrewarmUniform()
        {
            if (s_uniformReady) return true;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int sources = 0, made = 0;
            foreach (var who in Resources.FindObjectsOfTypeAll<Intruder>())
            {
                if (who == null || who._dressed) continue;   // (an officer's materials are already the copies; a hitman's are the prefab's own)
                sources++;
                var weapons = who.GetComponentsInChildren<IntruderWeapon>(true);
                foreach (var smr in who.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    bool onWeapon = false;
                    foreach (var w in weapons) if (w != null && smr.transform.IsChildOf(w.transform)) { onWeapon = true; break; }
                    if (onWeapon) continue;
                    foreach (var src in smr.sharedMaterials)
                    {
                        if (src == null || (s_uniforms.TryGetValue(src, out var u) && u != null)) continue;
                        s_uniforms[src] = MakeUniform(src);
                        made++;
                    }
                }
            }
            if (sources == 0) return false;
            s_uniformReady = true;
            Debug.Log($"[HIS] Officer uniform prewarmed: {made} navy material(s) from {sources} intruder(s) in {clock.ElapsedMilliseconds} ms ({s_uniformInfo})");
            return true;
        }
        static bool s_loggedUniform;
        static string s_uniformInfo = "none made";
        static int s_recoloured;
        static string s_samples = "";

        static Material MakeUniform(Material src)
        {
            var tex = src.HasProperty("_BaseMap") && src.GetTexture("_BaseMap") != null ? src.GetTexture("_BaseMap")
                    : src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            var navy = tex != null ? NavyAtlas(tex) : null;
            s_uniformInfo = $"{src.name} [{src.shader.name}], atlas {(tex != null ? tex.name : "none")} → {(navy != null ? $"navy copy, {s_recoloured} navy px; read back {s_samples}" : "tint")}";
            var m = new Material(src) { name = src.name + " (police)" };
            if (navy != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", navy);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", navy);
            }
            else
            {
                // No GPU readback (batch runs): a light blue-grey tint only, which leaves skin nearly as it was.
                var tint = new Color(0.86f, 0.9f, 1.05f, 1f);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", m.GetColor("_BaseColor") * tint);
                else m.color *= tint;
            }
            return m;
        }

        /// <summary>The officers' copy of his atlas: the clothes police navy (sRGB 25, 36, 78 at the clothes' mid shade).</summary>
        static Texture2D NavyAtlas(Texture src) => ClothAtlas(src, 25f, 36f, 78f, "police navy", police: true);

        /// <summary>
        /// A 1024² copy of his atlas with the clothes turned <paramref name="label"/> (<paramref name="nr"/>, <paramref name="ng"/>,
        /// <paramref name="nb"/>: sRGB at the clothes' mid shade): dark, neutral-to-cool pixels (his jacket, shirt, trousers) move
        /// to it keeping their shading; skin, eyes, lips and brown hair are left alone; near-black (boots, belt) stays black.
        /// Blit + ReadPixels once (~10 ms), then uploaded non-readable. Generalised from the navy (research/27 §3) for a Marshal
        /// night's light clothes; <paramref name="police"/> keeps the navy's own read-back numbers for the uniform's log line.
        /// </summary>
        static Texture2D ClothAtlas(Texture src, float nr, float ng, float nb, string label, bool police)
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return null;
            RenderTexture rt = null; var prev = RenderTexture.active;
            try
            {
                const int n = 1024;
                rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, true, false) { name = src.name + " (" + label + ")" };
                tex.ReadPixels(new Rect(0, 0, n, n), 0, 0, false);
                var px = tex.GetPixels32();
                if (police) s_samples = $"jacket {px[666 * n + 153]}, trousers {px[(n - 768) * n + 153]}, hand {px[691 * n + 665]}";
                for (int i = 0; i < px.Length; i++)
                {
                    var p = px[i];
                    float lum = (0.299f * p.r + 0.587f * p.g + 0.114f * p.b) / 255f;
                    if (lum > 0.24f || p.r > p.b + 10) continue;                  // skin, eyes, lips, brown hair: as they are
                    float w = (1f - Edge(0.15f, 0.24f, lum)) * Edge(0.018f, 0.045f, lum)
                              * Edge(1f, 4f, p.b - p.r);                          // the cool dark cloth; neutral black boots stay black
                    if (w <= 0f) continue;
                    float k = 0.55f + 5.5f * lum;                                   // keep the folds and seams
                    px[i] = new Color32((byte)Mathf.Lerp(p.r, Mathf.Min(255f, nr * k), w), (byte)Mathf.Lerp(p.g, Mathf.Min(255f, ng * k), w),
                                        (byte)Mathf.Lerp(p.b, Mathf.Min(255f, nb * k), w), p.a);
                }
                tex.SetPixels32(px);
                tex.Apply(true, true);
                if (police)
                {
                    s_recoloured = 0;
                    foreach (var q in px) if (q.b > q.r + 20) s_recoloured++;
                }
                return tex;
            }
            catch (System.Exception e) { Debug.LogWarning($"[HIS] {(police ? "Officer uniform" : $"Clothes ({label})")}: no recolour ({e.Message}); tint only"); return null; }
            finally { RenderTexture.active = prev; if (rt != null) RenderTexture.ReleaseTemporary(rt); }
        }

        // ───────────────────────────── a Marshal night's light clothes (research/27) ─────────────────────────────
        /// <summary>The friendlies' clothes: Ricky's light-grey sweats, Deputy Ortiz's light tan windbreaker (the navy keeps its own cache).</summary>
        enum ClothLook { Sweats, Windbreaker }
        static readonly ClothLook[] s_lookKinds = { ClothLook.Sweats, ClothLook.Windbreaker };
        /// <summary>One copy per source material and look, shared by every man who wears it (as <see cref="s_uniforms"/>).</summary>
        static readonly Dictionary<(Material, ClothLook), Material> s_looks = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetLookStatics() => s_looks.Clear(); // domain reload off

        /// <summary>
        /// The look's sRGB at the clothes' mid shade. The pass scales it by 0.65–1.9 with the cloth's own shading, so over Alex's
        /// clothes the sweats come out about 110–180 grey and the windbreaker a light tan: both light, neither a hitman's dark,
        /// and different from each other by hue.
        /// </summary>
        static void LookColor(ClothLook look, out float r, out float g, out float b, out string label)
        {
            if (look == ClothLook.Sweats) { r = 128f; g = 128f; b = 126f; label = "grey sweats"; }
            else { r = 136f; g = 120f; b = 88f; label = "tan windbreaker"; }
        }

        /// <summary>His body materials swapped for this look's copies (made once per source material). Returns how many.</summary>
        int RecolorCloth(ClothLook look)
        {
            int swapped = 0;
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.GetComponentInParent<IntruderWeapon>() != null) continue;
                var mats = smr.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var src = mats[i];
                    if (src == null) continue;
                    if (!s_looks.TryGetValue((src, look), out var u) || u == null) s_looks[(src, look)] = u = MakeLook(src, look);
                    if (u != null && u != src) { mats[i] = u; changed = true; swapped++; }
                }
                if (changed) smr.sharedMaterials = mats;
            }
            return swapped;
        }

        static Material MakeLook(Material src, ClothLook look)
        {
            LookColor(look, out float r, out float g, out float b, out string label);
            var tex = src.HasProperty("_BaseMap") && src.GetTexture("_BaseMap") != null ? src.GetTexture("_BaseMap")
                    : src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            var atlas = tex != null ? ClothAtlas(tex, r, g, b, label, police: false) : null;
            var m = new Material(src) { name = src.name + " (" + label + ")" };
            if (atlas != null)
            {
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", atlas);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", atlas);
            }
            else
            {
                // No GPU readback (batch runs): a light tint only, as the navy's (nothing judges a look in a batch run).
                var tint = look == ClothLook.Sweats ? new Color(1.08f, 1.08f, 1.08f, 1f) : new Color(1.08f, 1.04f, 0.94f, 1f);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", m.GetColor("_BaseColor") * tint);
                else m.color *= tint;
            }
            return m;
        }

        /// <summary>
        /// Make Ricky's sweats and Ortiz's windbreaker ahead of their spawns (research/27 §3: during the night's intro card, the
        /// <see cref="PrewarmUniform"/> pattern — each copy is a Blit + ReadPixels + upload, a 10–20 ms hitch the first spawn
        /// would pay otherwise). Sources: the undressed intruders in memory, the prefab asset included. Only missing copies are
        /// made, so a second call is free. False = no intruder loaded yet (try again later).
        /// </summary>
        public static bool PrewarmMarshalLooks()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            PrewarmVisitorPrints();                          // Ortiz's U.S. MARSHAL print, the pizza lid (VISITORS): made once, under the card
            int sources = 0, made = 0;
            foreach (var who in Resources.FindObjectsOfTypeAll<Intruder>())
            {
                if (who == null || who._dressed) continue;   // (a dressed man's materials may be copies already)
                sources++;
                var weapons = who.GetComponentsInChildren<IntruderWeapon>(true);
                foreach (var smr in who.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    bool onWeapon = false;
                    foreach (var w in weapons) if (w != null && smr.transform.IsChildOf(w.transform)) { onWeapon = true; break; }
                    if (onWeapon) continue;
                    foreach (var src in smr.sharedMaterials)
                    {
                        if (src == null) continue;
                        foreach (var look in s_lookKinds)
                        {
                            if (s_looks.TryGetValue((src, look), out var u) && u != null) continue;
                            s_looks[(src, look)] = MakeLook(src, look);
                            made++;
                        }
                    }
                }
            }
            if (sources == 0) return false;
            if (made > 0) Debug.Log($"[HIS] Marshal looks prewarmed: {made} material(s) (grey sweats, tan windbreaker) from {sources} intruder(s) in {clock.ElapsedMilliseconds} ms");
            return true;
        }

        /// <summary>GLSL smoothstep (0 below <paramref name="e0"/>, 1 above <paramref name="e1"/>) — not Mathf.SmoothStep, which interpolates.</summary>
        static float Edge(float e0, float e1, float x) { float t = Mathf.Clamp01((x - e0) / (e1 - e0)); return t * t * (3f - 2f * t); }

        // ───────────────────────────── the Taser ─────────────────────────────
        /// <summary>
        /// The yellow Taser in his gun hand instead of the pistol (the pistol stays "in hand" but hidden, so the aim pose and
        /// its muzzle ray carry on: the Taser points where the pistol would).
        /// </summary>
        void ShowTaser(bool on)
        {
            // A hitman's SMG (research/27): no Taser, and the pistol under it stays hidden (OfficerWounded calls ShowTaser(false)).
            if (_weapon == null || _smg != null) return;
            _pistolRenderers ??= _weapon.GetComponentsInChildren<Renderer>(true);
            foreach (var r in _pistolRenderers) if (r != null && r.gameObject != _taser && (_taser == null || !r.transform.IsChildOf(_taser.transform))) r.enabled = !on;
            if (on && _taser == null)
            {
                var root = new GameObject("Taser").transform;
                var m = _weapon.Muzzle != null ? _weapon.Muzzle : _weapon.transform;
                root.SetParent(_weapon.transform, false);
                root.SetPositionAndRotation(m.position, m.rotation);
                var parts = new List<Renderer>();
                // Body back from the muzzle along the barrel, a black grip under it, the cartridge face at the front.
                Prop(parts, root, PrimitiveType.Cube, m.position - m.forward * 0.08f, m.rotation, new Vector3(0.035f, 0.05f, 0.16f), TaserYellow, 0f);
                Prop(parts, root, PrimitiveType.Cube, m.position - m.forward * 0.13f - m.up * 0.06f, m.rotation * Quaternion.Euler(15f, 0f, 0f), new Vector3(0.03f, 0.09f, 0.04f), Black, 0.2f);
                Prop(parts, root, PrimitiveType.Cube, m.position + m.forward * 0.004f, m.rotation, new Vector3(0.03f, 0.045f, 0.01f), Black, 0.2f);
                int layer = _weapon.gameObject.layer;
                foreach (var r in parts) r.gameObject.layer = layer;
                _taser = root.gameObject;
            }
            if (_taser != null) _taser.SetActive(on);
        }
    }
}
