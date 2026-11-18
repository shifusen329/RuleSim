using RaidReplay.Core.Model;
using WrathCombo.CustomRules;
using static SharedBuiltins;

/// <summary> Red Mage action ids (RDM_Helper.cs). </summary>
internal static class RA
{
    public const uint Jolt = 7503, Riposte = 7504, Verthunder = 7505, Corpsacorps = 7506, Veraero = 7507, Verfire = 7510, Verstone = 7511,
        Zwerchhau = 7512, Redoublement = 7516, Fleche = 7517, Acceleration = 7518, ContreSixte = 7519, Embolden = 7520, Manafication = 7521,
        Verflare = 7525, Verholy = 7526, EnchantedRiposte = 7527, EnchantedZwerchhau = 7528, EnchantedRedoublement = 7529, Engagement = 16527,
        Scorch = 16530, Verthunder3 = 25855, Veraero3 = 25856, MagickBarrier = 25857, Resolution = 25858, Jolt3 = 37004, ViceOfThorns = 37005,
        GrandImpact = 37006, Prefulgence = 37007, Swiftcast = 7561, LucidDreaming = 7562, Addle = 7560, Surecast = 7559, Wait = 1000004;
}

/// <summary> Red Mage statuses (RDM_Helper.cs Buffs; the game data). </summary>
internal static class RS
{
    public const uint VerfireReady = 1234, VerstoneReady = 1235, Acceleration = 1238, Embolden = 1239, Dualcast = 1249, Manafication = 1971,
        MagickedSwordplay = 3875, ThornedFlourish = 3876, GrandImpactReady = 3877, PrefulgenceReady = 3878, Swiftcast = 167;
    public static readonly uint[] Procs = [VerfireReady, VerstoneReady, GrandImpactReady, PrefulgenceReady, ThornedFlourish, Dualcast];
    public static string Name(uint s) => s switch
    {
        VerfireReady => "Verfire Ready", VerstoneReady => "Verstone Ready", GrandImpactReady => "Grand Impact Ready", PrefulgenceReady => "Prefulgence Ready",
        ThornedFlourish => "Thorned Flourish", Dualcast => "Dualcast", Acceleration => "Acceleration", Swiftcast => "Swiftcast", _ => s.ToString(),
    };
}

/// <summary>
///     A Red Mage action at level 100 (game data, 2026-11-18: RuleSim tools/GameDump): kind, cast (ms; 0 = instant), recast (0 =
///     the spell GCD), cooldown and charges, potency (in combo for the melee steps), range, whether it is a spell.
/// </summary>
internal sealed record RDef(uint Id, string Name, AKind Kind, int CastMs = 0, int RecastMs = 0, int CooldownMs = 0, int Charges = 1, int Potency = 0,
    double Range = 25, bool Melee = false, bool NeedsTarget = true);

/// <summary>
///     The simulated Red Mage (2026-11-18): the rules choose its actions; position, movement, target, hits, deaths, enemy casts and
///     other players' buffs come from the logged player (World.Me) and the pull. Hard casts take their cast time (scaled by the
///     logged GCD) and are cancelled by the logged movement before their last 0.5 s (slidecast); Verfire/Verstone procs are rolled.
///     Not modelled: the mana imbalance rule (the rules keep the colours within 24), MP, Verraise, positional knockbacks beyond the
///     World's incapacitation list.
/// </summary>
internal sealed class RdmSim : IRuleRuntime
{
    public readonly World W;
    public int T;
    public readonly int GcdMs;
    public uint Trigger = RA.Jolt;
    public readonly Random Rng;

    // gauge
    public int White, Black, Stacks;

    // combo (the melee combo and the finisher chain share the game's one combo slot)
    public uint ComboAction;
    public int ComboUntil = int.MinValue;
    public double ComboTimer => ComboUntil > T ? (ComboUntil - T) / 1000.0 : 0;

    // timing
    public int GcdReadyAt = -100_000, LockUntil = -100_000, LastGcdAt = int.MinValue, Weaves;
    public uint CastAction; public int CastEnd = int.MinValue; private Actor? castTarget; private string castRule = "";
    private bool castAccel;
    private readonly Dictionary<uint, SamCd> cds = new();
    private readonly Dictionary<uint, int> lastUse = new();
    private readonly Dictionary<uint, (int Expire, int Stacks)> self = new();
    private bool wasDead;
    private Actor? pendingTarget;
    private (uint, string) lastChoice;

    // results
    public readonly List<SimEvent> Log = new();
    public readonly List<(int T, string Name, double Potency, double Mult)> Damage = new();
    public readonly Dictionary<string, int> RuleFires = new();
    public readonly Dictionary<string, int> Expired = new();
    public readonly Dictionary<string, int> Uses = new();
    public int Gcds, Cancelled, Unmodelled, AvailMs, DeadMs, ManaLost;
    public bool Trace; public int TraceFrom, TraceTo;

    public static readonly Dictionary<uint, RDef> Defs = new RDef[]
    {
        new(RA.Jolt3, "Jolt III", AKind.Gcd, CastMs: 2000, Potency: 360),
        new(RA.Verthunder3, "Verthunder III", AKind.Gcd, CastMs: 5000, Potency: 440),
        new(RA.Veraero3, "Veraero III", AKind.Gcd, CastMs: 5000, Potency: 440),
        new(RA.Verfire, "Verfire", AKind.Gcd, CastMs: 2000, Potency: 380),
        new(RA.Verstone, "Verstone", AKind.Gcd, CastMs: 2000, Potency: 380),
        new(RA.GrandImpact, "Grand Impact", AKind.Gcd, Potency: 600),
        new(RA.Verflare, "Verflare", AKind.Gcd, Potency: 650),
        new(RA.Verholy, "Verholy", AKind.Gcd, Potency: 650),
        new(RA.Scorch, "Scorch", AKind.Gcd, Potency: 750),
        new(RA.Resolution, "Resolution", AKind.Gcd, Potency: 850),
        new(RA.EnchantedRiposte, "Enchanted Riposte", AKind.Gcd, RecastMs: 1500, Potency: 340, Range: 3, Melee: true),
        new(RA.EnchantedZwerchhau, "Enchanted Zwerchhau", AKind.Gcd, RecastMs: 1500, Potency: 380, Range: 3, Melee: true),
        new(RA.EnchantedRedoublement, "Enchanted Redoublement", AKind.Gcd, RecastMs: 2200, Potency: 560, Range: 3, Melee: true),
        new(RA.Fleche, "Fleche", AKind.Ogcd, CooldownMs: 25000, Potency: 480),
        new(RA.ContreSixte, "Contre Sixte", AKind.Ogcd, CooldownMs: 45000, Potency: 420),
        new(RA.Engagement, "Engagement", AKind.Ogcd, CooldownMs: 35000, Charges: 2, Potency: 180, Range: 3),
        new(RA.Corpsacorps, "Corps-a-corps", AKind.Ogcd, CooldownMs: 35000, Charges: 2, Potency: 130),
        new(RA.Embolden, "Embolden", AKind.Ogcd, CooldownMs: 120000, NeedsTarget: false),
        new(RA.Manafication, "Manafication", AKind.Ogcd, CooldownMs: 110000, NeedsTarget: false),
        new(RA.Acceleration, "Acceleration", AKind.Ogcd, CooldownMs: 55000, Charges: 2, NeedsTarget: false),
        new(RA.Swiftcast, "Swiftcast", AKind.Ogcd, CooldownMs: 40000, NeedsTarget: false),
        new(RA.Prefulgence, "Prefulgence", AKind.Ogcd, CooldownMs: 1000, Potency: 1200),
        new(RA.ViceOfThorns, "Vice of Thorns", AKind.Ogcd, CooldownMs: 1000, Potency: 950),
        new(RA.LucidDreaming, "Lucid Dreaming", AKind.Ogcd, CooldownMs: 60000, NeedsTarget: false),
        new(RA.Addle, "Addle", AKind.Ogcd, CooldownMs: 90000),
        new(RA.MagickBarrier, "Magick Barrier", AKind.Ogcd, CooldownMs: 120000, NeedsTarget: false),
        new(RA.Surecast, "Surecast", AKind.Ogcd, CooldownMs: 120000, NeedsTarget: false),
        new(RA.Wait, "Wait", AKind.Wait),
    }.ToDictionary(d => d.Id);

    /// <summary> Own Embolden: "Increases own magic damage dealt by 10%" (the game data at level 100); all Red Mage damage is magic. </summary>
    public const double EmboldenSelf = 1.10;

    public RdmSim(World w, int gcdMs, int seed)
    {
        W = w;
        GcdMs = gcdMs;
        Rng = new Random(seed);
        foreach (var d in Defs.Values.Where(d => d.CooldownMs > 0)) cds[d.Id] = new SamCd(d.CooldownMs, d.Charges);
        // The logged player stood still through each cast they completed (bar the last 0.5 s slide): the position samples come about
        // once a second, and the World places a step at the end of its interval, which can put it inside those casts.
        stillWindows = w.R.Casts.Where(c => c.Source.Id == w.Me.Id && c.Outcome == CastOutcome.Completed && c.DurationMs > 0)
            .Select(c => (S: c.StartMs, E: c.StartMs + c.DurationMs - 500)).Where(x => x.E > x.S).OrderBy(x => x.S).ToList();
    }

    private readonly List<(int S, int E)> stillWindows;

    /// <summary>
    ///     Moving now, or within LookaheadMs (a player sees the movement coming and doesn't start a cast they would lose: the logged
    ///     Red Mages cancelled 1-9 casts per 10 minutes, the replay without this about 60). The rules' "moving" and the start of a hard
    ///     cast use it; what cancels a cast already going is the movement itself (MovingAt).
    /// </summary>
    public int LookaheadMs = 1500;
    public bool Moving()
    {
        for (var dt = 0; dt <= LookaheadMs; dt += 100) if (MovingAt(T + dt)) return true;
        return false;
    }

    /// <summary> The logged movement at t, except inside the logged player's own completed casts (stillWindows). </summary>
    public bool MovingAt(int t)
    {
        if (!W.Moving(t)) return false;
        int lo = 0, hi = stillWindows.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (stillWindows[mid].E < t) lo = mid + 1;
            else if (stillWindows[mid].S > t) hi = mid - 1;
            else return false;
        }
        return true;
    }

    // ---------------- state helpers ----------------

    public bool Has(uint status) => self.TryGetValue(status, out var s) && s.Expire > T;
    public double Left(uint status) => self.TryGetValue(status, out var s) && s.Expire > T ? Math.Min(999, (s.Expire - T) / 1000.0) : 0;
    public int StacksOf(uint status) => Has(status) ? self[status].Stacks : 0;
    private void Give(uint status, int ms, int stacks = 0) => self[status] = (T + ms, stacks);
    private void Drop(uint status) => self.Remove(status);
    public bool Casting => CastEnd > T;
    private int Scale(int ms) => (int)Math.Round(ms * GcdMs / 2500.0);

    private List<(int T, Actor Target)>? hitsCache;

    /// <summary> SamSim.TargetAt's rule (the logged player's last damaging action's primary target within 15 s, else the biggest targetable boss). </summary>
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
        if (Casting) return false;
        var lockRem = Math.Max(0, LockUntil - T);
        var gcdRem = Math.Max(0, GcdReadyAt - T);
        return Weaves < 2 && gcdRem > 600 + lockRem;
    }

    private bool InCombo(params uint[] ids) => ComboTimer > 0 && ids.Contains(ComboAction);

    public uint OriginalHook(uint id) => id switch
    {
        // Jolt III's button: Resolution after Scorch, Scorch after Verflare/Verholy, Grand Impact when ready, else Jolt III.
        RA.Jolt or RA.Jolt3 => InCombo(RA.Scorch) ? RA.Resolution : InCombo(RA.Verflare, RA.Verholy) ? RA.Scorch : Has(RS.GrandImpactReady) ? RA.GrandImpact : RA.Jolt3,
        RA.Verthunder => Stacks >= 3 ? RA.Verflare : RA.Verthunder3,
        RA.Veraero => Stacks >= 3 ? RA.Verholy : RA.Veraero3,
        RA.Riposte => RA.EnchantedRiposte,
        RA.Zwerchhau => RA.EnchantedZwerchhau,
        RA.Redoublement => RA.EnchantedRedoublement,
        _ => id,
    };

    private bool Swordplay => StacksOf(RS.MagickedSwordplay) > 0;

    public bool ActionReady(uint id)
    {
        if (id == 0) return false;
        if (id >= 1_000_000) return true;
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (W.Dead(W.Me, T)) return false;
        var usable = id switch
        {
            RA.Verfire => Has(RS.VerfireReady),
            RA.Verstone => Has(RS.VerstoneReady),
            RA.GrandImpact => Has(RS.GrandImpactReady),
            RA.Verflare or RA.Verholy => Stacks >= 3,
            RA.Scorch => InCombo(RA.Verflare, RA.Verholy),
            RA.Resolution => InCombo(RA.Scorch),
            RA.EnchantedRiposte => Swordplay || White >= 20 && Black >= 20,
            RA.EnchantedZwerchhau or RA.EnchantedRedoublement => Swordplay || White >= 15 && Black >= 15,
            RA.Prefulgence => Has(RS.PrefulgenceReady),
            RA.ViceOfThorns => Has(RS.ThornedFlourish),
            RA.Manafication => T >= 0,
            _ => true,
        };
        if (!usable) return false;
        if (cds.TryGetValue(id, out var cd) && cd.Charges(T) < 1)
            return d.Kind == AKind.Gcd && cd.Remaining(T) <= Math.Max(0, GcdReadyAt - T) / 1000.0 + 0.5;
        return true;
    }

    /// <summary> A rule's "statusOff": the status goes (Acceleration clicked off before the pull keeps its Grand Impact Ready). </summary>
    public void StatusOff(uint statusId)
    {
        if (!Has(statusId)) return;
        Drop(statusId);
        Log.Add(new SimEvent(T, $"clicked off {RS.Name(statusId)}", "", ""));
    }

    public uint Retarget(uint action, uint trigger, RuleTargetSpec[] targets)
    {
        foreach (var spec in targets)
            if (spec.Kind == RuleTarget.Self) { pendingTarget = W.Me; return action; }
        return 0;
    }

    // ---------------- the simulation step ----------------

    public void Advance(int dt)
    {
        T += dt;
        var dead = W.Dead(W.Me, T);
        if (dead && !wasDead)
        {
            self.Clear(); White = Black = Stacks = 0; ComboUntil = int.MinValue; CastEnd = int.MinValue;
            Log.Add(new SimEvent(T, "DIED", "", ""));
        }
        wasDead = dead;
        if (dead && T >= 0) DeadMs += dt;

        if (CastEnd != int.MinValue)
        {
            if (T >= CastEnd)
            {
                // The cast lands at its end: damage, mana, procs, and Dualcast for the next spell.
                var end = CastEnd; CastEnd = int.MinValue;
                var now = T; T = end;
                Resolve(Defs[CastAction], castTarget, castRule, hardCast: true, accel: castAccel);
                Give(RS.Dualcast, 15000);
                T = now;
            }
            else if (CastEnd - T > 500 && MovingAt(T))
            {
                // Moved before the slidecast window: the cast is lost and the GCD comes back.
                Log.Add(new SimEvent(T, $"CANCELLED {Defs[CastAction].Name}", "", castRule));
                CastEnd = int.MinValue; Cancelled++;
                if (T >= 0) Gcds--;
                GcdReadyAt = T + 100; LockUntil = T + 100;
            }
        }

        foreach (var st in RS.Procs)
            if (self.TryGetValue(st, out var e) && e.Expire <= T)
            {
                self.Remove(st);
                if (T >= 0) Expired[RS.Name(st)] = Expired.GetValueOrDefault(RS.Name(st)) + 1;
                Log.Add(new SimEvent(T, $"EXPIRED {RS.Name(st)}", "", ""));
            }
        if (ComboUntil != int.MinValue && ComboUntil <= T) ComboUntil = int.MinValue;

        if (T >= 0 && !dead && Target is not null && Incapacitated() is null) AvailMs += dt;
    }

    public int NextWake(int poll)
    {
        var next = T + poll;
        if (GcdReadyAt > T && GcdReadyAt < next) next = GcdReadyAt;
        if (LockUntil > T && LockUntil < next) next = LockUntil;
        if (CastEnd > T && CastEnd < next) next = CastEnd;
        return Math.Max(T + 1, next);
    }

    public void Press(IReadOnlyList<CompiledRuleSet> files)
    {
        if (W.Dead(W.Me, T) || Casting) return;
        if (Incapacitated() is not null) return;
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
        if (!Defs.TryGetValue(a, out var d)) { Unmodelled++; return; }
        if (d.Kind == AKind.Wait) return;
        var target = pendingTarget ?? Target;
        var range = d.Melee && Has(RS.Manafication) ? 25 : d.Range;
        if (d.Kind == AKind.Ogcd)
        {
            if (T < LockUntil || !ActionReady(a)) return;
            if (d.NeedsTarget && (target is null || target.IsPlayer || Dist(target) > range)) return;
            Use(d);
            Resolve(d, target, rule, hardCast: false, accel: false);
            LockUntil = T + 600;
            Weaves++;
            return;
        }
        if (!gcdFree || T < LockUntil || !ActionReady(a)) return;
        if (d.NeedsTarget && (target is null || target.IsPlayer || Dist(target) > range)) return;

        Weaves = 0;
        LastGcdAt = T;
        if (T >= 0) Gcds++;
        if (d.CastMs > 0)
        {
            // Acceleration (Verthunder III / Veraero III; it keeps Dualcast), else Dualcast, else Swiftcast, else a hard cast.
            var accel = Has(RS.Acceleration) && a is RA.Verthunder3 or RA.Veraero3;
            if (accel || Has(RS.Dualcast) || Has(RS.Swiftcast))
            {
                if (accel) Drop(RS.Acceleration); else if (Has(RS.Dualcast)) Drop(RS.Dualcast); else Drop(RS.Swiftcast);
                GcdReadyAt = T + GcdMs; LockUntil = T + 600;
                Use(d);
                Resolve(d, target, rule, hardCast: false, accel: accel);
                return;
            }
            if (Moving()) { if (T >= 0) Gcds--; return; }   // a cast can't start while moving
            var cast = Scale(d.CastMs);
            CastAction = a; castTarget = target; castRule = rule; castAccel = false;
            CastEnd = T + cast;
            GcdReadyAt = T + Math.Max(GcdMs, cast);
            LockUntil = CastEnd + 100;
            Use(d);
            Log.Add(new SimEvent(T, $"cast> {d.Name}", "", rule));
            return;
        }
        // Instant GCDs: everything but Grand Impact spends Dualcast.
        if (a != RA.GrandImpact) Drop(RS.Dualcast);
        GcdReadyAt = T + (d.RecastMs > 0 ? d.RecastMs : GcdMs);
        LockUntil = T + 600;
        Use(d);
        Resolve(d, target, rule, hardCast: false, accel: false);
    }

    public string State() =>
        $"W{White} B{Black} st{Stacks} combo {(ComboTimer > 0 ? (Defs.TryGetValue(ComboAction, out var c) ? c.Name : ComboAction.ToString()) : "-")} " +
        string.Join(" ", new[] { RS.Dualcast, RS.Acceleration, RS.Swiftcast, RS.VerfireReady, RS.VerstoneReady, RS.GrandImpactReady, RS.Embolden, RS.Manafication,
            RS.MagickedSwordplay, RS.PrefulgenceReady, RS.ThornedFlourish }.Where(Has).Select(p => $"{RS.Name(p).Replace(" ", "")}@{Left(p):0}")) +
        $" accel {cds[RA.Acceleration].Charges(T)}/{cd(RA.Acceleration):0} emb {cd(RA.Embolden):0} mana {cd(RA.Manafication):0}";

    private double cd(uint id) => Cd(id);

    private void Use(RDef d)
    {
        lastUse[d.Id] = T;
        if (cds.TryGetValue(d.Id, out var c)) c.Use(T);
        if (T >= -20000) Uses[d.Name] = Uses.GetValueOrDefault(d.Name) + 1;
    }

    private void Mana(int w, int b)
    {
        var nw = White + w; var nb = Black + b;
        if (T >= 0) ManaLost += Math.Max(0, nw - 100) + Math.Max(0, nb - 100);
        White = Math.Clamp(nw, 0, 100); Black = Math.Clamp(nb, 0, 100);
    }

    private void Resolve(RDef d, Actor? target, string rule, bool hardCast, bool accel)
    {
        RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
        Log.Add(new SimEvent(T, d.Name, target is null || target == W.Me ? "" : target.Name, rule));
        var potency = d.Potency;
        bool Roll(double p) => Rng.NextDouble() < p;
        var isGcd = d.Kind == AKind.Gcd;
        var keepCombo = false;
        switch (d.Id)
        {
            case RA.Jolt3: Mana(2, 2); break;
            case RA.Verthunder3: Mana(0, 6); if (accel || Roll(0.5)) Give(RS.VerfireReady, 30000); break;
            case RA.Veraero3: Mana(6, 0); if (accel || Roll(0.5)) Give(RS.VerstoneReady, 30000); break;
            case RA.Verfire: Drop(RS.VerfireReady); Mana(0, 5); break;
            case RA.Verstone: Drop(RS.VerstoneReady); Mana(5, 0); break;
            case RA.GrandImpact: Drop(RS.GrandImpactReady); Mana(3, 3); break;
            case RA.Verflare:
            {
                var sure = White > Black;
                Stacks = 0; Mana(0, 11); if (sure || Roll(0.2)) Give(RS.VerfireReady, 30000);
                ComboAction = d.Id; ComboUntil = T + 30000; keepCombo = true; break;
            }
            case RA.Verholy:
            {
                var sure = Black > White;
                Stacks = 0; Mana(11, 0); if (sure || Roll(0.2)) Give(RS.VerstoneReady, 30000);
                ComboAction = d.Id; ComboUntil = T + 30000; keepCombo = true; break;
            }
            case RA.Scorch: Mana(4, 4); ComboAction = d.Id; ComboUntil = T + 30000; keepCombo = true; break;
            case RA.Resolution: Mana(4, 4); break;
            case RA.EnchantedRiposte:
                SpendMelee(20);
                ComboAction = d.Id; ComboUntil = T + 30000; keepCombo = true; break;
            case RA.EnchantedZwerchhau:
                if (!InCombo(RA.EnchantedRiposte)) potency = 190;
                SpendMelee(15);
                ComboAction = d.Id; ComboUntil = T + 30000; keepCombo = true; break;
            case RA.EnchantedRedoublement:
                if (!InCombo(RA.EnchantedZwerchhau)) potency = 190;
                SpendMelee(15); break;
            case RA.Acceleration: Give(RS.Acceleration, 20000); Give(RS.GrandImpactReady, 30000); break;
            case RA.Swiftcast: Give(RS.Swiftcast, 10000); break;
            case RA.Embolden: Give(RS.Embolden, 20000); Give(RS.ThornedFlourish, 30000); break;
            case RA.Manafication:
                Give(RS.Manafication, 30000); Give(RS.MagickedSwordplay, 30000, 3); Give(RS.PrefulgenceReady, 30000);
                ComboUntil = int.MinValue; break;
            case RA.Prefulgence: Drop(RS.PrefulgenceReady); break;
            case RA.ViceOfThorns: Drop(RS.ThornedFlourish); break;
        }
        if (isGcd && !keepCombo) ComboUntil = int.MinValue;

        // Damage: the target within reach. A pre-pull spell that lands in the last second before the pull is the pull's first hit.
        if (potency > 0 && T >= -1000)
        {
            var reach = d.Melee && Has(RS.Manafication) ? 25 : Math.Max(d.Range, 3);
            if (target is { IsPlayer: false } && Dist(target) <= reach)
                Damage.Add((Math.Max(0, T), d.Name, potency, (Has(RS.Embolden) ? EmboldenSelf : 1) * W.RaidBuffs(target, Math.Max(0, T)).Mult));
        }
    }

    private void SpendMelee(int cost)
    {
        if (Swordplay)
        {
            var (exp, st) = self[RS.MagickedSwordplay];
            if (st <= 1) Drop(RS.MagickedSwordplay); else self[RS.MagickedSwordplay] = (exp, st - 1);
        }
        else Mana(-cost, -cost);
        Stacks = Math.Min(3, Stacks + 1);
    }

    public double Cd(uint id)
    {
        if (cds.TryGetValue(id, out var c)) return c.Remaining(T);
        return Defs.TryGetValue(id, out var d) && d.Kind == AKind.Gcd ? Math.Max(0, GcdReadyAt - T) / 1000.0 : 0;
    }

    public bool JustUsed(uint id, double secs) => lastUse.TryGetValue(id, out var t) && T - t <= secs * 1000;

    public Func<double>? Gauge(string field) => field.ToLowerInvariant() switch
    {
        "whitemana" => () => White,
        "blackmana" => () => Black,
        "manastacks" => () => Stacks,
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
        F("charges", Act, 1, a => { var id = a[0].Id; return () => cds.TryGetValue(id, out var c) ? c.Charges(T) : 1; });
        F("chargeCd", Act, 1, a => { var id = a[0].Id; return () => cds.TryGetValue(id, out var c) ? c.ChargeRemaining(T) : 0; });
        F("justUsed", [ParamKind.Action, ParamKind.Number], 1, a => { var id = a[0].Id; var s = a[1].Value; return () => B(JustUsed(id, s?.Invoke() ?? 3)); });
        F("inRange", Act, 1, a => { var id = OriginalHook(a[0].Id); return () => B(Defs.TryGetValue(OriginalHook(id), out var d) && (!d.NeedsTarget || Target is { } t && Dist(t) <= (d.Melee && Has(RS.Manafication) ? 25 : d.Range))); });
        F("combo", Act, 1, a => { var id = a[0].Id; return () => B(ComboTimer > 0 && ComboAction == id); });
        F("enemies", Act, 1, a => { var id = a[0].Id; return () => Target is null ? 0 : Math.Max(1, EnemiesWithin(Defs.TryGetValue(id, out var d) ? d.Range : 25)); });
        F("hook", Act, 1, a => { var id = a[0].Id; return () => OriginalHook(id); });
        F("buff", St, 1, a => { var id = a[0].Id; return () => B(Has(id)); });
        F("buffTime", St, 1, a => { var id = a[0].Id; return () => Left(id); });
        F("buffStacks", St, 1, a => { var id = a[0].Id; return () => StacksOf(id); });
        F("buffAny", St, 1, a => { var id = a[0].Id; return () => B(Has(id) || W.OthersHave(W.Me, id, T)); });
        F("buffAnyTime", St, 1, a => { var id = a[0].Id; return () => Math.Max(Left(id), W.OthersTime(W.Me, id, T)); });
        F("debuff", St, 1, _ => () => 0);
        F("debuffTime", St, 1, _ => () => 0);
        F("debuffAny", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(W.OthersHave(t, id, T)) : 0; });
        F("debuffAnyTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? W.OthersTime(t, id, T) : 0; });
        F("partyWith", St, 1, a => { var id = a[0].Id; return () => { var l = W.Party.Where(p => !W.Dead(p, T)).ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p => p.Id == W.Me.Id && Has(id) || W.OthersHave(p, id, T)) / l.Count; }; });
        Val("gcd", () => GcdMs / 1000.0);
        Val("remainingGcd", () => Math.Max(0, GcdReadyAt - T) / 1000.0);
        Val("canWeave", () => B(CanWeave()));
        Val("casting", () => B(Casting));
        Val("moving", () => B(Moving()));
        Val("stoodStill", () => Moving() ? 0 : 1);
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
        return f;
    }
}
