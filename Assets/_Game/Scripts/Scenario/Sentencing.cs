using System.Collections.Generic;
using System.Text;

namespace HIS.Scenario
{
    /// <summary>One charge on the final card: what, the code (California's, or a federal count), and a rough term.</summary>
    public struct Crime
    {
        public string Name, Code;
        public int Count;
        /// <summary>A middle term in years (determinate), or 0 for a life term / a misdemeanor.</summary>
        public float Years;
        /// <summary>Life with the possibility of parole (Years = the minimum before parole), or life without it.</summary>
        public bool Life, NoParole;
        /// <summary>A misdemeanor: county jail, months.</summary>
        public float JailMonths;
        /// <summary>A federal count (a Marshal's bad call, research/27 §4): the estimate then says "in prison", not "in state prison".</summary>
        public bool Federal;
    }

    /// <summary>
    /// The final card's tally (Brett 0.1.25: "final bad call should tally up your crimes and estimate your sentence dont drop
    /// the soap joke is welcome"). Pure data, no Unity: a rough, game-level estimate from middle terms of the California code
    /// (not legal advice): voluntary manslaughter PC 192(a) 3/6/11 + personal use of a firearm PC 12022.5(a) 3/4/10;
    /// assault with a deadly weapon PC 245(a) 2/3/4; battery PC 242 up to 6 months; brandishing a gun at an officer PC 417(c)
    /// 16 months/2/3; assault with a firearm on a peace officer PC 245(d)(1) 4/6/8; attempted murder of a peace officer
    /// PC 664(e) life with parole (15 years minimum); murder of a peace officer PC 190(c) with the PC 190.2(a)(7) special
    /// circumstance: life without parole; resisting an officer PC 148(a)(1) up to a year in county jail. A Marshal's bad call adds
    /// the federal count (research/27 §4): deprivation of rights under color of law, 18 U.S.C. 242 (up to a year; up to ten with
    /// bodily injury; any term of years or life if death results), game middle terms 12 months / 5 / 15 years.
    /// </summary>
    public static class Sentencing
    {
        public static Crime FromJudgeCharge(string charge, bool gun) => charge switch
        {
            Judge.ChargeKilled => new Crime { Name = "Voluntary manslaughter", Code = "PC 192(a)", Count = 1, Years = 6f + (gun ? 4f : 0f) },
            Judge.ChargeInjured => new Crime { Name = "Assault with a deadly weapon", Code = "PC 245(a)", Count = 1, Years = 3f + (gun ? 4f : 0f) },
            Judge.ChargeBattery => new Crime { Name = "Battery", Code = "PC 242", Count = 1, JailMonths = 6f },
            _ => new Crime { Name = charge, Code = "", Count = 1, Years = 2f },
        };

        public static Crime Brandishing => new() { Name = "Brandishing a gun at an officer", Code = "PC 417(c)", Count = 1, Years = 2f };
        public static Crime AssaultOnOfficer(int n) => new() { Name = "Assault with a firearm on a peace officer", Code = "PC 245(d)(1)", Count = n, Years = 6f };
        public static Crime AttemptedMurderOfOfficer(int n) => new() { Name = "Attempted murder of a peace officer", Code = "PC 664(e)", Count = n, Years = 15f, Life = true };
        public static Crime MurderOfOfficer(int n) => new() { Name = "Murder of a peace officer", Code = "PC 190(c)", Count = n, Life = true, NoParole = true };
        public static Crime Resisting => new() { Name = "Resisting an officer", Code = "PC 148(a)(1)", Count = 1, JailMonths = 12f };

        /// <summary>
        /// The federal count on a Marshal's bad call (research/27 §4): "Deprivation of rights under color of law", 18 U.S.C. 242.
        /// Game middle terms: he died 15 years, he was hurt 5 years, otherwise 12 months. Added after the judge's charge, so the
        /// booking placard still reads the judge's charge (the card's first crime).
        /// </summary>
        public static Crime ColorOfLaw(bool killed, bool injured) => new()
        {
            Name = "Deprivation of rights under color of law", Code = "18 U.S.C. 242", Count = 1, Federal = true,
            Years = killed ? 15f : injured ? 5f : 0f, JailMonths = killed || injured ? 0f : 12f,
        };

        /// <summary>The federal count for a judge's charge (<see cref="Judge.ChargeKilled"/> = he died, <see cref="Judge.ChargeInjured"/> = hurt, else neither).</summary>
        public static Crime ColorOfLaw(string judgeCharge) => ColorOfLaw(judgeCharge == Judge.ChargeKilled, judgeCharge == Judge.ChargeInjured);

        /// <summary>The line a Marshal's bad call on a visitor adds to the card's reason (research/27 §4).</summary>
        public const string OprReferral = "Referred to the Office of Professional Responsibility.";

        /// <summary>
        /// "Estimated sentence: 16 years" / "27 years to life" / "Life without the possibility of parole" / "8 months in county jail".
        /// With a federal count among them it's "in prison" (no "state", no "county jail": research/27 §4).
        /// </summary>
        public static string Estimate(IReadOnlyList<Crime> crimes)
        {
            bool life = false, noParole = false, federal = false;
            float years = 0f, months = 0f;
            foreach (var c in crimes)
            {
                if (c.Federal) federal = true;
                if (c.NoParole) noParole = true;
                else if (c.Life) { life = true; years += c.Years * c.Count; }
                else if (c.JailMonths > 0f) months += c.JailMonths * c.Count;
                else years += c.Years * c.Count;
            }
            if (noParole) return "Life without the possibility of parole";
            if (life) return $"{Round(years)} years to life";
            if (years >= 1f) return federal ? $"{Round(years + months / 12f)} years in prison" : $"{Round(years + months / 12f)} years in state prison";
            if (months > 0f) return federal ? $"{Round(months)} months in prison" : $"{Round(months)} months in county jail";
            return "No time";
        }

        static int Round(float v) => (int)System.Math.Round(v, System.MidpointRounding.AwayFromZero);

        /// <summary>The card block: every charge (×N), the estimate, the joke (arrests only).</summary>
        public static string Tally(IReadOnlyList<Crime> crimes, bool arrested, int wavesBeaten)
        {
            if (crimes.Count == 0) return null;
            var sb = new StringBuilder();
            sb.Append(arrested ? "<b>Charges</b>" : "<b>You'd have faced</b>");
            foreach (var c in crimes)
                sb.Append("\n• ").Append(c.Name).Append(c.Count > 1 ? $" ×{c.Count}" : "")
                  .Append(string.IsNullOrEmpty(c.Code) ? "" : $" <color=#8A93A0>({c.Code})</color>");
            if (wavesBeaten > 0) sb.Append($"\n<color=#B8C0CC>Police waves you beat: {wavesBeaten}</color>");
            sb.Append("\n<b>Estimated sentence: ").Append(Estimate(crimes)).Append("</b>");
            if (arrested) sb.Append("\n<i>Don't drop the soap.</i>");
            return sb.ToString();
        }
    }
}
