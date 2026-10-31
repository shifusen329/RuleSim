using RaidReplay.Core.Model;
using WrathCombo.CustomRules;
using static SharedBuiltins;

/// <summary> Samurai action ids (WrathCombo's SAM.cs / SAM_Helper.cs and RoleActions.cs; Tengentsu is Wrath's 7498). </summary>
internal static class SA
{
    public const uint Hakaze = 7477, Jinpu = 7478, Shifu = 7479, Yukikaze = 7480, Gekko = 7481, Kasha = 7482, Fuga = 7483, Mangetsu = 7484,
        Oka = 7485, Enpi = 7486, Midare = 7487, Tenka = 7488, Higanbana = 7489, Shinten = 7490, Kyuten = 7491, Gyoten = 7492, Yaten = 7493,
        Hagakure = 7495, Guren = 7496, Meditate = 7497, Tengentsu = 7498, Meikyo = 7499, Seigan = 7501, Iaijutsu = 7867, Senei = 16481,
        Ikishoten = 16482, Tsubame = 16483, KaeshiHiganbana = 16484, KaeshiGoken = 16485, KaeshiSetsu = 16486, Shoha = 16487, Fuko = 25780,
        Ogi = 25781, KaeshiNami = 25782, Gyofu = 36963, Zanshin = 36964, TendoGoken = 36965, TendoSetsu = 36966, TendoKaeshiGoken = 36967,
        TendoKaeshiSetsu = 36968, TrueNorth = 7546, Feint = 7549, Bloodbath = 7542, SecondWind = 7541, LegSweep = 7863, ArmsLength = 7548,
        Sprint = 3, Wait = 1_000_004;

    public static readonly uint[] AutoCrits = [Midare, TendoSetsu, KaeshiSetsu, TendoKaeshiSetsu, Ogi, KaeshiNami];
    public static readonly uint[] ComboFamily = [Gyofu, Hakaze, Jinpu, Shifu, Yukikaze, Gekko, Kasha, Fuko, Mangetsu, Oka];
}

/// <summary> Samurai statuses (Wrath's SAM.Buffs/Debuffs and role buffs; 3808 Directional Disregard). </summary>
internal static class SS
{
    public const uint Meikyo = 1233, Fugetsu = 1298, Fuka = 1299, OgiReady = 2959, TsubameReady = 4216, TendoKaeshiReady = 4218,
        KaeshiGokenReady = 3852, TendoKaeshiGokenReady = 4217, ZanshinReady = 3855, Tengentsu = 3853, Foresight = 3854, Tendo = 3856,
        Higanbana = 1228, TrueNorth = 1250, Feint = 1195, Bloodbath = 84, ArmsLength = 1209, Sprint = 50, DirectionalDisregard = 3808,
        Partner = 1824, Devilment = 1825;
}

/// <summary>
///     One Samurai action. Range is yalms to the target's hitbox edge (0 = self). CastMs is the base cast (1.3 s for
///     Iaijutsu and Ogi Namikiri), scaled by skill speed and Fuka like the GCD. ComboFrom/ComboPotency: the combo step
///     it continues. Positional: 1 rear (Gekko), 2 flank (Kasha), worth PosBonus potency when hit.
/// </summary>
internal sealed record SDef(uint Id, string Name, AKind Kind, double Range = 3, int CastMs = 0, int CooldownMs = 0, int Charges = 1,
    int Potency = 0, int ComboPotency = 0, uint ComboFrom = 0, int PosBonus = 0, int Positional = 0, bool AutoCrit = false);

/// <summary> A cooldown with charges, in ms (lazy recharge). </summary>
internal sealed class SamCd(int recast, int max)
{
    private int n = max, next = int.MaxValue;
    public readonly int Recast = recast, Max = max;

    private void Upd(int t)
    {
        while (n < Max && t >= next) { n++; next = n < Max ? next + Recast : int.MaxValue; }
    }

    public int Charges(int t) { Upd(t); return n; }
    public void Use(int t) { Upd(t); if (n == Max) next = t + Recast; n--; }

    /// <summary> Like Wrath's GetCooldownRemainingTime: seconds until every charge is back. </summary>
    public double Remaining(int t) { Upd(t); return n == Max ? 0 : (next - t + (Max - n - 1) * (double)Recast) / 1000.0; }

    /// <summary> Like GetCooldownChargeRemainingTime: seconds until the next charge (0 when full). </summary>
    public double ChargeRemaining(int t) { Upd(t); return n == Max ? 0 : (next - t) / 1000.0; }
}

/// <summary>
///     The simulated Samurai (2026-10-21): the rules choose its actions every 20 ms; position, target, movement, hits,
///     deaths, enemy casts and other players' buffs come from the logged stand-in (World.Me). Ported from
///     scratchpad/sim/Program.cs (state machine, ids, potencies) with the environment taken from the log.
/// </summary>
internal sealed class SamSim : IRuleRuntime
{
    public readonly World W;
    public int T;
    /// <summary> GCD with Fuka (13% haste); without it GcdMs / 0.87. </summary>
    public readonly int GcdMs, GcdBaseMs;
    public int SlideMs = 700;
    public uint Trigger = SA.Gyofu;
    /// <summary> Whose logged hits count as hits on the Samurai (Tengentsu, damage taken): the stand-in, or for meleeSlot=2 the M2 player. </summary>
    public Actor HitsActor;
    /// <summary> Higanbana is lost when the target changes (the brief); false keeps it ticking on the old target (the game). </summary>
    public bool DotLostOnSwitch = true;
    /// <summary> Tengentsu's Foresight grants 10 Kenki (36 of 36 procs in the user's gauge); false isolates the plan's weave cost. </summary>
    public bool ForesightKenki = true;

    // gauge
    public int Kenki, Meditation, MeikyoStacks;
    public bool Setsu, Getsu, Ka;
    public int KaeshiNamiUntil = int.MinValue;
    public int Sen => (Setsu ? 1 : 0) + (Getsu ? 1 : 0) + (Ka ? 1 : 0);
    public bool KaeshiNamiReady => KaeshiNamiUntil > T;

    // combo
    public uint ComboAction;
    public int ComboUntil = int.MinValue;
    public double ComboTimer => ComboUntil > T ? (ComboUntil - T) / 1000.0 : 0;

    // timing
    public int GcdReadyAt = -100_000, LockUntil = -100_000, LastGcdAt = int.MinValue, Weaves;
    public (SDef Def, int Start, int End, Actor? Target, string Rule)? Cast;
    private readonly Dictionary<uint, SamCd> cds = new();
    private readonly Dictionary<uint, int> lastUse = new();
    private readonly Dictionary<uint, (int Expire, int Stacks)> self = new();
    private readonly Dictionary<(uint Target, uint Status), int> onTarget = new();
    private int lastMovingAt = int.MinValue, movingSince = int.MinValue;
    private bool wasDead;
    private int? windowStart;
    private bool inWindow;
    private Actor? lastTarget;
    private readonly List<(int T, string Name, double Pct, Actor Source)> hits;
    public IReadOnlyList<(int T, string Name, double Pct, Actor Source)> MergedHits => hits;

    /// <summary>
    ///     What a Tengentsu proc saved, in % of max HP (teng_opt.py's model): 10% of the hit it caught, 10% of every hit in the
    ///     next 9 s (Foresight), and Foresight's three regen ticks of 5.5% (200 potency) up to the HP missing in the log.
    /// </summary>
    public (double Mit, double Heal) ProcValue(int hitT, double pct)
    {
        var mit = 0.1 * pct + 0.1 * hits.Where(h => h.T > hitT && h.T <= hitT + 9000).Sum(h => h.Pct);
        double heal = 0;
        for (var k = 0; k < 3; k++) heal += Math.Min(5.5, Math.Max(0, 100 - W.HpPct(HitsActor, hitT + 1500 + 3000 * k)));
        return (mit, heal);
    }
    private int hitIndex;

    // ---------------- results ----------------
    public readonly List<SimEvent> Log = new();
    /// <summary> Direct damage: potency, World.RaidBuffs multiplier (crit/DH buffs taken out), crit/DH expectation relative to a normal hit. </summary>
    public readonly List<(int T, string Src, double Potency, double Mult, double Crit, bool AutoCrit, int RaidCount)> Damage = new();
    public sealed class DotApp
    {
        public required Actor Target; public int CastStart, Land, Expire, EndedAt = int.MaxValue; public double Mult, Crit; public string Why = "";
    }
    public readonly List<DotApp> Dots = new();
    public readonly Dictionary<string, int> RuleFires = new();
    public int Cancels, Gcds, Unmodelled, KenkiWasted, MeditationWasted, CastsLostTarget;
    public readonly List<(int T, string Name, string Rule, int MovedInMs)> CancelLog = new();
    public readonly List<(int T, string Phase, string Reason, string Rule, int Ms)> Idle = new();
    public int AvailMs, BusyMs, OutOfMeleeMs, BanaUpMs, DeadMs, IncapMs;
    public readonly Dictionary<string, (int Avail, int Busy, int Out, int Bana)> PhaseMs = new();
    public readonly List<(int T, string Name, bool Needed, bool Geometry, bool TrueNorth, string Angle)> Positionals = new();
    public readonly List<(int T, string Rule, bool UsedByPositional)> TrueNorths = new();
    public readonly List<(int T, string Rule, int Hit, string HitName)> Tengentsus = new();
    public readonly List<(int T, string HitName, double Pct, bool Tengentsu)> HitsTaken = new();
    public readonly List<(int T, Actor? Target, string Rule)> Feints = new();
    public int MeikyoCapMs;
    public readonly List<(int T, string What)> Expired = new();
    /// <summary> The rules' state at every GCD press: (time, rule, action, state). </summary>
    public readonly List<(int T, string Action, string Rule, string State)> GcdStates = new();
    private static readonly uint[] Watched = [SS.Fugetsu, SS.Fuka, SS.Meikyo, SS.Tendo, SS.TsubameReady, SS.TendoKaeshiReady, SS.OgiReady, SS.ZanshinReady];

    public static string StatusName(uint st) => st switch
    {
        SS.Fugetsu => "Fugetsu", SS.Fuka => "Fuka", SS.Meikyo => "Meikyo Shisui", SS.Tendo => "Tendo", SS.TsubameReady => "held Kaeshi: Setsugekka",
        SS.TendoKaeshiReady => "held Tendo Kaeshi", SS.OgiReady => "Ogi Namikiri Ready", SS.ZanshinReady => "Zanshin Ready", _ => st.ToString(),
    };

    /// <summary> A compact view of the rules' state. </summary>
    public string State()
    {
        string L(uint st) => Has(st) ? $"{Left(st):0}" : "-";
        var tgt = Target;
        return $"sen {(Setsu ? "S" : "")}{(Getsu ? "G" : "")}{(Ka ? "K" : "")}{(Sen == 0 ? "0" : "")} kenki {Kenki} med {Meditation} meikyo {cds[SA.Meikyo].Charges(T)}ch" +
               $"{(Has(SS.Meikyo) ? $"+buff{MeikyoStacks}@{Left(SS.Meikyo):0}" : "")} cdNext {cds[SA.Meikyo].ChargeRemaining(T):0} tendo {L(SS.Tendo)} kaeshi {L(SS.TsubameReady)} tendoK {L(SS.TendoKaeshiReady)} ogi {L(SS.OgiReady)}" +
               $"{(KaeshiNamiReady ? " kaeshiN" : "")} combo {(ComboTimer > 0 ? $"{Defs[ComboAction].Name}@{ComboTimer:0}" : "-")} fugetsu {L(SS.Fugetsu)} fuka {L(SS.Fuka)} senei {cds[SA.Senei].Remaining(T):0} iki {cds[SA.Ikishoten].Remaining(T):0} raidIn {RaidIn():0} bana {TargetLeft(tgt, SS.Higanbana):0}";
    }

    public int MeikyoCharges => cds[SA.Meikyo].Charges(T);
    public readonly List<int> KenkiAt = new();
    public int KenkiGained, KenkiSpent;
    public readonly Dictionary<string, int> KenkiBy = new(), KenkiWastedBy = new();
    public bool Trace; public int TraceFrom, TraceTo;
    private (uint, string) lastChoice;

    public static readonly Dictionary<uint, SDef> Defs = new SDef[]
    {
        new(SA.Gyofu, "Gyofu", AKind.Gcd, Potency: 240),
        new(SA.Jinpu, "Jinpu", AKind.Gcd, Potency: 140, ComboPotency: 300, ComboFrom: SA.Gyofu),
        new(SA.Shifu, "Shifu", AKind.Gcd, Potency: 140, ComboPotency: 300, ComboFrom: SA.Gyofu),
        new(SA.Yukikaze, "Yukikaze", AKind.Gcd, Potency: 160, ComboPotency: 340, ComboFrom: SA.Gyofu),
        new(SA.Gekko, "Gekko", AKind.Gcd, Potency: 170, ComboPotency: 370, ComboFrom: SA.Jinpu, PosBonus: 50, Positional: 1),
        new(SA.Kasha, "Kasha", AKind.Gcd, Potency: 170, ComboPotency: 370, ComboFrom: SA.Shifu, PosBonus: 50, Positional: 2),
        new(SA.Enpi, "Enpi", AKind.Gcd, Range: 20, Potency: 100),
        new(SA.Higanbana, "Higanbana", AKind.Gcd, Range: 6, CastMs: 1300, Potency: 200),
        new(SA.Tenka, "Tenka Goken", AKind.Gcd, Range: 8, CastMs: 1300, Potency: 300),
        new(SA.Midare, "Midare Setsugekka", AKind.Gcd, Range: 6, CastMs: 1300, Potency: 640, AutoCrit: true),
        new(SA.TendoGoken, "Tendo Goken", AKind.Gcd, Range: 8, CastMs: 1300, Potency: 410),
        new(SA.TendoSetsu, "Tendo Setsugekka", AKind.Gcd, Range: 6, CastMs: 1300, Potency: 1100, AutoCrit: true),
        new(SA.KaeshiGoken, "Kaeshi: Goken", AKind.Gcd, Range: 8, Potency: 300),
        new(SA.TendoKaeshiGoken, "Tendo Kaeshi Goken", AKind.Gcd, Range: 8, Potency: 410),
        new(SA.KaeshiSetsu, "Kaeshi: Setsugekka", AKind.Gcd, Range: 6, Potency: 640, AutoCrit: true),
        new(SA.TendoKaeshiSetsu, "Tendo Kaeshi Setsugekka", AKind.Gcd, Range: 6, Potency: 1100, AutoCrit: true),
        new(SA.Ogi, "Ogi Namikiri", AKind.Gcd, Range: 8, CastMs: 1300, Potency: 1000, AutoCrit: true),
        new(SA.KaeshiNami, "Kaeshi: Namikiri", AKind.Gcd, Range: 8, Potency: 1000, AutoCrit: true),
        new(SA.Meikyo, "Meikyo Shisui", AKind.Ogcd, Range: 0, CooldownMs: 55000, Charges: 2),
        new(SA.Senei, "Hissatsu: Senei", AKind.Ogcd, CooldownMs: 60000, Potency: 800),
        new(SA.Ikishoten, "Ikishoten", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(SA.Zanshin, "Zanshin", AKind.Ogcd, Range: 8, CooldownMs: 1000, Potency: 940),
        new(SA.Shoha, "Shoha", AKind.Ogcd, Range: 8, CooldownMs: 15000, Potency: 640),
        new(SA.Shinten, "Hissatsu: Shinten", AKind.Ogcd, CooldownMs: 1000, Potency: 250),
        new(SA.Gyoten, "Hissatsu: Gyoten", AKind.Ogcd, Range: 20, CooldownMs: 5000, Potency: 100),
        new(SA.TrueNorth, "True North", AKind.Ogcd, Range: 0, CooldownMs: 45000, Charges: 2),
        new(SA.Tengentsu, "Tengentsu", AKind.Ogcd, Range: 0, CooldownMs: 15000),
        new(SA.Feint, "Feint", AKind.Ogcd, Range: 10, CooldownMs: 90000),
        new(SA.Bloodbath, "Bloodbath", AKind.Ogcd, Range: 0, CooldownMs: 90000),
        new(SA.SecondWind, "Second Wind", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(SA.LegSweep, "Leg Sweep", AKind.Ogcd, CooldownMs: 40000),
        new(SA.ArmsLength, "Arm's Length", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(SA.Sprint, "Sprint", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(SA.Wait, "Wait", AKind.Wait),
    }.ToDictionary(d => d.Id);

    public SamSim(World w, int gcdMs, Actor? hitsActor = null)
    {
        W = w;
        GcdMs = gcdMs;
        GcdBaseMs = (int)Math.Round(gcdMs / 0.87);
        HitsActor = hitsActor ?? w.Me;
        // As scratchpad/sam/teng_opt.py: one ability's simultaneous hits (within 0.15 s) are one hit, damage summed;
        // mis-decoded values over 150% of HP and unnamed abilities are dropped.
        hits = new();
        foreach (var h in w.HitsOn(HitsActor).Where(h => h.Pct <= 150 && !h.Name.StartsWith("unknown_")))
            if (hits.Count > 0 && h.T - hits[^1].T < 150) hits[^1] = hits[^1] with { Pct = hits[^1].Pct + h.Pct };
            else hits.Add(h);
        foreach (var d in Defs.Values.Where(d => d.CooldownMs > 0)) cds[d.Id] = new SamCd(d.CooldownMs, d.Charges);
    }

    // ---------------- state helpers ----------------

    public bool Has(uint status) => self.TryGetValue(status, out var s) && s.Expire > T;
    public double Left(uint status) => self.TryGetValue(status, out var s) && s.Expire > T ? (s.Expire - T) / 1000.0 : 0;
    private void Give(uint status, int ms, int stacks = 0) => self[status] = (T + ms, stacks);
    private void Drop(uint status) => self.Remove(status);
    public bool Fuka => Has(SS.Fuka);
    public int RecastMs => Fuka ? GcdMs : GcdBaseMs;
    /// <summary> Iaijutsu/Ogi cast: 1.3 s scaled by skill speed (GCD base / 2.5 s) and Fuka. </summary>
    public int CastTime(SDef d) => d.CastMs == 0 ? 0 : (int)Math.Round(d.CastMs * (GcdBaseMs / 2500.0) * (Fuka ? 0.87 : 1));

    public Actor? Target => TargetAt(W, T);

    private static readonly Dictionary<World, List<(int T, Actor Target)>> primaryHits = new();

    /// <summary>
    ///     What the stand-in has targeted: World.Target's rule (the enemy its last damaging action hit within 15 s, if still
    ///     targetable, else the biggest targetable boss), but taking each action's primary target, not the first enemy in its
    ///     hit list, and an AoE that also hit the current target keeps it. Model fix 2026-10-21: the Reaper's AoEs (Communio,
    ///     Sacrificium, Harvest Moon, Gluttony...) also hit Exdeath in Phase 3 and list him first, which flipped the replay's
    ///     target to Exdeath and ended Higanbana on Chaos. World.Target (the Sage's) is unchanged.
    /// </summary>
    public static Actor? TargetAt(World w, int t)
    {
        if (!primaryHits.TryGetValue(w, out var l))
        {
            l = new List<(int, Actor)>();
            Actor? last = null;
            foreach (var a in w.R.Actions.Where(a => a.Source.Id == w.Me.Id).OrderBy(a => a.T))
            {
                var damaged = a.Hits.Where(h => h.Kind == HitKind.Damage && h.Damage > 0 && !h.Target.IsPlayer).ToList();
                if (damaged.Count == 0) continue;
                // an AoE that also hit the current target doesn't change it; otherwise its primary target, else the first hit
                var tgt = last is not null && damaged.Any(h => h.Target == last) ? last
                    : a.PrimaryTarget is { IsPlayer: false } p && damaged.Any(h => h.Target == p) ? p : damaged[0].Target;
                l.Add((damaged[0].T, tgt));
                last = tgt;
            }
            l.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            primaryHits[w] = l;
        }
        int lo = 0, hi = l.Count - 1, ans = -1;
        while (lo <= hi) { var mid = (lo + hi) / 2; if (l[mid].T <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
        if (ans >= 0 && l[ans].T >= t - 15000 && l[ans].Target is var lt && lt.IsPresent(t) && lt.IsTargetable(t)) return lt;
        return w.Bosses.Where(b => b.IsPresent(t) && b.IsTargetable(t) && !b.IsHidden(t)).MaxBy(b => b.MaxHp);
    }

    public bool DebuffOn(Actor? t, uint status) => t is not null && TargetLeft(t, status) > 0;

    public double TargetLeft(Actor? t, uint status)
    {
        if (t is null) return 0;
        if (status == SS.Higanbana)
        {
            var d = Dots.LastOrDefault(x => x.Target.Id == t.Id && x.Land <= T);
            return d is not null && d.EndedAt > T && d.Expire > T ? (d.Expire - T) / 1000.0 : 0;
        }
        return onTarget.TryGetValue((t.Id, status), out var e) && e > T ? (e - T) / 1000.0 : 0;
    }

    public bool NeedsPositionals(Actor? t) => t is not null && !t.IsPlayer && !W.OthersHave(t, SS.DirectionalDisregard, T);

    /// <summary> Wrath's AngleToTarget: the player's bearing from the target minus the target's facing; front/flank/rear 90° arcs. </summary>
    public string AngleTo(Actor? t)
    {
        if (t is null || !t.Track.TrySample(T, out var tp, out var th)) return "unknown";
        var pp = W.Pos(W.Me, T);
        var rot = Angles.Toward(tp, pp) - th;
        var deg = rot * 180 / MathF.PI + (rot < 0 ? 360 : 0);
        return deg switch
        {
            >= 315f or <= 45f => "front",
            >= 45f and <= 135f => "flank",
            >= 135f and <= 225f => "rear",
            >= 225f and <= 315f => "flank",
            _ => "unknown",
        };
    }

    public double Dist(Actor? t) => t is null ? 99 : DistFor(W, W.Me, t, T);

    public static double DistFor(World w, Actor a, Actor t, int time) =>
        Math.Max(0, System.Numerics.Vector2.Distance(w.Pos(a, time), w.Pos(t, time)) - World.Radius(a) - EffectiveRadius(w, t));

    public static IEnumerable<(Actor Actor, float Radius)> RadiusFixes(World w) { EffectiveRadius(w, w.Me); return radii[w].Select(kv => (kv.Key, kv.Value)); }

    /// <summary> 3 y weaponskills of the melee jobs and tanks, by log name: the party's hits with them show how big a target's hitbox really is. </summary>
    public static readonly HashSet<string> MeleeGcds = ["Slice", "Waxing Slice", "Infernal Slice", "Gibbet", "Gallows", "Executioner's Gibbet", "Executioner's Gallows",
        "Void Reaping", "Cross Reaping", "Shadow of Death", "Soul Slice", "Gyofu", "Hakaze", "Jinpu", "Shifu", "Yukikaze", "Gekko", "Kasha",
        "Fast Blade", "Riot Blade", "Royal Authority", "Rage of Halone", "Atonement", "Supplication", "Sepulchre", "Goring Blade",
        "Heavy Swing", "Maim", "Storm's Path", "Storm's Eye", "Fell Cleave", "Inner Beast", "Inner Chaos",
        "Hard Slash", "Syphon Strike", "Souleater", "Bloodspiller", "Scarlet Delirium", "Comeuppance", "Torcleaver",
        "Keen Edge", "Brutal Shell", "Solid Barrel", "Burst Strike", "Gnashing Fang", "Savage Claw", "Wicked Talon", "Noble Blood", "Lion Heart", "Reign of Beasts",
        "True Thrust", "Vorpal Thrust", "Full Thrust", "Chaos Thrust", "Wheeling Thrust", "Fang and Claw", "Drakesbane", "Raiden Thrust", "Lance Barrage",
        "Spiral Blow", "Heavens' Thrust", "Chaotic Spring", "Bootshine", "True Strike", "Snap Punch", "Dragon Kick", "Twin Snakes", "Demolish",
        "Leaping Opo", "Rising Raptor", "Pouncing Coeurl", "Spinning Edge", "Gust Slash", "Aeolian Edge", "Armor Crush",
        "Steel Fangs", "Reaving Fangs", "Hunter's Sting", "Swiftskin's Sting", "Flanksting Strike", "Flanksbane Fang", "Hindsting Strike", "Hindsbane Fang"];

    private static readonly Dictionary<World, Dictionary<Actor, float>> radii = new();

    /// <summary>
    ///     The target's hitbox radius: the log's, or larger when the party's 3 y weaponskills show it is (model fix
    ///     2026-10-21: the 2026-10-05 clear has Kefka Says at radius 0 and Kefka Reimagined at 6, with melee hits landing
    ///     up to 8 y and 5 y past them). Inferred radius = the 98th percentile of (centre distance at the hit - 3 y - the
    ///     attacker's 0.5 y), from 20 or more hits on that actor, used when it is over 1 y more than the log's (on the
    ///     clear's bosses with a known radius the same estimate lands within 0.7 y of it: 6.7/5.3/5.6/3.8 for 6/6/6/3.8).
    /// </summary>
    public static float EffectiveRadius(World w, Actor t)
    {
        if (!radii.TryGetValue(w, out var map))
        {
            map = new Dictionary<Actor, float>();
            foreach (var g in w.R.Actions.Where(e => e.Source.IsPlayer && MeleeGcds.Contains(e.Name))
                         .SelectMany(e => e.Hits.Where(h => !h.Target.IsPlayer && h.Kind is HitKind.Damage or HitKind.Blocked or HitKind.Parried).Take(1).Select(h => (h.Target, D: System.Numerics.Vector2.Distance(e.SourcePos, h.TargetPos))))
                         .Where(x => x.D > 0.01 && x.D < 40).GroupBy(x => x.Target))
            {
                var l = g.Select(x => x.D).OrderBy(x => x).ToList();
                if (Environment.GetEnvironmentVariable("SAM_RADIUS_DEBUG") is not null)
                    Console.WriteLine($"   radius {g.Key.Name} {g.Key.Id:X} gen {g.Key.Generation} data {g.Key.Radius:0.0}: {l.Count} melee hits, centre distance p50 {l[l.Count / 2]:0.0} p90 {l[(int)(l.Count * 0.9)]:0.0} p98 {l[(int)(l.Count * 0.98)]:0.0} max {l[^1]:0.0}; by " +
                                      string.Join(",", w.R.Actions.Where(e => e.Source.IsPlayer && MeleeGcds.Contains(e.Name) && e.Hits.Any(h => h.Target == g.Key)).GroupBy(e => e.Source.Name).Select(x => $"{x.Key.Split(' ')[0]} {x.Count()}")));
                if (l.Count < 20) continue;
                var inferred = (float)(l[(int)(l.Count * 0.98)] - 3.5);
                if (inferred > g.Key.Radius + 1.0f) map[g.Key] = inferred;
            }
            radii[w] = map;
        }
        return map.TryGetValue(t, out var r) ? r : t.Radius;
    }

    /// <summary> Statuses in the log that stop all actions (the user's DMU logs show Sleep, Confused and Down for the Count). </summary>
    public static readonly string[] Incapacitating = ["Sleep", "Stun", "Down for the Count", "Confused", "Petrification", "Deep Freeze", "Knockdown", "Fetters"];

    private static readonly Dictionary<World, List<int>> knockbacks = new();

    /// <summary>
    ///     Why the stand-in can't act at t, or null: an incapacitating status on them, or the first second after a
    ///     knockback hit (no actions while being pushed).
    /// </summary>
    public static string? Incapacitated(World w, int t)
    {
        foreach (var n in Incapacitating)
            if (w.OthersHaveNamed(w.Me, n, t, out _)) return n;
        if (!knockbacks.TryGetValue(w, out var kb))
            knockbacks[w] = kb = w.R.Actions.SelectMany(a => a.Hits).Where(h => h.Target.Id == w.Me.Id && h.Knockback).Select(h => h.T).OrderBy(x => x).ToList();
        var i = kb.BinarySearch(t); if (i < 0) i = ~i - 1;
        return i >= 0 && t < kb[i] + 1000 ? "knockback" : null;
    }

    private static uint Hooked(uint id, SamSim s) => s.OriginalHook(id);

    // ---------------- IRuleRuntime ----------------

    public bool CanWeave()
    {
        var lockRem = Math.Max(0, LockUntil - T) / 1000.0;
        var castRem = Cast is { } c ? (c.End - T) / 1000.0 : 0;
        var gcdRem = Math.Max(0, GcdReadyAt - T) / 1000.0;
        return Weaves < 2 && lockRem <= 0.6 && castRem <= 0.5 && gcdRem > castRem + 0.6 + lockRem;
    }

    public uint OriginalHook(uint id) => id switch
    {
        SA.Hakaze => SA.Gyofu,
        SA.Iaijutsu => Sen switch
        {
            1 => SA.Higanbana,
            2 => Has(SS.Tendo) ? SA.TendoGoken : SA.Tenka,
            3 => Has(SS.Tendo) ? SA.TendoSetsu : SA.Midare,
            _ => SA.Iaijutsu,
        },
        SA.Tsubame => Has(SS.TendoKaeshiReady) ? SA.TendoKaeshiSetsu : Has(SS.TendoKaeshiGokenReady) ? SA.TendoKaeshiGoken
            : Has(SS.TsubameReady) ? SA.KaeshiSetsu : Has(SS.KaeshiGokenReady) ? SA.KaeshiGoken : SA.Tsubame,
        SA.Ogi => KaeshiNamiReady ? SA.KaeshiNami : SA.Ogi,
        _ => id,
    };

    /// <summary>
    ///     Wrath's ActionReady: learned, a charge or (for weaponskills) the GCD, and the game's own usability (Kenki,
    ///     Sen, Meditation, the ready statuses). Range isn't part of it. The game adjusts base ids (Hakaze, Iaijutsu,
    ///     Tsubame-gaeshi, Ogi Namikiri) to their current form first.
    /// </summary>
    public bool ActionReady(uint id)
    {
        if (id == 0) return false;
        if (id >= 1_000_000) return true;
        id = OriginalHook(id);
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (W.Dead(W.Me, T)) return false;
        var usable = id switch
        {
            SA.Senei or SA.Shinten or SA.Kyuten or SA.Guren => Kenki >= 25,
            SA.Gyoten or SA.Yaten => Kenki >= 10,
            SA.Zanshin => Has(SS.ZanshinReady) && Kenki >= 50,
            SA.Shoha => Meditation >= 3,
            SA.Ikishoten => T >= 0,
            SA.Higanbana => Sen == 1,
            SA.Tenka => Sen == 2,
            SA.TendoGoken => Sen == 2 && Has(SS.Tendo),
            SA.Midare => Sen == 3,
            SA.TendoSetsu => Sen == 3 && Has(SS.Tendo),
            SA.KaeshiSetsu => Has(SS.TsubameReady),
            SA.TendoKaeshiSetsu => Has(SS.TendoKaeshiReady),
            SA.KaeshiGoken => Has(SS.KaeshiGokenReady),
            SA.TendoKaeshiGoken => Has(SS.TendoKaeshiGokenReady),
            SA.Ogi => Has(SS.OgiReady),
            SA.KaeshiNami => KaeshiNamiReady,
            _ => true,
        };
        if (!usable) return false;
        if (d.Kind == AKind.Gcd) return true;   // cd(GCD) <= remainingGcd + 0.5 always holds
        return !cds.TryGetValue(id, out var cd) || cd.Charges(T) >= 1;
    }

    private Actor? pendingTarget;

    public uint Retarget(uint action, uint trigger, RuleTargetSpec[] targets)
    {
        foreach (var spec in targets)
        {
            bool Ok(Actor? a) => a is not null && spec.Filters.All(f => (f.AnyShield ? false : HasAnyOn(a, f.StatusId)) != f.Negate);
            IEnumerable<Actor> Living() => W.Party.Where(p => !W.Dead(p, T) && System.Numerics.Vector2.Distance(W.Pos(p, T), W.Pos(W.Me, T)) <= 30);
            Actor? Lowest(IEnumerable<Actor> l) => l.Where(Ok).MinBy(p => W.HpPct(p, T));
            var c = spec.Kind switch
            {
                RuleTarget.Self => Ok(W.Me) ? W.Me : null,
                RuleTarget.Lowest or RuleTarget.Heal => Lowest(Living()),
                RuleTarget.Tank => Lowest(Living().Where(p => World.IsTank(p) && p.Id != W.Me.Id)),
                RuleTarget.Healer => Lowest(Living().Where(p => World.IsHealer(p) && p.Id != W.Me.Id)),
                RuleTarget.Dps => Lowest(Living().Where(p => !World.IsTank(p) && !World.IsHealer(p) && p.Id != W.Me.Id)),
                RuleTarget.TargetsTarget => W.BossTarget(Target, T) is { } tt && !W.Dead(tt, T) && Ok(tt) ? tt : null,
                RuleTarget.Raise => W.Party.FirstOrDefault(p => W.Dead(p, T)),
                _ => null,
            };
            if (c is not null) { pendingTarget = c; return action; }
        }
        return 0;
    }

    private bool HasAnyOn(Actor a, uint status) => a.Id == W.Me.Id ? Has(status) || W.OthersHave(a, status, T) : DebuffOn(a, status) || W.OthersHave(a, status, T);

    // ---------------- the simulation step ----------------

    public void Advance(int dt)
    {
        T += dt;
        var dead = W.Dead(W.Me, T);
        if (dead && !wasDead)
        {
            if (Cast is { } c) { Log.Add(new SimEvent(T, $"(cast lost: died) {c.Def.Name}", "", c.Rule)); Cast = null; Gcds--; }
            // Death clears the gauge (the user's logs show Kenki to 0), your statuses and your effects on enemies.
            self.Clear(); onTarget.Clear();
            foreach (var d in Dots.Where(d => d.EndedAt > T)) { d.EndedAt = Math.Max(T, d.Land); d.Why = "died"; }
            Kenki = Meditation = MeikyoStacks = 0; Setsu = Getsu = Ka = false; KaeshiNamiUntil = int.MinValue; ComboUntil = int.MinValue;
            Log.Add(new SimEvent(T, "DIED", "", ""));
        }
        wasDead = dead;
        if (dead && T >= 0) DeadMs += dt;

        // Things that ran out unused (added for the Phase 1 -> 2 transition study, 2026-10-21).
        foreach (var st in Watched)
            if (self.TryGetValue(st, out var e) && e.Expire <= T)
            {
                self.Remove(st);
                if (st == SS.Meikyo && MeikyoStacks <= 0) continue;
                if (st == SS.OgiReady && Cast is { } oc && oc.Def.Id == SA.Ogi) continue;   // used: the cast started while it was up
                Expired.Add((T, StatusName(st) + (st == SS.Meikyo ? $" ({MeikyoStacks} stacks)" : "")));
                Log.Add(new SimEvent(T, $"EXPIRED {StatusName(st)}{(st == SS.Meikyo ? $" ({MeikyoStacks} stacks)" : "")}", "", ""));
                if (st == SS.Meikyo) MeikyoStacks = 0;
            }
        if (KaeshiNamiUntil != int.MinValue && KaeshiNamiUntil <= T)
        {
            KaeshiNamiUntil = int.MinValue;
            Expired.Add((T, "Kaeshi: Namikiri"));
            Log.Add(new SimEvent(T, "EXPIRED Kaeshi: Namikiri", "", ""));
        }
        if (ComboUntil != int.MinValue && ComboUntil <= T)
        {
            if (ComboAction is SA.Gyofu or SA.Jinpu or SA.Shifu)
            {
                Expired.Add((T, $"combo after {Defs[ComboAction].Name}"));
                Log.Add(new SimEvent(T, $"COMBO DROPPED after {Defs[ComboAction].Name}", "", ""));
            }
            ComboUntil = int.MinValue;
        }

        if (W.Moving(T)) { if (movingSince == int.MinValue) movingSince = T; lastMovingAt = T; }
        else movingSince = int.MinValue;

        // Target change: the brief has Higanbana lost when the target changes.
        var tgt = Target;
        if (tgt is not null && lastTarget is not null && tgt.Id != lastTarget.Id && DotLostOnSwitch)
            foreach (var d in Dots.Where(d => d.Target.Id != tgt.Id && d.EndedAt > T && d.Expire > T)) { d.EndedAt = Math.Max(T, d.Land); d.Why = "target changed"; }
        if (tgt is not null) lastTarget = tgt;

        // Hits on the Samurai (or the M2 player standing in for it): the first one while Tengentsu is up takes it and
        // grants Tengentsu's Foresight and 10 Kenki (as the user's logs show).
        while (hitIndex < hits.Count && hits[hitIndex].T <= T)
        {
            var h = hits[hitIndex++];
            if (h.T < 0 || W.Dead(HitsActor, h.T)) continue;
            var teng = Has(SS.Tengentsu);
            HitsTaken.Add((h.T, h.Name, h.Pct, teng));
            if (teng)
            {
                Drop(SS.Tengentsu);
                Give(SS.Foresight, 9000);
                if (ForesightKenki) AddKenki(10, "Tengentsu's Foresight");
                if (Tengentsus.Count > 0 && Tengentsus[^1].Hit == 0) Tengentsus[^1] = Tengentsus[^1] with { Hit = h.T, HitName = h.Name };
            }
        }

        var incap = Incapacitated(W, T);
        if (Cast is { } cast)
        {
            if (incap is not null && T < cast.End - SlideMs)
            {
                Cancels++;
                Gcds--;
                CancelLog.Add((T, cast.Def.Name, cast.Rule + $" (interrupted: {incap})", T - cast.Start));
                Log.Add(new SimEvent(T, $"CANCEL {cast.Def.Name}", "", cast.Rule, $"interrupted ({incap})"));
                Cast = null;
                GcdReadyAt = T;
                LockUntil = T;
            }
            else if (W.Moving(T) && T < cast.End - SlideMs)
            {
                Cancels++;
                Gcds--;
                CancelLog.Add((T, cast.Def.Name, cast.Rule, T - cast.Start));
                Log.Add(new SimEvent(T, $"CANCEL {cast.Def.Name}", "", cast.Rule, $"moved {(T - cast.Start) / 1000.0:0.0}s in"));
                Cast = null;
                GcdReadyAt = T;
                LockUntil = T;
            }
            else if (T >= cast.End)
            {
                Cast = null;
                LockUntil = T + 100;
                if (cast.Target is null || !cast.Target.IsPresent(T) || !cast.Target.IsTargetable(T))
                {
                    CastsLostTarget++;
                    Gcds--;
                    GcdReadyAt = T;
                    Log.Add(new SimEvent(T, $"(cast lost: target gone) {cast.Def.Name}", "", cast.Rule));
                }
                else Land(cast.Def, cast.Target, cast.Rule, cast.Start);
            }
        }

        // Accounting while the fight is on. Time incapacitated (Sleep, Down for the Count, knockbacks...) is downtime.
        if (T >= 0 && !dead && tgt is not null && incap is not null) IncapMs += dt;
        if (T >= 0 && !dead && tgt is not null && incap is null)
        {
            var phase = W.R.PhaseAt(T);
            var p = PhaseMs.GetValueOrDefault(phase);
            p.Avail += dt; AvailMs += dt;
            if (Cast is not null || T < GcdReadyAt) { p.Busy += dt; BusyMs += dt; }
            if (Dist(tgt) > 3) { p.Out += dt; OutOfMeleeMs += dt; }
            if (TargetLeft(tgt, SS.Higanbana) > 0) { p.Bana += dt; BanaUpMs += dt; }
            PhaseMs[phase] = p;
            if (cds[SA.Meikyo].Charges(T) >= 2) MeikyoCapMs += dt;
        }
    }

    private void AddIdle(string reason, string rule)
    {
        if (T < 0 || W.Dead(W.Me, T) || Target is null) return;
        var phase = W.R.PhaseAt(T);
        if (Idle.Count > 0 && Idle[^1] is var last && last.Reason == reason && last.Rule == rule && last.T + last.Ms >= T - 20)
            Idle[^1] = last with { Ms = last.Ms + 20 };
        else Idle.Add((T, phase, reason, rule, 20));
    }

    public void Press(IReadOnlyList<CompiledRuleSet> files)
    {
        if (W.Dead(W.Me, T)) return;
        if (Incapacitated(W, T) is not null) return;
        UpdateRaidWindow();
        pendingTarget = null;
        uint a = 0;
        var rule = "-";
        foreach (var f in files)
        {
            if (!f.Enabled) continue;
            a = f.Evaluate(Trigger, this);
            if (a != 0) { rule = $"{f.Name}: {(f.LastFired >= 0 ? f.Rules[f.LastFired].Name : "?")}"; break; }
        }
        if (a == 0) { a = Trigger; rule = "(no rule: the button's own action)"; }
        a = OriginalHook(a);   // the game uses the adjusted action

        var gcdFree = Cast is null && T >= GcdReadyAt;
        if (Trace && T >= TraceFrom && T <= TraceTo && (a, rule) != lastChoice)
        {
            lastChoice = (a, rule);
            Console.WriteLine($"   trace {SageSim.F(T)} -> {(Defs.TryGetValue(a, out var dd) ? dd.Name : a.ToString())} ({rule}) gcdRem={Math.Max(0, GcdReadyAt - T)} lock={Math.Max(0, LockUntil - T)} cast={(Cast is null ? "-" : Cast.Value.Def.Name)} moving={W.Moving(T)} tgt={Target?.Name ?? "none"} dist={Dist(Target):0.0} {AngleTo(Target)} ready={ActionReady(a)} kenki={Kenki} sen={Sen} med={Meditation} meikyo={MeikyoStacks}@{Left(SS.Meikyo):0.0} bana={TargetLeft(Target, SS.Higanbana):0.0} raid={W.RaidBuffs(Target, T).Count}");
        }

        if (!Defs.TryGetValue(a, out var d))
        {
            Unmodelled++;
            if (gcdFree) AddIdle($"unmodelled action {a}", rule);
            return;
        }
        if (d.Kind == AKind.Wait)
        {
            if (gcdFree) AddIdle("Wait", rule);
            return;
        }
        var target = pendingTarget ?? Target;
        if (d.Kind == AKind.Ogcd)
        {
            if (gcdFree && T < LockUntil) AddIdle("oGCD animation lock past the GCD", rule);
            if (Cast is not null || T < LockUntil || !ActionReady(a)) return;
            if (d.Range > 0 && (target is null || target.IsPlayer || Dist(target) > d.Range)) { if (gcdFree) AddIdle($"out of range: {d.Name}", rule); return; }
            Use(d, d.Range > 0 ? target : W.Me, rule);
            LockUntil = T + 600;
            Weaves++;
            return;
        }
        // GCD
        if (!gcdFree) return;
        if (T < LockUntil) { AddIdle("oGCD animation lock past the GCD", rule); return; }
        if (!ActionReady(a)) { AddIdle($"not ready: {d.Name}", rule); return; }
        if (target is null || target.IsPlayer || Dist(target) > d.Range) { AddIdle($"out of range: {d.Name}", rule); return; }
        var castMs = CastTime(d);
        if (castMs > 0 && W.Moving(T)) { AddIdle($"moving, cast held: {d.Name}", rule); return; }
        GcdStates.Add((T, d.Name, rule, State()));
        GcdReadyAt = T + RecastMs;
        LastGcdAt = T;
        Weaves = 0;
        Gcds++;
        if (castMs > 0)
        {
            Cast = (d, T, T + castMs, target, rule);
            Log.Add(new SimEvent(T, $"cast> {d.Name}", "", rule));
        }
        else
        {
            LockUntil = T + 600;
            Use(d, target, rule);
        }
    }

    private void Use(SDef d, Actor? target, string rule) => Land(d, target, rule, T);

    private void Land(SDef d, Actor? target, string rule, int start)
    {
        lastUse[d.Id] = T;
        RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
        if (cds.TryGetValue(d.Id, out var cd)) cd.Use(T);
        var meikyo = Has(SS.Meikyo) && MeikyoStacks > 0;
        if (meikyo && SA.ComboFamily.Contains(d.Id) && --MeikyoStacks <= 0) Drop(SS.Meikyo);

        var potency = d.Potency;
        var note = "";
        bool InCombo(uint from) => ComboUntil > T && ComboAction == from;
        void Combo(uint id) { ComboAction = id; ComboUntil = T + 30000; }
        switch (d.Id)
        {
            case SA.Gyofu: AddKenki(5, d.Name); Combo(d.Id); break;
            case SA.Jinpu:
                if (InCombo(SA.Gyofu)) { potency = d.ComboPotency; AddKenki(5, d.Name); Give(SS.Fugetsu, 40000); Combo(d.Id); }
                else ComboAction = 0;
                break;
            case SA.Shifu:
                if (InCombo(SA.Gyofu)) { potency = d.ComboPotency; AddKenki(5, d.Name); Give(SS.Fuka, 40000); Combo(d.Id); }
                else ComboAction = 0;
                break;
            case SA.Yukikaze:
                if (meikyo || InCombo(SA.Gyofu)) { potency = d.ComboPotency; AddKenki(15, d.Name); Setsu = true; }
                Combo(d.Id);
                break;
            case SA.Gekko or SA.Kasha:
            {
                if (meikyo || InCombo(d.ComboFrom))
                {
                    potency = d.ComboPotency;
                    AddKenki(10, d.Name);
                    if (d.Id == SA.Gekko) Getsu = true; else Ka = true;
                    // Under Meikyo they grant Fugetsu/Fuka (not in the plain combo; the user's logs show only Jinpu/Shifu do).
                    if (meikyo) Give(d.Id == SA.Gekko ? SS.Fugetsu : SS.Fuka, 40000);
                }
                Combo(d.Id);
                var needed = NeedsPositionals(target);
                var angle = AngleTo(target);
                var geometry = angle == (d.Positional == 1 ? "rear" : "flank");
                var tn = Has(SS.TrueNorth);
                Positionals.Add((T, d.Name, needed, geometry, tn, angle));
                if (!needed || geometry || tn) potency += d.PosBonus;
                note = !needed ? "no positional needed" : geometry ? $"{angle} hit" : tn ? $"True North ({angle})" : $"MISSED ({angle})";
                break;
            }
            case SA.Enpi: AddKenki(10, d.Name); break;
            case SA.Higanbana:
                ClearSen(); AddMeditation();
                if (target is not null && !target.IsPlayer)
                {
                    var land = start + 1400;
                    var (mult, crit) = Factors(target, land, false);
                    Dots.Add(new DotApp { Target = target, CastStart = start, Land = land, Expire = land + 60000, Mult = mult, Crit = crit });
                    // the previous one on this target ends when this one lands
                    foreach (var o in Dots.Where(o => o.Target.Id == target.Id && o.Land < land && o.EndedAt > land)) { o.EndedAt = land; o.Why = "refreshed"; }
                    note = $"lands {SageSim.F(land)}";
                }
                break;
            case SA.Tenka: ClearSen(); AddMeditation(); Give(SS.KaeshiGokenReady, 30000); break;
            case SA.TendoGoken: ClearSen(); AddMeditation(); Drop(SS.Tendo); Give(SS.TendoKaeshiGokenReady, 30000); break;
            case SA.Midare: ClearSen(); AddMeditation(); Give(SS.TsubameReady, 30000); break;
            case SA.TendoSetsu: ClearSen(); AddMeditation(); Drop(SS.Tendo); Give(SS.TendoKaeshiReady, 30000); break;
            case SA.KaeshiSetsu: Drop(SS.TsubameReady); break;
            case SA.TendoKaeshiSetsu: Drop(SS.TendoKaeshiReady); break;
            case SA.KaeshiGoken: Drop(SS.KaeshiGokenReady); break;
            case SA.TendoKaeshiGoken: Drop(SS.TendoKaeshiGokenReady); break;
            case SA.Ogi: Drop(SS.OgiReady); AddMeditation(); KaeshiNamiUntil = T + 30000; break;
            case SA.KaeshiNami: KaeshiNamiUntil = int.MinValue; break;
            case SA.Meikyo: Give(SS.Meikyo, 20000, 3); MeikyoStacks = 3; Give(SS.Tendo, 30000); break;
            case SA.Senei: SpendKenki(25); break;
            case SA.Shinten: SpendKenki(25); break;
            case SA.Gyoten: SpendKenki(10); break;
            case SA.Zanshin: SpendKenki(50); Drop(SS.ZanshinReady); break;
            case SA.Ikishoten: AddKenki(50, d.Name); Give(SS.OgiReady, 30000); Give(SS.ZanshinReady, 30000); break;
            case SA.Shoha: Meditation = 0; break;
            case SA.TrueNorth: Give(SS.TrueNorth, 10000); TrueNorths.Add((T, rule, false)); break;
            // Its status shows from the press for about 4.6 s in the user's logs (4 s + latency) unless a hit takes it.
            case SA.Tengentsu: Give(SS.Tengentsu, 4600); Tengentsus.Add((T, rule, 0, "")); break;
            case SA.Feint:
                if (target is not null && !target.IsPlayer) onTarget[(target.Id, SS.Feint)] = T + 15000;
                Feints.Add((T, target, rule));
                note = target?.Name ?? "";
                break;
            case SA.Sprint: Give(SS.Sprint, 10000); break;
            case SA.Bloodbath: Give(SS.Bloodbath, 20000); break;
            case SA.ArmsLength: Give(SS.ArmsLength, 6000); break;
        }
        if (d.Positional > 0 && Has(SS.TrueNorth) && TrueNorths.Count > 0 && NeedsPositionals(target) && AngleTo(target) != (d.Positional == 1 ? "rear" : "flank"))
            TrueNorths[^1] = TrueNorths[^1] with { UsedByPositional = true };
        if (potency > 0 && target is not null && !target.IsPlayer && T >= 0)
        {
            var (mult, crit) = Factors(target, T, d.AutoCrit);
            Damage.Add((T, d.Name, potency, mult, crit, d.AutoCrit, W.RaidBuffs(target, T).Count));
        }
        Log.Add(new SimEvent(T, d.Name, "", rule, note));
    }

    // Crit 27%, crit damage x1.62, direct hit 25% (the old sim's stats). Crit-rate and DH-rate buffs are applied as rates;
    // World.BuffValue's flat stand-ins for them are taken out of the World multiplier so they aren't counted twice.
    public const double BaseCrit = 0.27, CritDmg = 1.62, BaseDh = 0.25;
    private static readonly double NormalCrit = (1 + BaseCrit * (CritDmg - 1)) * (1 + BaseDh * 0.25);

    /// <summary> (raid-buff damage multiplier, crit/DH expectation relative to an unbuffed normal hit) at t on the target. </summary>
    public (double Mult, double Crit) Factors(Actor target, int t, bool autoCrit)
    {
        var mult = W.RaidBuffs(target, t).Mult;
        double crit = 0, dh = 0;
        if (W.OthersHaveNamed(W.Me, "Battle Litany", t, out _)) { crit += 0.10; mult /= 1 + World.BuffValue["Battle Litany"]; }
        if (W.OthersHaveNamed(target, "Chain Stratagem", t, out _)) { crit += 0.10; mult /= 1 + World.BuffValue["Chain Stratagem"]; }
        if (W.OthersHaveNamed(W.Me, "Battle Voice", t, out _)) { dh += 0.20; mult /= 1 + World.BuffValue["Battle Voice"]; }
        if (W.OthersHave(W.Me, SS.Devilment, t)) { crit += 0.20; dh += 0.20; }
        var c = autoCrit ? CritDmg * (1 + crit * (CritDmg - 1)) : 1 + (BaseCrit + crit) * (CritDmg - 1);
        return (mult, c * (1 + (BaseDh + dh) * 0.25) / NormalCrit);
    }

    private void ClearSen() => Setsu = Getsu = Ka = false;

    private void AddKenki(int n, string src)
    {
        var total = Kenki + n;
        if (total > 100) { if (T >= 0) { KenkiWasted += total - 100; KenkiWastedBy[src] = KenkiWastedBy.GetValueOrDefault(src) + total - 100; Expired.Add((T, $"{total - 100} Kenki ({src})")); } total = 100; }
        if (T >= 0) { KenkiGained += total - Kenki; KenkiBy[src] = KenkiBy.GetValueOrDefault(src) + (total - Kenki); }
        Kenki = total;
    }

    private void SpendKenki(int n) { Kenki -= n; if (T >= 0) KenkiSpent += n; }

    private void AddMeditation() { if (Meditation >= 3) { if (T >= 0) { MeditationWasted++; Expired.Add((T, "Meditation stack")); } } else Meditation++; }

    private void UpdateRaidWindow()
    {
        if (T < 0) { windowStart = null; inWindow = false; return; }
        var up = W.RaidBuffs(Target, T).Count > 0;
        if (up && !inWindow) windowStart = T;
        inWindow = up;
    }

    /// <summary> RaidSchedule.SecondsUntilNext: the last window's start + 120 s, else 2 s past each two minutes. </summary>
    public double RaidIn()
    {
        if (inWindow) return 0;
        if (T < 0) return 2;
        double next = windowStart is { } s ? s + 120000 : 2000 + 120000 * Math.Ceiling((T - 2000) / 120000.0);
        return Math.Max(0, next - T) / 1000.0;
    }

    public double StoodStill => W.Moving(T) ? 0 : (lastMovingAt == int.MinValue ? 999 : (T - lastMovingAt) / 1000.0);

    public double Cd(uint id)
    {
        id = OriginalHook(id);
        if (cds.TryGetValue(id, out var c)) return c.Remaining(T);
        return Defs.TryGetValue(id, out var d) && d.Kind == AKind.Gcd ? Math.Max(0, GcdReadyAt - T) / 1000.0 : 0;
    }

    public double Charges(uint id)
    {
        id = OriginalHook(id);
        return cds.TryGetValue(id, out var c) ? c.Charges(T) : ActionReady(id) ? 1 : 0;
    }

    public double ChargeCd(uint id)
    {
        id = OriginalHook(id);
        return cds.TryGetValue(id, out var c) ? c.ChargeRemaining(T) : 0;
    }

    public bool JustUsed(uint id, double secs) => lastUse.TryGetValue(id, out var t) && T - t <= secs * 1000;

    public bool InRange(uint id)
    {
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (d.Range == 0) return true;
        var t = Target;
        return t is not null && Dist(t) <= d.Range;
    }

    public double TimeOfLastUse(uint id) => lastUse.TryGetValue(id, out var t) ? t : int.MinValue;

    // ---------------- gauge fields (Dalamud's SAMGauge) ----------------

    public Func<double>? Gauge(string field) => field.ToLowerInvariant() switch
    {
        "kenki" => () => Kenki,
        "meditationstacks" => () => Meditation,
        "hassetsu" => () => B(Setsu),
        "hasgetsu" => () => B(Getsu),
        "haska" => () => B(Ka),
        "sen" => () => (Setsu ? 1 : 0) | (Getsu ? 2 : 0) | (Ka ? 4 : 0),
        "kaeshi" => () => KaeshiNamiReady ? 4 : 0,   // Kaeshi.Namikiri
        _ => null,
    };

    // ---------------- the rule functions (RuleBuiltins.cs names and meanings) ----------------

    public IReadOnlyDictionary<string, RuleFunction> Functions()
    {
        var f = new Dictionary<string, RuleFunction>(StringComparer.OrdinalIgnoreCase);
        void F(string name, ParamKind[] p, int req, Func<RuleArg[], Func<double>> bind) => Fn(f, name, p, req, bind);
        void Val(string name, Func<double> get) => V(f, name, get);
        ParamKind[] Act = [ParamKind.Action], St = [ParamKind.Status];
        AddMath(f);
        AddEnemyCasts(f, W, () => T);
        AddWorld(f, W, () => T, () => Target);

        F("ready", Act, 1, a => { var id = a[0].Id; return () => B(ActionReady(id)); });
        F("cd", Act, 1, a => { var id = a[0].Id; return () => Cd(id); });
        F("charges", Act, 1, a => { var id = a[0].Id; return () => Charges(id); });
        F("chargeCd", Act, 1, a => { var id = a[0].Id; return () => ChargeCd(id); });
        F("justUsed", [ParamKind.Action, ParamKind.Number], 1, a => { var id = a[0].Id; var s = a[1].Value; return () => B(JustUsed(id, s?.Invoke() ?? 3)); });
        F("inRange", Act, 1, a => { var id = a[0].Id; return () => B(InRange(id)); });
        F("combo", Act, 1, a => { var id = a[0].Id; return () => B(ComboTimer > 0 && ComboAction == id); });
        F("enemies", Act, 1, _ => () => Target is null ? 0 : 1);
        F("hook", Act, 1, a => { var id = a[0].Id; return () => OriginalHook(id); });
        F("buff", St, 1, a => { var id = a[0].Id; return () => B(Has(id)); });
        F("buffTime", St, 1, a => { var id = a[0].Id; return () => Left(id); });
        F("buffStacks", St, 1, a => { var id = a[0].Id; return () => id == SS.Meikyo ? (Has(id) ? MeikyoStacks : 0) : self.TryGetValue(id, out var s) && s.Expire > T ? s.Stacks : 0; });
        F("buffAny", St, 1, a => { var id = a[0].Id; return () => B(Has(id) || W.OthersHave(W.Me, id, T)); });
        F("buffAnyTime", St, 1, a => { var id = a[0].Id; return () => Math.Max(Left(id), W.OthersTime(W.Me, id, T)); });
        F("debuff", St, 1, a => { var id = a[0].Id; return () => B(DebuffOn(Target, id)); });
        F("debuffTime", St, 1, a => { var id = a[0].Id; return () => TargetLeft(Target, id); });
        F("debuffAny", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(DebuffOn(t, id) || W.OthersHave(t, id, T)) : 0; });
        F("debuffAnyTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? Math.Max(TargetLeft(t, id), W.OthersTime(t, id, T)) : 0; });
        Val("gcd", () => RecastMs / 1000.0);
        Val("remainingGcd", () => Math.Max(0, GcdReadyAt - T) / 1000.0);
        Val("canWeave", () => B(CanWeave()));
        Val("moving", () => B(W.Moving(T)));
        Val("stoodStill", () => StoodStill);
        Val("movingTime", () => movingSince == int.MinValue ? 0 : (T - movingSince) / 1000.0);
        Val("sinceGcd", () => LastGcdAt == int.MinValue ? 99 : (T - LastGcdAt) / 1000.0);
        Val("inMelee", () => Target is { } t ? B(Dist(t) <= 3) : 0);
        Val("distance", () => Target is { } t ? Dist(t) : 0);
        Val("rear", () => B(AngleTo(Target) == "rear"));
        Val("flank", () => B(AngleTo(Target) == "flank"));
        Val("front", () => B(AngleTo(Target) == "front"));
        Val("needsPositionals", () => B(NeedsPositionals(Target)));
        Val("countdown", () => T < 0 ? -T / 1000.0 : 0);
        Val("comboTimer", () => ComboTimer);
        Val("comboAction", () => ComboTimer > 0 ? ComboAction : 0);
        Val("mp", () => 10000);
        Val("casting", () => B(Cast is not null));
        Val("raidIn", RaidIn);
        foreach (var n in new[] { "raidwide", "stackMarker", "tankbuster", "canEsuna", "healCleansable" }) Val(n, () => 0);
        Val("raidwideIn", () => 99);
        // Party values (not used by the Samurai files; as in the Sage replay).
        IEnumerable<Actor> Living(bool inRange = true) => W.Party.Where(p => !W.Dead(p, T) && (!inRange || p.Id == W.Me.Id || System.Numerics.Vector2.Distance(W.Pos(p, T), W.Pos(W.Me, T)) <= 30));
        Val("partyAvgHp", () => Living(false).Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Average());
        Val("partyLowestHp", () => Living().Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Min());
        F("partyBelow", [ParamKind.Number], 1, a => { var n = a[0].Value!; return () => Living().Count(p => W.HpPct(p, T) <= n()); });
        Val("partyAlive", () => Living(false).Count());
        Val("partyDead", () => W.Party.Count(p => W.Dead(p, T)));
        F("partyWith", St, 1, a => { var id = a[0].Id; return () => { var l = Living(false).ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p => HasAnyOn(p, id)) / l.Count; }; });
        Val("partyInCombat", () => B(T >= 0));
        Val("tankHp", () => Living().Where(World.IsTank).Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Min());
        F("tankHas", St, 1, a => { var id = a[0].Id; return () => B(Living(false).Any(p => World.IsTank(p) && HasAnyOn(p, id))); });
        Actor? Heal() => Living().MinBy(p => W.HpPct(p, T));
        Val("healHp", () => Heal() is { } h ? W.HpPct(h, T) : 100);
        Val("healHpShielded", () => Heal() is { } h ? W.HpPct(h, T) : 100);
        Val("healIsTank", () => B(Heal() is { } h && World.IsTank(h)));
        Val("healIsSelf", () => B(Heal()?.Id == W.Me.Id));
        F("healHas", St, 1, a => { var id = a[0].Id; return () => B(Heal() is { } h && HasAnyOn(h, id)); });
        F("healTime", St, 1, a => { var id = a[0].Id; return () => Heal() is { } h ? W.OthersTime(h, id, T) : 0; });
        Val("healDistance", () => Heal() is { } h ? W.Dist(W.Me, h, T) : 0);
        Actor? TT() => W.BossTarget(Target, T) is { } tt && !W.Dead(tt, T) ? tt : null;
        Val("ttIsParty", () => B(TT() is not null));
        Val("ttIsTank", () => B(TT() is { } t && World.IsTank(t)));
        Val("ttIsSelf", () => B(TT()?.Id == W.Me.Id));
        Val("ttHp", () => TT() is { } t ? W.HpPct(t, T) : 100);
        F("ttHas", St, 1, a => { var id = a[0].Id; return () => B(TT() is { } t && HasAnyOn(t, id)); });
        Val("canRaise", () => B(W.Party.Any(p => W.Dead(p, T))));
        foreach (var n in new[] { "selfShielded", "tanksUnshielded", "healersUnshielded", "dpsUnshielded", "partyUnshielded" }) Val(n, () => 0);
        Val("button", () => Trigger);
        Val("buttonType", () => 1);
        return f;
    }
}
