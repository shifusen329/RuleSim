using System.Numerics;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;
using static SharedBuiltins;

/// <summary> Astrologian action ids (WrathCombo's AST_Helper.cs and RoleActions.cs). </summary>
internal static class AA
{
    public const uint Malefic = 3596, Malefic2 = 3598, Malefic3 = 7442, Malefic4 = 16555, FallMalefic = 25871, Gravity = 3615, Gravity2 = 25872,
        Oracle = 37029, EarthlyStar = 7439, StellarDetonation = 8324, AstralDraw = 37017, UmbralDraw = 37018, Play1 = 37019, Play2 = 37020,
        Play3 = 37021, Arrow = 37024, Balance = 37023, Bole = 37027, Ewer = 37028, Spear = 37026, Spire = 37025, MinorArcana = 37022,
        Lord = 7444, Lady = 7445, Divination = 16552, Lightspeed = 3606, Combust = 3599, Combust2 = 3608, Combust3 = 16554,
        Benefic = 3594, Benefic2 = 3610, AspectedBenefic = 3595, Helios = 3600, AspectedHelios = 3601, HeliosConj = 37030, Ascend = 3603,
        EssentialDignity = 3614, CelestialOpposition = 16553, CelestialIntersection = 16556, Horoscope = 16557, HoroscopeHeal = 16558,
        Exaltation = 25873, Macrocosmos = 25874, Microcosmos = 25875, Synastry = 3612, NeutralSect = 16559, SunSign = 37031,
        CollectiveUnconscious = 3613, Lucid = 7562, Swiftcast = 7561, Surecast = 7559, Esuna = 7568, Rescue = 7571, Sprint = 3,
        Wait = 1_000_004;

    public static readonly uint[] MaleficButtons = [Malefic, Malefic2, Malefic3, Malefic4, FallMalefic];
}

/// <summary> Astrologian statuses (Wrath's AST.Buffs/Debuffs; the ones Wrath lacks are the game's, as the 2026-10-05 clear logs them). </summary>
internal static class AS
{
    public const uint AspectedBenefic = 835, HeliosConj = 3894, Intersection = 1889, Horoscope = 1890, HoroscopeHelios = 1891,
        NeutralSect = 1892, Suntouched = 3895, NSShield = 1921, Divination = 1878, Divining = 3893, GiantDominance = 1248,
        EarthlyDominance = 1224, Macrocosmos = 2718, Balance = 3887, Arrow = 3888, Spear = 3889, Bole = 3890, Ewer = 3891, Spire = 3892,
        Lightspeed = 841, SelfSynastry = 845, TargetSynastry = 846, Combust3 = 1881, Swiftcast = 167, Lucid = 1204,
        // not in Wrath's AST class (the game's, from the clear)
        CollectiveUnconscious = 849, WheelOfFortune = 956, Opposition = 1879, SunSign = 3896, Exaltation = 2717, Accretion = 1604;
    public static readonly uint[] OtherShields = [2607, 2609, 297, 1918];   // RuleBuiltins.ShieldStatuses: E.Diagnosis, E.Prognosis, Galvanize, Catalyze
}

/// <summary> Dalamud's CardType values (gauge Card1/Card2/Card3/DrawnCrownCard) and DrawType (ActiveDraw). </summary>
internal static class Card
{
    public const int None = 0, Balance = 1, Bole = 2, Arrow = 3, Spear = 4, Ewer = 5, Spire = 6, Lord = 7, Lady = 8;
    public const int Astral = 0, Umbral = 1;
    public static string Name(int c) => c switch
    {
        Balance => "the Balance", Bole => "the Bole", Arrow => "the Arrow", Spear => "the Spear", Ewer => "the Ewer", Spire => "the Spire",
        Lord => "Lord of Crowns", Lady => "Lady of Crowns", _ => "",
    };
}

/// <summary>
///     One Astrologian action. Range: yalms to an enemy's hitbox edge (enemy actions), to the ally (PartyTarget), or the
///     radius around you (SelfAoe: Lord of Crowns, Macrocosmos). CastMs is the base cast, scaled by spell speed; Lightspeed
///     takes 2.5 s off it. A GCD with a cooldown (Macrocosmos) also needs its charge.
/// </summary>
internal sealed record AstDef(uint Id, string Name, AKind Kind, double Range = 25, int CastMs = 0, int CooldownMs = 0, int Charges = 1,
    int Mp = 0, int Potency = 0, bool PartyTarget = false, bool HealSpell = false, bool SelfAoe = false);

/// <summary>
///     The simulated Astrologian (2026-10-30): the rules choose its actions every 20 ms; position, target, movement, hits,
///     deaths, enemy casts and other players' buffs come from the logged player (World.Me). Timing, movement, incapacitation,
///     MP and idle accounting follow WhmSim. The card gauge (Card1-3, DrawnCrownCard, the next draw), Divination/Divining,
///     Earthly Star (Earthly then Giant Dominance, auto at 20 s), Lightspeed, Horoscope (upgraded by Helios/Helios
///     Conjunction), Neutral Sect's barriers, Macrocosmos's compiled damage and the party mitigations are modelled; the
///     replaced player's own statuses are left out of the world (World excludes them), so only the rules' count.
/// </summary>
internal sealed class AstSim : IRuleRuntime
{
    public readonly World W;
    public int T;
    public readonly int GcdMs;
    public int SlideMs = 700;
    public uint Trigger = AA.FallMalefic;
    public bool Trace; public int TraceFrom, TraceTo;
    /// <summary> HP per potency (for Macrocosmos's compiled damage); set by the command before the replay. </summary>
    public double HpPerPot = 95;

    // gauge and MP
    public int Card1, Card2, Card3, Crown, NextDraw = Card.Astral, LastDraw = -1, Mp = 10000;
    private int mpTickAt = -15000;

    // timing
    public int GcdReadyAt = -100_000, LockUntil = -100_000, LastGcdAt = int.MinValue, Weaves;
    public (AstDef Def, int Start, int End, Actor? Target, string Rule)? Cast;
    private readonly Dictionary<uint, SamCd> cds = new();
    private readonly Dictionary<uint, int> lastUse = new();
    private readonly Dictionary<(uint, uint), SimStatus> st = new();
    // Combust III: target id -> (expire, next tick, multiplier)
    private readonly Dictionary<uint, (int Expire, int Next, double Mult, Actor Target, int Land)> dots = new();
    private int lastMovingAt = int.MinValue, movingSince = int.MinValue;
    private bool wasDead;
    private int? windowStart;
    private bool inWindow;
    private Actor? pendingTarget;
    private (uint, string) lastChoice;
    private int hitIndex;
    // Earthly Star: where, when placed (int.MinValue: none), whether Giant Dominance has come
    private Vector2 starPos; private int starAt = int.MinValue; private bool starGiant;
    // Macrocosmos: when it landed (int.MinValue: none) and on whom
    private int macroAt = int.MinValue; private readonly List<Actor> macroOn = new();
    // Synastry's partner
    private Actor? synastry; private int synastryUntil = int.MinValue;
    // barriers (Neutral Sect, Intersection, the Spire): (target, status) -> (potency left)
    private readonly List<(int T, Actor Target, double Potency, string Src)> pendingTicks = new();

    // ---------------- results ----------------
    public readonly List<SimEvent> Log = new();
    public readonly List<(int T, string Src, double Potency, double Mult)> Damage = new();
    public readonly List<(int T, Actor Target, double Potency, string Src)> Heals = new();
    public readonly Dictionary<string, int> RuleFires = new();
    public readonly List<string> Anomalies = new();
    public int Cancels, Gcds, Unmodelled, CastsLostTarget, MinMp = 10000, CardsLost;
    public readonly List<(int T, string Name, string Rule, int MovedInMs)> CancelLog = new();
    public readonly List<(int T, string Phase, string Reason, string Rule, int Ms)> Idle = new();
    public int AvailMs, BusyMs, DeadMs, IncapMs, DotUpMs, ClipMs;
    public readonly Dictionary<string, (int Avail, int Busy, int Dot)> PhaseMs = new();
    public readonly List<(int T, string Name, string Rule, int GcdLeft)> Ogcds = new();
    /// <summary> Barriers on party members: (target, status, landed, ended, how). </summary>
    public readonly List<(Actor Target, uint Status, int From, int To, string How)> ShieldLog = new();
    /// <summary> Card plays: (time, card, target, right role, inside the rules' Divination). </summary>
    public readonly List<(int T, string Card, Actor Target, bool Right, bool InDiv)> CardPlays = new();
    public readonly List<(int T, string Lost)> LostLog = new();
    public readonly List<(int T, string What, bool InDiv)> BurstPresses = new();   // Divination, Lord of Crowns, Oracle
    public readonly List<(int Placed, int Ended, bool Manual, bool Giant)> StarLog = new();
    /// <summary> The rules' heals that landed on a player with an Accretion (1604): (time, target, source, seconds since
    /// another Accretion came off; a removal inside its 2 s vulnerability wipes, so under 3.2 s is the danger). </summary>
    public readonly List<(int T, Actor Target, string Src, double SinceOtherOff)> AccretionHeals = new();
    public readonly List<(int T, int What, int Reached, int Alive, string Rule)> ReachLog = new();
    /// <summary> Every barrier the rules put up, as it is put up: (time, target, status, potency). </summary>
    public readonly List<(int T, Actor Target, uint Status, double Potency)> BarrierLog = new();
    /// <summary> Macrocosmos: (landed, healed, players holding it). </summary>
    public readonly List<(int From, int To, int On)> MacroLog = new();

    public static readonly Dictionary<uint, AstDef> Defs = new AstDef[]
    {
        new(AA.FallMalefic, "Fall Malefic", AKind.Gcd, CastMs: 1500, Mp: 400, Potency: 270),
        new(AA.Combust3, "Combust III", AKind.Gcd, Mp: 400, Potency: 70),
        new(AA.Gravity2, "Gravity II", AKind.Gcd, CastMs: 1500, Mp: 400, Potency: 140),
        new(AA.HeliosConj, "Helios Conjunction", AKind.Gcd, Range: 0, CastMs: 1500, Mp: 800, HealSpell: true),
        new(AA.Helios, "Helios", AKind.Gcd, Range: 0, CastMs: 1500, Mp: 700, HealSpell: true),
        new(AA.AspectedBenefic, "Aspected Benefic", AKind.Gcd, Range: 30, Mp: 400, PartyTarget: true, HealSpell: true),
        new(AA.Benefic2, "Benefic II", AKind.Gcd, Range: 30, CastMs: 1500, Mp: 700, PartyTarget: true, HealSpell: true),
        new(AA.Benefic, "Benefic", AKind.Gcd, Range: 30, CastMs: 1500, Mp: 400, PartyTarget: true, HealSpell: true),
        new(AA.Macrocosmos, "Macrocosmos", AKind.Gcd, Range: 20, CooldownMs: 180000, Mp: 600, Potency: 270, SelfAoe: true),
        new(AA.Ascend, "Ascend", AKind.Gcd, Range: 30, CastMs: 8000, Mp: 2400, PartyTarget: true),
        new(AA.Esuna, "Esuna", AKind.Gcd, Range: 30, CastMs: 1000, Mp: 400, PartyTarget: true),
        new(AA.Divination, "Divination", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(AA.Lightspeed, "Lightspeed", AKind.Ogcd, Range: 0, CooldownMs: 60000, Charges: 2),
        new(AA.AstralDraw, "Astral Draw", AKind.Ogcd, Range: 0, CooldownMs: 55000),
        new(AA.UmbralDraw, "Umbral Draw", AKind.Ogcd, Range: 0, CooldownMs: 55000),
        new(AA.Balance, "the Balance", AKind.Ogcd, Range: 30, PartyTarget: true),
        new(AA.Spear, "the Spear", AKind.Ogcd, Range: 30, PartyTarget: true),
        new(AA.Arrow, "the Arrow", AKind.Ogcd, Range: 30, PartyTarget: true),
        new(AA.Bole, "the Bole", AKind.Ogcd, Range: 30, PartyTarget: true),
        new(AA.Spire, "the Spire", AKind.Ogcd, Range: 30, PartyTarget: true),
        new(AA.Ewer, "the Ewer", AKind.Ogcd, Range: 30, PartyTarget: true),
        new(AA.Lord, "Lord of Crowns", AKind.Ogcd, Range: 20, Potency: 400, SelfAoe: true),
        new(AA.Lady, "Lady of Crowns", AKind.Ogcd, Range: 0),
        new(AA.Oracle, "Oracle", AKind.Ogcd, Range: 25, Potency: 860),
        new(AA.EarthlyStar, "Earthly Star", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(AA.StellarDetonation, "Stellar Detonation", AKind.Ogcd, Range: 0, CooldownMs: 3000),
        new(AA.EssentialDignity, "Essential Dignity", AKind.Ogcd, Range: 30, CooldownMs: 40000, Charges: 3, PartyTarget: true),
        new(AA.CelestialIntersection, "Celestial Intersection", AKind.Ogcd, Range: 30, CooldownMs: 30000, Charges: 2, PartyTarget: true),
        new(AA.Exaltation, "Exaltation", AKind.Ogcd, Range: 30, CooldownMs: 60000, PartyTarget: true),
        new(AA.CollectiveUnconscious, "Collective Unconscious", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(AA.CelestialOpposition, "Celestial Opposition", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(AA.Horoscope, "Horoscope", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(AA.HoroscopeHeal, "Horoscope (read)", AKind.Ogcd, Range: 0),
        new(AA.NeutralSect, "Neutral Sect", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(AA.SunSign, "Sun Sign", AKind.Ogcd, Range: 0),
        new(AA.Microcosmos, "Microcosmos", AKind.Ogcd, Range: 0),
        new(AA.Synastry, "Synastry", AKind.Ogcd, Range: 30, CooldownMs: 120000, PartyTarget: true),
        new(AA.Lucid, "Lucid Dreaming", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(AA.Swiftcast, "Swiftcast", AKind.Ogcd, Range: 0, CooldownMs: 40000),
        new(AA.Surecast, "Surecast", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(AA.Rescue, "Rescue", AKind.Ogcd, Range: 30, CooldownMs: 120000, PartyTarget: true),
        new(AA.Sprint, "Sprint", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(AA.Wait, "Wait", AKind.Wait),
    }.ToDictionary(d => d.Id);

    public static readonly HashSet<string> GcdNames = Defs.Values.Where(d => d.Kind == AKind.Gcd).Select(d => d.Name).ToHashSet();

    /// <summary>
    ///     Movement for a remote player (2026-10-30): positions of other players arrive coarsely, and the burst model flags
    ///     32.7% of the clear Astrologian's completed casts as moved (the user's own Sage 7%). With StillCasts, the stretches
    ///     where the logged player completed a cast (start to end) count as standing still: they demonstrably were.
    /// </summary>
    public bool StillCasts = true;
    private readonly List<(int S, int E)> realCasts;
    private int rcIdx;

    public bool Moving(int t)
    {
        if (!W.Moving(t)) return false;
        if (!StillCasts) return true;
        while (rcIdx > 0 && realCasts[rcIdx].S > t) rcIdx--;
        while (rcIdx + 1 < realCasts.Count && realCasts[rcIdx + 1].S <= t) rcIdx++;
        return !(realCasts.Count > 0 && realCasts[rcIdx].S <= t && t < realCasts[rcIdx].E);
    }

    public AstSim(World w, int gcdMs, bool preCards)
    {
        W = w;
        GcdMs = gcdMs;
        realCasts = w.R.Casts.Where(c => c.Source.Id == w.Me.Id && c.Outcome != CastOutcome.Cancelled && c.DurationMs >= 1000)
            .Select(c => (c.StartMs, c.EndMs > c.StartMs ? c.EndMs : c.StartMs + c.DurationMs)).OrderBy(c => c.Item1).ToList();
        foreach (var d in Defs.Values.Where(d => d.CooldownMs > 0 && d.Id != AA.UmbralDraw)) cds[d.Id] = new SamCd(d.CooldownMs, d.Charges);
        // Cards drawn before the countdown (Astral Draw at least 55 s before the pull, so Umbral Draw is ready at it).
        if (preCards) { Card1 = Card.Balance; Card2 = Card.Arrow; Card3 = Card.Spire; Crown = Card.Lord; NextDraw = Card.Umbral; LastDraw = Card.Astral; }
    }

    // ---------------- state helpers ----------------

    public bool Has(Actor a, uint status) => st.TryGetValue((a.Id, status), out var s) && s.Expire > T;
    public bool Has(uint status) => Has(W.Me, status);
    public double Left(Actor a, uint status) => st.TryGetValue((a.Id, status), out var s) && s.Expire > T ? (s.Expire - T) / 1000.0 : 0;
    public int Stacks(Actor a, uint status) => st.TryGetValue((a.Id, status), out var s) && s.Expire > T ? s.Stacks : 0;
    private void Give(Actor a, uint status, int ms, int stacks = 0) => st[(a.Id, status)] = new SimStatus { Expire = T + ms, From = T, Stacks = stacks };
    private void Drop(Actor a, uint status) => st.Remove((a.Id, status));
    public bool HasAny(Actor a, uint status) => Has(a, status) || DotOn(a, status) || W.OthersHave(a, status, T);
    private bool DotOn(Actor a, uint status) => status == AS.Combust3 && dots.TryGetValue(a.Id, out var d) && d.Expire > T;
    public bool Shielded(Actor a) => AS.OtherShields.Any(s => W.OthersHave(a, s, T));

    public int CastTime(AstDef d)
    {
        if (d.CastMs == 0) return 0;
        var baseMs = Has(AS.Lightspeed) ? Math.Max(0, d.CastMs - 2500) : d.CastMs;
        return (int)Math.Round(baseMs * (GcdMs / 2500.0));
    }

    public Actor? Target => SamSim.TargetAt(W, T);
    public double Dist(Actor? t) => t is null ? 99 : W.Dist(W.Me, t, T);
    private double AllyDist(Actor p) => Vector2.Distance(W.Pos(p, T), W.Pos(W.Me, T));

    public IEnumerable<Actor> LivingParty(bool inRange = true) =>
        W.Party.Where(p => !W.Dead(p, T) && (!inRange || p.Id == W.Me.Id || AllyDist(p) <= 30));

    /// <summary> HP % the rules see: the logged HP, or with --cfhp the tanks' counterfactual HP (AstTankModel). </summary>
    public Func<Actor, int, double>? HpView;
    public double Hp(Actor p) => HpView is null ? W.HpPct(p, T) : HpView(p, T);
    public Actor? HealTarget => LivingParty().MinBy(p => Hp(p));
    public Actor? TT => W.BossTarget(Target, T) is { } tt && !W.Dead(tt, T) ? tt : null;
    public double DotLeft(Actor? t) => t is not null && dots.TryGetValue(t.Id, out var d) && d.Expire > T ? (d.Expire - T) / 1000.0 : 0;
    public bool StarOut => starAt != int.MinValue;

    /// <summary> The party's raid buffs on you (World: everyone else's) plus the rules' own Divination. </summary>
    public (int Count, double Left, double Mult) RaidBuffs(Actor? target)
    {
        var r = W.RaidBuffs(target, T);
        if (Has(AS.Divination)) return (r.Count + 1, Math.Max(r.Left, Left(W.Me, AS.Divination)), r.Mult * 1.06);
        return r;
    }

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
        AA.Malefic or AA.Malefic2 or AA.Malefic3 or AA.Malefic4 => AA.FallMalefic,
        AA.Combust or AA.Combust2 => AA.Combust3,
        AA.Gravity => AA.Gravity2,
        AA.AspectedHelios => AA.HeliosConj,
        AA.AstralDraw or AA.UmbralDraw => NextDraw == Card.Astral ? AA.AstralDraw : AA.UmbralDraw,
        AA.Play1 => Card1 switch { Card.Balance => AA.Balance, Card.Spear => AA.Spear, _ => AA.Play1 },
        AA.Play2 => Card2 switch { Card.Arrow => AA.Arrow, Card.Bole => AA.Bole, _ => AA.Play2 },
        AA.Play3 => Card3 switch { Card.Spire => AA.Spire, Card.Ewer => AA.Ewer, _ => AA.Play3 },
        AA.MinorArcana => Crown switch { Card.Lord => AA.Lord, Card.Lady => AA.Lady, _ => AA.MinorArcana },
        AA.Horoscope when Has(AS.Horoscope) || Has(AS.HoroscopeHelios) => AA.HoroscopeHeal,
        AA.Macrocosmos when Has(AS.Macrocosmos) => AA.Microcosmos,
        _ => id,
    };

    /// <summary> The cooldown an action runs on (the two draws share one). </summary>
    private uint CdKey(uint id) => id == AA.UmbralDraw ? AA.AstralDraw : id;

    private bool MpOk(AstDef d) => d.Mp <= Mp;

    /// <summary>
    ///     Wrath's ActionReady: a charge or the GCD, and the game's own usability. The game resolves a button to its current
    ///     form for that (Play I with the Balance held is ready; with no card it isn't), so the play buttons and the draws
    ///     are checked through OriginalHook here.
    /// </summary>
    public bool ActionReady(uint id)
    {
        if (id == 0) return false;
        if (id >= 1_000_000) return true;
        if (id is AA.Play1 or AA.Play2 or AA.Play3 or AA.MinorArcana or AA.AstralDraw or AA.UmbralDraw) id = OriginalHook(id);
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (W.Dead(W.Me, T)) return false;
        var usable = id switch
        {
            AA.Balance => Card1 == Card.Balance,
            AA.Spear => Card1 == Card.Spear,
            AA.Arrow => Card2 == Card.Arrow,
            AA.Bole => Card2 == Card.Bole,
            AA.Spire => Card3 == Card.Spire,
            AA.Ewer => Card3 == Card.Ewer,
            AA.Lord => Crown == Card.Lord,
            AA.Lady => Crown == Card.Lady,
            AA.Oracle => Has(AS.Divining),
            AA.SunSign => Has(AS.Suntouched),
            AA.HoroscopeHeal => Has(AS.Horoscope) || Has(AS.HoroscopeHelios),
            AA.Microcosmos => Has(AS.Macrocosmos),
            AA.StellarDetonation => StarOut,
            _ => MpOk(d),
        };
        if (!usable) return false;
        if (cds.TryGetValue(CdKey(id), out var cd) && cd.Charges(T) < 1) return false;
        return true;
    }

    // ---------------- targets ----------------

    private static readonly Dictionary<byte, int> CardPriority = new()
    {
        [34] = 1, [30] = 2, [22] = 3, [20] = 4, [41] = 5, [32] = 6, [39] = 7, [37] = 8, [19] = 9, [21] = 10,
        [42] = 11, [35] = 12, [27] = 13, [31] = 14, [23] = 15, [25] = 16, [38] = 17, [24] = 18, [40] = 19, [28] = 20,
    };

    public static bool MeleeOrTank(Actor a) => World.IsTank(a) || a.Job is 20 or 22 or 30 or 34 or 39 or 41 or 2 or 4 or 29;
    public static bool RangedOrHealer(Actor a) => !MeleeOrTank(a);
    public static bool Dps(Actor a) => !World.IsTank(a) && !World.IsHealer(a);

    /// <summary>
    ///     Wrath's AST.CardTarget: with a Play I card held, the living party members other than you within 30 y who have no
    ///     Balance or Spear (from anyone); none when there is at most one. Then by step: the card's role, not under damage
    ///     down or raise sickness; any DPS; anyone; ... ordered by Wrath's job priority, then max HP.
    /// </summary>
    public Actor? CardTarget()
    {
        if (Card1 is not (Card.Balance or Card.Spear)) return null;
        var party = W.Party.Where(p => !W.Dead(p, T) && p.Id != W.Me.Id && AllyDist(p) <= 30 && !HasAny(p, AS.Balance) && !HasAny(p, AS.Spear)).ToList();
        if (party.Count <= 1) return null;
        bool Dd(Actor p) => W.OthersHaveNamed(p, "Damage Down", T, out _);
        bool Sick(Actor p) => W.OthersHave(p, 43, T) || W.OthersHave(p, 44, T);
        bool Brink(Actor p) => W.OthersHave(p, 44, T);
        Func<Actor, bool>[] steps =
        [
            p => (Card1 == Card.Balance ? MeleeOrTank(p) : RangedOrHealer(p)) && !Dd(p) && !Sick(p),
            p => Dps(p) && !Dd(p) && !Sick(p),
            p => !Dd(p) && !Sick(p),
            p => !Sick(p),
            p => !Brink(p),
            _ => true,
        ];
        foreach (var step in steps)
        {
            var f = party.Where(step).ToList();
            if (f.Count == 0) continue;
            return f.OrderBy(p => CardPriority.GetValueOrDefault(p.Job, 255)).ThenByDescending(p => p.MaxHp).First();
        }
        return null;
    }

    public uint Retarget(uint action, uint trigger, RuleTargetSpec[] targets)
    {
        foreach (var spec in targets)
        {
            bool Ok(Actor? a) => a is not null && spec.Filters.All(f => (f.AnyShield ? Shielded(a) : HasAny(a, f.StatusId)) != f.Negate);
            IEnumerable<Actor> Others(Func<Actor, bool> role) => LivingParty().Where(p => role(p) && p.Id != W.Me.Id);
            Actor? Lowest(IEnumerable<Actor> l) => l.Where(Ok).MinBy(p => Hp(p));
            var c = spec.Kind switch
            {
                RuleTarget.Heal => HealTarget is { } h && Ok(h) ? h : null,
                RuleTarget.Lowest => Lowest(LivingParty()),
                RuleTarget.Tank => Lowest(Others(World.IsTank)),
                RuleTarget.Healer => Lowest(Others(World.IsHealer)),
                RuleTarget.Dps => Lowest(Others(Dps)),
                RuleTarget.TargetsTarget => TT is { } tt && Ok(tt) ? tt : null,
                RuleTarget.Self => Ok(W.Me) ? W.Me : null,
                RuleTarget.Raise => W.Party.FirstOrDefault(p => W.Dead(p, T)),
                RuleTarget.CastTarget => CastTargetAt(spec.Cast ?? "", p => Ok(p)),
                RuleTarget.Card => CardTarget() is { } ct && Ok(ct) ? ct : null,
                _ => null,
            };
            if (c is not null) { pendingTarget = c; return action; }
        }
        return 0;
    }

    private Actor? CastTargetAt(string cast, Func<Actor, bool> ok)
    {
        static string N(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var casts = W.R.Casts.Where(c => c.StartMs <= T && c.Source.Kind is ActorKind.Boss or ActorKind.Enemy or ActorKind.Helper && N(c.Name) == cast).ToList();
        if (casts.Count == 0) return null;
        var last = casts.Max(c => c.StartMs);
        var aimed = casts.Where(c => last - c.StartMs <= 1000 && c.Target is not null).Select(c => c.Target!.Id).ToHashSet();
        return LivingParty().FirstOrDefault(p => aimed.Contains(p.Id) && ok(p));
    }

    // ---------------- heals ----------------

    /// <summary> A heal landing (or a HoT tick): recorded, and checked against Phase 3's Accretions. </summary>
    private void AddHeal(int t, Actor tgt, double pot, string src)
    {
        if (W.Dead(tgt, t) || pot <= 0) return;
        Heals.Add((t, tgt, pot, src));
        if (W.OthersHave(tgt, AS.Accretion, t))
        {
            var e = W.LastLost(W.Party, AS.Accretion, t);
            AccretionHeals.Add((t, tgt, src, e < 0 ? 999 : (t - e) / 1000.0));
        }
    }

    /// <summary> Healing potency multiplier: Neutral Sect +20% on heal spells, the Arrow +10% received (yours or anyone's). </summary>
    private double HealMult(AstDef? d, Actor tgt) => (d is { HealSpell: true } && Has(AS.NeutralSect) ? 1.2 : 1) * (HasAny(tgt, AS.Arrow) ? 1.1 : 1);

    private void Barrier(Actor who, uint status, double potency)
    {
        if (st.TryGetValue((who.Id, status), out var old) && old.Expire > T) EndShield(who, status, old, T, "replaced");
        st[(who.Id, status)] = new SimStatus { Expire = T + 30000, From = T, Stacks = (int)Math.Round(potency) };
        BarrierLog.Add((T, who, status, potency));
    }

    private void EndShield(Actor who, uint status, SimStatus s, int at, string how)
    {
        st.Remove((who.Id, status));
        ShieldLog.Add((who, status, s.From, at, how));
    }

    private static readonly uint[] Barriers = [AS.NSShield, AS.Intersection, AS.Spire];

    // ---------------- the simulation step ----------------

    public void Advance(int dt)
    {
        T += dt;
        var me = W.Me;
        var dead = W.Dead(me, T);
        if (dead && !wasDead)
        {
            if (Cast is { } c) { Log.Add(new SimEvent(T, $"(cast lost: died) {c.Def.Name}", "", c.Rule)); Cast = null; Gcds--; }
            foreach (var k in st.Keys.Where(k => k.Item1 == me.Id).ToList()) st.Remove(k);
            dots.Clear();
            if (Card1 + Card2 + Card3 + Crown > 0) LostLog.Add((T, "died"));
            Card1 = Card2 = Card3 = Crown = 0;
            if (StarOut) { StarLog.Add((starAt, T, false, starGiant)); starAt = int.MinValue; }
            Log.Add(new SimEvent(T, "DIED", "", ""));
        }
        wasDead = dead;
        if (dead && T >= 0) DeadMs += dt;

        if (Moving(T)) { if (movingSince == int.MinValue) movingSince = T; lastMovingAt = T; }
        else movingSince = int.MinValue;

        // MP: 200 every 3 s, 550 more under Lucid Dreaming.
        if (T >= mpTickAt) { mpTickAt += 3000; if (!dead) Mp = Math.Min(10000, Mp + 200 + (Has(AS.Lucid) ? 550 : 0)); }

        // HoT ticks and delayed heals (Exaltation's end, Horoscope, Macrocosmos) on living targets.
        for (var i = pendingTicks.Count - 1; i >= 0; i--)
        {
            var pt = pendingTicks[i];
            if (pt.T > T) continue;
            pendingTicks.RemoveAt(i);
            AddHeal(pt.T, pt.Target, pt.Potency, pt.Src);
        }

        // Combust III ticks while the target can be hit.
        foreach (var (tid, d) in dots.ToList())
        {
            var next = d.Next;
            while (next <= T && next <= d.Expire)
            {
                if (d.Target.IsPresent(next) && d.Target.IsTargetable(next) && next >= 0) Damage.Add((next, "Combust III (tick)", 70, d.Mult));
                next += 3000;
            }
            if (next > d.Expire) dots.Remove(tid); else dots[tid] = d with { Next = next };
        }

        // Earthly Star: Giant Dominance after 10 s, the Stellar Explosion by itself at 20 s.
        if (StarOut)
        {
            if (!starGiant && T >= starAt + 10000) { starGiant = true; Drop(me, AS.EarthlyDominance); st[(me.Id, AS.GiantDominance)] = new SimStatus { Expire = starAt + 20000, From = T }; }
            if (T >= starAt + 20000) Detonate(false, "(auto)");
        }

        // Horoscope that runs out heals by itself (200; Horoscope Helios 400).
        foreach (var k in st.Where(kv => (kv.Key.Item2 is AS.Horoscope or AS.HoroscopeHelios) && kv.Value.Expire <= T).Select(kv => kv.Key).ToList())
        {
            if (W.Party.FirstOrDefault(p => p.Id == k.Item1) is { } who) AddHeal(st[k].Expire, who, k.Item2 == AS.HoroscopeHelios ? 400 : 200, "Horoscope (expired)");
            st.Remove(k);
        }
        // Macrocosmos runs out: the heal (200 + half the damage compiled).
        if (macroAt != int.MinValue && T >= macroAt + 15000) Micro("(expired)");

        // Hits on the party: barriers break on a non-auto hit or the second auto.
        while (hitIndex < W.HitsOnParty.Count && W.HitsOnParty[hitIndex].T <= T)
        {
            var (ht, tgt, auto, _, _) = W.HitsOnParty[hitIndex++];
            foreach (var sh in Barriers)
                if (st.TryGetValue((tgt.Id, sh), out var s) && s.Expire > ht && ht >= s.From + Metrics.LandMs)
                {
                    s.Hits++;
                    if (!auto || s.Hits >= 2) EndShield(tgt, sh, s, ht, "broken");
                }
        }
        foreach (var k in st.Where(kv => Barriers.Contains(kv.Key.Item2) && kv.Value.Expire <= T).Select(kv => kv.Key).ToList())
            if (W.Party.FirstOrDefault(p => p.Id == k.Item1) is { } who) EndShield(who, k.Item2, st[k], st[k].Expire, "expired");

        var incap = SamSim.Incapacitated(W, T);
        if (Cast is { } cast)
        {
            var reason = incap is not null ? $"interrupted ({incap})" : Moving(T) ? "moved" : null;
            if (reason is not null && T < cast.End - SlideMs)
            {
                Cancels++;
                Gcds--;
                CancelLog.Add((T, cast.Def.Name, cast.Rule + (incap is not null ? $" (interrupted: {incap})" : ""), T - cast.Start));
                Log.Add(new SimEvent(T, $"CANCEL {cast.Def.Name}", "", cast.Rule, reason == "moved" ? $"moved {(T - cast.Start) / 1000.0:0.0}s in" : reason));
                Cast = null;
                GcdReadyAt = T;
                LockUntil = T;
            }
            else if (T >= cast.End)
            {
                Cast = null;
                LockUntil = T + 100;
                var enemyAction = cast.Def.Potency > 0 && !cast.Def.SelfAoe;
                if (enemyAction && (cast.Target is null || !cast.Target.IsPresent(T) || !cast.Target.IsTargetable(T)))
                {
                    CastsLostTarget++;
                    Gcds--;
                    GcdReadyAt = T;
                    Log.Add(new SimEvent(T, $"(cast lost: target gone) {cast.Def.Name}", "", cast.Rule));
                }
                else Land(cast.Def, cast.Target, cast.Rule);
            }
        }

        var tgtNow = Target;
        if (T >= 0 && !dead && tgtNow is not null && incap is not null) IncapMs += dt;
        if (T >= 0 && !dead && tgtNow is not null && incap is null)
        {
            var phase = W.R.PhaseAt(T);
            var p = PhaseMs.GetValueOrDefault(phase);
            p.Avail += dt; AvailMs += dt;
            if (Cast is not null || T < GcdReadyAt) { p.Busy += dt; BusyMs += dt; }
            if (DotLeft(tgtNow) > 0) { p.Dot += dt; DotUpMs += dt; }
            PhaseMs[phase] = p;
        }
    }

    /// <summary> At the pull's end: barriers still up go to the log, the star and Macrocosmos as they stand. </summary>
    public void Finish()
    {
        foreach (var k in st.Where(kv => Barriers.Contains(kv.Key.Item2)).Select(kv => kv.Key).ToList())
            if (W.Party.FirstOrDefault(p => p.Id == k.Item1) is { } who) EndShield(who, k.Item2, st[k], Math.Min(st[k].Expire, T), "pull ended");
        if (StarOut) StarLog.Add((starAt, T, false, starGiant));
    }

    private void Detonate(bool manual, string how)
    {
        var giant = starGiant;
        var at = manual ? T : starAt + 20000;
        foreach (var b in W.Bosses.Where(b => b.IsPresent(at) && b.IsTargetable(at) && !b.IsHidden(at) && Vector2.Distance(W.Pos(b, at), starPos) <= 20 + b.Radius))
            if (at >= -3000) Damage.Add((Math.Max(0, at), giant ? "Stellar Explosion" : "Stellar Burst", giant ? 310 : 205, RaidBuffs(b).Mult));
        foreach (var p in W.Party.Where(p => !W.Dead(p, at) && Vector2.Distance(W.Pos(p, at), starPos) <= 20))
            AddHeal(at, p, (giant ? 720 : 540) * HealMult(null, p), giant ? "Stellar Explosion" : "Stellar Burst");
        StarLog.Add((starAt, at, manual, giant));
        Log.Add(new SimEvent(at, giant ? "Stellar Explosion" : "Stellar Burst", "", how));
        starAt = int.MinValue; starGiant = false;
        Drop(W.Me, AS.EarthlyDominance); Drop(W.Me, AS.GiantDominance);
    }

    /// <summary> Macrocosmos's heal: 200 + 50% of the damage each holder took to HP since it landed (the log's hits). </summary>
    private void Micro(string how)
    {
        var at = Math.Min(T, macroAt + 15000);
        foreach (var p in macroOn.Where(p => !W.Dead(p, at) && (how != "Microcosmos" || AllyDist(p) <= 20)))
        {
            var taken = W.R.Actions.Where(a => !a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet)
                .SelectMany(a => a.Hits.Where(h => h.Target.Id == p.Id && h.T >= macroAt + Metrics.LandMs && h.T <= at && h.Kind is HitKind.Damage or HitKind.Blocked or HitKind.Parried))
                .Sum(h => (double)h.Damage);
            AddHeal(at, p, 200 * HealMult(null, p) + 0.5 * taken / Math.Max(1, HpPerPot), "Macrocosmos (heal)");
            Drop(p, AS.Macrocosmos);
        }
        MacroLog.Add((macroAt, at, macroOn.Count));
        macroOn.Clear();
        macroAt = int.MinValue;
    }

    private void AddIdle(string reason, string rule)
    {
        if (T < 0 || W.Dead(W.Me, T) || Target is null) return;
        if (reason.StartsWith("oGCD animation lock")) ClipMs += 20;
        var phase = W.R.PhaseAt(T);
        if (Idle.Count > 0 && Idle[^1] is var last && last.Reason == reason && last.Rule == rule && last.T + last.Ms >= T - 20)
            Idle[^1] = last with { Ms = last.Ms + 20 };
        else Idle.Add((T, phase, reason, rule, 20));
    }

    public void Press(IReadOnlyList<CompiledRuleSet> files)
    {
        if (W.Dead(W.Me, T)) return;
        if (SamSim.Incapacitated(W, T) is not null) return;
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
        a = OriginalHook(a);

        var gcdFree = Cast is null && T >= GcdReadyAt;
        if (Trace && T >= TraceFrom && T <= TraceTo && (a, rule) != lastChoice)
        {
            lastChoice = (a, rule);
            Console.WriteLine($"   trace {SageSim.F(T)} -> {(Defs.TryGetValue(a, out var dd) ? dd.Name : a.ToString())} ({rule}) gcdRem={Math.Max(0, GcdReadyAt - T)} lock={Math.Max(0, LockUntil - T)} cast={(Cast is null ? "-" : Cast.Value.Def.Name)} moving={Moving(T)} tgt={Target?.Name ?? "none"} dist={Dist(Target):0.0} ready={ActionReady(a)} mp={Mp} cards={Card1},{Card2},{Card3},{Crown} next={(NextDraw == 0 ? "Astral" : "Umbral")} combust={DotLeft(Target):0.0} raid={RaidBuffs(Target).Count} raidIn={RaidIn():0}");
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
        var target = pendingTarget ?? (d.PartyTarget ? W.Me : Target);
        if (d.Kind == AKind.Ogcd)
        {
            if (gcdFree && T < LockUntil) AddIdle("oGCD animation lock past the GCD", rule);
            if (Cast is not null || T < LockUntil || !ActionReady(a)) return;
            if (d.PartyTarget && (target is null || !target.IsPlayer || W.Dead(target, T) || (target.Id != W.Me.Id && AllyDist(target) > d.Range))) return;
            if (d.Id == AA.Oracle && (target is null || target.IsPlayer || Dist(target) > d.Range)) return;
            Ogcds.Add((T, d.Name, rule, Math.Max(0, GcdReadyAt - T)));
            RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
            Land(d, d.PartyTarget ? target : d.Id is AA.EarthlyStar ? pendingTarget : Target, rule);
            LockUntil = T + 600;
            Weaves++;
            return;
        }
        // GCD
        if (!gcdFree) return;
        if (T < LockUntil) { AddIdle("oGCD animation lock past the GCD", rule); return; }
        if (!ActionReady(a)) { AddIdle($"not ready: {d.Name}", rule); return; }
        var enemyAction = d.Potency > 0 && !d.SelfAoe;
        if (enemyAction && (target is null || target.IsPlayer || Dist(target) > d.Range)) { AddIdle($"out of range: {d.Name}", rule); return; }
        if (d.PartyTarget && (target is null || !target.IsPlayer || (d.Id != AA.Ascend && W.Dead(target, T)) || (target.Id != W.Me.Id && AllyDist(target) > d.Range))) { AddIdle($"no target: {d.Name}", rule); return; }
        var castMs = CastTime(d);
        var swift = castMs > 0 && Has(AS.Swiftcast);
        if (castMs > 0 && !swift && Moving(T)) { AddIdle($"moving, cast held: {d.Name}", rule); return; }
        GcdReadyAt = T + GcdMs;
        LastGcdAt = T;
        Weaves = 0;
        Gcds++;
        RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
        if (castMs > 0 && !swift)
        {
            Cast = (d, T, T + castMs, target, rule);
            Log.Add(new SimEvent(T, $"cast> {d.Name}", target is { IsPlayer: true } && target.Id != W.Me.Id ? target.Name : "", rule));
        }
        else
        {
            if (swift) Drop(W.Me, AS.Swiftcast);
            LockUntil = T + 600;
            Land(d, target, rule);
        }
    }

    private void Land(AstDef d, Actor? target, string rule)
    {
        var me = W.Me;
        lastUse[d.Id] = T;
        if (cds.TryGetValue(CdKey(d.Id), out var cd)) cd.Use(T);
        if (d.Mp > 0) Mp -= d.Mp;   // taken when the cast completes; a cancelled cast costs nothing
        if (T >= 0) MinMp = Math.Min(MinMp, Mp);
        var note = "";
        var inDiv = Has(AS.Divination);
        void Heal(Actor? tgt, double pot, string src) { if (tgt is not null) AddHeal(T, tgt, pot * HealMult(d, tgt), src); }
        IEnumerable<Actor> Near(double r) => LivingParty(false).Where(p => AllyDist(p) <= r);
        void Hit(Actor? tgt, double pot, string src)
        {
            if (tgt is null || tgt.IsPlayer || T < -3000) return;
            Damage.Add((Math.Max(0, T), src, pot, RaidBuffs(tgt).Mult));
        }
        IEnumerable<Actor> Enemies(Actor? around, double r) => W.Bosses.Where(b => b.IsPresent(T) && b.IsTargetable(T) && !b.IsHidden(T)
            && (around is null ? W.Dist(W.Me, b, T) <= r : b == around || AroundTarget(around, b) <= r));
        void Hots(Actor p, int pot, uint status, string src)
        {
            Give(p, status, 15000);
            pendingTicks.RemoveAll(x => x.Target.Id == p.Id && x.Src == src);   // a new one replaces the old
            for (var k = 1; k <= 5; k++) pendingTicks.Add((T + 3000 * k, p, pot * HealMult(null, p), src));
        }
        void Upgrade(Actor p) { if (Has(p, AS.Horoscope)) { Drop(p, AS.Horoscope); Give(p, AS.HoroscopeHelios, 30000); } }
        void Synastry(double pot) { if (synastry is { } s && T < synastryUntil && !W.Dead(s, T)) AddHeal(T, s, 0.4 * pot, "Synastry"); }
        switch (d.Id)
        {
            case AA.FallMalefic: Hit(target, 270, d.Name); break;
            case AA.Gravity2:
                foreach (var e in Enemies(target, 8)) Hit(e, 140, e == target ? d.Name : d.Name + " (2nd target)");
                break;
            case AA.Combust3:
                if (target is not null && !target.IsPlayer)
                {
                    if (dots.TryGetValue(target.Id, out var old)) note = $"refreshed with {(old.Expire - T) / 1000.0:0.0}s left";
                    dots[target.Id] = (T + 30000, T + 1500, RaidBuffs(target).Mult, target, T);
                }
                break;
            case AA.Macrocosmos:
            {
                var first = true;
                foreach (var e in Enemies(null, 20).OrderBy(e => e == Target ? 0 : 1)) { Hit(e, first ? 270 : 162, first ? d.Name : d.Name + " (2nd target)"); first = false; }
                if (macroAt != int.MinValue) Micro("(replaced)");
                macroAt = T; macroOn.Clear();
                foreach (var p in Near(20)) { Give(p, AS.Macrocosmos, 15000); macroOn.Add(p); }
                ReachLog.Add((T, (int)d.Id, macroOn.Count, LivingParty(false).Count(), rule));
                break;
            }
            case AA.Microcosmos: if (macroAt != int.MinValue) Micro("Microcosmos"); break;
            case AA.HeliosConj:
                foreach (var p in Near(20))
                {
                    Heal(p, 250, d.Name);
                    Hots(p, 175, AS.HeliosConj, "Helios Conjunction (regen)");
                    if (Has(AS.NeutralSect)) Barrier(p, AS.NSShield, 250 * 1.2 * 1.25);
                    Upgrade(p);
                }
                break;
            case AA.Helios: foreach (var p in Near(20)) { Heal(p, 400, d.Name); Upgrade(p); } break;
            case AA.AspectedBenefic:
                if (target is null) break;
                Heal(target, 250, d.Name); Synastry(250 * HealMult(d, target));
                Hots(target, 250, AS.AspectedBenefic, "Aspected Benefic (regen)");
                if (Has(AS.NeutralSect)) Barrier(target, AS.NSShield, 250 * 1.2 * 2.5);
                note = $"{target.Name} at {Hp(target):0}%";
                break;
            case AA.Benefic2: Heal(target, 800, d.Name); Synastry(800); note = target is null ? "" : $"{target.Name} at {Hp(target):0}%"; break;
            case AA.Benefic: Heal(target, 500, d.Name); Synastry(500); break;
            case AA.Ascend: note = target?.Name ?? ""; break;
            case AA.Divination:
                Give(me, AS.Divination, 20000); Give(me, AS.Divining, 30000);
                foreach (var p in Near(30).Where(p => p.Id != me.Id)) Give(p, AS.Divination, 20000);
                BurstPresses.Add((T, d.Name, true));
                note = $"other raid buffs {W.RaidBuffs(Target, T).Count}";
                break;
            case AA.Lightspeed: Give(me, AS.Lightspeed, 15000); note = Moving(T) ? "moving" : ""; break;
            case AA.AstralDraw or AA.UmbralDraw:
            {
                var lost = new[] { Card1, Card2, Card3, Crown }.Where(c => c != 0).Select(Card.Name).ToList();
                if (lost.Count > 0 && T >= 0) { CardsLost += lost.Count; LostLog.Add((T, string.Join(", ", lost))); note = "overwrote " + string.Join(", ", lost); }
                Mp = Math.Min(10000, Mp + 2000);   // each draw restores 20% of max MP (the clear's MP: +2,000 at every draw)
                if (d.Id == AA.AstralDraw) { Card1 = Card.Balance; Card2 = Card.Arrow; Card3 = Card.Spire; Crown = Card.Lord; LastDraw = Card.Astral; NextDraw = Card.Umbral; }
                else { Card1 = Card.Spear; Card2 = Card.Bole; Card3 = Card.Ewer; Crown = Card.Lady; LastDraw = Card.Umbral; NextDraw = Card.Astral; }
                break;
            }
            case AA.Balance or AA.Spear:
                if (target is null) break;
                Give(target, d.Id == AA.Balance ? AS.Balance : AS.Spear, 15000);
                Card1 = 0;
                var right = d.Id == AA.Balance ? MeleeOrTank(target) : RangedOrHealer(target);
                CardPlays.Add((T, d.Name, target, right, inDiv));
                note = $"{target.Name} ({World.RoleOf(target)}{(right ? "" : ", wrong role")}){(inDiv ? " in Divination" : "")}";
                break;
            case AA.Arrow: if (target is not null) Give(target, AS.Arrow, 15000); Card2 = 0; CardPlays.Add((T, d.Name, target!, true, inDiv)); break;
            case AA.Bole: if (target is not null) Give(target, AS.Bole, 15000); Card2 = 0; CardPlays.Add((T, d.Name, target!, true, inDiv)); break;
            case AA.Spire: if (target is not null) Barrier(target, AS.Spire, 400 * HealMult(null, target)); Card3 = 0; CardPlays.Add((T, d.Name, target!, true, inDiv)); break;
            case AA.Ewer: if (target is not null) Hots(target, 200, AS.Ewer, "the Ewer"); Card3 = 0; CardPlays.Add((T, d.Name, target!, true, inDiv)); break;
            case AA.Lord:
                foreach (var e in Enemies(null, 20)) Hit(e, 400, e == Target ? d.Name : d.Name + " (2nd target)");
                Crown = 0;
                BurstPresses.Add((T, d.Name, inDiv));
                break;
            case AA.Lady: foreach (var p in Near(20)) Heal(p, 400, d.Name); Crown = 0; break;
            case AA.Oracle:
                foreach (var e in Enemies(target, 5)) Hit(e, e == target ? 860 : 430, e == target ? d.Name : d.Name + " (2nd target)");
                Drop(me, AS.Divining);
                BurstPresses.Add((T, d.Name, inDiv));
                break;
            case AA.EarthlyStar:
                if (StarOut) Detonate(true, "(replaced)");
                starPos = pendingTarget is { IsPlayer: false } ground ? W.Pos(ground, T) : W.Pos(me, T);
                if (pendingTarget is null && Target is { } bt) starPos = W.Pos(bt, T);
                starAt = T; starGiant = false;
                Give(me, AS.EarthlyDominance, 10000);
                note = $"{(pendingTarget is { IsPlayer: true } ? "at you" : "at the target")}, boss {Target?.Name ?? "-"} {(Target is { } tb ? Vector2.Distance(W.Pos(tb, T), starPos) : 0):0}y away";
                break;
            case AA.StellarDetonation: if (StarOut) Detonate(true, rule); break;
            case AA.EssentialDignity:
                if (target is null) break;
                var hp = Hp(target);
                Heal(target, 400 + 500 * Math.Clamp((100 - hp) / 70.0, 0, 1), d.Name);
                note = $"{target.Name} at {hp:0}%";
                break;
            case AA.CelestialIntersection:
                if (target is null) break;
                Heal(target, 200, d.Name);
                Barrier(target, AS.Intersection, 400 * HealMult(null, target));
                note = $"{target.Name} at {Hp(target):0}%";
                break;
            case AA.Exaltation:
                if (target is null) break;
                Give(target, AS.Exaltation, 8000);
                pendingTicks.Add((T + 8000, target, 500 * HealMult(null, target), "Exaltation"));
                note = $"{target.Name} ({World.RoleOf(target)})";
                break;
            case AA.CollectiveUnconscious:
                ReachLog.Add((T, (int)d.Id, Near(30).Count(), LivingParty(false).Count(), rule));
                foreach (var p in Near(30)) Give(p, AS.CollectiveUnconscious, 10000);
                foreach (var p in Near(8)) Hots(p, 100, AS.WheelOfFortune, "Wheel of Fortune");
                break;
            case AA.CelestialOpposition:
                foreach (var p in Near(20)) { Heal(p, 200, d.Name); Hots(p, 100, AS.Opposition, "Opposition (regen)"); }
                break;
            case AA.Horoscope: foreach (var p in Near(30)) Give(p, AS.Horoscope, 10000); break;
            case AA.HoroscopeHeal:
                foreach (var p in Near(30))
                {
                    if (Has(p, AS.HoroscopeHelios)) { Heal(p, 400, "Horoscope Helios"); Drop(p, AS.HoroscopeHelios); }
                    else if (Has(p, AS.Horoscope)) { Heal(p, 200, "Horoscope"); Drop(p, AS.Horoscope); }
                }
                foreach (var k in st.Keys.Where(k => (k.Item2 is AS.Horoscope or AS.HoroscopeHelios)).ToList()) st.Remove(k);   // read: ends for everyone
                break;
            case AA.NeutralSect: Give(me, AS.NeutralSect, 20000); Give(me, AS.Suntouched, 30000); break;
            case AA.SunSign:
                ReachLog.Add((T, (int)d.Id, Near(30).Count(), LivingParty(false).Count(), rule));
                Drop(me, AS.Suntouched);
                foreach (var p in Near(30)) Give(p, AS.SunSign, 15000);
                break;
            case AA.Synastry: if (target is not null) { synastry = target; synastryUntil = T + 20000; Give(target, AS.TargetSynastry, 20000); Give(me, AS.SelfSynastry, 20000); } break;
            case AA.Swiftcast: Give(me, AS.Swiftcast, 10000); break;
            case AA.Lucid: Give(me, AS.Lucid, 21000); break;
        }
        Log.Add(new SimEvent(T, d.Name, target is null || target.Id == me.Id || !target.IsPlayer ? "" : target.Name, rule, note));
    }

    public static string F(int ms) => SageSim.F(ms);

    private void UpdateRaidWindow()
    {
        if (T < 0) { windowStart = null; inWindow = false; return; }
        var up = RaidBuffs(Target).Count > 0;
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

    public double StoodStill => Moving(T) ? 0 : (lastMovingAt == int.MinValue ? 999 : (T - lastMovingAt) / 1000.0);

    public double Cd(uint id)
    {
        id = CdKey(OriginalHook(id));
        if (cds.TryGetValue(id, out var c)) return c.Remaining(T);
        return Defs.TryGetValue(id, out var d) && d.Kind == AKind.Gcd ? Math.Max(0, GcdReadyAt - T) / 1000.0 : 0;
    }

    public double Charges(uint id)
    {
        id = CdKey(OriginalHook(id));
        return cds.TryGetValue(id, out var c) ? c.Charges(T) : ActionReady(id) ? 1 : 0;
    }

    public double ChargeCd(uint id)
    {
        id = CdKey(OriginalHook(id));
        return cds.TryGetValue(id, out var c) ? c.ChargeRemaining(T) : 0;
    }

    public bool JustUsed(uint id, double secs) => lastUse.TryGetValue(id, out var t) && T - t <= secs * 1000
        || (id is AA.Play1 or AA.Play2 or AA.Play3 or AA.MinorArcana) && lastUse.Where(kv => kv.Key is AA.Balance or AA.Spear or AA.Arrow or AA.Bole or AA.Spire or AA.Ewer or AA.Lord or AA.Lady).Any(kv => T - kv.Value <= secs * 1000);

    public bool InRange(uint id)
    {
        id = OriginalHook(id);
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (d.Range == 0 || d.PartyTarget) return true;
        var t = Target;
        return t is not null && Dist(t) <= d.Range;
    }

    /// <summary> For a circle centred on the target: from the target's centre to the other enemy's hitbox edge. </summary>
    private double AroundTarget(Actor around, Actor b) => Math.Max(0, Vector2.Distance(W.Pos(around, T), W.Pos(b, T)) - b.Radius);

    /// <summary> Enemies an action would hit: Gravity II within 8 y of the target, Oracle 5 y, Lord of Crowns, Macrocosmos and
    /// Earthly Star 20 y around you; anything else the target. </summary>
    public List<Actor> EnemiesFor(uint id)
    {
        id = OriginalHook(id);
        var t = Target;
        IEnumerable<Actor> live = W.Bosses.Where(b => b.IsPresent(T) && b.IsTargetable(T) && !b.IsHidden(T));
        return id switch
        {
            AA.Gravity2 => t is null ? [] : live.Where(b => b == t || AroundTarget(t, b) <= 8).ToList(),
            AA.Oracle => t is null ? [] : live.Where(b => b == t || AroundTarget(t, b) <= 5).ToList(),
            AA.Lord or AA.Macrocosmos or AA.EarthlyStar or AA.StellarDetonation => live.Where(b => W.Dist(W.Me, b, T) <= 20).ToList(),
            _ => t is null ? [] : [t],
        };
    }

    // ---------------- gauge fields (Dalamud's ASTGauge) ----------------

    public Func<double>? Gauge(string field) => field.ToLowerInvariant() switch
    {
        "card1" => () => Card1,
        "card2" => () => Card2,
        "card3" => () => Card3,
        "drawncrowncard" => () => Crown,
        // Unconfirmed in game: taken as the deck the cards in hand came from (Astral 0 after Astral Draw). hook(AstralDraw)
        // is the safe way to ask which draw is next.
        "activedraw" => () => LastDraw < 0 ? Card.Astral : LastDraw,
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
        // the rules' own Divination counts as a raid buff (World only sees everyone else's)
        Val("raidBuffs", () => RaidBuffs(Target).Count);
        Val("raidLeft", () => RaidBuffs(Target).Left);

        F("ready", Act, 1, a => { var id = a[0].Id; return () => B(ActionReady(OriginalHook(id))); });
        F("cd", Act, 1, a => { var id = a[0].Id; return () => Cd(id); });
        F("charges", Act, 1, a => { var id = a[0].Id; return () => Charges(id); });
        F("chargeCd", Act, 1, a => { var id = a[0].Id; return () => ChargeCd(id); });
        F("justUsed", [ParamKind.Action, ParamKind.Number], 1, a => { var id = a[0].Id; var s = a[1].Value; return () => B(JustUsed(id, s?.Invoke() ?? 3)); });
        F("inRange", Act, 1, a => { var id = a[0].Id; return () => B(InRange(id)); });
        F("combo", Act, 1, _ => () => 0);
        F("enemies", Act, 1, a => { var id = a[0].Id; return () => EnemiesFor(id).Count; });
        F("enemiesHp", Act, 1, a => { var id = a[0].Id; return () => EnemiesFor(id).Select(e => W.HpPct(e, T)).DefaultIfEmpty(100).Average(); });
        F("hook", Act, 1, a => { var id = a[0].Id; return () => OriginalHook(id); });
        F("buff", St, 1, a => { var id = a[0].Id; return () => B(Has(id)); });
        F("buffTime", St, 1, a => { var id = a[0].Id; return () => Left(W.Me, id); });
        F("buffStacks", St, 1, a => { var id = a[0].Id; return () => Stacks(W.Me, id); });
        F("buffAny", St, 1, a => { var id = a[0].Id; return () => B(Has(id) || W.OthersHave(W.Me, id, T)); });
        F("buffAnyTime", St, 1, a => { var id = a[0].Id; return () => Math.Max(Left(W.Me, id), W.OthersTime(W.Me, id, T)); });
        F("debuff", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(id == AS.Combust3 ? DotLeft(t) > 0 : Has(t, id)) : 0; });
        F("debuffTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? (id == AS.Combust3 ? DotLeft(t) : Left(t, id)) : 0; });
        F("debuffAny", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(HasAny(t, id)) : 0; });
        F("debuffAnyTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? Math.Max(id == AS.Combust3 ? DotLeft(t) : Left(t, id), W.OthersTime(t, id, T)) : 0; });
        Val("gcd", () => GcdMs / 1000.0);
        Val("remainingGcd", () => Math.Max(0, GcdReadyAt - T) / 1000.0);
        Val("canWeave", () => B(CanWeave()));
        Val("moving", () => B(Moving(T)));
        Val("stoodStill", () => StoodStill);
        Val("movingTime", () => movingSince == int.MinValue ? 0 : (T - movingSince) / 1000.0);
        Val("sinceGcd", () => LastGcdAt == int.MinValue ? 99 : (T - LastGcdAt) / 1000.0);
        Val("inMelee", () => Target is { } t ? B(Dist(t) <= 3) : 0);
        Val("distance", () => Target is { } t ? Dist(t) : 0);
        foreach (var n in new[] { "rear", "flank", "front", "needsPositionals", "comboTimer", "comboAction", "raidwide", "stackMarker", "tankbuster", "canEsuna", "healCleansable", "partnerNonOptimal" })
            Val(n, () => 0);
        Val("countdown", () => T < 0 ? -T / 1000.0 : 0);
        Val("raidwideIn", () => 99);
        Val("raidIn", RaidIn);
        Val("mp", () => Mp);
        Val("casting", () => B(Cast is not null));
        Val("partyAvgHp", () => LivingParty(false).Select(p => Hp(p)).DefaultIfEmpty(100).Average());
        Val("partyLowestHp", () => LivingParty().Select(p => Hp(p)).DefaultIfEmpty(100).Min());
        F("partyBelow", [ParamKind.Number], 1, a => { var n = a[0].Value!; return () => LivingParty().Count(p => Hp(p) <= n()); });
        Val("partyAlive", () => LivingParty(false).Count());
        Val("partyDead", () => W.Party.Count(p => W.Dead(p, T)));
        F("partyWith", St, 1, a => { var id = a[0].Id; return () => { var l = LivingParty(false).ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p => HasAny(p, id)) / l.Count; }; });
        F("partyLost", St, 1, a => { var id = a[0].Id; return () => { var e = W.LastLost(W.Party, id, T); return e < 0 ? 999 : (T - e) / 1000.0; }; });
        F("partyBeyond", [ParamKind.Number], 1, a => { var y = a[0].Value!; return () => LivingParty(false).Count(p => p.Id != W.Me.Id && AllyDist(p) > y() + 0.5); });
        Val("partyInCombat", () => B(T >= 0));
        Val("tankHp", () => LivingParty().Where(World.IsTank).Select(p => Hp(p)).DefaultIfEmpty(100).Min());
        F("tankHas", St, 1, a => { var id = a[0].Id; return () => B(LivingParty(false).Any(p => World.IsTank(p) && HasAny(p, id))); });
        Val("healHp", () => HealTarget is { } h ? Hp(h) : 100);
        Val("healHpShielded", () => HealTarget is { } h ? Hp(h) : 100);
        Val("healIsTank", () => B(HealTarget is { } h && World.IsTank(h)));
        Val("healIsSelf", () => B(HealTarget?.Id == W.Me.Id));
        F("healHas", St, 1, a => { var id = a[0].Id; return () => B(HealTarget is { } h && HasAny(h, id)); });
        F("healTime", St, 1, a => { var id = a[0].Id; return () => HealTarget is { } h ? Math.Max(Left(h, id), W.OthersTime(h, id, T)) : 0; });
        Val("healDistance", () => HealTarget is { } h ? W.Dist(W.Me, h, T) : 0);
        Val("ttIsParty", () => B(TT is not null));
        Val("ttIsTank", () => B(TT is { } t && World.IsTank(t)));
        Val("ttIsSelf", () => B(TT?.Id == W.Me.Id));
        Val("ttHp", () => TT is { } t ? Hp(t) : 100);
        F("ttHas", St, 1, a => { var id = a[0].Id; return () => B(TT is { } t && HasAny(t, id)); });
        Val("canRaise", () => B(W.Party.Any(p => W.Dead(p, T))));
        Val("raiseDeadFor", () => W.R.Deaths.Where(d => d.T <= T && (d.RaisedMs < 0 || T < d.RaisedMs)).Select(d => (T - d.T) / 1000.0).DefaultIfEmpty(0).Max());
        Val("coHealerAlive", () => B(W.Party.Any(p => World.IsHealer(p) && p.Id != W.Me.Id && !W.Dead(p, T))));
        Val("selfShielded", () => B(Shielded(W.Me)));
        Val("tanksUnshielded", () => LivingParty().Count(p => World.IsTank(p) && p.Id != W.Me.Id && !Shielded(p)));
        Val("healersUnshielded", () => LivingParty().Count(p => World.IsHealer(p) && p.Id != W.Me.Id && !Shielded(p)));
        Val("dpsUnshielded", () => LivingParty().Count(p => !World.IsTank(p) && !World.IsHealer(p) && !Shielded(p)));
        Val("partyUnshielded", () => LivingParty().Count(p => !Shielded(p)));
        Val("button", () => Trigger);
        Val("buttonType", () => 1);
        return f;
    }
}
