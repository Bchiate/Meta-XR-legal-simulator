namespace HIS.Core
{
    /// <summary>
    /// Who owns the controllers right now. Gameplay input (grab, fire, punch, yell) only runs in <see cref="Mode.Gameplay"/>;
    /// panels switch to UI, the pause system to Paused.
    /// </summary>
    public static class InputGate
    {
        public enum Mode { Gameplay, UI, Paused }

        public static Mode Current { get; private set; } = Mode.Gameplay;
        public static bool GameplayActive => Current == Mode.Gameplay;

        public static event System.Action<Mode> Changed;

        public static void Set(Mode mode)
        {
            if (mode == Current) return;
            Current = mode;
            Changed?.Invoke(mode);
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { Current = Mode.Gameplay; Changed = null; } // Editor play sessions with domain reload off
    }
}
