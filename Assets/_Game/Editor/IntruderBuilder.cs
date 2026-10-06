using System.Linq;
using HIS.Intruder;
using HIS.Weapons;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Animations;
using UnityEngine.Playables;
using BodyPart = HIS.Weapons.BodyPart;

/// <summary>Builds the rigged intruder prefab (Mixamo Alex) with Animator, bone hitboxes, wounds and AI. HIS > Build Alex Prefab.</summary>
public static class IntruderBuilder
{
    const string ModelPath = "Assets/ThirdParty/Mixamo/Alex/Alex.fbx";
    const string AnimDir = "Assets/ThirdParty/Mixamo/Animations/";
    const string TexDir = "Assets/ThirdParty/Mixamo/Alex/Textures/";
    const string ControllerPath = "Assets/_Game/Animation/Intruder_Alex.controller";
    const string PrefabPath = "Assets/_Game/Prefabs/Intruder_Alex.prefab";
    const string MaterialPath = "Assets/_Game/Materials/Intruder_Alex.mat";

    [MenuItem("HIS/Build Alex Prefab")]
    public static void Build()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        if (model == null) { Debug.LogError("[HIS] Alex.fbx not found"); return; }

        ReimportIfUntrimmed();
        var controller = BuildController();

        var root = new GameObject("Intruder_Alex");
        var agent = root.AddComponent<NavMeshAgent>();
        agent.radius = 0.3f; agent.height = 1.8f; agent.speed = IntruderClips.WalkSpeed; agent.angularSpeed = 240f; agent.acceleration = 4f; agent.stoppingDistance = 0.6f;
        // Off in the prefab (Intruder.Awake turns it off anyway; WalkIn turns it on on the NavMesh): no "Failed to create agent
        // because it is not close enough to the NavMesh" at every spawn (0.1.56's log had 31).
        agent.enabled = false;
        root.AddComponent<Intruder>();
        var wounds = root.AddComponent<WoundSystem>();
        SetPrivate(wounds, "bloodMaterial", AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Materials/Blood_Decal.mat"));
        SetPrivate(wounds, "wallSplatterMaterial", AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Materials/Blood_Splatter.mat"));

        var body = (GameObject)PrefabUtility.InstantiatePrefab(model);
        body.name = "Model";
        body.transform.SetParent(root.transform, false);

        // Scale sanity: Mixamo "FBX for Unity" should already be metres; fix if it came in as centimetres.
        var bounds = RendererBounds(body);
        if (bounds.size.y > 20f) body.transform.localScale = Vector3.one * 0.01f;
        else if (bounds.size.y < 0.2f) body.transform.localScale = Vector3.one * 100f;
        bounds = RendererBounds(body);
        Debug.Log($"[HIS] Alex height {bounds.size.y:F2} m");

        var animator = body.GetComponent<Animator>() ?? body.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        body.AddComponent<IntruderAnimator>();

        // Material: PBR + Depth-API occlusion, from the extracted Mixamo textures.
        var mat = BuildMaterial();
        foreach (var r in body.GetComponentsInChildren<Renderer>(true))
        {
            r.sharedMaterials = Enumerable.Repeat(mat, r.sharedMaterials.Length).ToArray();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (r is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
        }

        AddHitboxes(animator);
        AddKnife(animator);
        AddPistol(animator);

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        Debug.Log($"[HIS] Built {PrefabPath}");
    }

    /// <summary>
    /// Meta's recommended Depth API bias (the door uses it too). At 0 a gun, knife or body lying on the real floor was only
    /// opaque where it stood more than ~6.6 cm off it (soft occlusion's 4% band): the dropped guns and the corpses' lower
    /// halves looked see-through and shimmered (Brett 0.1.41). Real furniture still hides a man once it is >6% nearer.
    /// </summary>
    const float FloorSafeDepthBias = 0.06f;

    static Material BuildMaterial()
    {
        var shader = Shader.Find("HIS/OcclusionLitPBR");
        if (shader == null) throw new System.Exception("[HIS] HIS/OcclusionLitPBR shader missing");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, MaterialPath); }
        mat.shader = shader;
        mat.SetTexture("_MainTex", AssetDatabase.LoadAssetAtPath<Texture2D>(TexDir + "Ch18_1001_Diffuse.png"));
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(TexDir + "Ch18_1001_Normal.png");
        if (normal != null)
        {
            var ti = AssetImporter.GetAtPath(TexDir + "Ch18_1001_Normal.png") as TextureImporter;
            if (ti != null && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); }
            mat.SetTexture("_BumpMap", normal);
        }
        mat.SetFloat("_Metallic", 0f);
        mat.SetFloat("_Glossiness", 0.55f);
        mat.SetFloat("_EnvironmentDepthBias", FloorSafeDepthBias);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Measured clip facts live in IntruderClips (HIS > Debug > Measure Intruder Clips). Every cycle's time scale is
    // gameplay speed / natural speed, so the planted foot moves exactly as fast as the agent carries him (no skating).
    const string ArmsMaskPath = "Assets/_Game/Animation/Intruder_ArmsMask.mask";
    const int Crawl = (int)IntruderAnimator.Gait.Crawl, Kneel = (int)IntruderAnimator.Gait.Kneel,
              Prone = (int)IntruderAnimator.Gait.Prone, Sit = (int)IntruderAnimator.Gait.Sit,
              Seated = (int)IntruderAnimator.Gait.Seated, Driving = (int)IntruderAnimator.Gait.Driving,
              Crouch = (int)IntruderAnimator.Gait.Crouch, Cower = (int)IntruderAnimator.Gait.Cower, GaitCount = 14;
    /// <summary>In a car seat: straight in from his feet and straight back out (the car door hides the step).</summary>
    static bool InSeat(int g) => g is Seated or Driving;
    /// <summary>On his feet: Normal, Limp, Calm, Drunk, Sneak, Gun (the rest are on the floor or on his knees).</summary>
    static bool Upright(int g) => g is 0 or 1 or 3 or 4 or 5 or 9;

    static AnimatorController BuildController()
    {
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("GaitRate", AnimatorControllerParameterType.Float);
        foreach (var t in new[] { "Kick", "Attack", "Hit", "Fall", "Die", "Act" }) ctrl.AddParameter(t, AnimatorControllerParameterType.Trigger);
        foreach (var i in new[] { "Gait", "AttackIndex", "HitPart", "DeathIndex", "FallIndex", "ActIndex" }) ctrl.AddParameter(i, AnimatorControllerParameterType.Int);
        ctrl.AddParameter("Knife", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Acting", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Aim", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("ExitRate", AnimatorControllerParameterType.Float);   // the car-exit clips' pace (IntruderAnimator.ExitCar)
        var gaitRate = ctrl.parameters.First(p => p.name == "GaitRate"); gaitRate.defaultFloat = 1f;
        ctrl.parameters = ctrl.parameters.Select(p => p.name == "GaitRate" ? gaitRate : p).ToArray();
        var exitRate = ctrl.parameters.First(p => p.name == "ExitRate"); exitRate.defaultFloat = 1f;
        ctrl.parameters = ctrl.parameters.Select(p => p.name == "ExitRate" ? exitRate : p).ToArray();

        var sm = ctrl.layers[0].stateMachine;

        // Normal gait (attackers, Speed): fight idle → walk (at the gameplay walk speed) → run (at the gameplay run speed).
        var loco = LocomotionTree(ctrl, "Locomotion", "FightIdle");
        sm.defaultState = loco;
        // Calm gait (surrenderer): the same walk/run over a relaxed standing idle.
        var calm = LocomotionTree(ctrl, "Calm", "Idle");

        // Limp (one leg hit): idle → injured walk at the limp speed.
        var limp = Tree(ctrl, "Limp", ("Idle", 0f, 1f), ("InjuredWalking", IntruderClips.LimpSpeed, IntruderClips.LimpSpeed / IntruderClips.LimpNatural));

        // Crawl (legs out): one face-down cycle whose playback rate follows his speed (GaitRate = speed / natural; 0 = still).
        var crawl = sm.AddState("Crawl");
        crawl.motion = Clip("ZombieCrawl");
        crawl.speedParameterActive = true; crawl.speedParameter = "GaitRate";

        // Drunk (the neighbour): sway → stagger at 0.75 → the drunk jog out of the door when told to leave.
        var drunk = Tree(ctrl, "Drunk", ("DrunkIdle", 0f, 1f),
            ("DrunkWalk", IntruderClips.DrunkWalkSpeed, IntruderClips.DrunkWalkSpeed / IntruderClips.DrunkWalkNatural),
            ("DrunkRun", IntruderClips.DrunkRunSpeed, IntruderClips.DrunkRunSpeed / IntruderClips.DrunkRunNatural));
        // Sneak (the burglar): crouched creep → a jog looking back over his shoulder → flat-out sprint.
        var sneak = Tree(ctrl, "Sneak", ("Idle", 0f, 1f),
            ("CrouchedWalk", IntruderClips.SneakSpeed, IntruderClips.SneakSpeed / IntruderClips.SneakNatural),
            ("RunLookBack", IntruderClips.LookBackRunSpeed, 1f),
            ("Running", IntruderClips.RunSpeed, IntruderClips.RunSpeed / IntruderClips.RunNatural));

        // Held postures: on his knees, lying flat face down (the arms layer adds hands on/behind the head to both), on his backside.
        var kneel = sm.AddState("Kneel"); kneel.motion = Clip("KneelIdle");
        var prone = sm.AddState("Prone"); prone.motion = Clip("GetUpFromStomach", "ProneHold");
        var sit = sm.AddState("Sit"); sit.motion = Clip("StumbleBackFall", "SitHold");

        // Gun (the gunman on his feet): pistol low ready at a stand → the aimed pistol walk (1.5 m/s, 0.63×) → the sprint.
        // The GunArms layer holds the pistol over all three (low ready or aimed); fleeing it lets the sprint's own arms pump.
        var gun = Tree(ctrl, "Gun", ("PistolIdleReady", 0f, 1f),
            ("PistolWalkAimed", IntruderClips.GunWalkSpeed, IntruderClips.GunWalkSpeed / IntruderClips.GunWalkNatural),
            ("Running", IntruderClips.RunSpeed, IntruderClips.RunSpeed / IntruderClips.RunNatural));

        // Riding in a police car (Brett 0.1.48: "theyre like kneeling instead of sitting"): the passengers sit, the driver has his
        // hands on the wheel. The kneel stood in for both (the floor-sit is a backside fall, reachable only by falling).
        var seated = sm.AddState("Seated"); seated.motion = Clip("SittingIdle");
        var driving = sm.AddState("Driving"); driving.motion = Clip("Driving");
        // Getting out (Brett 2026-10-05: "have the flow of cops exiting look better"): Mixamo Exiting Car out of the driver's side,
        // and its mirror out of the passenger's (MixamoImportSettings.Sided), at the rider's own pace (ExitRate, ~1.5×). The brain
        // cross-fades in from the seat (IntruderAnimator.ExitCar) and sets the gun gait as he stands; the state hands him to it
        // once he's clear of the door (below: IntruderClips.ExitClear — the take's tail, turning back to shut it, is skipped).
        // Their travel and turn go to the brain (IntruderCarExit: no NavMesh out on the street).
        var exitCar = sm.AddState("ExitCar"); exitCar.motion = Clip("ExitingCar", "ExitCar");
        var exitCarMirror = sm.AddState("ExitCarMirror"); exitCarMirror.motion = Clip("ExitingCar", "ExitCarMirror");

        // Ricky, the protectee on a Marshal night (research/27 §3): crouched in his hiding spot (LowCrouchIdle), and ducked with
        // his arms over his head (DuckHold: DuckCover's end held, MixamoImportSettings.Extras). He never stands up: the brain
        // snaps him straight into the crouch, and from the crouch the duck is a slow blend into the hold (DuckCover is a take
        // that starts on his feet, as Mixamo's do — Logs/poses/DuckCover.png — so playing it would stand him up first). From
        // his feet (no brain does that today) DuckCover plays whole.
        var crouch = sm.AddState("Crouch"); crouch.motion = Clip("LowCrouchIdle");
        var cower = sm.AddState("Cower"); cower.motion = Clip("DuckCover", "DuckHold") ?? Clip("KneelIdle"); // (no cut yet: kneeling, not a T-pose)
        var duck = sm.AddState("DuckCover"); duck.motion = Clip("DuckCover");

        var gaits = new[] { loco, limp, crawl, calm, drunk, sneak, kneel, prone, sit, gun, seated, driving, crouch, cower };   // index = IntruderAnimator.Gait value

        // The ways down and up (root motion: the travel goes through the NavMesh).
        var toKnees = sm.AddState("FallToKnees"); toKnees.motion = Clip("FallToKnees");
        var toProne = sm.AddState("CrouchToProne"); toProne.motion = Clip("CrouchToProne");
        var kneelUp = sm.AddState("KneelToStand"); kneelUp.motion = Clip("KneelToStand");
        var proneUp = sm.AddState("GetUpFromStomach"); proneUp.motion = Clip("GetUpFromStomach");
        var sitUp = sm.AddState("GetUpFromBack"); sitUp.motion = Clip("GetUpFromBack");

        // Where a one-shot hands him back to for each gait: kneeling or going prone from his feet starts with the drop to his knees.
        var entry = (AnimatorState[])gaits.Clone();
        entry[Kneel] = toKnees; entry[Prone] = toKnees;

        // Gait changes. On his feet: straight blends (a moment longer to drop into the crawl). Down: the matching way down.
        // From the floor/knees: the matching way up. No route = he stays where he is (e.g. sitting told to kneel).
        for (int from = 0; from < GaitCount; from++)
            for (int to = 0; to < GaitCount; to++)
            {
                if (from == to) continue;
                AnimatorState dest = null; float blend = 0.25f;
                if (to == Crawl) { if (from != Crawl) { dest = crawl; blend = 0.5f; } }
                // Ricky's two postures: in and out of them by plain blends, no way down/up clips (he's already low).
                else if (to == Cower) { if (Upright(from)) { dest = duck; blend = 0.2f; } else if (from is Crouch or Kneel) { dest = cower; blend = 0.6f; } }
                else if (to == Crouch) { if (Upright(from) || from is Cower or Kneel) { dest = crouch; blend = from == Cower ? 0.6f : 0.4f; } }
                else if (from is Crouch or Cower) { if (Upright(to)) { dest = gaits[to]; blend = 0.5f; } else if (to == Kneel) { dest = kneel; blend = 0.5f; } }
                else if (InSeat(to)) { if (Upright(from) || InSeat(from)) { dest = gaits[to]; blend = 0.3f; } }
                else if (InSeat(from)) { if (Upright(to)) { dest = gaits[to]; blend = 0.4f; } }
                else if (Upright(from)) dest = Upright(to) ? gaits[to] : to is Kneel or Prone ? toKnees : null;
                else if (Upright(to)) { dest = from == Kneel ? kneelUp : from == Sit ? sitUp : proneUp; blend = 0.3f; }
                else if (from == Kneel && to == Prone) { dest = toProne; blend = 0.3f; }
                if (dest == null) continue;
                var tr = gaits[from].AddTransition(dest);
                tr.AddCondition(AnimatorConditionMode.Equals, to, "Gait");
                tr.hasExitTime = false; tr.duration = blend;
            }

        // Each way down/up ends where the gait says (so "get out!" mid-kneel finishes kneeling, then stands up).
        Exits(toKnees, 0.9f, 0.25f, g => Upright(g) ? kneelUp : g == Kneel ? kneel : g == Prone ? toProne : null, crawl);
        // DuckCover (from his feet) settles into the hold, or wherever the gait has gone since.
        Exits(duck, 0.95f, 0.25f, g => g == Cower ? cower : g == Crouch ? crouch : Upright(g) ? gaits[g] : g == Kneel ? kneel : null, crawl);
        // CrouchToProne is down by 0.62 (1.1 s) and then props itself up on its elbows (a rifle prone): leave there and lower
        // flat into the hold.
        Exits(toProne, 0.62f, 0.6f, g => Upright(g) ? proneUp : prone, crawl);
        foreach (var (up, exitAt) in new[] { (kneelUp, 0.9f), (proneUp, 0.92f), (sitUp, 0.85f) })
            Exits(up, exitAt, 0.4f, g => Upright(g) ? gaits[g] : g is Kneel or Prone ? toKnees : null, crawl);
        // Out of the car: at his rider's pace; clear of the door, into the standing gait the brain set as he stood (his legs shot
        // out once he's up: the crawl at once). Seated or driving still (the gait he rode in): it holds — the brain ends it.
        foreach (var st in new[] { exitCar, exitCarMirror })
        {
            st.speedParameterActive = true; st.speedParameter = "ExitRate";
            Exits(st, IntruderClips.ExitClear / IntruderClips.ExitLength, 0.3f, g => Upright(g) ? gaits[g] : null, crawl);
        }

        // One-shots via Any State. Each returns to the gait he's in now (a limping man doesn't pop back to a normal walk).
        OneShot(sm, entry, crawl, "Kick", "Kick", Clip("MmaKick"), null, 0);
        var attacks = new[] { "CrossPunch", "Hook", "JabCross", "Stabbing", "SlashAdvance", "BayonetStab" };
        for (int i = 0; i < attacks.Length; i++) OneShot(sm, entry, crawl, "Attack_" + attacks[i], "Attack", Clip(attacks[i]), "AttackIndex", i);
        // HitOnLegs is not a standing reaction (it knocks him flat on his back) — legs use HitToBody; legs out is its own fall (Fall_LegsOut).
        var hits = new[] { "HeadHit", "StandingReactLargeGut", "HitToBody", "HitToBody", "PistolHit" };   // + the gunman's flinch (gun stays up)
        for (int i = 0; i < hits.Length; i++) OneShot(sm, entry, crawl, "Hit_" + i + "_" + hits[i], "Hit", Clip(hits[i]), "HitPart", i, 0.08f, selfRestart: true);

        // Gestures (ActKind order): in place over the gait; they end at the clip's end or as soon as the brain drops "Acting"
        // (challenged mid-hiccup, the door swung open under his hand).
        var acts = new[] { "DrunkTryDoor", "DrunkStumble", "DrunkHiccup", "PointThumb", "Shrug", "DrunkWalkBack", "CaughtReact",
                           "Rummaging", "LookAroundNervous", "LookOverShoulder",
                           "TossAway", "PistolDraw", "PistolLower", "PistolGrabFloor" };   // + the gunman (ActKind 10–13)
        for (int i = 0; i < acts.Length; i++)
        {
            var st = OneShot(sm, entry, crawl, "Act_" + acts[i], "Act", Clip(acts[i]), "ActIndex", i, 0.25f, selfRestart: true);
            for (int g = 0; g < GaitCount; g++)
            {
                if (g == Crawl) continue;
                var stop = st.AddTransition(entry[g]);
                stop.AddCondition(AnimatorConditionMode.IfNot, 0, "Acting");
                stop.AddCondition(AnimatorConditionMode.Equals, g, "Gait");
                stop.hasExitTime = false; stop.duration = 0.3f;
            }
        }

        // Legs shot out: TripFall's lunge (extracted root motion → NavMesh, scaled by IntruderAnimator.Fall's travel), handed to
        // the face-down crawl as his hands reach for the floor — before the clip rolls him onto his side (IntruderClips.FallToCrawl).
        var fall = AnyFall(sm, "Fall_LegsOut", Clip("TripFall"), IntruderAnimator.FallKind.LegsOut, 0.15f);
        var fallOut = fall.AddTransition(crawl); fallOut.hasExitTime = true; fallOut.exitTime = IntruderClips.FallToCrawl; fallOut.duration = IntruderClips.FallBlend;
        // The drunk: trips and face-plants (then lies face down: Prone), or staggers back onto his backside (Sit).
        var trip = AnyFall(sm, "Fall_TripFall", Clip("TripFall"), IntruderAnimator.FallKind.Trip, 0.15f);
        Exits(trip, 0.93f, 0.3f, g => Upright(g) ? proneUp : prone, crawl);
        var back = AnyFall(sm, "Fall_StumbleBackFall", Clip("StumbleBackFall"), IntruderAnimator.FallKind.StumbleBack, 0.15f);
        Exits(back, 0.93f, 0.3f, g => Upright(g) ? sitUp : sit, crawl);

        var deaths = new[] { "DeathFromTheFront", "DeathFromFrontHeadshot", "StandingDeathBackward01", "DeathFromTheBack", "StandingDeathForward01",
                             "KneelDeath", "ProneDeath", "StumbleBackFall" };
        for (int i = 0; i < deaths.Length; i++)
        {
            // Kneeling: KneelDeath from its knees (it starts standing); OnBack: sitting, he slumps flat onto his back (a held
            // lying-flat frame of GetUpFromBack, blended slowly so it reads as a slump).
            var clip = i == (int)IntruderAnimator.DeathKind.Kneeling ? Clip("KneelDeath", "KneelDeathFromKnees")
                     : i == (int)IntruderAnimator.DeathKind.OnBack ? Clip("GetUpFromBack", "OnBackHold") : Clip(deaths[i]);
            var st = sm.AddState("Death_" + i + "_" + clip.name); st.motion = clip;
            float blend = i == (int)IntruderAnimator.DeathKind.OnBack ? 0.8f : i >= (int)IntruderAnimator.DeathKind.Kneeling ? 0.3f : 0.1f;
            var tr = sm.AddAnyStateTransition(st); tr.AddCondition(AnimatorConditionMode.If, 0, "Die"); tr.AddCondition(AnimatorConditionMode.Equals, i, "DeathIndex");
            tr.duration = blend; tr.canTransitionToSelf = false; tr.hasExitTime = false;
            if (i != (int)IntruderAnimator.DeathKind.Prone) continue;
            // Face down: from the crawl (propped on his elbows) the whole ProneDeath slump; lying flat in the Prone gait (the
            // surrender, the drunk's face-plant) only its end, so he doesn't push up onto his elbows first.
            tr.AddCondition(AnimatorConditionMode.NotEqual, Prone, "Gait");
            var flat = sm.AddState("Death_" + i + "_ProneDeathFlat"); flat.motion = Clip("ProneDeath", "ProneDeathFlat");
            var ft = sm.AddAnyStateTransition(flat); ft.AddCondition(AnimatorConditionMode.If, 0, "Die"); ft.AddCondition(AnimatorConditionMode.Equals, i, "DeathIndex");
            ft.AddCondition(AnimatorConditionMode.Equals, Prone, "Gait");
            ft.duration = 0.4f; ft.canTransitionToSelf = false; ft.hasExitTime = false;
        }
        // Hands-up (surrender) is IK over the body clip: the base layer needs the IK pass.
        var layers = ctrl.layers; layers[0].iKPass = true; ctrl.layers = layers;
        AddArmsLayer(ctrl);
        AddGripLayer(ctrl);
        AddPistolLayers(ctrl);
        AssetDatabase.SaveAssets();
        return ctrl;
    }

    /// <summary>
    /// Layer 1: hands on head over the kneel. KneelHandsOnHead is a one-frame female-authored pose, so an arms-only mask keeps
    /// its legs and spine out; IntruderAnimator fades the layer in only while he's settled in the Kneel state.
    /// </summary>
    static void AddArmsLayer(AnimatorController ctrl)
    {
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(ArmsMaskPath);
        if (mask == null) { mask = new AvatarMask(); AssetDatabase.CreateAsset(mask, ArmsMaskPath); }
        for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++)
            mask.SetHumanoidBodyPartActive(part, part is AvatarMaskBodyPart.LeftArm or AvatarMaskBodyPart.RightArm
                                                   or AvatarMaskBodyPart.LeftFingers or AvatarMaskBodyPart.RightFingers);
        EditorUtility.SetDirty(mask);
        ctrl.AddLayer("Arms");
        var layers = ctrl.layers;
        layers[1].avatarMask = mask; layers[1].defaultWeight = 0f; layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
        ctrl.layers = layers;
        var st = ctrl.layers[1].stateMachine.AddState("HandsOnHead"); st.motion = Clip("KneelHandsOnHead");
    }

    /// <summary>
    /// Layer 2: his right fingers closed around the knife handle (the <see cref="GripClip"/> fist) whatever the body plays — the
    /// walk, run and idles open the hand. IntruderAnimator sets it to 1 while the knife is in his hand.
    /// </summary>
    static void AddGripLayer(AnimatorController ctrl)
    {
        const string path = "Assets/_Game/Animation/Intruder_GripMask.mask";
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
        if (mask == null) { mask = new AvatarMask(); AssetDatabase.CreateAsset(mask, path); }
        for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++)
            mask.SetHumanoidBodyPartActive(part, part == AvatarMaskBodyPart.RightFingers);
        EditorUtility.SetDirty(mask);
        ctrl.AddLayer("Grip");
        var layers = ctrl.layers;
        layers[2].avatarMask = mask; layers[2].defaultWeight = 0f; layers[2].blendingMode = AnimatorLayerBlendingMode.Override;
        ctrl.layers = layers;
        var st = ctrl.layers[2].stateMachine.AddState("Grip"); st.motion = Clip(GripClip);
    }

    /// <summary>
    /// Layer 3 "PistolGrip": his right fingers round the pistol grip (PistolIdleReady's fist) whatever the body plays (the sprint,
    /// a flinch, the fall). Layer 4 "GunArms": both arms (+ fingers) hold the pistol over the Gun gait — a 1D tree on Aim,
    /// PistolIdleReady (0, muzzle low) → PistolIdleAimed (1). IntruderAnimator drives both weights and Aim.
    /// </summary>
    static void AddPistolLayers(AnimatorController ctrl)
    {
        var grip = AssetDatabase.LoadAssetAtPath<AvatarMask>("Assets/_Game/Animation/Intruder_GripMask.mask");
        var arms = AssetDatabase.LoadAssetAtPath<AvatarMask>(ArmsMaskPath);
        ctrl.AddLayer("PistolGrip");
        ctrl.AddLayer("GunArms");
        var layers = ctrl.layers;
        layers[3].avatarMask = grip; layers[3].defaultWeight = 0f; layers[3].blendingMode = AnimatorLayerBlendingMode.Override;
        layers[4].avatarMask = arms; layers[4].defaultWeight = 0f; layers[4].blendingMode = AnimatorLayerBlendingMode.Override;
        ctrl.layers = layers;
        var st = ctrl.layers[3].stateMachine.AddState("PistolGrip"); st.motion = Clip("PistolIdleReady");
        ctrl.CreateBlendTreeInController("GunArms", out var tree, 4);
        tree.blendType = BlendTreeType.Simple1D; tree.blendParameter = "Aim"; tree.useAutomaticThresholds = false;
        tree.AddChild(Clip("PistolIdleReady"), 0f);
        tree.AddChild(Clip("PistolIdleAimed"), 1f);
    }

    /// <summary>
    /// PistolHit and PistolLower got import trims (MixamoImportSettings) after they were imported: reimport just those two
    /// rather than bumping the postprocessor version (that reimports all ~80 Mixamo files on the slow cores).
    /// </summary>
    static void ReimportIfUntrimmed()
    {
        // DuckHold (Ricky's duck, research/27 §3) is an extra cut added to MixamoImportSettings after DuckCover was imported.
        // ExitCar / ExitCarMirror (2026-10-05): the car exit's two sides, if ExitingCar.fbx was imported before its rules were in.
        foreach (var (fbx, extra) in new[] { ("DuckCover", "DuckHold"), ("ExitingCar", "ExitCar"), ("ExitingCar", "ExitCarMirror") })
        {
            if (AssetDatabase.LoadAllAssetsAtPath(AnimDir + fbx + ".fbx").OfType<AnimationClip>().Any(c => c.name == extra)) continue;
            AssetDatabase.ImportAsset(AnimDir + fbx + ".fbx", ImportAssetOptions.ForceUpdate);
            bool cut = AssetDatabase.LoadAllAssetsAtPath(AnimDir + fbx + ".fbx").OfType<AnimationClip>().Any(c => c.name == extra);
            Debug.Log($"[HIS] reimported {fbx} for its {extra} cut ({(cut ? "there now" : "STILL MISSING")})");
        }
        foreach (var (fbx, maxLen) in new[] { ("PistolHit", 1.7f), ("PistolLower", 1f) })
        {
            var clip = AssetDatabase.LoadAllAssetsAtPath(AnimDir + fbx + ".fbx").OfType<AnimationClip>().FirstOrDefault(c => c.name == fbx);
            if (clip == null || clip.length <= maxLen) continue;
            AssetDatabase.ImportAsset(AnimDir + fbx + ".fbx", ImportAssetOptions.ForceUpdate);
            Debug.Log($"[HIS] reimported {fbx} for its trim (now {clip.length:F2} s)");
        }
    }

    static AnimatorState LocomotionTree(AnimatorController ctrl, string name, string idleClip) =>
        Tree(ctrl, name, (idleClip, 0f, 1f), ("Walking", IntruderClips.WalkSpeed, IntruderClips.WalkSpeed / IntruderClips.WalkNatural),
             ("Running", IntruderClips.RunSpeed, IntruderClips.RunSpeed / IntruderClips.RunNatural));

    /// <summary>A Speed blend tree: (clip, threshold = gameplay speed, time scale = gameplay / natural speed).</summary>
    static AnimatorState Tree(AnimatorController ctrl, string name, params (string clip, float speed, float timeScale)[] children)
    {
        var st = ctrl.CreateBlendTreeInController(name, out var tree);
        tree.blendType = BlendTreeType.Simple1D; tree.blendParameter = "Speed"; tree.useAutomaticThresholds = false;
        foreach (var c in children) tree.AddChild(Clip(c.clip), c.speed);
        var list = tree.children;
        for (int i = 0; i < list.Length; i++) list[i].timeScale = children[i].timeScale;
        tree.children = list;
        return st;
    }

    static AnimatorState AnyFall(AnimatorStateMachine sm, string name, AnimationClip clip, IntruderAnimator.FallKind kind, float blendIn)
    {
        var st = sm.AddState(name); st.motion = clip;
        var tr = sm.AddAnyStateTransition(st);
        tr.AddCondition(AnimatorConditionMode.If, 0, "Fall"); tr.AddCondition(AnimatorConditionMode.Equals, (int)kind, "FallIndex");
        tr.duration = blendIn; tr.hasExitTime = false; tr.canTransitionToSelf = false;
        return st;
    }

    /// <summary>Exit-time transitions to wherever the gait now points (null = none); legs shot out → the crawl at once.</summary>
    static void Exits(AnimatorState st, float exitAt, float blend, System.Func<int, AnimatorState> target, AnimatorState crawl)
    {
        var drop = st.AddTransition(crawl);
        drop.AddCondition(AnimatorConditionMode.Equals, Crawl, "Gait");
        drop.hasExitTime = false; drop.duration = 0.45f;
        for (int g = 0; g < GaitCount; g++)
        {
            if (g == Crawl) continue;
            var dest = target(g);
            if (dest == null || dest == st) continue;
            var tr = st.AddTransition(dest);
            tr.AddCondition(AnimatorConditionMode.Equals, g, "Gait");
            tr.hasExitTime = true; tr.exitTime = exitAt; tr.duration = blend;
        }
    }

    static AnimatorState OneShot(AnimatorStateMachine sm, AnimatorState[] entry, AnimatorState crawl, string name, string trigger, AnimationClip clip,
                                 string indexParam, int index, float blendIn = 0.12f, bool selfRestart = false)
    {
        var st = sm.AddState(name); st.motion = clip;
        var tr = sm.AddAnyStateTransition(st);
        tr.AddCondition(AnimatorConditionMode.If, 0, trigger);
        if (indexParam != null) tr.AddCondition(AnimatorConditionMode.Equals, index, indexParam);
        tr.duration = blendIn; tr.hasExitTime = false; tr.canTransitionToSelf = selfRestart;
        // Legs shot out mid-clip (drop in place): leave for the crawl right away instead of finishing the clip standing.
        Exits(st, IntruderClips.OneShotExit, 0.15f, g => entry[g], crawl);
        return st;
    }

    /// <summary>The take in Animations/&lt;fbx&gt;.fbx, or one of its extra cuts (MixamoImportSettings.Extras) by name.</summary>
    static AnimationClip Clip(string fbx, string clipName = null)
    {
        clipName ??= fbx;
        var clip = AssetDatabase.LoadAllAssetsAtPath(AnimDir + fbx + ".fbx").OfType<AnimationClip>().FirstOrDefault(c => c.name == clipName);
        if (clip == null) Debug.LogError($"[HIS] clip missing: {fbx}/{clipName}");
        return clip;
    }

    /// <summary>Capsules along the humanoid bones. Head gets a sphere. Multipliers match the placeholder.</summary>
    static void AddHitboxes(Animator animator)
    {
        void Capsule(HumanBodyBones bone, HumanBodyBones toward, float radius, BodyPart part, float mult, string name)
        {
            var b = animator.GetBoneTransform(bone); var t = animator.GetBoneTransform(toward);
            if (b == null || t == null) { Debug.LogWarning($"[HIS] bone missing for {name}"); return; }
            var col = b.gameObject.AddComponent<CapsuleCollider>();
            var local = b.InverseTransformPoint(t.position);
            int axis = Mathf.Abs(local.x) >= Mathf.Abs(local.y) && Mathf.Abs(local.x) >= Mathf.Abs(local.z) ? 0 : Mathf.Abs(local.y) >= Mathf.Abs(local.z) ? 1 : 2;
            float len = local.magnitude;
            col.direction = axis; col.radius = radius; col.height = len + radius * 2f; col.center = local * 0.5f;
            var hb = b.gameObject.AddComponent<Hitbox>(); hb.Part = part; hb.Multiplier = mult;
            AddKinematicBody(b.gameObject);
        }
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (head != null)
        {
            var s = head.gameObject.AddComponent<SphereCollider>(); s.radius = 0.12f; s.center = new Vector3(0, 0.08f, 0.02f);
            var hb = head.gameObject.AddComponent<Hitbox>(); hb.Part = BodyPart.Head; hb.Multiplier = 3f;
            AddKinematicBody(head.gameObject);
        }
        Capsule(HumanBodyBones.Neck, HumanBodyBones.Head, 0.06f, BodyPart.Neck, 2f, "neck");
        Capsule(HumanBodyBones.Chest, HumanBodyBones.Neck, 0.17f, BodyPart.Torso, 1f, "torso");
        Capsule(HumanBodyBones.Hips, HumanBodyBones.Chest, 0.15f, BodyPart.Abdomen, 0.9f, "abdomen");
        Capsule(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, 0.055f, BodyPart.LeftArm, 0.6f, "l-upper-arm");
        Capsule(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, 0.045f, BodyPart.LeftArm, 0.5f, "l-fore-arm");
        Capsule(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 0.055f, BodyPart.RightArm, 0.6f, "r-upper-arm");
        Capsule(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, 0.045f, BodyPart.RightArm, 0.5f, "r-fore-arm");
        Capsule(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, 0.085f, BodyPart.LeftLeg, 0.7f, "l-thigh");
        Capsule(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, 0.065f, BodyPart.LeftLeg, 0.6f, "l-shin");
        Capsule(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, 0.085f, BodyPart.RightLeg, 0.7f, "r-thigh");
        Capsule(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, 0.065f, BodyPart.RightLeg, 0.6f, "r-shin");
    }

    /// <summary>
    /// A Ka-Bar in his right fist (hidden unless the scenario arms him). Hammer grip worked out from the T-pose bones:
    /// the handle sits across the palm, the blade leaves the fist on the thumb/index side.
    /// </summary>
    /// <summary>The fist every knife-holding clip uses (the Grip layer holds its right fingers whatever the body plays).</summary>
    const string GripClip = "KnifeIdle";

    /// <summary>
    /// The Ka-Bar in his right fist, placed in the Grip pose (KnifeIdle's closed fingers, sampled onto the rig and then undone):
    /// the handle runs through the tunnel of the curled fingers (the centres of the index…little curls), blade out of the
    /// thumb/index side, the guard just clear of the index finger, the edge toward his knuckles (a forward hammer grip).
    /// The bind pose's open hand can't say where a closed fist's inside is — placed from it, the knife lay on top of the fingers.
    /// </summary>
    static void AddKnife(Animator animator)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Models/Model_KaBar.prefab");
        var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        var fingers = new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal };
        if (model == null || hand == null || fingers.Any(f => animator.GetBoneTransform(f) == null)) { Debug.LogWarning("[HIS] knife prop skipped (model or finger bones missing)"); return; }
        var knife = (GameObject)PrefabUtility.InstantiatePrefab(model);
        PrefabUtility.UnpackPrefabInstance(knife, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        knife.name = "IntruderKnife";

        var bones = animator.GetComponentsInChildren<Transform>(true);
        var saved = bones.Select(b => (b.localPosition, b.localRotation, b.localScale)).ToArray();
        var graph = PlayableGraph.Create("KnifeGrip");
        try
        {
            var output = AnimationPlayableOutput.Create(graph, "out", animator);
            output.SetSourcePlayable(AnimationClipPlayable.Create(graph, Clip(GripClip)));
            graph.Evaluate(0f);
            // Each curled finger's joints (knuckle, middle, end joint, tip) ring the handle: their mean is the tunnel through it.
            var centre = new Vector3[fingers.Length];
            for (int i = 0; i < fingers.Length; i++)
            {
                var p = animator.GetBoneTransform(fingers[i]);
                var m = p.GetChild(0); var d = m.GetChild(0);
                var tip = d.childCount > 0 ? d.GetChild(0).position : d.position + (d.position - m.position);
                centre[i] = (p.position + m.position + d.position + tip) * 0.25f;
            }
            var mid = (centre[0] + centre[1] + centre[2] + centre[3]) * 0.25f;
            var bladeDir = (centre[0] - centre[3]).normalized;                           // little → index: out of the thumb side
            var knuckles = animator.GetBoneTransform(HumanBodyBones.RightMiddleIntermediate).position;
            var edge = Vector3.ProjectOnPlane(knuckles - mid, bladeDir).normalized;      // toward the front of the fist
            // Model_KaBar is centred on its bounds with the blade along +Z and the spine up (+Y); the guard is 3 cm in front of
            // the centre. Guard 1.5 cm past the index finger's middle.
            knife.transform.rotation = Quaternion.LookRotation(bladeDir, -edge);
            knife.transform.position = mid + bladeDir * (Vector3.Dot(centre[0] - mid, bladeDir) + 0.015f + 0.03f);
            knife.transform.SetParent(hand, true);
        }
        finally
        {
            graph.Destroy();
            for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = saved[i].localPosition; bones[i].localRotation = saved[i].localRotation; bones[i].localScale = saved[i].localScale; }
        }
        foreach (var c in knife.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        // Depth-occluded like his body (a knife must disappear behind the real couch with the hand holding it).
        var occl = Shader.Find("HIS/OcclusionLitPBR");
        foreach (var r in knife.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            if (occl == null || r.sharedMaterial == null) continue;
            var src = r.sharedMaterial;
            const string path = "Assets/_Game/Materials/Intruder_Knife.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) { mat = new Material(occl); AssetDatabase.CreateAsset(mat, path); }
            mat.shader = occl;
            foreach (var (from, to) in new[] { ("_BaseMap", "_MainTex"), ("_MainTex", "_MainTex"), ("_BumpMap", "_BumpMap") })
                if (src.HasProperty(from) && mat.HasProperty(to) && src.GetTexture(from) != null) mat.SetTexture(to, src.GetTexture(from));
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0.9f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.7f);
            if (mat.HasProperty("_EnvironmentDepthBias")) mat.SetFloat("_EnvironmentDepthBias", FloorSafeDepthBias);
            EditorUtility.SetDirty(mat);
            r.sharedMaterial = mat;
        }
        knife.AddComponent<IntruderWeapon>();
        knife.SetActive(false);
    }

    /// <summary>The fist the pistol is placed in: Mixamo's aimed two-hand hold (both hands on the grip, eye-line aim).</summary>
    const string PistolPoseClip = "PistolIdleAimed";

    /// <summary>
    /// The Glock (Model_G19: +Z out of the bore, +Y up, GripPoint = the tang, Muzzle at the slide's face) in his right fist,
    /// placed in the <see cref="PistolPoseClip"/> pose sampled onto the rig and then undone (as the knife): the grip runs
    /// through the tunnel of his curled middle/ring/little fingers, the barrel on the clip's own aim line (straight ahead at
    /// his eye height — Mixamo aims at the actor's eye line), the slide up toward his index knuckle (index above little
    /// finger: an upright grip, the hand's own cant kept). Occluding material like his body; IntruderWeapon (Gun, Muzzle) and
    /// IntruderGunFx (flash, report) on it; hidden unless the scenario arms him.
    /// </summary>
    static void AddPistol(Animator animator)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Models/Model_G19.prefab");
        var hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        var fingers = new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightRingProximal, HumanBodyBones.RightLittleProximal };
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (model == null || hand == null || head == null || fingers.Any(f => animator.GetBoneTransform(f) == null)) { Debug.LogWarning("[HIS] pistol prop skipped (model or bones missing)"); return; }
        var gun = (GameObject)PrefabUtility.InstantiatePrefab(model);
        PrefabUtility.UnpackPrefabInstance(gun, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        gun.name = "IntruderPistol";
        var gripPoint = gun.transform.Find("GripPoint");
        var muzzle = gun.transform.Find("Muzzle");
        if (gripPoint == null || muzzle == null) { Debug.LogWarning("[HIS] pistol prop skipped (GripPoint/Muzzle missing)"); Object.DestroyImmediate(gun); return; }

        var bones = animator.GetComponentsInChildren<Transform>(true);
        var saved = bones.Select(b => (b.localPosition, b.localRotation, b.localScale)).ToArray();
        var graph = PlayableGraph.Create("PistolGrip");
        try
        {
            var output = AnimationPlayableOutput.Create(graph, "out", animator);
            output.SetSourcePlayable(AnimationClipPlayable.Create(graph, Clip(PistolPoseClip)));
            graph.Evaluate(0f);
            var centre = new Vector3[fingers.Length];
            for (int i = 0; i < fingers.Length; i++)
            {
                var p = animator.GetBoneTransform(fingers[i]);
                var m = p.GetChild(0); var d = m.GetChild(0);
                var tip = d.childCount > 0 ? d.GetChild(0).position : d.position + (d.position - m.position);
                centre[i] = (p.position + m.position + d.position + tip) * 0.25f;
            }
            var mid = (centre[1] + centre[2] + centre[3]) / 3f;                       // the grip, mid-height (index is on the trigger)
            var root = animator.transform;
            var eyeLine = root.position + root.forward * 10f + Vector3.up * (head.position.y - root.position.y);
            var barrel = (eyeLine - mid).normalized;
            var up = Vector3.ProjectOnPlane(animator.GetBoneTransform(fingers[0]).position - animator.GetBoneTransform(fingers[3]).position, barrel).normalized;
            var rot = Quaternion.LookRotation(barrel, up);
            // The tang sits ~4.5 cm above the grip's middle (the fist's centre), the raked backstrap putting it ~0.4 cm back.
            var tang = mid + up * GripTangAbove - barrel * 0.004f;
            gun.transform.rotation = rot;
            gun.transform.position = tang - rot * Vector3.Scale(gun.transform.lossyScale, gripPoint.localPosition);
            var web = (animator.GetBoneTransform(HumanBodyBones.RightThumbProximal).position + animator.GetBoneTransform(fingers[0]).position) * 0.5f;
            Debug.Log($"[HIS] Intruder pistol: tang {Vector3.Distance(tang, web) * 100f:F1} cm from the thumb/index web; barrel pitch {Vector3.SignedAngle(Vector3.ProjectOnPlane(barrel, Vector3.up), barrel, -root.right):F1}°");
            gun.transform.SetParent(hand, true);
        }
        finally
        {
            graph.Destroy();
            for (int i = 0; i < bones.Length; i++) { bones[i].localPosition = saved[i].localPosition; bones[i].localRotation = saved[i].localRotation; bones[i].localScale = saved[i].localScale; }
        }
        foreach (var c in gun.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);
        var occl = Shader.Find("HIS/OcclusionLitPBR");
        const string matPath = "Assets/_Game/Materials/Intruder_Pistol.mat";
        Material mat = null;
        foreach (var r in gun.GetComponentsInChildren<Renderer>(true))
        {
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            if (occl == null || r.sharedMaterial == null) continue;
            if (mat == null)
            {
                mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                if (mat == null) { mat = new Material(occl); AssetDatabase.CreateAsset(mat, matPath); }
                mat.shader = occl;
                mat.CopyMatchingPropertiesFromMaterial(r.sharedMaterial);
                if (mat.HasProperty("_EnvironmentDepthBias")) mat.SetFloat("_EnvironmentDepthBias", FloorSafeDepthBias);
                EditorUtility.SetDirty(mat);
            }
            r.sharedMaterial = mat;
        }
        var w = gun.AddComponent<IntruderWeapon>();
        w.Setup(HIS.Scenario.WeaponKind.Gun, muzzle, new Vector3(0.035f, 0.13f, 0.19f));
        gun.AddComponent<IntruderGunFx>();
        gun.SetActive(false);
    }

    /// <summary>Tang height above the fist's centre (m): Model_G19's backstrap is ~9 cm of grip under the slide.</summary>
    const float GripTangAbove = 0.045f;

    // Colliders on bones the Animator moves every frame belong on kinematic bodies (PhysX treats body-less colliders as
    // static and rebuilds them when moved). Also the first half of a ragdoll.
    static void AddKinematicBody(GameObject bone)
    {
        var rb = bone.AddComponent<Rigidbody>();
        rb.isKinematic = true; rb.useGravity = false;
        rb.interpolation = RigidbodyInterpolation.None;
        rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
    }

    static Bounds RendererBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); return b;
    }

    static void SetPrivate(Object target, string field, Object value)
    {
        var so = new SerializedObject(target); so.FindProperty(field).objectReferenceValue = value; so.ApplyModifiedPropertiesWithoutUndo();
    }

    [MenuItem("HIS/Inspect Weapon Models")]
    public static void InspectModels()
    {
        foreach (var path in new[] { "Assets/ThirdParty/Sketchfab/G19_Rotuma/G19_Display.fbx", "Assets/ThirdParty/Sketchfab/Remington870_tris09/Remington870.fbx", "Assets/ThirdParty/Sketchfab/KaBar_vitvitskyi/Ka_Bar_pack.obj" })
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { Debug.LogWarning("[HIS] not imported yet: " + path); continue; }
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(go);
            var b = RendererBounds(inst);
            var sb = new System.Text.StringBuilder($"[HIS] {System.IO.Path.GetFileName(path)} bounds size={b.size} center={b.center}\n");
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
                sb.AppendLine($"   {Path(r.transform, inst.transform)} size={r.bounds.size} center={r.bounds.center} mats={string.Join(",", r.sharedMaterials.Select(m => m ? m.name : "null"))}");
            Debug.Log(sb.ToString());
            Object.DestroyImmediate(inst);
        }
    }

    static string Path(Transform t, Transform root)
    {
        var s = t.name; while (t.parent != null && t.parent != root) { t = t.parent; s = t.name + "/" + s; } return s;
    }
}
