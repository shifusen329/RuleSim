using RaidReplay.Core.Model;
using WrathCombo.CustomRules;
using static SharedBuiltins;

/// <summary> Gunbreaker action ids (GNB.cs). </summary>
internal static class GA
{
    public const uint KeenEdge = 16137, NoMercy = 16138, BrutalShell = 16139, LightningShot = 16143, DangerZone = 16144, SolidBarrel = 16145,
        GnashingFang = 16146, SavageClaw = 16147, WickedTalon = 16150, SonicBreak = 16153, Continuation = 16155, JugularRip = 16156,
        AbdomenTear = 16157, EyeGouge = 16158, BowShock = 16159, BurstStrike = 16162, Bloodfest = 16164, BlastingZone = 16165,
        Hypervelocity = 25759, DoubleDown = 25760, ReignOfBeasts = 36937, NobleBlood = 36938, LionHeart = 36939, Wait = 1000004;
}

/// <summary> Gunbreaker statuses (GNB.cs Buffs; the game data). </summary>
internal static class GS
{
    public const uint NoMercy = 1831, ReadyToRip = 1842, ReadyToTear = 1843, ReadyToGouge = 1844, ReadyToBlast = 2686, ReadyToReign = 3840,
        ReadyToBreak = 3886, Bloodfest = 5051;
    public static readonly uint[] Continuations = [ReadyToRip, ReadyToTear, ReadyToGouge, ReadyToBlast];
    public static readonly uint[] Procs = [ReadyToRip, ReadyToTear, ReadyToGouge, ReadyToBlast, ReadyToReign, ReadyToBreak];
    public static string Name(uint s) => s switch
    {
        NoMercy => "No Mercy", ReadyToRip => "Ready to Rip", ReadyToTear => "Ready to Tear", ReadyToGouge => "Ready to Gouge", ReadyToBlast => "Ready to Blast",
        ReadyToReign => "Ready to Reign", ReadyToBreak => "Ready to Break", Bloodfest => "Bloodfest", _ => s.ToString(),
    };
}

/// <summary>
///     A Gunbreaker action at level 100 (game data, 2026-11-23: RuleSim tools/GameDump --job=37): kind, cooldown and charges (0 = the
///     GCD only), potency (in combo for the combo steps), DoT potency in all (its ticks, snapshotted), range.
/// </summary>
internal sealed record GDef(uint Id, string Name, AKind Kind, int CooldownMs = 0, int Charges = 1, int Potency = 0, int Dot = 0, double Range = 3,
    bool NeedsTarget = true);

/// <summary>
///     The simulated Gunbreaker (2026-11-23, user: "run it through the sim and compare to the previous ruleset"): the rules choose its
///     actions; position, target, deaths, enemy casts and other players' buffs come from the logged player (World.Me) and the pull.
///     Cartridges: Solid Barrel in combo +1 (wasted at the cap), Bloodfest +3 with the cap at 6 for its 30 s (the extra ones vanish
///     when it ends), Burst Strike / Gnashing Fang -1, Double Down -2. The Keen Edge combo isn't broken by the cartridge weaponskills;
///     the Gnashing Fang and Reign of Beasts combos are the gauge's combo step; a weaponskill ends any Continuation proc. Score:
///     potency x No Mercy (+20%, DoTs snapshot it) x other players' raid buffs. Not modelled: auto-attacks, mitigation and other
///     non-damage actions (a rule that picks one skips that press), the enmity/positioning of a tank.
/// </summary>
internal sealed class GnbSim : IRuleRuntime
{
    public readonly World W;
    public int T;
    public readonly int GcdMs;
    public uint Trigger = GA.KeenEdge;

    // gauge
    public int Carts, Step;
    public int StepUntil = int.MinValue;

    // the Keen Edge combo
    public uint ComboAction;
    public int ComboUntil = int.MinValue;
    public double ComboTimer => ComboUntil > T ? (ComboUntil - T) / 1000.0 : 0;

    // timing
    public int GcdReadyAt = -100_000, LockUntil = -100_000, LastGcdAt = int.MinValue, Weaves;
    private readonly Dictionary<uint, SamCd> cds = new();
    private readonly Dictionary<uint, int> lastUse = new();
    private readonly Dictionary<uint, (int Expire, int Stacks)> self = new();
    private bool wasDead;
    private (uint, string) lastChoice;

    // results
    public readonly List<SimEvent> Log = new();
    public readonly List<(int T, string Name, double Potency, double Mult)> Damage = new();
    public readonly Dictionary<string, int> RuleFires = new();
    public readonly Dictionary<string, int> Expired = new();
    public readonly Dictionary<string, int> Uses = new();
    public int Gcds, Unmodelled, AvailMs, DeadMs, CartsMade, CartsSpent, CartsWasted, CartsVanished, CartsLostToDeath;
    public double MsAt2Outside;
    public int SpentInNoMercy, SpentOutside;
    public bool Trace; public int TraceFrom, TraceTo;

    public static readonly Dictionary<uint, GDef> Defs = new GDef[]
    {
        new(GA.KeenEdge, "Keen Edge", AKind.Gcd, Potency: 300),
        new(GA.BrutalShell, "Brutal Shell", AKind.Gcd, Potency: 380),
        new(GA.SolidBarrel, "Solid Barrel", AKind.Gcd, Potency: 460),
        new(GA.BurstStrike, "Burst Strike", AKind.Gcd, Potency: 420),
        new(GA.GnashingFang, "Gnashing Fang", AKind.Gcd, CooldownMs: 30000, Charges: 2, Potency: 440),
        new(GA.SavageClaw, "Savage Claw", AKind.Gcd, Potency: 500),
        new(GA.WickedTalon, "Wicked Talon", AKind.Gcd, Potency: 560),
        new(GA.DoubleDown, "Double Down", AKind.Gcd, CooldownMs: 60000, Potency: 1000, Range: 5),
        new(GA.SonicBreak, "Sonic Break", AKind.Gcd, Potency: 340, Dot: 600),
        new(GA.ReignOfBeasts, "Reign of Beasts", AKind.Gcd, Potency: 800),
        new(GA.NobleBlood, "Noble Blood", AKind.Gcd, Potency: 900),
        new(GA.LionHeart, "Lion Heart", AKind.Gcd, Potency: 1000),
        new(GA.LightningShot, "Lightning Shot", AKind.Gcd, Potency: 150, Range: 20),
        new(GA.JugularRip, "Jugular Rip", AKind.Ogcd, Potency: 220, Range: 5),
        new(GA.AbdomenTear, "Abdomen Tear", AKind.Ogcd, Potency: 260, Range: 5),
        new(GA.EyeGouge, "Eye Gouge", AKind.Ogcd, Potency: 300, Range: 5),
        new(GA.Hypervelocity, "Hypervelocity", AKind.Ogcd, Potency: 180, Range: 5),
        new(GA.BlastingZone, "Blasting Zone", AKind.Ogcd, CooldownMs: 30000, Potency: 800),
        new(GA.BowShock, "Bow Shock", AKind.Ogcd, CooldownMs: 60000, Potency: 150, Dot: 300, Range: 5),
        new(GA.NoMercy, "No Mercy", AKind.Ogcd, CooldownMs: 60000, NeedsTarget: false),
        new(GA.Bloodfest, "Bloodfest", AKind.Ogcd, CooldownMs: 60000, Range: 25),
        new(GA.Wait, "Wait", AKind.Wait),
    }.ToDictionary(d => d.Id);

    public const double NoMercyMult = 1.20;

    public GnbSim(World w, int gcdMs)
    {
        W = w;
        GcdMs = gcdMs;
        foreach (var d in Defs.Values.Where(d => d.CooldownMs > 0)) cds[d.Id] = new SamCd(d.CooldownMs, d.Charges);
    }

    public int ChargesNow(uint id) => cds.TryGetValue(id, out var c) ? c.Charges(T) : 0;

    // ---------------- state helpers ----------------

    public bool Has(uint status) => self.TryGetValue(status, out var s) && s.Expire > T;
    public double Left(uint status) => self.TryGetValue(status, out var s) && s.Expire > T ? Math.Min(999, (s.Expire - T) / 1000.0) : 0;
    private void Give(uint status, int ms) => self[status] = (T + ms, 0);
    private void Drop(uint status) => self.Remove(status);
    public int MaxCarts => Has(GS.Bloodfest) ? 6 : 3;

    private List<(int T, Actor Target)>? hitsCache;

    /// <summary> RdmSim.Target's rule (the logged player's last damaging action's target within 15 s, else the biggest targetable boss). </summary>
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
            var boss = W.Bosses.Where(b => b.IsPresent(T) && b.IsTargetable(T) && !b.IsHidden(T)).MaxBy(b => b.MaxHp);
            return boss ?? (T < 0 ? W.Bosses.Where(b => b.IsPresent(0) && b.IsTargetable(0)).MaxBy(b => b.MaxHp) : null);
        }
    }

    public double Dist(Actor? t) => t is null ? 99 : W.Dist(W.Me, t, Math.Max(0, T));

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

    private bool InCombo(uint id) => ComboTimer > 0 && ComboAction == id;
    private int StepNow => StepUntil > T ? Step : 0;

    public uint OriginalHook(uint id) => id switch
    {
        GA.GnashingFang => StepNow == 1 ? GA.SavageClaw : StepNow == 2 ? GA.WickedTalon : GA.GnashingFang,
        GA.ReignOfBeasts => StepNow == 3 ? GA.NobleBlood : StepNow == 4 ? GA.LionHeart : GA.ReignOfBeasts,
        GA.Continuation => Has(GS.ReadyToRip) ? GA.JugularRip : Has(GS.ReadyToTear) ? GA.AbdomenTear : Has(GS.ReadyToGouge) ? GA.EyeGouge
            : Has(GS.ReadyToBlast) ? GA.Hypervelocity : GA.Continuation,
        GA.DangerZone => GA.BlastingZone,
        _ => id,
    };

    public bool ActionReady(uint id)
    {
        if (id == 0) return false;
        if (id >= 1_000_000) return true;
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (W.Dead(W.Me, T)) return false;
        var usable = id switch
        {
            GA.BurstStrike => Carts >= 1,
            GA.GnashingFang => Carts >= 1 && StepNow == 0,
            GA.SavageClaw => StepNow == 1,
            GA.WickedTalon => StepNow == 2,
            GA.DoubleDown => Carts >= 2,
            GA.SonicBreak => Has(GS.ReadyToBreak),
            GA.ReignOfBeasts => Has(GS.ReadyToReign) && StepNow == 0,
            GA.NobleBlood => StepNow == 3,
            GA.LionHeart => StepNow == 4,
            GA.JugularRip => Has(GS.ReadyToRip),
            GA.AbdomenTear => Has(GS.ReadyToTear),
            GA.EyeGouge => Has(GS.ReadyToGouge),
            GA.Hypervelocity => Has(GS.ReadyToBlast),
            GA.NoMercy or GA.Bloodfest => T >= 0,
            _ => true,
        };
        if (!usable) return false;
        if (cds.TryGetValue(id, out var cd) && cd.Charges(T) < 1)
            return d.Kind == AKind.Gcd && cd.Remaining(T) <= Math.Max(0, GcdReadyAt - T) / 1000.0 + 0.5;
        return true;
    }

    public uint Retarget(uint action, uint trigger, RuleTargetSpec[] targets) => 0;

    // ---------------- the simulation step ----------------

    public void Advance(int dt)
    {
        var t0 = T;
        T += dt;
        var dead = W.Dead(W.Me, T);
        if (dead && !wasDead)
        {
            if (T >= 0) CartsLostToDeath += Carts;
            self.Clear(); Carts = 0; Step = 0; StepUntil = int.MinValue; ComboUntil = int.MinValue;
            Log.Add(new SimEvent(T, "DIED", "", ""));
        }
        wasDead = dead;
        if (dead && T >= 0) DeadMs += dt;

        // Bloodfest's extra cartridges go when it ends.
        if (self.TryGetValue(GS.Bloodfest, out var bf) && bf.Expire <= T)
        {
            self.Remove(GS.Bloodfest);
            if (Carts > 3) { if (T >= 0) CartsVanished += Carts - 3; Log.Add(new SimEvent(T, $"VANISHED {Carts - 3} (Bloodfest ended)", "", "")); Carts = 3; }
        }
        foreach (var st in GS.Procs)
            if (self.TryGetValue(st, out var e) && e.Expire <= T)
            {
                self.Remove(st);
                if (T >= 0) Expired[GS.Name(st)] = Expired.GetValueOrDefault(GS.Name(st)) + 1;
                Log.Add(new SimEvent(T, $"EXPIRED {GS.Name(st)}", "", ""));
            }
        if (ComboUntil != int.MinValue && ComboUntil <= T) ComboUntil = int.MinValue;
        if (StepUntil != int.MinValue && StepUntil <= T) { Step = 0; StepUntil = int.MinValue; }

        if (t0 >= 0 && !dead && Carts >= 2 && !Has(GS.NoMercy) && Target is not null) MsAt2Outside += dt;
        if (T >= 0 && !dead && Target is not null && Incapacitated() is null) AvailMs += dt;
    }

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
        if (!Defs.TryGetValue(a, out var d)) { Unmodelled++; return; }
        if (d.Kind == AKind.Wait) return;
        var target = Target;
        if (d.Kind == AKind.Ogcd)
        {
            if (T < LockUntil || !ActionReady(a)) return;
            if (d.NeedsTarget && (target is null || target.IsPlayer || Dist(target) > d.Range)) return;
            Use(d);
            Resolve(d, target, rule);
            LockUntil = T + 600;
            Weaves++;
            return;
        }
        if (!gcdFree || T < LockUntil || !ActionReady(a)) return;
        if (d.NeedsTarget && (target is null || target.IsPlayer || Dist(target) > d.Range)) return;
        Weaves = 0;
        LastGcdAt = T;
        if (T >= 0) Gcds++;
        GcdReadyAt = T + GcdMs;
        LockUntil = T + 600;
        Use(d);
        Resolve(d, target, rule);
    }

    public string State() =>
        $"carts {Carts}/{MaxCarts} step {StepNow} combo {(ComboTimer > 0 ? Defs[ComboAction].Name : "-")} " +
        string.Join(" ", new[] { GS.NoMercy, GS.Bloodfest, GS.ReadyToBreak, GS.ReadyToReign, GS.ReadyToRip, GS.ReadyToTear, GS.ReadyToGouge, GS.ReadyToBlast }
            .Where(Has).Select(p => $"{GS.Name(p).Replace(" ", "")}@{Left(p):0}")) +
        $" GF {cds[GA.GnashingFang].Charges(T)}/{Cd(GA.GnashingFang):0} NM {Cd(GA.NoMercy):0} BF {Cd(GA.Bloodfest):0} DD {Cd(GA.DoubleDown):0}";

    private void Use(GDef d)
    {
        lastUse[d.Id] = T;
        if (cds.TryGetValue(d.Id, out var c)) c.Use(T);
        if (T >= -20000) Uses[d.Name] = Uses.GetValueOrDefault(d.Name) + 1;
    }

    private void Spend(int n)
    {
        Carts -= n;
        if (T < 0) return;
        CartsSpent += n;
        if (Has(GS.NoMercy)) SpentInNoMercy += n; else SpentOutside += n;
    }

    private void Make(int n)
    {
        var room = MaxCarts - Carts;
        var add = Math.Min(n, room);
        Carts += add;
        if (T < 0) return;
        CartsMade += add;
        CartsWasted += n - add;
    }

    private void Resolve(GDef d, Actor? target, string rule)
    {
        RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
        Log.Add(new SimEvent(T, d.Name, target is null || target == W.Me ? "" : target.Name, rule));
        var potency = d.Potency;
        if (d.Kind == AKind.Gcd)
            foreach (var c in GS.Continuations) Drop(c);   // any weaponskill ends a Continuation proc
        switch (d.Id)
        {
            case GA.KeenEdge: ComboAction = GA.KeenEdge; ComboUntil = T + 30000; break;
            case GA.BrutalShell:
                if (InCombo(GA.KeenEdge)) { ComboAction = GA.BrutalShell; ComboUntil = T + 30000; }
                else { potency = 240; ComboUntil = int.MinValue; }
                break;
            case GA.SolidBarrel:
                if (InCombo(GA.BrutalShell)) Make(1); else potency = 240;
                ComboUntil = int.MinValue; break;
            case GA.BurstStrike: Spend(1); Give(GS.ReadyToBlast, 10000); break;
            case GA.GnashingFang: Spend(1); Step = 1; StepUntil = T + 30000; Give(GS.ReadyToRip, 10000); break;
            case GA.SavageClaw: Step = 2; StepUntil = T + 30000; Give(GS.ReadyToTear, 10000); break;
            case GA.WickedTalon: Step = 0; StepUntil = int.MinValue; Give(GS.ReadyToGouge, 10000); break;
            case GA.DoubleDown: Spend(2); break;
            case GA.SonicBreak: Drop(GS.ReadyToBreak); break;
            case GA.ReignOfBeasts: Drop(GS.ReadyToReign); Step = 3; StepUntil = T + 30000; break;
            case GA.NobleBlood: Step = 4; StepUntil = T + 30000; break;
            case GA.LionHeart: Step = 0; StepUntil = int.MinValue; break;
            case GA.JugularRip: Drop(GS.ReadyToRip); break;
            case GA.AbdomenTear: Drop(GS.ReadyToTear); break;
            case GA.EyeGouge: Drop(GS.ReadyToGouge); break;
            case GA.Hypervelocity: Drop(GS.ReadyToBlast); break;
            case GA.NoMercy: Give(GS.NoMercy, 20000); Give(GS.ReadyToBreak, 30000); break;
            case GA.Bloodfest: Give(GS.Bloodfest, 30000); Make(3); Give(GS.ReadyToReign, 30000); break;
        }

        var total = potency + d.Dot;
        if (total > 0 && T >= -1000 && target is { IsPlayer: false } && Dist(target) <= Math.Max(d.Range, 3))
            Damage.Add((Math.Max(0, T), d.Name, total, (Has(GS.NoMercy) ? NoMercyMult : 1) * W.RaidBuffs(target, Math.Max(0, T)).Mult));
    }

    public double Cd(uint id)
    {
        if (cds.TryGetValue(id, out var c)) return c.Remaining(T);
        return Defs.TryGetValue(id, out var d) && d.Kind == AKind.Gcd ? Math.Max(0, GcdReadyAt - T) / 1000.0 : 0;
    }

    public bool JustUsed(uint id, double secs) => lastUse.TryGetValue(id, out var t) && T - t <= secs * 1000;

    public Func<double>? Gauge(string field) => field.ToLowerInvariant() switch
    {
        "ammo" => () => Carts,
        "ammocombostep" => () => StepNow,
        "maxtimerduration" => () => 0,
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
        F("cd", Act, 1, a => { var id = a[0].Id; return () => Cd(OriginalHook(id)); });
        F("charges", Act, 1, a => { var id = a[0].Id; return () => cds.TryGetValue(id, out var c) ? c.Charges(T) : 1; });
        F("chargeCd", Act, 1, a => { var id = a[0].Id; return () => cds.TryGetValue(id, out var c) ? c.ChargeRemaining(T) : 0; });
        F("justUsed", [ParamKind.Action, ParamKind.Number], 1, a => { var id = a[0].Id; var s = a[1].Value; return () => B(JustUsed(id, s?.Invoke() ?? 3)); });
        F("inRange", Act, 1, a => { var id = a[0].Id; return () => B(Defs.TryGetValue(OriginalHook(id), out var d) && (!d.NeedsTarget || Target is { } t && Dist(t) <= d.Range)); });
        F("combo", Act, 1, a => { var id = a[0].Id; return () => B(ComboTimer > 0 && ComboAction == id); });
        F("enemies", Act, 1, a => { var id = a[0].Id; return () => Target is null ? 0 : Math.Max(1, EnemiesWithin(Defs.TryGetValue(id, out var d) ? d.Range : 5)); });
        F("hook", Act, 1, a => { var id = a[0].Id; return () => OriginalHook(id); });
        F("buff", St, 1, a => { var id = a[0].Id; return () => B(Has(id)); });
        F("buffTime", St, 1, a => { var id = a[0].Id; return () => Left(id); });
        F("buffStacks", St, 1, _ => () => 0);
        F("buffAny", St, 1, a => { var id = a[0].Id; return () => B(Has(id) || W.OthersHave(W.Me, id, T)); });
        F("buffAnyTime", St, 1, a => { var id = a[0].Id; return () => Math.Max(Left(id), W.OthersTime(W.Me, id, T)); });
        F("debuff", St, 1, _ => () => 0);
        F("debuffTime", St, 1, _ => () => 0);
        F("debuffAny", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(W.OthersHave(t, id, T)) : 0; });
        F("debuffAnyTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? W.OthersTime(t, id, T) : 0; });
        F("partyWith", St, 1, a => { var id = a[0].Id; return () => { var l = W.Party.Where(p => !W.Dead(p, T)).ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p => W.OthersHave(p, id, T)) / l.Count; }; });
        Val("gcd", () => GcdMs / 1000.0);
        Val("remainingGcd", () => Math.Max(0, GcdReadyAt - T) / 1000.0);
        Val("canWeave", () => B(CanWeave()));
        Val("casting", () => 0);
        Val("moving", () => B(W.Moving(T)));
        Val("stoodStill", () => W.Moving(T) ? 0 : 1);
        Val("movingTime", () => 0);
        Val("sinceGcd", () => LastGcdAt == int.MinValue ? 99 : (T - LastGcdAt) / 1000.0);
        Val("inMelee", () => Target is { } t ? B(Dist(t) <= 3) : 0);
        Val("distance", () => Target is { } t ? Dist(t) : 0);
        foreach (var n in new[] { "rear", "flank", "front", "needsPositionals", "raidwide", "stackMarker", "tankbuster", "canEsuna", "healCleansable",
                     "selfShielded", "tanksUnshielded", "healersUnshielded", "dpsUnshielded", "partyUnshielded" }) Val(n, () => 0);
        Val("countdown", () => T < 0 ? -T / 1000.0 : 0);
        Val("comboTimer", () => ComboTimer);
        Val("comboAction", () => ComboTimer > 0 ? ComboAction : 0);
        Val("mp", () => 10000);
        Val("raidwideIn", () => 99);
        Val("partyAvgHp", () => 100);
        Val("partyLowestHp", () => 100);
        Val("button", () => Trigger);
        Val("buttonType", () => 1);
        // Party values for the Dancing Mad plan layer (DMU_Mit_GNB.json), as in the Samurai and Sage replays.
        bool HasAnyOn(Actor p, uint id) => p.Id == W.Me.Id && Has(id) || W.OthersHave(p, id, T);
        IEnumerable<Actor> Living(bool inRange = true) => W.Party.Where(p => !W.Dead(p, T) && (!inRange || p.Id == W.Me.Id || System.Numerics.Vector2.Distance(W.Pos(p, T), W.Pos(W.Me, T)) <= 30));
        F("partyJob", [ParamKind.Name], 1, a => { var ab = a[0].Name!.Trim().ToUpperInvariant(); return () => W.Party.Count(p => p.Id != W.Me.Id && World.JobAbbr(p) == ab); });
        Val("partyAvgHp", () => Living(false).Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Average());
        Val("partyLowestHp", () => Living().Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Min());
        F("partyBelow", [ParamKind.Number], 1, a => { var n = a[0].Value!; return () => Living().Count(p => W.HpPct(p, T) <= n()); });
        Val("partyAlive", () => Living(false).Count());
        Val("partyDead", () => W.Party.Count(p => W.Dead(p, T)));
        F("partyWith", St, 1, a => { var id = a[0].Id; return () => { var l = Living(false).ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p => HasAnyOn(p, id)) / l.Count; }; });
        Val("partyInCombat", () => B(T >= 0));
        Val("tankHp", () => Living().Where(World.IsTank).Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Min());
        F("tankHas", St, 1, a => { var id = a[0].Id; return () => B(Living(false).Any(p => World.IsTank(p) && HasAnyOn(p, id))); });
        Actor? TT() => W.BossTarget(Target, T) is { } tt && !W.Dead(tt, T) ? tt : null;
        Val("ttIsParty", () => B(TT() is not null));
        Val("ttIsTank", () => B(TT() is { } t && World.IsTank(t)));
        Val("ttIsSelf", () => B(TT()?.Id == W.Me.Id));
        Val("ttHp", () => TT() is { } t ? W.HpPct(t, T) : 100);
        F("ttHas", St, 1, a => { var id = a[0].Id; return () => B(TT() is { } t && HasAnyOn(t, id)); });
        Val("canRaise", () => B(W.Party.Any(p => W.Dead(p, T))));
        return f;
    }
}
