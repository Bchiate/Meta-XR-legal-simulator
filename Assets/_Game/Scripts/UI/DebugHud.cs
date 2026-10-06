using HIS.Core;
using HIS.Intruder;
using HIS.Room;
using HIS.Weapons;
using Meta.XR.EnvironmentDepth;
using TMPro;
using UnityEngine;

namespace HIS.UI
{
    /// <summary>
    /// Small wrist-worn status readout for playtests (build, fps, room, intruder, weapons) on the left controller.
    /// Development builds and the Editor only — release builds switch it off at startup.
    /// </summary>
    public class DebugHud : MonoBehaviour
    {
        [SerializeField] TMP_Text text;
        [SerializeField] IntruderSpawner spawner;
        [SerializeField] EnvironmentDepthManager depth;
        [SerializeField] HandController leftHand, rightHand;
        float _fpsAccum; int _fpsFrames; float _fps;
        float _nextRefresh;

        void Awake()
        {
            if (!Debug.isDebugBuild) { gameObject.SetActive(false); return; }
        }

        void Update()
        {
            _fpsAccum += Time.unscaledDeltaTime; _fpsFrames++;
            if (_fpsAccum >= 0.5f) { _fps = _fpsFrames / _fpsAccum; _fpsAccum = 0; _fpsFrames = 0; }
            if (text == null || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.2f; // 5 Hz is plenty and keeps string churn down

            var rs = RoomScanner.Instance;
            string room = rs == null ? "no scanner" :
                          !rs.IsReady ? "loading room..." :
                          $"room {rs.Room.Anchors.Count} anchors, {rs.Doors.Count} door ({rs.DoorDistance:F1} m)";
            var ps = PlayerState.Instance;
            string health = ps == null ? "" : ps.IsDead ? "<color=#ff3030><b>YOU DIED — restarting</b></color>" :
                            $"HP {ps.Health:F0}/{ps.MaxHealth:F0}{(ps.LimbWounds > 0 ? $" limb wound {ps.LimbWounds}/{PlayerState.LimbWoundsToDie}" : "")}";
            if (ps != null && !ps.IsDead && Time.time - ps.LastHitTime < 0.4f) health = $"<color=#ff3030>{health}  HIT!</color>";
            string intruder = spawner == null ? "" :
                              spawner.Active != null ? $"intruder: {spawner.Active.Summary}  bodies {CorpseRegistry.Count}" : spawner.Status;
            string occl = depth == null ? "depth: none" : $"depth: {(EnvironmentDepthManager.IsSupported ? (depth.IsDepthAvailable ? "on" : "starting") : "unsupported")}";
            string weapons = $"L: {(leftHand != null && leftHand.Held != null ? leftHand.Held.GetComponent<WeaponBase>()?.Status : "empty")}   " +
                             $"R: {(rightHand != null && rightHand.Held != null ? rightHand.Held.GetComponent<WeaponBase>()?.Status : "empty")}";
            string paused = GamePause.Instance != null && GamePause.Instance.IsPaused ? "  <color=#ffd040>PAUSED</color>" : "";
            var dir = HIS.Scenario.ScenarioDirector.Instance;
            string scenario = dir == null ? "" : dir.CurrentMode == HIS.Scenario.ScenarioDirector.Mode.Sandbox ? "SANDBOX (hold both sticks in: scenarios)" :
                              $"{dir.Status}{(dir.Intruder != null ? $" | {dir.Intruder.Summary}" : "")}";
            text.text = $"{BuildInfo.Label} | heat {ThermalGovernor.HudLine}\n{_fps:F0} fps | {occl} | {health}{paused}\n{room}\n{intruder}\n{scenario}\n{weapons}";
        }
    }
}
