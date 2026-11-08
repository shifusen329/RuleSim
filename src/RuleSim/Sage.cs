using System.Text.Json;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

internal enum AKind { Gcd, Ogcd, Wait }

internal sealed record ADef(uint Id, string Name, AKind Kind, int CastMs = 0, int RecastMs = -1, int CooldownMs = 0, int Charges = 1,
    int Mp = 0, int Potency = 0, double Range = 25, int Gall = 0, int Sting = 0, bool PartyTarget = false, bool Heal = false);

internal sealed class SimStatus
{
    public int Expire;
    public int From = int.MinValue;
    public int Stacks;
    public int Hits;
}

internal sealed record SimEvent(int T, string What, string Target, string Rule, string Note = "");

/// <summary>
///     Names from the plugin's own constants: sge_symbols.json (generated from SGE_Helper.cs, RoleActions.cs, SCH_Helper.cs),
///     sam_symbols.json (gen_symbols.py, from SAM.cs/SAM_Helper.cs, RoleActions.cs and every job's Buffs/Debuffs, as
///     JobRuleSymbols does). Gauge fields come from the simulated job.
/// </summary>
internal sealed class SimSymbols : IRuleSymbols
{
    private readonly Dictionary<string, uint> actions, statuses;
    private readonly SageSim? sim;
    private readonly Func<string, Func<double>?>? gauge;

    public SimSymbols(string path, Func<string, Func<double>?> gauge) : this(path, (SageSim?)null) => this.gauge = gauge;

    public SimSymbols(string path, SageSim? sim)
    {
        this.sim = sim;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        actions = new(StringComparer.OrdinalIgnoreCase);
        statuses = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.GetProperty("actions").EnumerateObject()) actions[p.Name] = p.Value.GetUInt32();
        foreach (var p in doc.RootElement.GetProperty("statuses").EnumerateObject()) statuses[p.Name] = p.Value.GetUInt32();
    }

    public bool TryResolveAction(string name, out uint id) => actions.TryGetValue(name, out id);
    public bool TryResolveStatus(string name, out uint id) => statuses.TryGetValue(name, out id);

    public bool TryResolveGauge(string field, out Func<double>? getter)
    {
        if (gauge is not null) { getter = gauge(field); return getter is not null; }
        var sim = this.sim!;
        getter = field.ToLowerInvariant() switch
        {
            "addersgall" => () => sim.Gall,
            "addersting" => () => sim.Sting,
            "addersgalltimer" => () => sim.GallTimerMs,
            "eukrasia" => () => sim.Has(sim.W.Me, S.Eukrasia) ? 1 : 0,
            _ => null,
        };
        return getter is not null;
    }
}

internal static class A
{
    public const uint Dosis = 24283, Dosis2 = 24306, Dosis3 = 24312, EDosis3 = 24314, Diagnosis = 24284, EDiagnosis = 24291, Prognosis = 24286,
        EPrognosis2 = 37034, Eukrasia = 24290, Phlegma = 24289, Phlegma3 = 24313, Toxikon = 24304, Toxikon2 = 24316, Psyche = 37033,
        Pneuma = 24318, Dyskrasia = 24297, Dyskrasia2 = 24315, EDyskrasia = 37032, Kardia = 24285, Soteria = 24294, Zoe = 24300,
        Physis = 24288, Physis2 = 24302, Holos = 24310, Panhaima = 24311, Philosophia = 37035, Kerachole = 24298, Ixochole = 24299,
        Taurochole = 24303, Druochole = 24296, Haima = 24305, Krasis = 24317, Rhizomata = 24309, Pepsis = 24301, Icarus = 24295,
        Egeiro = 24287, Lucid = 7562, Swiftcast = 7561, Esuna = 7568, Wait = 1_000_004;
}

internal static class S
{
    // Wrath's ids; the ones Wrath lacks (Taurochole, Physis II, Krasis, Lucid) are the game's.
    public const uint Eukrasia = 2606, Kardia = 2604, Kardion = 2605, Zoe = 2611, EDiag = 2607, EProg = 2609, Haima = 2612, Panhaima = 2613,
        Kerachole = 2618, Holosakos = 3365, Eudaimonia = 3899, EDosis3 = 2616, Soteria = 2610, Swiftcast = 167, Lucid = 1204,
        Taurochole = 2619, Physis2 = 2620, Krasis = 2622, Galvanize = 297, Catalyze = 1918;
    public static readonly uint[] Shields = [EDiag, EProg, Galvanize, Catalyze];
}

/// <summary> The simulated Sage: the rules choose its actions; the world (the logged pull) supplies everything else. </summary>
internal sealed class SageSim : IRuleRuntime
{
    public readonly World W;
    public int T;
    public readonly int GcdMs;
    public double SpeedRatio => GcdMs / 2500.0;

    // resources
    public int Gall = 3, GallTimerMs, Sting = 3, Mp = 10000;
    // timing
    public int GcdReadyAt = -100_000, LockUntil = -100_000, LastGcdAt = int.MinValue, Weaves;
    public (ADef Def, int Start, int End, Actor? Target, string Rule)? Cast;
    private readonly Dictionary<uint, int> readyAt = new();
    private readonly Dictionary<uint, (int N, int Next)> charges = new();
    private readonly Dictionary<uint, int> lastUse = new();
    // statuses the simulated Sage applied: (target id, status) -> status
    private readonly Dictionary<(uint, uint), SimStatus> statuses = new();
    // DoTs: target id -> (expire, next tick, multiplier)
    private readonly Dictionary<uint, (int Expire, int Next, double Mult, Actor Target)> dots = new();
    private int hitIndex, mpTickAt = 3000;
    private int lastMovingAt = int.MinValue, movingSince = int.MinValue;
    private bool wasDead;
    private int? windowStart;
    private bool inWindow;

    // results
    public readonly List<SimEvent> Log = new();
    public readonly List<(int T, string Src, double Potency, double Mult)> Damage = new();
    public readonly List<string> Anomalies = new();
    public readonly Dictionary<string, int> RuleFires = new();
    public int Cancels, Gcds, IdleMs, Unmodelled;
    public int SlideMs = 700;   // movement in the last 0.7 s of a cast (log time) does not cancel it; calibrated
    public double CastLostMs;
    public readonly List<int> DotApplications = new();
    public int GallCappedMs, StingCappedMs, StingLost;
    // Healing the rules deliver, in potency (Metrics converts to HP): direct heals, HoT ticks, Kardia, Eudaimonia.
    public readonly List<(int T, Actor Target, double Potency, string Src)> Heals = new();
    private readonly List<(int T, Actor Target, double Potency, string Src)> pendingTicks = new();
    public int MinMp = 10000;
    private Actor? pendingTarget;
    public bool Trace; public int TraceFrom, TraceTo; public static readonly bool TraceAll = Environment.GetEnvironmentVariable("TRACE_ALL") is not null;
    private (uint, string) lastChoice;
    private string lastEukrasiaRule = "";

    public static readonly Dictionary<uint, ADef> Defs = new ADef[]
    {
        new(A.Dosis3, "Dosis III", AKind.Gcd, CastMs: 1500, Mp: 400, Potency: 370),
        new(A.EDosis3, "Eukrasian Dosis III", AKind.Gcd, RecastMs: 1500, Mp: 400),
        new(A.Eukrasia, "Eukrasia", AKind.Gcd, RecastMs: 1000, Range: 99),
        new(A.Phlegma3, "Phlegma III", AKind.Gcd, CooldownMs: 40000, Charges: 2, Mp: 400, Potency: 600, Range: 6),
        new(A.Toxikon2, "Toxikon II", AKind.Gcd, Potency: 330, Sting: 1),
        new(A.Dyskrasia2, "Dyskrasia II", AKind.Gcd, Mp: 400, Potency: 170, Range: 5),
        new(A.EDyskrasia, "Eukrasian Dyskrasia", AKind.Gcd, RecastMs: 1500, Mp: 400, Range: 5),
        new(A.Pneuma, "Pneuma", AKind.Gcd, CastMs: 1500, CooldownMs: 120000, Mp: 700, Potency: 330, Heal: true),
        new(A.Diagnosis, "Diagnosis", AKind.Gcd, CastMs: 1500, Mp: 400, PartyTarget: true, Heal: true, Range: 30),
        new(A.EDiagnosis, "Eukrasian Diagnosis", AKind.Gcd, RecastMs: 1500, Mp: 900, PartyTarget: true, Heal: true, Range: 30),
        new(A.Prognosis, "Prognosis", AKind.Gcd, CastMs: 2000, Mp: 700, Heal: true, Range: 99),
        new(A.EPrognosis2, "Eukrasian Prognosis II", AKind.Gcd, RecastMs: 1500, Mp: 900, Heal: true, Range: 99),
        new(A.Egeiro, "Egeiro", AKind.Gcd, CastMs: 8000, Mp: 2400, PartyTarget: true, Range: 30),
        new(A.Esuna, "Esuna", AKind.Gcd, CastMs: 1000, Mp: 400, PartyTarget: true, Range: 30),
        new(A.Psyche, "Psyche", AKind.Ogcd, CooldownMs: 60000, Potency: 600),
        new(A.Kardia, "Kardia", AKind.Ogcd, CooldownMs: 5000, PartyTarget: true, Range: 30),
        new(A.Soteria, "Soteria", AKind.Ogcd, CooldownMs: 60000, Range: 99),
        new(A.Zoe, "Zoe", AKind.Ogcd, CooldownMs: 90000, Range: 99),
        new(A.Physis2, "Physis II", AKind.Ogcd, CooldownMs: 60000, Range: 99),
        new(A.Holos, "Holos", AKind.Ogcd, CooldownMs: 120000, Range: 99),
        new(A.Panhaima, "Panhaima", AKind.Ogcd, CooldownMs: 120000, Range: 99),
        new(A.Philosophia, "Philosophia", AKind.Ogcd, CooldownMs: 180000, Range: 99),
        new(A.Kerachole, "Kerachole", AKind.Ogcd, CooldownMs: 30000, Gall: 1, Range: 99),
        new(A.Ixochole, "Ixochole", AKind.Ogcd, CooldownMs: 30000, Gall: 1, Range: 99),
        new(A.Taurochole, "Taurochole", AKind.Ogcd, CooldownMs: 45000, Gall: 1, PartyTarget: true, Range: 30),
        new(A.Druochole, "Druochole", AKind.Ogcd, CooldownMs: 1000, Gall: 1, PartyTarget: true, Range: 30),
        new(A.Haima, "Haima", AKind.Ogcd, CooldownMs: 120000, PartyTarget: true, Range: 30),
        new(A.Krasis, "Krasis", AKind.Ogcd, CooldownMs: 60000, PartyTarget: true, Range: 30),
        new(A.Rhizomata, "Rhizomata", AKind.Ogcd, CooldownMs: 90000, Range: 99),
        new(A.Pepsis, "Pepsis", AKind.Ogcd, CooldownMs: 30000, Range: 99),
        new(A.Lucid, "Lucid Dreaming", AKind.Ogcd, CooldownMs: 60000, Range: 99),
        new(A.Swiftcast, "Swiftcast", AKind.Ogcd, CooldownMs: 40000, Range: 99),
        new(A.Wait, "Wait", AKind.Wait),
    }.ToDictionary(d => d.Id);

    public SageSim(World w, int gcdMs)
    {
        W = w;
        GcdMs = gcdMs;
        foreach (var d in Defs.Values.Where(d => d.Charges > 1)) charges[d.Id] = (d.Charges, int.MaxValue);
    }

    // ---------------- status helpers ----------------

    public bool Has(Actor target, uint status) => statuses.TryGetValue((target.Id, status), out var s) && s.Expire > T && s.From <= T;
    // Audit model (SIM_LAG=1): party statuses land 0.8 s after the press then 0.13 s apart (you first, tanks last), and a hit
    // removes a shield 0.65 s after its line, as in the 2026-10-26 logs.
    public int LastEProgLost = -1;
    public static readonly bool Lag = Environment.GetEnvironmentVariable("SIM_LAG") == "1";
    public static int BreakLagMs => Lag ? 650 : 0;
    public int LandOrder(Actor p) => p.Id == W.Me.Id ? 0 : 1 + W.Party.Where(q => q.Id != W.Me.Id).OrderBy(q => World.IsTank(q) ? 1 : 0).ThenBy(q => q.Id).ToList().FindIndex(q => q.Id == p.Id);
    public int LandMsFor(Actor p) => Lag ? 600 : 0;   // E.Prognosis II lands on everyone with its own action line (2026-10-26 log), ~0.6 s after the press
    public double TimeLeft(Actor target, uint status) => statuses.TryGetValue((target.Id, status), out var s) && s.Expire > T ? (s.Expire - T) / 1000.0 : 0;
    public bool HasAny(Actor target, uint status) => Has(target, status) || W.OthersHave(target, status, T);
    private void Give(Actor target, uint status, int ms, int stacks = 0) => statuses[(target.Id, status)] = new SimStatus { Expire = T + ms, Stacks = stacks };
    private void Remove(Actor target, uint status) => statuses.Remove((target.Id, status));
    public bool Shielded(Actor a) => S.Shields.Any(s => HasAny(a, s));

    public IEnumerable<Actor> LivingParty(bool inRange = true) =>
        W.Party.Where(p => !W.Dead(p, T) && (!inRange || p.Id == W.Me.Id || Vector2Dist(p) <= 30));

    private double Vector2Dist(Actor p) => System.Numerics.Vector2.Distance(W.Pos(p, T), W.Pos(W.Me, T));

    public Actor? Target => W.Target(T);

    public Actor? TT
    {
        get
        {
            var tt = W.BossTarget(Target, T);
            return tt is not null && !W.Dead(tt, T) ? tt : null;
        }
    }

    public Actor? HealTarget => LivingParty().MinBy(p => W.HpPct(p, T));

    // ---------------- IRuleRuntime ----------------

    public bool CanWeave()
    {
        var lockRem = Math.Max(0, LockUntil - T) / 1000.0;
        var castRem = Cast is { } c ? (c.End - T) / 1000.0 : 0;
        var gcdRem = Math.Max(0, GcdReadyAt - T) / 1000.0;
        return Weaves < 2 && lockRem <= 0.6 && castRem <= 0.5 && gcdRem > castRem + 0.6 + lockRem;
    }

    public bool ActionReady(uint id)
    {
        if (id >= 1_000_000) return true;
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (W.Dead(W.Me, T)) return false;
        if (d.Gall > Gall || d.Sting > Sting || d.Mp > Mp) return false;
        if (d.Charges > 1) return charges[id].N >= 1;
        var own = Math.Max(0, (readyAt.TryGetValue(id, out var ra) ? ra : T) - T);
        if (d.Kind == AKind.Ogcd) return own <= 0;
        var gcdRem = Math.Max(0, GcdReadyAt - T);
        return own <= gcdRem + 500;
    }

    public uint OriginalHook(uint id)
    {
        var euk = Has(W.Me, S.Eukrasia);
        return id switch
        {
            A.Dosis or A.Dosis2 or A.Dosis3 => euk ? A.EDosis3 : A.Dosis3,
            A.Diagnosis => euk ? A.EDiagnosis : A.Diagnosis,
            A.Prognosis => euk ? A.EPrognosis2 : A.Prognosis,
            A.Dyskrasia or A.Dyskrasia2 => euk ? A.EDyskrasia : A.Dyskrasia2,
            A.Phlegma => A.Phlegma3,
            A.Toxikon => A.Toxikon2,
            A.Physis => A.Physis2,
            _ => id,
        };
    }

    public uint Retarget(uint action, uint trigger, RuleTargetSpec[] targets)
    {
        foreach (var spec in targets)
            if (Candidate(spec) is { } found)
            {
                pendingTarget = found;
                return action;
            }
        return 0;
    }

    private Actor? Candidate(RuleTargetSpec spec)
    {
        bool Ok(Actor? a) => a is not null && spec.Filters.All(f => (f.AnyShield ? Shielded(a) : HasAny(a, f.StatusId)) != f.Negate);
        IEnumerable<Actor> Others(Func<Actor, bool> role) => LivingParty().Where(p => role(p) && p.Id != W.Me.Id);
        Actor? Lowest(IEnumerable<Actor> l) => l.Where(Ok).MinBy(p => W.HpPct(p, T));
        var c = spec.Kind switch
        {
            RuleTarget.Heal => HealTarget is { } h && Ok(h) ? h : null,
            RuleTarget.Lowest => Lowest(LivingParty()),
            RuleTarget.Tank => Lowest(Others(World.IsTank)),
            RuleTarget.Healer => Lowest(Others(World.IsHealer)),
            RuleTarget.Dps => Lowest(Others(p => !World.IsTank(p) && !World.IsHealer(p))),
            RuleTarget.TargetsTarget => TT is { } tt && Ok(tt) ? tt : null,
            RuleTarget.Self => Ok(W.Me) ? W.Me : null,
            RuleTarget.Raise => W.Party.FirstOrDefault(p => W.Dead(p, T)),
            RuleTarget.CastTarget => CastTargetAt(spec.Cast ?? "", p => Ok(p)),
            _ => null,
        };
        return c;
    }

    /// <summary> The living party member the latest enemy cast of that name was aimed at (as WhmSim and AstSim). </summary>
    private Actor? CastTargetAt(string cast, Func<Actor, bool> ok)
    {
        static string N(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var casts = W.R.Casts.Where(c => c.StartMs <= T && c.Source.Kind is ActorKind.Boss or ActorKind.Enemy or ActorKind.Helper && N(c.Name) == cast).ToList();
        if (casts.Count == 0) return null;
        var last = casts.Max(c => c.StartMs);
        var aimed = casts.Where(c => last - c.StartMs <= 1000 && c.Target is not null).Select(c => c.Target!.Id).ToHashSet();
        return LivingParty().FirstOrDefault(p => aimed.Contains(p.Id) && ok(p));
    }

    // ---------------- the simulation step ----------------

    public void Advance(int dt)
    {
        T += dt;
        var me = W.Me;
        var dead = W.Dead(me, T);
        if (dead && !wasDead)
        {
            if (Cast is { } c) { Log.Add(new SimEvent(T, $"(cast lost: died) {c.Def.Name}", "", c.Rule)); Cast = null; }
            foreach (var k in statuses.Keys.Where(k => k.Item1 == me.Id || k.Item2 == S.Kardion).ToList()) statuses.Remove(k);
            dots.Clear();
            Log.Add(new SimEvent(T, "DIED", "", ""));
        }
        wasDead = dead;

        if (W.Moving(T)) { if (movingSince == int.MinValue) movingSince = T; lastMovingAt = T; }
        else movingSince = int.MinValue;

        // Addersgall: one every 20 s, the timer counts up and stops at 3.
        if (Gall < 3) { GallTimerMs += dt; if (GallTimerMs >= 20000) { Gall++; GallTimerMs -= 20000; } }
        if (Gall >= 3) { GallTimerMs = 0; if (T >= 0) GallCappedMs += dt; }
        if (Sting >= 3 && T >= 0) StingCappedMs += dt;
        if (T >= mpTickAt) { mpTickAt += 3000; Mp = Math.Min(10000, Mp + 200 + (Has(me, S.Lucid) ? 550 : 0)); }

        foreach (var (id, ch) in charges.ToList())
            if (ch.N < Defs[id].Charges && T >= ch.Next)
                charges[id] = (ch.N + 1, ch.N + 1 < Defs[id].Charges ? ch.Next + Defs[id].CooldownMs : int.MaxValue);

        // HoT ticks (Kerachole, Physis II) on living targets.
        for (var i = pendingTicks.Count - 1; i >= 0; i--)
        {
            var pt = pendingTicks[i];
            if (pt.T > T) continue;
            pendingTicks.RemoveAt(i);
            if (!W.Dead(pt.Target, pt.T)) Heals.Add((pt.T, pt.Target, pt.Potency * HealMult(pt.Target, false), pt.Src));
        }

        // DoT ticks while the target can be hit.
        foreach (var (tid, d) in dots.ToList())
        {
            var next = d.Next;
            while (next <= T && next <= d.Expire)
            {
                if (d.Target.IsPresent(next) && d.Target.IsTargetable(next)) Damage.Add((next, "Eukrasian Dosis III (tick)", 75, d.Mult));
                next += 3000;
            }
            if (next > d.Expire) dots.Remove(tid); else dots[tid] = d with { Next = next };
        }

        // Shields break on hits (a non-auto hit, or the second auto on a tank).
        while (hitIndex < W.HitsOnParty.Count && W.HitsOnParty[hitIndex].T + BreakLagMs <= T)
        {
            var (hitT, tgt, auto, _, _) = W.HitsOnParty[hitIndex++];
            foreach (var sh in new[] { S.EDiag, S.EProg })
                if (statuses.TryGetValue((tgt.Id, sh), out var st) && st.Expire > T && st.From <= hitT)
                {
                    st.Hits++;
                    if (!auto || st.Hits >= 2)
                    {
                        statuses.Remove((tgt.Id, sh));
                        if (sh == S.EProg) LastEProgLost = T;
                        if (sh == S.EDiag || tgt.Id == me.Id) { if (Sting >= 3) StingLost++; Sting = Math.Min(3, Sting + 1); }
                    }
                }
        }

        // Casting: cancelled by moving before the last 0.5 s; otherwise it lands.
        if (Cast is { } cast)
        {
            if (W.Moving(T) && T < cast.End - SlideMs)
            {
                Cancels++;
                Gcds--;   // counted when the cast started; a cancelled cast is not a GCD
                CastLostMs += T - cast.Start;
                Log.Add(new SimEvent(T, $"CANCEL {cast.Def.Name}", "", cast.Rule, $"moved {(T - cast.Start) / 1000.0:0.0}s in"));
                Cast = null;
                GcdReadyAt = T;
                LockUntil = T;
            }
            else if (T >= cast.End)
            {
                Cast = null;
                LockUntil = T + 100;
                Land(cast.Def, cast.Target, cast.Rule);
            }
        }
    }

    public void Press(IReadOnlyList<CompiledRuleSet> files, uint trigger)
    {
        if (W.Dead(W.Me, T)) return;
        UpdateRaidWindow();
        pendingTarget = null;
        uint a = 0;
        string rule = "-";
        foreach (var f in files)
        {
            if (!f.Enabled) continue;
            a = f.Evaluate(trigger, this);
            if (a != 0) { rule = $"{f.Name}: {(f.LastFired >= 0 ? f.Rules[f.LastFired].Name : "?")}"; break; }
        }
        if (a == 0) { a = A.Dosis3; rule = "(no rule: stock mode)"; }
        if (Trace && T >= TraceFrom && T <= TraceTo && ((a, rule) != lastChoice || TraceAll && T % 100 == 0))
        {
            lastChoice = (a, rule);
            Console.WriteLine($"   trace {F(T)} -> {(Defs.TryGetValue(a, out var dd) ? dd.Name : a.ToString())} ({rule}) gcdRem={Math.Max(0, GcdReadyAt - T)} lock={Math.Max(0, LockUntil - T)} cast={(Cast is null ? "-" : Cast.Value.Def.Name)} moving={W.Moving(T)} tgt={Target?.Name ?? "none"} dist={(Target is null ? -1 : W.Dist(W.Me, Target, T)):0.0} ready={ActionReady(a)} mp={Mp} gall={Gall}@{GallTimerMs} sting={Sting} avgHp={LivingParty(false).Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Average():0} lowHp={LivingParty().Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Min():0}");
        }

        var gcdUp = Cast is null && T >= GcdReadyAt && T >= LockUntil;
        if (!Defs.TryGetValue(a, out var d))
        {
            Unmodelled++;
            return;
        }
        if (d.Kind == AKind.Wait)
        {
            if (gcdUp && T >= 0 && Target is not null) IdleMs += 20;
            return;
        }
        var target = pendingTarget ?? (d.PartyTarget ? W.Me : Target);
        if (d.Kind == AKind.Ogcd)
        {
            if (Cast is not null || T < LockUntil || !ActionReady(a)) return;
            if (d.PartyTarget && (target is null || W.Dead(target, T) || W.Dist(W.Me, target, T) > d.Range)) return;
            if (!d.PartyTarget && d.Potency > 0 && (target is null || W.Dist(W.Me, target, T) > d.Range)) return;
            Use(d, target, rule);
            LockUntil = T + 600;
            Weaves++;
            return;
        }
        // GCD
        if (!gcdUp || !ActionReady(a))
        {
            return;
        }
        if (d.Potency > 0 || d.Id is A.EDosis3)
            if (target is null || W.Dist(W.Me, target, T) > d.Range) { if (T >= 0 && Target is not null) IdleMs += 20; return; }
        if (d.PartyTarget && (target is null || W.Dead(target, T) || W.Dist(W.Me, target, T) > d.Range)) return;
        var castMs = (int)(d.CastMs * SpeedRatio);
        var swift = castMs > 0 && Has(W.Me, S.Swiftcast);
        if (castMs > 0 && !swift && W.Moving(T)) { IdleMs += 20; return; }   // casts can't start while moving
        var recast = d.RecastMs > 0 ? (int)(d.RecastMs * (d.Id == A.Eukrasia ? 1 : SpeedRatio)) : GcdMs;
        GcdReadyAt = T + recast;
        LastGcdAt = T;
        Weaves = 0;
        Gcds += d.Id == A.Eukrasia ? 0 : 1;
        if (castMs > 0 && !swift)
        {
            Cast = (d, T, T + castMs, target, rule);
            Log.Add(new SimEvent(T, $"cast> {d.Name}", target?.Name ?? "", rule));
        }
        else
        {
            if (swift) Remove(W.Me, S.Swiftcast);
            LockUntil = T + 600;
            Land(d, target, rule);
        }
    }

    private void Use(ADef d, Actor? target, string rule)
    {
        RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
        Land(d, target, rule);
    }

    private void Land(ADef d, Actor? target, string rule)
    {
        var me = W.Me;
        lastUse[d.Id] = T;
        Mp -= d.Mp;   // the game takes MP when the cast completes; a cancelled cast costs nothing
        if (T >= 0) MinMp = Math.Min(MinMp, Mp);
        var zoe = d.Heal && Has(me, S.Zoe);
        void Heal(Actor? tgt, double pot) { if (tgt is not null && !W.Dead(tgt, T)) Heals.Add((T, tgt, pot * HealMult(tgt, zoe), d.Name)); }
        void Hot(Actor tgt, double pot) { for (var k = 1; k <= 5; k++) pendingTicks.Add((T + 3000 * k, tgt, pot, d.Name)); }
        // Kardia (170, Soteria +70% for 4 heals) and Eudaimonia (150 to each holder) on every damaging spell.
        if (d.Id is A.Dosis3 or A.EDosis3 or A.Phlegma3 or A.Toxikon2 or A.Dyskrasia2 or A.EDyskrasia or A.Pneuma)
        {
            if (W.Party.FirstOrDefault(p => Has(p, S.Kardion)) is { } kt)
            {
                var sot = statuses.TryGetValue((me.Id, S.Soteria), out var so) && so.Expire > T && so.Stacks > 0;
                if (sot) so!.Stacks--;
                if (!W.Dead(kt, T)) Heals.Add((T, kt, 170 * (sot ? 1.7 : 1) * HealMult(kt, false), "Kardia"));
            }
            foreach (var p in W.Party.Where(p => Has(p, S.Eudaimonia) && !W.Dead(p, T)))
                Heals.Add((T, p, 150 * HealMult(p, false), "Eudaimonia"));
        }
        switch (d.Id)
        {
            case A.Diagnosis: Heal(target, 450); break;
            case A.EDiagnosis: Heal(target, 300); break;
            case A.Prognosis: foreach (var p in LivingParty().Where(p => Vector2Dist(p) <= 15)) Heal(p, 300); break;
            case A.EPrognosis2: foreach (var p in LivingParty().Where(p => Vector2Dist(p) <= 20)) Heal(p, 100); break;
            case A.Pneuma: foreach (var p in LivingParty().Where(p => Vector2Dist(p) <= 20)) Heal(p, 600); break;
            case A.Ixochole: foreach (var p in LivingParty().Where(p => Vector2Dist(p) <= 15)) Heal(p, 400); break;
            case A.Druochole: Heal(target, 600); break;
            case A.Taurochole: Heal(target, 700); break;
            case A.Holos: foreach (var p in LivingParty()) Heal(p, 300); break;
            case A.Kerachole: foreach (var p in LivingParty()) Hot(p, 100); break;
            case A.Physis2: foreach (var p in LivingParty()) Hot(p, 130); break;
        }
        if (d.Kind == AKind.Gcd) RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
        if (d.Charges > 1)
        {
            var ch = charges[d.Id];
            charges[d.Id] = (ch.N - 1, ch.N == d.Charges ? T + d.CooldownMs : ch.Next);
        }
        else if (d.CooldownMs > 0) readyAt[d.Id] = T + d.CooldownMs;
        if (d.Gall > 0) { Gall -= d.Gall; Mp = Math.Min(10000, Mp + 700); }
        if (d.Sting > 0) Sting -= d.Sting;
        var mult = W.RaidBuffs(Target, T).Mult;
        var note = "";
        if (d.Potency > 0 && target is not null && !target.IsPlayer) Damage.Add((T, d.Name, d.Potency, mult));
        if (d.Heal && Has(me, S.Zoe)) Remove(me, S.Zoe);
        switch (d.Id)
        {
            case A.Eukrasia: Give(me, S.Eukrasia, 999_000); lastEukrasiaRule = rule; break;
            case A.EDosis3:
                Remove(me, S.Eukrasia);
                if (target is not null)
                {
                    if (dots.TryGetValue(target.Id, out var old))
                    {
                        var left = (old.Expire - T) / 1000.0;
                        note = $"refreshed with {left:0.0}s left";
                        if (left > 3.0) Anomalies.Add($"{F(T)} DoT refreshed early ({left:0.0}s left), Eukrasia from: {lastEukrasiaRule}");
                    }
                    dots[target.Id] = (T + 30000, T + 1500, mult, target);
                    Give(target, S.EDosis3, 30000);
                    DotApplications.Add(T);
                }
                break;
            case A.EDiagnosis:
                Remove(me, S.Eukrasia);
                if (target is not null) Give(target, S.EDiag, 30000);
                break;
            case A.EPrognosis2:
            {
                Remove(me, S.Eukrasia);
                var party = LivingParty().Where(p => Vector2Dist(p) <= 20).ToList();
                var already = 100.0 * W.Party.Count(p => !W.Dead(p, T) && Shielded(p)) / Math.Max(1, W.Party.Count(p => !W.Dead(p, T)));
                if (already >= 50) Anomalies.Add($"{F(T)} Eukrasian Prognosis with {already:0}% of the party already shielded ({rule})");
                if (lastUse.TryGetValue(A.EPrognosis2 + 1_000_000_000, out var prev) && T - prev < 5000 && Has(me, S.EProg))
                    Anomalies.Add($"{F(T)} second shield {(T - prev) / 1000.0:0.0}s after the last, which was still up ({rule})");
                lastUse[A.EPrognosis2 + 1_000_000_000] = T;
                foreach (var p in party) { Give(p, S.EProg, 30000); statuses[(p.Id, S.EProg)].From = T + LandMsFor(p); }
                note = $"{party.Count} shielded";
                break;
            }
            case A.EDyskrasia: Remove(me, S.Eukrasia); break;
            case A.Kardia:
                foreach (var k in statuses.Keys.Where(k => k.Item2 == S.Kardion).ToList()) statuses.Remove(k);
                if (target is not null)
                {
                    Give(target, S.Kardion, 999_000);
                    if (!World.IsTank(target)) Anomalies.Add($"{F(T)} Kardia on {(target.Id == me.Id ? "YOURSELF" : $"{target.Name} ({World.RoleOf(target)})")} by {rule}");
                }
                Give(me, S.Kardia, 999_000);
                break;
            case A.Soteria: Give(me, S.Soteria, 15000, 4); break;
            case A.Zoe: Give(me, S.Zoe, 30000); break;
            case A.Swiftcast: Give(me, S.Swiftcast, 10000); break;
            case A.Lucid: Give(me, S.Lucid, 21000); break;
            case A.Physis2: foreach (var p in LivingParty()) Give(p, S.Physis2, 15000); break;
            case A.Holos: foreach (var p in LivingParty()) Give(p, S.Holosakos, 20000); break;
            case A.Panhaima: foreach (var p in LivingParty()) Give(p, S.Panhaima, 15000, 5); break;
            case A.Philosophia: foreach (var p in LivingParty()) Give(p, S.Eudaimonia, 20000); break;
            case A.Kerachole: foreach (var p in LivingParty()) Give(p, S.Kerachole, 15000); break;
            case A.Taurochole:
                if (target is not null)
                {
                    if (HasAny(target, S.Kerachole)) Anomalies.Add($"{F(T)} Taurochole on {target.Name} who already has Kerachole ({rule})");
                    Give(target, S.Taurochole, 15000);
                    note = $"{target.Name} at {W.HpPct(target, T):0}%";
                }
                break;
            case A.Druochole:
                if (target is not null)
                {
                    var hp = W.HpPct(target, T);
                    note = $"{target.Name} at {hp:0}%";
                    if (hp >= 95 && !rule.Contains("caps")) Anomalies.Add($"{F(T)} Druochole on {target.Name} at {hp:0}% ({rule})");
                }
                break;
            case A.Haima: if (target is not null) Give(target, S.Haima, 15000, 5); break;
            case A.Krasis: if (target is not null) Give(target, S.Krasis, 10000); break;
            case A.Rhizomata: Gall = Math.Min(3, Gall + 1); break;
            case A.Pepsis: foreach (var p in W.Party) { Remove(p, S.EDiag); Remove(p, S.EProg); } break;
        }
        Log.Add(new SimEvent(T, d.Name, target is null || target.Id == me.Id || !target.IsPlayer ? "" : target.Name, rule, note));
    }

    // Zoe +50% on the next heal GCD, Philosophia +20% healing magic, Physis II +10% healing received (its first 10 s),
    // Krasis +20% healing received.
    private double HealMult(Actor tgt, bool zoe) =>
        (zoe ? 1.5 : 1) * (Has(W.Me, S.Eudaimonia) ? 1.2 : 1) * (Has(tgt, S.Physis2) && TimeLeft(tgt, S.Physis2) > 5 ? 1.1 : 1) * (Has(tgt, S.Krasis) ? 1.2 : 1);

    public static string F(int ms) => $"{(ms < 0 ? "-" : "")}{Math.Abs(ms) / 60000}:{Math.Abs(ms) / 1000.0 % 60:00.0}";

    private void UpdateRaidWindow()
    {
        if (T < 0) { windowStart = null; inWindow = false; return; }
        var up = W.RaidBuffs(Target, T).Count > 0;
        if (up && !inWindow) windowStart = T;
        inWindow = up;
    }

    private double RaidIn()
    {
        if (inWindow) return 0;
        if (T < 0) return 2;
        double next = windowStart is { } s ? s + 120000 : 2000 + 120000 * Math.Ceiling((T - 2000) / 120000.0);
        return Math.Max(0, next - T) / 1000.0;
    }

    public double StoodStill => W.Moving(T) ? 0 : (lastMovingAt == int.MinValue ? 999 : (T - lastMovingAt) / 1000.0);

    public double Cd(uint id)
    {
        if (!Defs.TryGetValue(id, out var d)) return 0;
        if (d.Charges > 1) { var ch = charges[id]; return ch.N >= 1 ? 0 : Math.Max(0, ch.Next - T) / 1000.0; }
        return Math.Max(0, (readyAt.TryGetValue(id, out var ra) ? ra : T) - T) / 1000.0;
    }

    public double Charges(uint id) =>
        Defs.TryGetValue(id, out var d) && d.Charges > 1 ? charges[id].N : (ActionReady(id) ? 1 : 0);

    public double ChargeCd(uint id) =>
        Defs.TryGetValue(id, out var d) && d.Charges > 1 && charges[id].N < d.Charges ? Math.Max(0, charges[id].Next - T) / 1000.0 : 0;

    public bool JustUsed(uint id, double secs) => lastUse.TryGetValue(id, out var t) && T - t <= secs * 1000;

    // ---------------- the rule functions (same names and meanings as RuleBuiltins.cs) ----------------

    public IReadOnlyDictionary<string, RuleFunction> Functions()
    {
        var f = new Dictionary<string, RuleFunction>(StringComparer.OrdinalIgnoreCase);
        static double B(bool b) => b ? 1 : 0;
        void Fn(string name, ParamKind[] p, int req, Func<RuleArg[], Func<double>> bind) => f[name] = new RuleFunction(name, p, req, bind, "");
        void V(string name, Func<double> get) => Fn(name, [], 0, _ => get);
        ParamKind[] Act = [ParamKind.Action], St = [ParamKind.Status], Nm = [ParamKind.Name];
        // Shared with the Samurai replay (identical definitions, moved 2026-10-21).
        SharedBuiltins.AddMath(f);
        SharedBuiltins.AddEnemyCasts(f, W, () => T);
        SharedBuiltins.AddWorld(f, W, () => T, () => Target);

        Fn("ready", Act, 1, a => { var id = a[0].Id; return () => B(ActionReady(OriginalHook(id))); });
        Fn("cd", Act, 1, a => { var id = a[0].Id; return () => Cd(OriginalHook(id)); });
        Fn("charges", Act, 1, a => { var id = a[0].Id; return () => Charges(OriginalHook(id)); });
        Fn("chargeCd", Act, 1, a => { var id = a[0].Id; return () => ChargeCd(OriginalHook(id)); });
        Fn("justUsed", [ParamKind.Action, ParamKind.Number], 1, a => { var id = a[0].Id; var s = a[1].Value; return () => B(JustUsed(id, s?.Invoke() ?? 3)); });
        Fn("inRange", Act, 1, a =>
        {
            var id = a[0].Id;
            return () =>
            {
                var tgt = Target;
                if (tgt is null) return 0;
                var range = Defs.TryGetValue(OriginalHook(id), out var d) ? d.Range : 25;
                return B(W.Dist(W.Me, tgt, T) <= range);
            };
        });
        Fn("combo", Act, 1, _ => () => 0);
        Fn("enemies", Act, 1, _ => () => Target is null ? 0 : 1);
        Fn("hook", Act, 1, a => { var id = a[0].Id; return () => OriginalHook(id); });
        Fn("buff", St, 1, a => { var id = a[0].Id; return () => B(Has(W.Me, id)); });
        Fn("buffTime", St, 1, a => { var id = a[0].Id; return () => TimeLeft(W.Me, id); });
        Fn("buffStacks", St, 1, a => { var id = a[0].Id; return () => statuses.TryGetValue((W.Me.Id, id), out var s) && s.Expire > T ? s.Stacks : 0; });
        Fn("buffAny", St, 1, a => { var id = a[0].Id; return () => B(HasAny(W.Me, id)); });
        Fn("buffAnyTime", St, 1, a => { var id = a[0].Id; return () => TimeLeft(W.Me, id); });
        Fn("debuff", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(Has(t, id)) : 0; });
        Fn("debuffTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? TimeLeft(t, id) : 0; });
        Fn("debuffAny", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(HasAny(t, id)) : 0; });
        Fn("debuffAnyTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? TimeLeft(t, id) : 0; });
        V("gcd", () => GcdMs / 1000.0);
        V("remainingGcd", () => Math.Max(0, GcdReadyAt - T) / 1000.0);
        V("canWeave", () => B(CanWeave()));
        V("moving", () => B(W.Moving(T)));
        V("stoodStill", () => StoodStill);
        V("movingTime", () => movingSince == int.MinValue ? 0 : (T - movingSince) / 1000.0);
        V("sinceGcd", () => LastGcdAt == int.MinValue ? 99 : (T - LastGcdAt) / 1000.0);
        V("inMelee", () => Target is { } t ? B(W.Dist(W.Me, t, T) <= 3) : 0);
        V("distance", () => Target is { } t ? W.Dist(W.Me, t, T) : 0);
        foreach (var n in new[] { "rear", "flank", "front", "needsPositionals", "countdown", "comboTimer", "comboAction", "raidwide", "stackMarker", "tankbuster", "canEsuna", "healCleansable" })
            V(n, () => 0);
        V("raidwideIn", () => 99);
        V("raidIn", RaidIn);
        V("mp", () => Mp);
        V("casting", () => B(Cast is not null));
        V("partyAvgHp", () => LivingParty(false).Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Average());
        V("partyLowestHp", () => LivingParty().Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Min());
        Fn("partyBelow", [ParamKind.Number], 1, a => { var n = a[0].Value!; return () => LivingParty().Count(p => W.HpPct(p, T) < n()); });
        V("partyAlive", () => LivingParty(false).Count());
        V("partyDead", () => W.Party.Count(p => W.Dead(p, T)));
        Fn("partyWith", St, 1, a => { var id = a[0].Id; return () => { var l = W.Party.Where(p => !W.Dead(p, T)).ToList(); return l.Count == 0 ? 0 : 100.0 * l.Count(p => HasAny(p, id)) / l.Count; }; });
        Fn("partyBeyond", [ParamKind.Number], 1, a => { var y = a[0].Value!; return () => LivingParty(false).Count(p => p.Id != W.Me.Id && W.Dist(W.Me, p, T) > y() + 0.5); });
        Fn("partyLost", St, 1, a => { var id = a[0].Id; return () => { var e = id == S.EProg ? LastEProgLost : W.LastLost(W.Party, id, T); return e < 0 ? 999 : (T - e) / 1000.0; }; });
        V("partyInCombat", () => B(T >= 0));
        V("tankHp", () => LivingParty().Where(World.IsTank).Select(p => W.HpPct(p, T)).DefaultIfEmpty(100).Min());
        Fn("tankHas", St, 1, a => { var id = a[0].Id; return () => B(LivingParty(false).Any(p => World.IsTank(p) && HasAny(p, id))); });
        V("healHp", () => HealTarget is { } h ? W.HpPct(h, T) : 100);
        V("healHpShielded", () => HealTarget is { } h ? Math.Min(100, W.HpPct(h, T) + (World.AutoFix && Shielded(h) ? 10 : 0)) : 100);   // SIM_AUTOS=1: a shield counts as 10% (2026-10-28)
        V("healIsTank", () => B(HealTarget is { } h && World.IsTank(h)));
        V("healIsSelf", () => B(HealTarget?.Id == W.Me.Id));
        Fn("healHas", St, 1, a => { var id = a[0].Id; return () => B(HealTarget is { } h && HasAny(h, id)); });
        Fn("healTime", St, 1, a => { var id = a[0].Id; return () => HealTarget is { } h ? TimeLeft(h, id) : 0; });
        V("healDistance", () => HealTarget is { } h ? W.Dist(W.Me, h, T) : 0);
        V("ttIsParty", () => B(TT is not null));
        V("ttIsTank", () => B(TT is { } t && World.IsTank(t)));
        V("ttIsSelf", () => B(TT?.Id == W.Me.Id));
        V("ttHp", () => TT is { } t ? W.HpPct(t, T) : 100);
        Fn("ttHas", St, 1, a => { var id = a[0].Id; return () => B(TT is { } t && HasAny(t, id)); });
        V("canRaise", () => B(W.Party.Any(p => W.Dead(p, T))));
        V("selfShielded", () => B(Shielded(W.Me)));
        V("tanksUnshielded", () => LivingParty().Count(p => World.IsTank(p) && p.Id != W.Me.Id && !Shielded(p)));
        V("healersUnshielded", () => LivingParty().Count(p => World.IsHealer(p) && p.Id != W.Me.Id && !Shielded(p)));
        V("dpsUnshielded", () => LivingParty().Count(p => !World.IsTank(p) && !World.IsHealer(p) && !Shielded(p)));
        V("partyUnshielded", () => LivingParty().Count(p => !Shielded(p)));
        Fn("button", Act, 1, _ => () => 1);
        V("buttonType", () => 1);
        return f;
    }
}
