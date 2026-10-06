using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Import rules for everything under Assets/ThirdParty/Mixamo:
/// - the character FBX becomes a Humanoid with its own avatar and extracted textures,
/// - every clip FBX becomes Humanoid using the character's avatar, looped when it's a cycle, root motion baked out (NavMesh drives him).
/// - the car exit (ExitingCar.fbx) becomes its two sides, ExitCar and the mirrored ExitCarMirror, travel and turn extracted (Sided).
/// </summary>
public class MixamoImportSettings : AssetPostprocessor
{
    const string Root = "Assets/ThirdParty/Mixamo/";
    const string CharacterFbx = Root + "Alex/Alex.fbx";
    static readonly string[] Loops = { "Idle", "FightIdle", "KnifeIdle", "Walking", "Running", "InjuredWalking", "ZombieCrawl",
        // 2026-09-23 pull (scenario behaviours; pistol/family/teen sets are for scenarios not built yet)
        "DrunkIdle", "DrunkWalk", "DrunkWalkBack", "DrunkRun", "DizzyIdle", "DrunkTryDoor", "SneakForward", "CrouchedWalk", "Rummaging",
        "LookAroundNervous", "RunLookBack", "HandsUpIdle", "KneelIdle", "KneelHandsOnHead", "PistolIdleReady", "PistolIdleAimed",
        "PistolWalkAimed", "PistolRun", "PistolFire", "WalkNormal", "BackAwayFear", "LowCrouchIdle",
        "Driving", "SittingIdle" };   // riders in the police cars (Brett 0.1.48: "theyre like kneeling instead of sitting")
    // Clips whose forward travel is extracted as root motion (IntruderAnimator.OnAnimatorMove pushes it through the NavMesh)
    // instead of baked into the pose — baked, the body would snap back ~1.3 m when the state exits.
    // Getting down / up travels too (hips move 0.3–0.7 m: FallToKnees lurches onto the knees, the get-ups push off the floor).
    static readonly string[] RootMotionXZ = { "HitOnLegs", "TripFall", "StumbleBackFall", "CrouchToProne", "KnockedDownStomach",
                                              "FallToKnees", "GetUpFromBack", "GetUpFromStomach" };

    // Trims (source frames @30 fps), read off Logs/clip-trace.txt (HIS > Debug > Trace Intruder Clips):
    static readonly (string name, float first, float last)[] Trims =
    {
        ("FallToKnees", 15, 48),       // 0.5 s standing still cut; on his knees by 1.3 s; the hands start to join (prayer) at 1.6 s → cut there
        ("GetUpFromStomach", 36, 162), // lies still until 1.2 s; up by 5.4 s, then the fists come up to a guard → cut there
        ("GetUpFromBack", 105, 162),   // lies flat for 2.9 s and sits up by 3.5 s (= StumbleBackFall's end pose); up by 5.4 s, then guard
        ("KneelToStand", 0, 66),       // standing by 1.5 s, settled 2.2 s
        ("CaughtReact", 0, 33),        // the startle is at 0.5 s; the rest is 2.5 s of standing there
        ("TripFall", 18, 63),          // 0.6 s of brisk (1.5 m/s) walking before the stumble: cut, so it starts from his stagger and needs 2.3 m, not 3
        ("PutDown", 72, 252),          // 10.3 s: stands 2.4 s before bending, back up by 8.4 s (future scenario)
        ("Terrified", 0, 240),         // 20 s: cowers from 1.2 s; keep 8 s (future scenario)
        ("PistolHit", 15, 60),         // 2.7 s: aimed and still until 0.5 s, the flinch, back on aim by 2.0 s (Logs/poses/PistolHit.png)
        ("PistolLower", 88, 108),      // 7.1 s: aims until 2.9 s, the gun down to his hip by 3.6 s, then holsters it → keep the lowering
    };

    // Extra clips cut from the same take (a state can't start mid-clip on a non-looping motion — cycle offset only works on
    // loops): the held poses and the kneeling half of KneelDeath. (source, new clip, first, last frame @30 fps)
    static readonly (string source, string clip, float first, float last)[] Extras =
    {
        ("GetUpFromStomach", "ProneHold", 12, 13),        // lying flat on his front, arms at his sides (the take's still first 1.2 s;
                                                          // CrouchToProne ends propped on his elbows like a sniper). The Arms layer
                                                          // puts his hands behind his head over it (IntruderAnimator)
        ("ProneDeath", "ProneDeathFlat", 36, 56),         // ProneDeath's slump flat from 1.2 s: dying where he already lies flat
        ("StumbleBackFall", "SitHold", 99, 100),          // on his backside, propped up (the end of the stagger)
        ("GetUpFromBack", "OnBackHold", 30, 31),          // flat on his back, arms and legs on the floor (the take's first 1.8 s):
                                                          // a man shot sitting slumps into it (the old StumbleBackFall 1.8 s frame had his legs in the air)
        ("KneelDeath", "KneelDeathFromKnees", 57, 111),   // KneelDeath starts standing; on his knees at 1.9 s → from there
        ("DuckCover", "DuckHold", 45, 46),                // Ricky ducked, arms over his head (research/27 §3): DuckCover's end
                                                          // held (frames 0–46; check Logs/poses/DuckHold.png after a reimport)
    };

    // One-frame holds placed by their centre of mass instead of the take's origin. DuckCover's hips end 0.37 m behind where
    // the take started (Logs/clip-metrics.txt): held "as authored", Ricky would slide 0.37 m back out of his crouch as he
    // ducks — into the wall behind his hiding spot. Centred, he ducks where he is.
    static readonly string[] CentredHolds = { "DuckHold" };

    // Getting out of a car (Brett 2026-10-05: "have the flow of cops exiting look better"): Mixamo "Exiting Car" (Ch18, out of
    // the driver's side, 5.8 s, root motion) becomes two clips — ExitCar out to his left and ExitCarMirror (mirrored) out to his
    // right, the passenger's side — in place of the take's own. Their travel AND turn are extracted (IntruderCarExit carries him
    // along them out of the door: no NavMesh out on the street; the transform turns with him, so the hand-off to his gun gait
    // doesn't swing him back ~75°), from his centre of mass (standing at the hand-off his hips are over it: no slide there; the
    // seated start's offset is IntruderClips.ExitRootAhead); the height baked as authored (the brain sets the root's y).
    // (A new file: imported with these rules the first time — no version bump, no reimport of the other ~80.)
    static readonly (string source, string clip, bool mirror)[] Sided = { ("ExitingCar", "ExitCar", false), ("ExitingCar", "ExitCarMirror", true) };

    // Bump when the rules change so Unity reimports every Mixamo asset.
    public override uint GetVersion() => 7;

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(Root)) return;
        var imp = (ModelImporter)assetImporter;
        imp.animationType = ModelImporterAnimationType.Human;
        imp.importBlendShapes = false;
        imp.importCameras = false; imp.importLights = false;
        imp.isReadable = false;
        imp.meshCompression = ModelImporterMeshCompression.Medium;

        bool isCharacter = assetPath == CharacterFbx;
        if (isCharacter)
        {
            imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            imp.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            imp.materialLocation = ModelImporterMaterialLocation.External;
            imp.importAnimation = false;
            imp.optimizeGameObjects = false; // hitboxes need the bone transforms
        }
        else
        {
            imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(CharacterFbx);
            if (avatar != null) imp.sourceAvatar = avatar;
            imp.materialImportMode = ModelImporterMaterialImportMode.None;
            imp.importAnimation = true;
            imp.animationCompression = ModelImporterAnimationCompression.Optimal;
        }
    }

    void OnPreprocessAnimation()
    {
        if (!assetPath.StartsWith(Root) || assetPath == CharacterFbx) return;
        var imp = (ModelImporter)assetImporter;
        string name = Path.GetFileNameWithoutExtension(assetPath);
        bool loop = System.Array.IndexOf(Loops, name) >= 0;
        bool travel = System.Array.IndexOf(RootMotionXZ, name) >= 0;

        var clips = imp.defaultClipAnimations;
        for (int i = 0; i < clips.Length; i++)
        {
            clips[i].name = name;
            clips[i].loopTime = loop;
            clips[i].loopPose = loop;
            clips[i].lockRootRotation = true;      // bake root rotation
            clips[i].lockRootHeightY = true;       // bake root Y
            clips[i].lockRootPositionXZ = !travel;  // bake XZ: agent moves him; in-place clips anyway
            clips[i].keepOriginalOrientation = true;
            clips[i].keepOriginalPositionY = true;
            clips[i].keepOriginalPositionXZ = !travel; // travelling clips: root follows the centre of mass
            foreach (var (n, first, last) in Trims)
                if (n == name) { clips[i].firstFrame = first; clips[i].lastFrame = Mathf.Min(last, clips[i].lastFrame); }
        }
        var all = new System.Collections.Generic.List<ModelImporterClipAnimation>(clips);
        foreach (var (source, clip, first, last) in Extras)
        {
            if (source != name || clips.Length == 0) continue;
            var extra = imp.defaultClipAnimations[0];   // a fresh copy of the take
            extra.name = clip; extra.firstFrame = first; extra.lastFrame = last;
            extra.loopTime = extra.loopPose = last - first <= 1f; // one-frame holds loop in place
            extra.lockRootRotation = clips[0].lockRootRotation; extra.lockRootHeightY = clips[0].lockRootHeightY;
            // A one-frame hold has no travel: extracting its root motion made the looped frame's tiny COM step a velocity, so the
            // body slid while blending in (0.2–0.4 m) and kept creeping (OnBackHold: 0.3 m/s). Baked, still about the COM.
            extra.lockRootPositionXZ = extra.loopTime || clips[0].lockRootPositionXZ; extra.keepOriginalOrientation = clips[0].keepOriginalOrientation;
            extra.keepOriginalPositionY = clips[0].keepOriginalPositionY; extra.keepOriginalPositionXZ = clips[0].keepOriginalPositionXZ;
            if (System.Array.IndexOf(CentredHolds, clip) >= 0) extra.keepOriginalPositionXZ = false; // (root XZ on his centre of mass)
            all.Add(extra);
        }
        // The car exit's two sides replace the take's own clip (see Sided).
        bool sided = false;
        foreach (var (source, clip, mirror) in Sided)
        {
            if (source != name || clips.Length == 0) continue;
            if (!sided) { all.Clear(); sided = true; }
            var side = imp.defaultClipAnimations[0];    // a fresh copy of the take
            side.name = clip; side.mirror = mirror;
            side.loopTime = side.loopPose = false;
            side.lockRootRotation = false; side.keepOriginalOrientation = true;     // his turn out of the seat: extracted
            side.lockRootPositionXZ = false; side.keepOriginalPositionXZ = false;   // his travel out of the door: extracted, from his centre of mass
            side.lockRootHeightY = true; side.keepOriginalPositionY = true;         // the height as authored
            all.Add(side);
        }
        imp.clipAnimations = all.ToArray();
    }

    [MenuItem("HIS/Reimport Mixamo Assets")]
    public static void ReimportAll()
    {
        // Character first so clips can copy its avatar.
        AssetDatabase.ImportAsset(CharacterFbx, ImportAssetOptions.ForceUpdate);
        var imp = AssetImporter.GetAtPath(CharacterFbx) as ModelImporter;
        if (imp != null)
        {
            var texDir = Root + "Alex/Textures"; Directory.CreateDirectory(texDir);
            imp.ExtractTextures(texDir);
            AssetDatabase.ImportAsset(CharacterFbx, ImportAssetOptions.ForceUpdate);
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Root + "Animations" }))
            AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid), ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets();
        Debug.Log("[HIS] Mixamo assets reimported");
    }
}
