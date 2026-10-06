using System.Collections.Generic;
using UnityEngine;

namespace HIS.Intruder
{
    /// <summary>
    /// Keeps the last N dead intruders in the room. Sandbox keeps 2; the scenario director will set 1 (one incident, one body).
    /// Over the cap the oldest body retires: it vanishes, with its blood pool, once the player isn't looking at it.
    /// </summary>
    public static class CorpseRegistry
    {
        public static int MaxCorpses = 2;
        static readonly List<Intruder> _corpses = new();

        public static int Count { get { _corpses.RemoveAll(c => c == null); return _corpses.Count; } }

        public static void Register(Intruder corpse)
        {
            _corpses.RemoveAll(c => c == null);
            if (!_corpses.Contains(corpse)) _corpses.Add(corpse);
            while (_corpses.Count > Mathf.Max(0, Mathf.Min(MaxCorpses, HIS.Core.PerfBudget.MaxCorpses))) // 1 when the headset runs warm
            {
                var old = _corpses[0];
                _corpses.RemoveAt(0);
                if (old != null) old.Retire();
            }
        }

        public static void ClearAll()
        {
            foreach (var c in _corpses) if (c != null) Object.Destroy(c.gameObject);
            _corpses.Clear();
        }
    }
}
