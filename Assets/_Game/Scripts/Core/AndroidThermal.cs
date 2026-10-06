using System;
using System.Threading;
using UnityEngine;

namespace HIS.Core
{
    /// <summary>Where <see cref="ThermalGovernor"/> gets heat readings. Tests swap in a fake.</summary>
    public interface IThermalSource
    {
        /// <summary>Fill the fields this source knows (leave the rest). Called every few seconds on the main thread.</summary>
        void Read(ref ThermalSample s, float now);
        string Describe { get; }
    }

    /// <summary>
    /// Android (Quest) heat readings over JNI: PowerManager.getCurrentThermalStatus (API 29), getThermalHeadroom (API 30;
    /// NaN if asked more than once per 10 s, so it is rate-limited here) and the sticky ACTION_BATTERY_CHANGED intent
    /// (temperature in tenths of °C, plugged state). Any call that throws is switched off and logged once. Editor: reports nothing (all unknown).
    /// getThermalHeadroom runs on its own background thread: on Quest it is a blocking binder call into the thermal service
    /// that reads every sensor (~40–90 ms; 0.1.50 logged a ~50 ms frame every 15 s, 3–6 dropped frames each time, from this
    /// one call on the main thread). <see cref="Read"/> takes its last value.
    /// </summary>
    public sealed class AndroidThermalSource : IThermalSource, IDisposable
    {
        bool _statusOk = true, _headroomOk = true, _batteryOk = true;
        volatile float _headroom = float.NaN;   // written by the headroom thread
        volatile string _headroomError;         // set by the headroom thread when the call throws (logged on the main thread)
        volatile bool _stop;
        Thread _headroomThread;
        IntPtr _headroomMethod;          // PowerManager.getThermalHeadroom(int), looked up on the main thread
        /// <summary>The app is paused (headset off): the headroom thread skips its call (ThermalGovernor sets this).</summary>
        public static volatile bool AppPaused;
        AndroidJavaObject _activity, _power, _filter;
        IntPtr _registerReceiver;

        public string Describe => $"android(status {(_statusOk ? "ok" : "off")}, headroom {(_headroomOk ? "ok" : "off")}, battery {(_batteryOk ? "ok" : "off")})";

        public AndroidThermalSource()
        {
            // Plain runtime check (not #if) so the Editor compiles this path too; JNI only exists on the device.
            if (Application.platform != RuntimePlatform.Android) { _statusOk = _headroomOk = _batteryOk = false; return; }
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                _activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                _power = _activity.Call<AndroidJavaObject>("getSystemService", "power");
                _filter = new AndroidJavaObject("android.content.IntentFilter", "android.intent.action.BATTERY_CHANGED");
                // registerReceiver(null, filter) returns the sticky battery intent. Bound by explicit signature: a C# null
                // argument can't pick the overload through AndroidJavaObject.Call.
                var cls = AndroidJNI.GetObjectClass(_activity.GetRawObject());
                _registerReceiver = AndroidJNI.GetMethodID(cls, "registerReceiver",
                    "(Landroid/content/BroadcastReceiver;Landroid/content/IntentFilter;)Landroid/content/Intent;");
                AndroidJNI.DeleteLocalRef(cls);
                if (_registerReceiver == IntPtr.Zero) Off(ref _batteryOk, "registerReceiver not found");
            }
            catch (Exception e) { _statusOk = _headroomOk = _batteryOk = false; Debug.LogWarning($"[HIS] Thermal: Android source unavailable: {e.Message}"); }
            if (_power == null) { _statusOk = _headroomOk = false; }
            if (_headroomOk)
            {
                // Everything the thread needs is found here, on the main thread: a natively attached thread resolves classes
                // through the system class loader and can't see the app's (UnityPlayer), so it only makes the call itself.
                try
                {
                    var cls = AndroidJNI.GetObjectClass(_power.GetRawObject());
                    _headroomMethod = AndroidJNI.GetMethodID(cls, "getThermalHeadroom", "(I)F");
                    AndroidJNI.DeleteLocalRef(cls);
                }
                catch (Exception e) { Off(ref _headroomOk, "getThermalHeadroom lookup: " + e.Message); }
                if (_headroomMethod == IntPtr.Zero) Off(ref _headroomOk, "getThermalHeadroom not found");
            }
            if (_headroomOk)
            {
                _headroomThread = new Thread(HeadroomLoop) { IsBackground = true, Name = "HIS thermal headroom" };
                _headroomThread.Start();
            }
        }

        /// <summary>
        /// getThermalHeadroom every 10.5 s (it answers NaN to calls under 10 s apart), off the main thread, through the
        /// PowerManager global ref and the method id the constructor found. Ends quietly with the process (background thread).
        /// </summary>
        void HeadroomLoop()
        {
            AndroidJNI.AttachCurrentThread();
            try
            {
                var args = new[] { new jvalue { i = 10 } };
                while (!_stop)
                {
                    if (!AppPaused)
                    {
                        float v = AndroidJNI.CallFloatMethod(_power.GetRawObject(), _headroomMethod, args);
                        var jex = AndroidJNI.ExceptionOccurred();
                        if (jex != IntPtr.Zero)
                        {
                            AndroidJNI.ExceptionClear(); AndroidJNI.DeleteLocalRef(jex);
                            _headroom = float.NaN; _headroomError = "getThermalHeadroom: java exception"; break;
                        }
                        _headroom = v;
                    }
                    for (int i = 0; i < 21 && !_stop; i++) Thread.Sleep(500); // 10.5 s
                }
            }
            catch (Exception e) { _headroom = float.NaN; _headroomError = "getThermalHeadroom: " + e.Message; }
            finally { AndroidJNI.DetachCurrentThread(); }
        }

        public void Read(ref ThermalSample s, float now)
        {
            if (_statusOk)
                try { s.Status = _power.Call<int>("getCurrentThermalStatus"); }
                catch (Exception e) { Off(ref _statusOk, "getCurrentThermalStatus: " + e.Message); }
            if (_headroomOk && _headroomError is { } err) Off(ref _headroomOk, err);
            s.Headroom = _headroomOk ? _headroom : float.NaN; // the headroom thread's last value
            if (_batteryOk)
            {
                IntPtr raw = IntPtr.Zero;
                try
                {
                    raw = AndroidJNI.CallObjectMethod(_activity.GetRawObject(), _registerReceiver,
                        new[] { new jvalue { l = IntPtr.Zero }, new jvalue { l = _filter.GetRawObject() } });
                    var jex = AndroidJNI.ExceptionOccurred();
                    if (jex != IntPtr.Zero) { AndroidJNI.ExceptionClear(); AndroidJNI.DeleteLocalRef(jex); throw new Exception("java exception"); }
                    if (raw != IntPtr.Zero)
                    {
                        using var intent = new AndroidJavaObject(raw);
                        int t = intent.Call<int>("getIntExtra", "temperature", int.MinValue);
                        if (t != int.MinValue) s.BatteryC = t / 10f;
                        s.Plugged = intent.Call<int>("getIntExtra", "plugged", 0) != 0;
                    }
                }
                catch (Exception e) { Off(ref _batteryOk, "battery intent: " + e.Message); }
                finally { if (raw != IntPtr.Zero) AndroidJNI.DeleteLocalRef(raw); }
            }
        }

        /// <summary>Stops the headroom thread (the governor rebinding to another source).</summary>
        public void Dispose() => _stop = true;

        static void Off(ref bool flag, string why) { flag = false; Debug.LogWarning($"[HIS] Thermal: {why} — that signal is off for this session"); }
    }
}
