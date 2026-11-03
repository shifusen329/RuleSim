using System.Numerics;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;
using static SharedBuiltins;

/// <summary> Dancer action ids (DNC_Helper.cs; Improvised Finish from the game data). </summary>
internal static class DA
{
    public const uint Cascade = 15989, Fountain = 15990, ReverseCascade = 15991, Fountainfall = 15992, Windmill = 15993, Bladeshower = 15994,
        RisingWindmill = 15995, Bloodshower = 15996, StandardStep = 15997, TechnicalStep = 15998, Emboite = 15999, Entrechat = 16000,
        Jete = 16001, Pirouette = 16002, StandardFinish0 = 16003, StandardFinish1 = 16191, StandardFinish2 = 16192, TechnicalFinish0 = 16004,
        TechnicalFinish1 = 16193, TechnicalFinish2 = 16194, TechnicalFinish3 = 16195, TechnicalFinish4 = 16196, SaberDance = 16005,
        ClosedPosition = 16006, FanDance1 = 16007, FanDance2 = 16008, FanDance3 = 16009, EnAvant = 16010, Devilment = 16011, ShieldSamba = 16012,
        Flourish = 16013, Improvisation = 16014, ImprovisedFinish = 25789, CuringWaltz = 16015, Tillana = 25790, FanDance4 = 25791,
        StarfallDance = 25792, LastDance = 36983, FinishingMove = 36984, DanceOfTheDawn = 36985, Ending = 18073, Peloton = 7557,
        SecondWind = 7541, HeadGraze = 7551, ArmsLength = 7548, Wait = 1000004;
    public static readonly uint[] Steps = [Emboite, Entrechat, Jete, Pirouette];
    public static readonly uint[] StdFinishes = [StandardFinish0, StandardFinish1, StandardFinish2];
    public static readonly uint[] TechFinishes = [TechnicalFinish0, TechnicalFinish1, TechnicalFinish2, TechnicalFinish3, TechnicalFinish4];
}

/// <summary> Dancer statuses (DNC_Helper.cs Buffs and the game data). </summary>
internal static class DS
{
    public const uint StandardStep = 1818, TechnicalStep = 1819, ThreeFold = 1820, StandardFinish = 1821, TechnicalFinish = 1822, ClosedPosition = 1823,
        Partner = 1824, Devilment = 1825, ShieldSamba = 1826, Improvisation = 1827, SilkenSymmetry = 2693, SilkenFlow = 2694, ImprovisedFinish = 2697,
        FlourishingFinish = 2698, FourFold = 2699, FlourishingStarfall = 2700, FlourishingSymmetry = 3017, FlourishingFlow = 3018, LastDanceReady = 3867,
        FinishingMoveReady = 3868, DanceOfTheDawnReady = 3869, PartnerStandardFinish = 2105, EspritPartner = 1847, EspritParty = 1848, Peloton = 1199;
    public static readonly uint[] Procs = [SilkenSymmetry, SilkenFlow, FlourishingSymmetry, FlourishingFlow, ThreeFold, FourFold, LastDanceReady,
        FinishingMoveReady, DanceOfTheDawnReady, FlourishingStarfall, FlourishingFinish];
    public static string Name(uint s) => s switch
    {
        SilkenSymmetry => "Silken Symmetry", SilkenFlow => "Silken Flow", FlourishingSymmetry => "Flourishing Symmetry", FlourishingFlow => "Flourishing Flow",
        ThreeFold => "Threefold Fan Dance", FourFold => "Fourfold Fan Dance", LastDanceReady => "Last Dance Ready", FinishingMoveReady => "Finishing Move Ready",
        DanceOfTheDawnReady => "Dance of the Dawn Ready", FlourishingStarfall => "Flourishing Starfall", FlourishingFinish => "Flourishing Finish", _ => s.ToString(),
    };
}

/// <summary> An action: kind, the GCD it rolls (0 = the weaponskill GCD), cooldown, potency (level 100, 7.5), range. </summary>
internal sealed record DDef(uint Id, string Name, AKind Kind, int GcdMs = 0, int CooldownMs = 0, int Charges = 1, int Potency = 0, double Range = 25,
    bool SelfAoe = false, bool NeedsTarget = true);

/// <summary>
///     The simulated Dancer (2026-10-23): the rules choose its actions; position, target, hits, deaths, enemy casts, the party's
///     weaponskills and spells, and other players' buffs come from the logged Dancer (World.Me) and the pull. Procs are
///     rolled (50%), Esprit from the party under the simulated Technical Finish, and from the partner under the simulated
///     Standard Finish, at EspritRate per weaponskill/spell (measured 0.17 on the user's own gauge; the guides say ~0.2).
/// </summary>
internal sealed class DncSim : IRuleRuntime
{
    public readonly World W;
    public int T;
    public readonly int GcdMs;
    public uint Trigger = DA.Cascade;
    public readonly Random Rng;
    public double EspritRate = 0.18;
    public Actor? PartnerActor;

    // gauge
    public int Feathers, Esprit;
    public uint Dance;              // 0, DS.StandardStep or DS.TechnicalStep
    public int StepsDone;
    public uint[] StepOrder = [];
    public uint NextStep => Dance != 0 && StepsDone < StepOrder.Length ? StepOrder[StepsDone] : 15998u;

    // combo
    public uint ComboAction;
    public int ComboUntil = int.MinValue;
    public double ComboTimer => ComboUntil > T ? (ComboUntil - T) / 1000.0 : 0;

    // timing
    public int GcdReadyAt = -100_000, LockUntil = -100_000, LastGcdAt = int.MinValue, Weaves, CurrentGcdMs = 2500;
    private readonly Dictionary<uint, SamCd> cds = new();
    private readonly Dictionary<uint, int> lastUse = new();
    private readonly Dictionary<uint, (int Expire, int Stacks)> self = new();
    private bool wasDead;
    private int? windowStart;
    private bool inWindow;
    private Actor? pendingTarget;
    private (uint, string) lastChoice;

    // the sim's buffs on others: (actor, until)
    private readonly Dictionary<uint, int> techOn = new(), devOnPartner = new(), sfOnPartner = new();
    public readonly List<(int Start, int End, List<Actor> Who)> TechWindows = new();
    public readonly List<(int Start, int End)> DevWindows = new(), SfWindows = new();
    private readonly List<(int T, Actor Who)> partyGcds;
    private int partyIdx;

    // results
    public readonly List<SimEvent> Log = new();
    public readonly List<(int T, string Name, double Potency, double Mult)> Damage = new();
    public readonly Dictionary<string, int> RuleFires = new();
    public readonly Dictionary<string, int> Expired = new();
    public readonly List<(int T, string Phase, string Reason, string Rule, int Ms)> Idle = new();
    public int Gcds, Unmodelled, FeathersLost, EspritLost, EspritFromParty, AvailMs, BusyMs, DeadMs;
    public readonly List<(int T, string What)> Mit = new();
    public readonly List<(int T, List<string> Gcds)> Bursts = new();
    public bool Trace; public int TraceFrom, TraceTo;

    public static readonly Dictionary<uint, DDef> Defs = new DDef[]
    {
        new(DA.Cascade, "Cascade", AKind.Gcd, Potency: 220),
        new(DA.Fountain, "Fountain", AKind.Gcd, Potency: 280),
        new(DA.ReverseCascade, "Reverse Cascade", AKind.Gcd, Potency: 280),
        new(DA.Fountainfall, "Fountainfall", AKind.Gcd, Potency: 340),
        new(DA.Windmill, "Windmill", AKind.Gcd, Potency: 120, Range: 5, SelfAoe: true),
        new(DA.Bladeshower, "Bladeshower", AKind.Gcd, Potency: 160, Range: 5, SelfAoe: true),
        new(DA.RisingWindmill, "Rising Windmill", AKind.Gcd, Potency: 160, Range: 5, SelfAoe: true),
        new(DA.Bloodshower, "Bloodshower", AKind.Gcd, Potency: 200, Range: 5, SelfAoe: true),
        new(DA.SaberDance, "Saber Dance", AKind.Gcd, Potency: 540),
        new(DA.DanceOfTheDawn, "Dance of the Dawn", AKind.Gcd, Potency: 1000),
        new(DA.StarfallDance, "Starfall Dance", AKind.Gcd, Potency: 600),
        new(DA.Tillana, "Tillana", AKind.Gcd, Potency: 600, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.LastDance, "Last Dance", AKind.Gcd, Potency: 540),
        new(DA.FinishingMove, "Finishing Move", AKind.Gcd, CooldownMs: 30000, Potency: 850, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.StandardStep, "Standard Step", AKind.Gcd, GcdMs: 1500, CooldownMs: 30000, Range: 0, NeedsTarget: false),
        new(DA.TechnicalStep, "Technical Step", AKind.Gcd, GcdMs: 1500, CooldownMs: 120000, Range: 0, NeedsTarget: false),
        new(DA.Emboite, "Emboite", AKind.Gcd, GcdMs: 1000, Range: 0, NeedsTarget: false),
        new(DA.Entrechat, "Entrechat", AKind.Gcd, GcdMs: 1000, Range: 0, NeedsTarget: false),
        new(DA.Jete, "Jete", AKind.Gcd, GcdMs: 1000, Range: 0, NeedsTarget: false),
        new(DA.Pirouette, "Pirouette", AKind.Gcd, GcdMs: 1000, Range: 0, NeedsTarget: false),
        new(DA.StandardFinish0, "Standard Finish", AKind.Gcd, GcdMs: 1500, Potency: 360, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.StandardFinish1, "Single Standard Finish", AKind.Gcd, GcdMs: 1500, Potency: 540, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.StandardFinish2, "Double Standard Finish", AKind.Gcd, GcdMs: 1500, Potency: 850, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.TechnicalFinish0, "Technical Finish", AKind.Gcd, GcdMs: 1500, Potency: 350, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.TechnicalFinish1, "Single Technical Finish", AKind.Gcd, GcdMs: 1500, Potency: 540, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.TechnicalFinish2, "Double Technical Finish", AKind.Gcd, GcdMs: 1500, Potency: 720, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.TechnicalFinish3, "Triple Technical Finish", AKind.Gcd, GcdMs: 1500, Potency: 900, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.TechnicalFinish4, "Quadruple Technical Finish", AKind.Gcd, GcdMs: 1500, Potency: 1300, Range: 15, SelfAoe: true, NeedsTarget: false),
        new(DA.FanDance1, "Fan Dance", AKind.Ogcd, CooldownMs: 1000, Potency: 180),
        new(DA.FanDance2, "Fan Dance II", AKind.Ogcd, CooldownMs: 1000, Potency: 100, Range: 5, SelfAoe: true),
        new(DA.FanDance3, "Fan Dance III", AKind.Ogcd, CooldownMs: 1000, Potency: 220),
        new(DA.FanDance4, "Fan Dance IV", AKind.Ogcd, CooldownMs: 1000, Potency: 460, Range: 15),
        new(DA.Devilment, "Devilment", AKind.Ogcd, CooldownMs: 120000, Range: 0, NeedsTarget: false),
        new(DA.Flourish, "Flourish", AKind.Ogcd, CooldownMs: 60000, Range: 0, NeedsTarget: false),
        new(DA.ShieldSamba, "Shield Samba", AKind.Ogcd, CooldownMs: 90000, Range: 0, NeedsTarget: false),
        new(DA.Improvisation, "Improvisation", AKind.Ogcd, CooldownMs: 120000, Range: 0, NeedsTarget: false),
        new(DA.ImprovisedFinish, "Improvised Finish", AKind.Ogcd, CooldownMs: 1500, Range: 0, NeedsTarget: false),
        new(DA.CuringWaltz, "Curing Waltz", AKind.Ogcd, CooldownMs: 60000, Range: 0, NeedsTarget: false),
        new(DA.ClosedPosition, "Closed Position", AKind.Ogcd, CooldownMs: 30000, Range: 0, NeedsTarget: false),
        new(DA.Ending, "Ending", AKind.Ogcd, CooldownMs: 1000, Range: 0, NeedsTarget: false),
        new(DA.Peloton, "Peloton", AKind.Ogcd, CooldownMs: 5000, Range: 0, NeedsTarget: false),
        new(DA.EnAvant, "En Avant", AKind.Ogcd, CooldownMs: 30000, Charges: 3, Range: 0, NeedsTarget: false),
        new(DA.SecondWind, "Second Wind", AKind.Ogcd, CooldownMs: 120000, Range: 0, NeedsTarget: false),
        new(DA.Wait, "Wait", AKind.Wait),
    }.ToDictionary(d => d.Id);

    // Standard Step and Finishing Move share a recast (group 7): one SamCd under StandardStep's id.
    private uint CdKey(uint id) => id == DA.FinishingMove ? DA.StandardStep : id;

    public DncSim(World w, int gcdMs, int seed, HashSet<uint> gcdActions)
    {
        W = w;
        GcdMs = gcdMs;
        Rng = new Random(seed);
        foreach (var d in Defs.Values.Where(d => d.CooldownMs > 0 && d.Id != DA.FinishingMove)) cds[d.Id] = new SamCd(d.CooldownMs, d.Charges);
        partyGcds = w.R.Actions.Where(a => a.Source.IsPlayer && a.Source.Id != w.Me.Id && w.Party.Any(p => p.Id == a.Source.Id) && gcdActions.Contains(a.ActionId))
            .Select(a => (a.T, a.Source)).OrderBy(x => x.T).ToList();
        PartnerActor = Partner(w);
        if (PartnerActor is not null) self[DS.ClosedPosition] = (int.MaxValue, 0);
    }

    /// <summary> The logged Dancer's partner: its last Closed Position's target, else whoever carried its Dance Partner. </summary>
    public static Actor? Partner(World w)
    {
        // Whoever held Dance Partner longest in the pull (a Dancer can re-partner mid-fight: FFLogs' Skye Uwu had a Dragoon
        // for the opener, then a Samurai), else the last Closed Position's target.
        var end = w.R.EndMs;
        var longest = w.R.Statuses.Where(s => s.StatusId == DS.Partner && s.Source?.Id == w.Me.Id && s.Target is { IsPlayer: true } t && t.Id != w.Me.Id)
            .GroupBy(s => s.Target!.Id)
            .Select(g => (Who: g.First().Target!, Ms: g.Sum(s => Math.Max(0, Math.Min(s.EndMs < 0 ? end : s.EndMs, end) - Math.Max(s.StartMs, 0)))))
            .OrderByDescending(x => x.Ms).FirstOrDefault();
        if (longest.Who is not null && longest.Ms > 0) return longest.Who;
        var cp = w.R.Actions.LastOrDefault(a => a.Source.Id == w.Me.Id && a.ActionId == DA.ClosedPosition);
        return cp?.PrimaryTarget is { IsPlayer: true } p && p.Id != w.Me.Id ? p
            : w.R.Statuses.FirstOrDefault(s => s.StatusId == DS.Partner && s.Source?.Id == w.Me.Id)?.Target;
    }

    // ---------------- state helpers ----------------

    public bool Has(uint status) => self.TryGetValue(status, out var s) && s.Expire > T;
    public double Left(uint status) => self.TryGetValue(status, out var s) && s.Expire > T ? Math.Min(999, (s.Expire - T) / 1000.0) : 0;
    private void Give(uint status, int ms) => self[status] = (T + ms, 0);
    private void Drop(uint status) => self.Remove(status);

    private List<(int T, Actor Target)>? hitsCache;

    /// <summary> SamSim.TargetAt's rule (the logged Dancer's last damaging action's primary target within 15 s, else the biggest targetable boss), per instance. </summary>
    public Actor? Target
    {
        get
        {
            if (hitsCache is null)
            {
                hitsCache = new();
                Actor? last = null;
                foreach (var a in W.R.Actions.Where(a => a.Source.Id == W.Me.Id).OrderBy(a => a.T))
                {
                    var damaged = a.Hits.Where(h => h.Kind == HitKind.Damage && h.Damage > 0 && !h.Target.IsPlayer).ToList();
                    if (damaged.Count == 0) continue;
                    var tgt = last is not null && damaged.Any(h => h.Target == last) ? last
                        : a.PrimaryTarget is { IsPlayer: false } p && damaged.Any(h => h.Target == p) ? p : damaged[0].Target;
                    hitsCache.Add((damaged[0].T, tgt));
                    last = tgt;
                }
            }
            int lo = 0, hi = hitsCache.Count - 1, ans = -1;
            while (lo <= hi) { var mid = (lo + hi) / 2; if (hitsCache[mid].T <= T) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
            if (ans >= 0 && hitsCache[ans].T >= T - 15000 && hitsCache[ans].Target is var lt && lt.IsPresent(T) && lt.IsTargetable(T)) return lt;
            return W.Bosses.Where(b => b.IsPresent(T) && b.IsTargetable(T) && !b.IsHidden(T)).MaxBy(b => b.MaxHp);
        }
    }

    public double Dist(Actor? t) => t is null ? 99 : W.Dist(W.Me, t, T);

    /// <summary> Enemies (bosses and adds, present and targetable) within r yalms of the Dancer. </summary>
    public int EnemiesWithin(double r) => W.R.Actors.Count(a => a.Kind is ActorKind.Boss or ActorKind.Enemy && a.MaxHp > 0 && a.IsPresent(T) && a.IsTargetable(T) && !a.IsHidden(T) && W.Dist(W.Me, a, T) <= r);

    private List<int>? knockbacks;
    public string? Incapacitated()
    {
        foreach (var n in SamSim.Incapacitating)
            if (W.OthersHaveNamed(W.Me, n, T, out _)) return n;
        knockbacks ??= W.R.Actions.SelectMany(a => a.Hits).Where(h => h.Target.Id == W.Me.Id && h.Knockback).Select(h => h.T).OrderBy(x => x).ToList();
        var i = knockbacks.BinarySearch(T); if (i < 0) i = ~i - 1;
        return i >= 0 && T < knockbacks[i] + 1000 ? "knockback" : null;
    }

    // ---------------- IRuleRuntime ----------------

    public bool CanWeave()
    {
        var lockRem = Math.Max(0, LockUntil - T);
        var gcdRem = Math.Max(0, GcdReadyAt - T);
        return Weaves < 2 && gcdRem > 600 + lockRem;
    }

    public uint OriginalHook(uint id) => id switch
    {
        DA.StandardStep => Dance == DS.StandardStep ? DA.StdFinishes[Math.Min(StepsDone, 2)] : Has(DS.FinishingMoveReady) ? DA.FinishingMove : DA.StandardStep,
        DA.TechnicalStep => Dance == DS.TechnicalStep ? DA.TechFinishes[Math.Min(StepsDone, 4)] : Has(DS.FlourishingFinish) ? DA.Tillana : DA.TechnicalStep,
        DA.SaberDance => Has(DS.DanceOfTheDawnReady) ? DA.DanceOfTheDawn : DA.SaberDance,
        DA.Improvisation => Has(DS.Improvisation) ? DA.ImprovisedFinish : DA.Improvisation,
        DA.FanDance1 when Has(DS.ThreeFold) && Feathers == 0 => DA.FanDance1,
        _ => id,
    };

    public bool ActionReady(uint id)
    {
        if (id == 0) return false;
        if (id >= 1_000_000) return true;
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (W.Dead(W.Me, T)) return false;
        var dancing = Dance != 0;
        if (dancing && !(DA.Steps.Contains(id) || DA.StdFinishes.Contains(id) || DA.TechFinishes.Contains(id) || id is DA.StandardStep or DA.TechnicalStep
                         or DA.SecondWind or DA.HeadGraze or DA.ArmsLength or DA.Peloton or DA.EnAvant
                         // Shield Samba and Curing Waltz work while dancing (399 Sambas mid-dance in the logs).
                         or DA.ShieldSamba or DA.CuringWaltz))
            return false;
        var usable = id switch
        {
            DA.Emboite or DA.Entrechat or DA.Jete or DA.Pirouette => dancing,
            DA.StandardFinish0 or DA.StandardFinish1 or DA.StandardFinish2 => Dance == DS.StandardStep,
            DA.TechnicalFinish0 or DA.TechnicalFinish1 or DA.TechnicalFinish2 or DA.TechnicalFinish3 or DA.TechnicalFinish4 => Dance == DS.TechnicalStep,
            DA.StandardStep => Dance == DS.StandardStep || !dancing && !Has(DS.FinishingMoveReady),
            DA.TechnicalStep => Dance == DS.TechnicalStep || !dancing,
            DA.ReverseCascade or DA.RisingWindmill => Has(DS.SilkenSymmetry) || Has(DS.FlourishingSymmetry),
            DA.Fountainfall or DA.Bloodshower => Has(DS.SilkenFlow) || Has(DS.FlourishingFlow),
            DA.SaberDance => Esprit >= 50,
            DA.DanceOfTheDawn => Esprit >= 50 && Has(DS.DanceOfTheDawnReady),
            DA.StarfallDance => Has(DS.FlourishingStarfall),
            DA.Tillana => Has(DS.FlourishingFinish),
            DA.LastDance => Has(DS.LastDanceReady),
            DA.FinishingMove => Has(DS.FinishingMoveReady),
            DA.FanDance1 or DA.FanDance2 => Feathers > 0,
            DA.FanDance3 => Has(DS.ThreeFold),
            DA.FanDance4 => Has(DS.FourFold),
            DA.Flourish => T >= 0,
            DA.ImprovisedFinish => Has(DS.Improvisation),
            DA.Peloton => T < 0,
            DA.Ending => Has(DS.ClosedPosition),
            _ => true,
        };
        if (!usable) return false;
        if (id is DA.StandardStep or DA.TechnicalStep && dancing) return true;   // the finish
        var key = CdKey(id);
        if (cds.TryGetValue(key, out var cd) && cd.Charges(T) < 1)
            return d.Kind == AKind.Gcd && cd.Remaining(T) <= Math.Max(0, GcdReadyAt - T) / 1000.0 + 0.5;
        return true;
    }

    public uint Retarget(uint action, uint trigger, RuleTargetSpec[] targets)
    {
        foreach (var spec in targets)
        {
            Actor? c = spec.Kind switch
            {
                RuleTarget.Self => W.Me,
                RuleTarget.Partner => PartnerActor is { } p && !W.Dead(p, T) ? p : null,
                _ => null,
            };
            if (c is not null) { pendingTarget = c; return action; }
        }
        return 0;
    }

    // ---------------- the simulation step ----------------

    public void Advance(int dt)
    {
        T += dt;
        var dead = W.Dead(W.Me, T);
        if (dead && !wasDead)
        {
            self.Clear(); Feathers = Esprit = 0; Dance = 0; StepsDone = 0; ComboUntil = int.MinValue;
            Log.Add(new SimEvent(T, "DIED", "", ""));
        }
        wasDead = dead;
        if (dead && T >= 0) DeadMs += dt;

        // Procs that ran out unused.
        foreach (var st in DS.Procs)
            if (self.TryGetValue(st, out var e) && e.Expire <= T)
            {
                self.Remove(st);
                if (T >= 0) Expired[DS.Name(st)] = Expired.GetValueOrDefault(DS.Name(st)) + 1;
                Log.Add(new SimEvent(T, $"EXPIRED {DS.Name(st)}", "", ""));
            }
        foreach (var st in new[] { DS.StandardStep, DS.TechnicalStep })
            if (Dance == st && self.TryGetValue(st, out var e) && e.Expire <= T)
            {
                Dance = 0; StepsDone = 0; self.Remove(st);
                Log.Add(new SimEvent(T, $"DANCE RAN OUT {(st == DS.StandardStep ? "Standard Step" : "Technical Step")}", "", ""));
            }
        if (Has(DS.Improvisation) && W.Moving(T)) { Drop(DS.Improvisation); Log.Add(new SimEvent(T, "Improvisation ended (moved)", "", "")); }
        if (ComboUntil != int.MinValue && ComboUntil <= T) ComboUntil = int.MinValue;

        // Esprit from the party (Technical Finish) and the partner (Standard Finish), per weaponskill/spell.
        while (partyIdx < partyGcds.Count && partyGcds[partyIdx].T <= T)
        {
            var (pt, who) = partyGcds[partyIdx++];
            if (pt < 0 || dead) continue;
            var esprit = techOn.TryGetValue(who.Id, out var until) && until > pt
                         || PartnerActor is not null && who.Id == PartnerActor.Id && sfOnPartner.TryGetValue(who.Id, out var u2) && u2 > pt;
            if (esprit && Rng.NextDouble() < EspritRate) { AddEsprit(10); EspritFromParty += 10; }
        }

        var tgt = Target;
        if (T >= 0 && !dead && tgt is not null && Incapacitated() is null)
        {
            AvailMs += dt;
            if (T < GcdReadyAt) BusyMs += dt;
        }
    }

    private void AddEsprit(int n)
    {
        var before = Esprit;
        Esprit = Math.Clamp(Esprit + n, 0, 100);
        if (n > 0 && before + n > 100 && T >= 0) EspritLost += before + n - 100;
    }

    private void AddFeather()
    {
        if (Feathers >= 4) { if (T >= 0) FeathersLost++; }
        else Feathers++;
    }

    private void AddIdle(string reason, string rule)
    {
        if (T < 0 || W.Dead(W.Me, T) || Target is null) return;
        if (Idle.Count > 0 && Idle[^1] is var last && last.Reason == reason && last.Rule == rule && last.T + last.Ms >= T - 60)
            Idle[^1] = last with { Ms = last.Ms + (T - last.T - last.Ms) };
        else Idle.Add((T, W.R.PhaseAt(T), reason, rule, 20));
    }

    private void UpdateRaidWindow()
    {
        if (T < 0) { windowStart = null; inWindow = false; return; }
        var up = W.RaidBuffs(Target, T).Count > 0 || Has(DS.TechnicalFinish);
        if (up && !inWindow) windowStart = T;
        inWindow = up;
    }

    public double RaidIn()
    {
        if (inWindow) return 0;
        if (T < 0) return 2;
        double next = windowStart is { } s ? s + 120000 : 2000 + 120000 * Math.Ceiling((T - 2000) / 120000.0);
        return Math.Max(0, next - T) / 1000.0;
    }

    /// <summary> When the next press can do something: the GCD or the animation lock coming free, else the next poll. </summary>
    public int NextWake(int poll)
    {
        var next = T + poll;
        if (GcdReadyAt > T && GcdReadyAt < next) next = GcdReadyAt;
        if (LockUntil > T && LockUntil < next) next = LockUntil;
        return Math.Max(T + 1, next);
    }

    public void Press(IReadOnlyList<CompiledRuleSet> files)
    {
        if (W.Dead(W.Me, T)) return;
        if (Incapacitated() is not null) return;
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

        var gcdFree = T >= GcdReadyAt;
        if (Trace && T >= TraceFrom && T <= TraceTo && (a, rule) != lastChoice)
        {
            lastChoice = (a, rule);
            Console.WriteLine($"   trace {SageSim.F(T)} -> {(Defs.TryGetValue(a, out var dd) ? dd.Name : a.ToString())} ({rule}) gcdRem={Math.Max(0, GcdReadyAt - T)} lock={Math.Max(0, LockUntil - T)} tgt={Target?.Name ?? "none"} dist={Dist(Target):0.0} ready={ActionReady(a)} {State()}");
        }
        if (!Defs.TryGetValue(a, out var d)) { Unmodelled++; if (gcdFree) AddIdle($"unmodelled action {a}", rule); return; }
        if (d.Kind == AKind.Wait) { if (gcdFree) AddIdle("Wait", rule); return; }
        var target = pendingTarget ?? Target;
        if (d.Kind == AKind.Ogcd)
        {
            if (T < LockUntil || !ActionReady(a)) return;
            if (d.NeedsTarget && (target is null || target.IsPlayer || Dist(target) > d.Range)) { if (gcdFree) AddIdle($"out of range: {d.Name}", rule); return; }
            if (a != DA.ImprovisedFinish && Has(DS.Improvisation)) Drop(DS.Improvisation);
            Use(d, target, rule);
            LockUntil = T + 600;
            Weaves++;
            return;
        }
        if (!gcdFree) return;
        if (T < LockUntil) { AddIdle("oGCD animation lock past the GCD", rule); return; }
        if (!ActionReady(a)) { AddIdle($"not ready: {d.Name}", rule); return; }
        if (d.NeedsTarget && (target is null || target.IsPlayer || Dist(target) > d.Range)) { AddIdle($"out of range: {d.Name}", rule); return; }
        if (Has(DS.Improvisation)) Drop(DS.Improvisation);
        CurrentGcdMs = d.GcdMs > 0 ? d.GcdMs : GcdMs;
        GcdReadyAt = T + CurrentGcdMs;
        LastGcdAt = T;
        Weaves = 0;
        if (T >= 0) Gcds++;
        LockUntil = T + 600;
        Use(d, target, rule);
    }

    public string State() =>
        $"feathers {Feathers} esprit {Esprit} dance {(Dance == 0 ? "-" : Dance == DS.StandardStep ? "Std" : "Tech")}{(Dance != 0 ? $"{StepsDone}" : "")} " +
        string.Join(" ", DS.Procs.Where(Has).Select(p => $"{DS.Name(p).Replace(" ", "")}@{Left(p):0}")) +
        $" sf {Left(DS.StandardFinish):0} tf {Left(DS.TechnicalFinish):0} dev {Left(DS.Devilment):0} cdSS {Cd(DA.StandardStep):0} cdTS {Cd(DA.TechnicalStep):0} cdFl {Cd(DA.Flourish):0}";

    private void Use(DDef d, Actor? target, string rule)
    {
        lastUse[d.Id] = T;
        RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
        var key = CdKey(d.Id);
        if (cds.TryGetValue(key, out var cd) && !(d.Id is DA.StandardStep or DA.TechnicalStep && Dance != 0)) cd.Use(T);
        Log.Add(new SimEvent(T, d.Name, target is null || target == W.Me ? "" : target.Name, rule));

        var potency = d.Potency;
        bool InCombo(uint from) => ComboUntil > T && ComboAction == from;
        bool Roll() => Rng.NextDouble() < 0.5;
        switch (d.Id)
        {
            case DA.Cascade: AddEsprit(5); ComboAction = d.Id; ComboUntil = T + 30000; if (Roll()) Give(DS.SilkenSymmetry, 30000); break;
            case DA.Fountain:
                if (!InCombo(DA.Cascade)) potency = 120;
                AddEsprit(5); ComboUntil = int.MinValue; if (Roll()) Give(DS.SilkenFlow, 30000); break;
            case DA.Windmill: AddEsprit(5); ComboAction = d.Id; ComboUntil = T + 30000; if (Roll()) Give(DS.SilkenSymmetry, 30000); break;
            case DA.Bladeshower:
                if (!InCombo(DA.Windmill)) potency = 100;
                AddEsprit(5); ComboUntil = int.MinValue; if (Roll()) Give(DS.SilkenFlow, 30000); break;
            case DA.ReverseCascade or DA.RisingWindmill:
                ConsumeFirst(DS.FlourishingSymmetry, DS.SilkenSymmetry); AddEsprit(10); if (Roll()) AddFeather(); break;
            case DA.Fountainfall or DA.Bloodshower:
                ConsumeFirst(DS.FlourishingFlow, DS.SilkenFlow); AddEsprit(10); if (Roll()) AddFeather(); break;
            case DA.SaberDance: AddEsprit(-50); break;
            case DA.DanceOfTheDawn: AddEsprit(-50); Drop(DS.DanceOfTheDawnReady); break;
            case DA.StarfallDance: Drop(DS.FlourishingStarfall); break;
            case DA.Tillana: Drop(DS.FlourishingFinish); AddEsprit(50); break;
            case DA.LastDance: Drop(DS.LastDanceReady); break;
            case DA.FinishingMove: Drop(DS.FinishingMoveReady); StandardFinishBuffs(); break;
            case DA.StandardStep: StartDance(DS.StandardStep, 2); break;
            case DA.TechnicalStep: StartDance(DS.TechnicalStep, 4); break;
            case DA.Emboite or DA.Entrechat or DA.Jete or DA.Pirouette:
                if (Dance != 0 && NextStep == d.Id) StepsDone++;
                break;
            case DA.StandardFinish0 or DA.StandardFinish1 or DA.StandardFinish2:
                potency = Defs[DA.StdFinishes[Math.Min(StepsDone, 2)]].Potency;
                EndDance(); StandardFinishBuffs(); break;
            case DA.TechnicalFinish0 or DA.TechnicalFinish1 or DA.TechnicalFinish2 or DA.TechnicalFinish3 or DA.TechnicalFinish4:
            {
                potency = Defs[DA.TechFinishes[Math.Min(StepsDone, 4)]].Potency;
                var full = StepsDone >= 4;
                EndDance();
                if (full)
                {
                    Give(DS.TechnicalFinish, 20000);
                    Give(DS.FlourishingFinish, 30000);
                    Give(DS.DanceOfTheDawnReady, 30000);
                    var who = W.Party.Where(p => p.Id != W.Me.Id && !W.Dead(p, T) && Vector2.Distance(W.Pos(p, T), W.Pos(W.Me, T)) <= 30).ToList();
                    foreach (var p in who) techOn[p.Id] = T + 20000;
                    TechWindows.Add((T, T + 20000, who));
                    Bursts.Add((T, new List<string>()));
                }
                break;
            }
            case DA.FanDance1: Feathers--; if (Roll()) Give(DS.ThreeFold, 30000); break;
            case DA.FanDance2: Feathers--; if (Roll()) Give(DS.ThreeFold, 30000); break;
            case DA.FanDance3: Drop(DS.ThreeFold); break;
            case DA.FanDance4: Drop(DS.FourFold); break;
            case DA.Devilment:
                Give(DS.Devilment, 20000); Give(DS.FlourishingStarfall, 20000);
                if (PartnerActor is not null && Has(DS.ClosedPosition)) { devOnPartner[PartnerActor.Id] = T + 20000; DevWindows.Add((T, T + 20000)); }
                break;
            case DA.Flourish:
                Give(DS.FlourishingSymmetry, 30000); Give(DS.FlourishingFlow, 30000); Give(DS.ThreeFold, 30000); Give(DS.FourFold, 30000);
                Give(DS.FinishingMoveReady, 30000);
                break;
            case DA.ShieldSamba: Mit.Add((T, "Shield Samba")); break;
            case DA.Improvisation: Give(DS.Improvisation, 15000); Mit.Add((T, "Improvisation")); break;
            case DA.ImprovisedFinish: Drop(DS.Improvisation); Mit.Add((T, "Improvised Finish")); break;
            case DA.CuringWaltz: Mit.Add((T, "Curing Waltz")); break;
            case DA.ClosedPosition: self[DS.ClosedPosition] = (int.MaxValue, 0); if (pendingTarget is { IsPlayer: true } pt && pt.Id != W.Me.Id) PartnerActor = pt; break;
            case DA.Ending: Drop(DS.ClosedPosition); break;
            case DA.Peloton: Give(DS.Peloton, 30000); break;
        }
        if (Bursts.Count > 0 && Has(DS.TechnicalFinish) && d.Potency > 0) Bursts[^1].Gcds.Add(d.Name);

        if (potency > 0 && T >= 0)
        {
            // Damage lands when an enemy is in reach: the target within the action's range (self-centred AoEs: around you).
            var hit = target is { IsPlayer: false } && Dist(target) <= (d.SelfAoe ? d.Range : Math.Max(d.Range, 3));
            if (d.SelfAoe && target is null) hit = false;
            if (hit) Damage.Add((T, d.Name, potency * (d.Id == DA.StarfallDance ? Score.Starfall : 1), Score.SelfMult(Has(DS.StandardFinish), Has(DS.TechnicalFinish), Has(DS.Devilment)) * W.RaidBuffs(target, T).Mult));
        }
    }

    private void ConsumeFirst(uint a, uint b)
    {
        // The one running out first goes (both are 30 s procs; which the game takes isn't in the logs).
        if (Has(a) && Has(b)) Drop(Left(a) <= Left(b) ? a : b);
        else if (Has(a)) Drop(a);
        else Drop(b);
    }

    private void StandardFinishBuffs()
    {
        Give(DS.StandardFinish, 60000);
        Give(DS.LastDanceReady, 30000);
        if (PartnerActor is not null && Has(DS.ClosedPosition)) { sfOnPartner[PartnerActor.Id] = T + 60000; SfWindows.Add((T, T + 60000)); }
    }

    private void StartDance(uint status, int steps)
    {
        Dance = status;
        StepsDone = 0;
        Give(status, 15000);
        StepOrder = DA.Steps.OrderBy(_ => Rng.Next()).Take(steps).ToArray();
    }

    private void EndDance()
    {
        Drop(Dance);
        Dance = 0;
        StepsDone = 0;
    }

    public double Cd(uint id)
    {
        var key = CdKey(id);
        if (cds.TryGetValue(key, out var c)) return c.Remaining(T);
        return Defs.TryGetValue(id, out var d) && d.Kind == AKind.Gcd ? Math.Max(0, GcdReadyAt - T) / 1000.0 : 0;
    }

    public bool JustUsed(uint id, double secs) => lastUse.TryGetValue(id, out var t) && T - t <= secs * 1000;

    public Func<double>? Gauge(string field) => field.ToLowerInvariant() switch
    {
        "feathers" => () => Feathers,
        "esprit" => () => Esprit,
        "completedsteps" => () => StepsDone,
        "nextstep" => () => NextStep,
        "isdancing" => () => B(Dance != 0),
        _ => null,
    };

    public IReadOnlyDictionary<string, RuleFunction> Functions()
    {
        var f = new Dictionary<string, RuleFunction>(StringComparer.OrdinalIgnoreCase);
        void F(string name, ParamKind[] p, int req, Func<RuleArg[], Func<double>> bind) => Fn(f, name, p, req, bind);
        void Val(string name, Func<double> get) => V(f, name, get);
        ParamKind[] Act = [ParamKind.Action], St = [ParamKind.Status];
        AddMath(f);
        AddEnemyCasts(f, W, () => T);
        AddWorld(f, W, () => T, () => Target);
        F("ready", Act, 1, a => { var id = a[0].Id; return () => B(ActionReady(OriginalHook(id))); });
        F("cd", Act, 1, a => { var id = a[0].Id; return () => Cd(id); });
        F("charges", Act, 1, a => { var id = a[0].Id; return () => cds.TryGetValue(CdKey(id), out var c) ? c.Charges(T) : 1; });
        F("chargeCd", Act, 1, a => { var id = a[0].Id; return () => cds.TryGetValue(CdKey(id), out var c) ? c.ChargeRemaining(T) : 0; });
        F("justUsed", [ParamKind.Action, ParamKind.Number], 1, a => { var id = a[0].Id; var s = a[1].Value; return () => B(JustUsed(id, s?.Invoke() ?? 3)); });
        F("inRange", Act, 1, a => { var id = a[0].Id; return () => B(Defs.TryGetValue(id, out var d) && (!d.NeedsTarget || Target is { } t && Dist(t) <= d.Range)); });
        F("combo", Act, 1, a => { var id = a[0].Id; return () => B(ComboTimer > 0 && ComboAction == id); });
        F("enemies", Act, 1, a => { var id = a[0].Id; return () => Defs.TryGetValue(id, out var d) && d.SelfAoe ? EnemiesWithin(d.Range) : Target is null ? 0 : 1; });
        F("hook", Act, 1, a => { var id = a[0].Id; return () => OriginalHook(id); });
        F("buff", St, 1, a => { var id = a[0].Id; return () => B(Has(id)); });
        F("buffTime", St, 1, a => { var id = a[0].Id; return () => Left(id); });
        F("buffStacks", St, 1, _ => () => 0);
        F("buffAny", St, 1, a => { var id = a[0].Id; return () => B(Has(id) || W.OthersHave(W.Me, id, T)); });
        F("buffAnyTime", St, 1, a => { var id = a[0].Id; return () => Math.Max(Left(id), W.OthersTime(W.Me, id, T)); });
        F("debuff", St, 1, _ => () => 0);
        F("debuffTime", St, 1, _ => () => 0);
        F("debuffAny", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(W.OthersHave(t, id, T)) : 0; });
        // % of the living party with the status from anyone (DNC_ST / DNC_AoE's Accretion check before Curing Waltz).
        F("partyWith", St, 1, a => { var id = a[0].Id; return () => { var l = W.Party.Where(p => !W.Dead(p, T)).ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p => p.Id == W.Me.Id && Has(id) || W.OthersHave(p, id, T)) / l.Count; }; });
        F("debuffAnyTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? W.OthersTime(t, id, T) : 0; });
        Val("gcd", () => GcdMs / 1000.0);
        Val("remainingGcd", () => Math.Max(0, GcdReadyAt - T) / 1000.0);
        Val("canWeave", () => B(CanWeave()));
        Val("moving", () => B(W.Moving(T)));
        Val("stoodStill", () => W.Moving(T) ? 0 : 1);
        Val("movingTime", () => 0);
        Val("sinceGcd", () => LastGcdAt == int.MinValue ? 99 : (T - LastGcdAt) / 1000.0);
        Val("inMelee", () => Target is { } t ? B(Dist(t) <= 3) : 0);
        Val("distance", () => Target is { } t ? Dist(t) : 0);
        foreach (var n in new[] { "rear", "flank", "front", "needsPositionals", "casting", "raidwide", "stackMarker", "tankbuster", "canEsuna", "healCleansable",
                     "selfShielded", "tanksUnshielded", "healersUnshielded", "dpsUnshielded", "partyUnshielded" }) Val(n, () => 0);
        Val("countdown", () => T < 0 ? -T / 1000.0 : 0);
        Val("comboTimer", () => ComboTimer);
        Val("comboAction", () => ComboTimer > 0 ? ComboAction : 0);
        Val("mp", () => 10000);
        Val("raidIn", RaidIn);
        Val("raidwideIn", () => 99);
        Val("partnerNonOptimal", () => 0);
        Val("partyAvgHp", () => 100);
        Val("partyLowestHp", () => 100);
        Val("button", () => Trigger);
        Val("buttonType", () => 1);
        return f;
    }
}

/// <summary> The damage model shared by the sim and the logged Dancer's real actions. </summary>
internal static class Score
{
    public const double Dev = 1.155;       // Devilment: +20% crit and DH rate at 25% crit (1.6x) and 40% DH base
    public const double Starfall = 1.58;   // a critical direct hit against a normal hit's expectation
    public static double SelfMult(bool sf, bool tf, bool dev) => (sf ? 1.05 : 1) * (tf ? 1.05 : 1) * (dev ? Dev : 1);

    public static readonly Dictionary<string, int> Potency = new()
    {
        ["Cascade"] = 220, ["Fountain"] = 280, ["Reverse Cascade"] = 280, ["Fountainfall"] = 340, ["Windmill"] = 120, ["Bladeshower"] = 160,
        ["Rising Windmill"] = 160, ["Bloodshower"] = 200, ["Saber Dance"] = 540, ["Dance of the Dawn"] = 1000, ["Starfall Dance"] = 600,
        ["Tillana"] = 600, ["Last Dance"] = 540, ["Finishing Move"] = 850, ["Double Standard Finish"] = 850, ["Single Standard Finish"] = 540,
        ["Standard Finish"] = 360, ["Quadruple Technical Finish"] = 1300, ["Triple Technical Finish"] = 900, ["Double Technical Finish"] = 720,
        ["Single Technical Finish"] = 540, ["Technical Finish"] = 350, ["Fan Dance"] = 180, ["Fan Dance II"] = 100, ["Fan Dance III"] = 220, ["Fan Dance IV"] = 460,
    };
}
