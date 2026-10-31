using RaidReplay.Core.Model;
using WrathCombo.CustomRules;
using static SharedBuiltins;

/// <summary> White Mage action ids (WrathCombo's WHM_Helper.cs and RoleActions.cs). </summary>
internal static class WA
{
    public const uint Glare1 = 16533, Glare3 = 25859, Glare4 = 37009, Stone1 = 119, Stone2 = 127, Stone3 = 3568, Stone4 = 7431, Assize = 3571,
        Holy = 139, Holy3 = 25860, Aero = 121, Aero2 = 132, Dia = 16532, Cure = 120, Cure2 = 135, Cure3 = 131, Regen = 137,
        Solace = 16531, Rapture = 16534, Raise = 125, Benediction = 140, Misery = 16535, Medica1 = 124, Medica2 = 133, Medica3 = 37010,
        Tetragrammaton = 3570, Benison = 7432, Aquaveil = 25861, Caress = 37011, Asylum = 3569, Bell = 25862, BellRecast = 28509,
        ThinAir = 7430, PoM = 136, Plenary = 7433, Temperance = 16536, Lucid = 7562, Swiftcast = 7561, Surecast = 7559, Esuna = 7568,
        Rescue = 7571, Repose = 16560, Sprint = 3, Wait = 1_000_004;

    public static readonly uint[] StoneGlare = [Stone1, Stone2, Stone3, Stone4, Glare1, Glare3];
}

/// <summary> White Mage statuses (Wrath's WHM.Buffs/Debuffs; the ones Wrath lacks are the game's, from the Status sheet). </summary>
internal static class WS
{
    public const uint Regen = 158, Medica3 = 3880, PoM = 157, ThinAir = 1217, Benison = 1218, Aquaveil = 2708, SacredSight = 3879,
        Bell = 2709, DivineGrace = 3881, Temperance = 1872, Dia = 1871, Swiftcast = 167, Lucid = 1204,
        // not in Wrath's WHM class (game ids)
        TemperanceParty = 1873, Confession = 1219, CaressShield = 3903, DivineAura = 3904, Asylum = 1911, AsylumBuff = 1912;
    public static readonly uint[] OtherShields = [2607, 2609, 297, 1918];   // RuleBuiltins.ShieldStatuses: E.Diagnosis, E.Prognosis, Galvanize, Catalyze
}

/// <summary>
///     One White Mage action. Range: yalms to an enemy's hitbox edge (enemy actions), to the ally (PartyTarget), or the
///     self-centred radius (Assize's damage, Holy III). CastMs is the base cast, scaled by spell speed and Presence of Mind.
/// </summary>
internal sealed record WDef(uint Id, string Name, AKind Kind, double Range = 25, int CastMs = 0, int CooldownMs = 0, int Charges = 1,
    int Mp = 0, int Potency = 0, bool PartyTarget = false, bool HealSpell = false, bool SelfAoe = false);

/// <summary>
///     The simulated White Mage (2026-10-21): the rules choose its actions every 20 ms; position, target, movement, hits,
///     deaths, enemy casts and other players' buffs come from the logged player (World.Me). Timing, movement, incapacitation
///     and idle accounting follow SamSim; MP, heals and party statuses follow SageSim.
/// </summary>
internal sealed class WhmSim : IRuleRuntime
{
    public readonly World W;
    public int T;
    /// <summary> Base GCD (2.5 s scaled by spell speed); Presence of Mind takes 20% off recast and cast. </summary>
    public readonly int GcdMs;
    public int SlideMs = 700;
    public uint Trigger = WA.Glare3;
    public bool Trace; public int TraceFrom, TraceTo;

    // gauge and MP
    public int Lily = 3, Blood = 3, LilyTimerMs, Mp = 10000;
    private int mpTickAt = -15000;

    // timing
    public int GcdReadyAt = -100_000, LockUntil = -100_000, LastGcdAt = int.MinValue, Weaves;
    public (WDef Def, int Start, int End, Actor? Target, string Rule, bool Swift)? Cast;
    private readonly Dictionary<uint, SamCd> cds = new();
    private readonly Dictionary<uint, int> lastUse = new();
    // statuses the simulated White Mage applied: (target id, status) -> status
    private readonly Dictionary<(uint, uint), SimStatus> st = new();
    // Dia: target id -> (expire, next tick, multiplier)
    private readonly Dictionary<uint, (int Expire, int Next, double Mult, Actor Target, int Land)> dots = new();
    private int lastMovingAt = int.MinValue, movingSince = int.MinValue;
    private bool wasDead;
    private int? windowStart;
    private bool inWindow;
    private Actor? pendingTarget;
    private (uint, string) lastChoice;
    private int hitIndex;
    // Liturgy of the Bell: where it stands, when it ends, the last proc
    private System.Numerics.Vector2 bellPos; private int bellUntil = int.MinValue, bellLastProc = int.MinValue;
    // Asylum: where, until, next tick
    private System.Numerics.Vector2 asylumPos; private int asylumUntil = int.MinValue, asylumNext;
    private readonly List<(int T, Actor Target, double Potency, string Src)> pendingTicks = new();

    // ---------------- results ----------------
    public readonly List<SimEvent> Log = new();
    public readonly List<(int T, string Src, double Potency, double Mult)> Damage = new();
    public readonly List<(int T, Actor Target, double Potency, string Src)> Heals = new();
    public readonly Dictionary<string, int> RuleFires = new();
    public readonly List<string> Anomalies = new();
    public int Cancels, Gcds, Unmodelled, CastsLostTarget, BloodWasted, MinMp = 10000;
    public readonly List<(int T, string Name, string Rule, int MovedInMs)> CancelLog = new();
    public readonly List<(int T, string Phase, string Reason, string Rule, int Ms)> Idle = new();
    public int AvailMs, BusyMs, DeadMs, IncapMs, DiaUpMs, LilyCappedMs, ClipMs;
    public readonly Dictionary<string, (int Avail, int Busy, int Dia)> PhaseMs = new();
    public readonly List<int> DotApplications = new();
    /// <summary> Every oGCD press: (time, action, rule, GCD time left at the press). </summary>
    public readonly List<(int T, string Name, string Rule, int GcdLeft)> Ogcds = new();
    /// <summary> Caress shields and Benisons on party members: (target, landed, ended, how). </summary>
    public readonly List<(Actor Target, uint Status, int From, int To, string How)> ShieldLog = new();
    /// <summary> 2026-10-29 (tank-care audit): Lilies and Blood Lilies every 500 ms. </summary>
    public readonly List<(int T, int Lily, int Blood)> GaugeLog = new();
    public (int Lily, int Blood) GaugeAt(int t)
    {
        int lo = 0, hi = GaugeLog.Count - 1, ans = -1;
        while (lo <= hi) { var mid = (lo + hi) / 2; if (GaugeLog[mid].T <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1; }
        return ans < 0 ? (3, 3) : (GaugeLog[ans].Lily, GaugeLog[ans].Blood);
    }

    public static readonly Dictionary<uint, WDef> Defs = new WDef[]
    {
        new(WA.Glare3, "Glare III", AKind.Gcd, CastMs: 1500, Mp: 400, Potency: 310),
        new(WA.Glare4, "Glare IV", AKind.Gcd, Potency: 640),
        new(WA.Dia, "Dia", AKind.Gcd, Mp: 400, Potency: 75),
        new(WA.Misery, "Afflatus Misery", AKind.Gcd, Potency: 1240),
        new(WA.Rapture, "Afflatus Rapture", AKind.Gcd, Range: 0, HealSpell: true),
        new(WA.Solace, "Afflatus Solace", AKind.Gcd, Range: 30, PartyTarget: true, HealSpell: true),
        new(WA.Holy3, "Holy III", AKind.Gcd, Range: 8, CastMs: 1500, Mp: 400, Potency: 150, SelfAoe: true),
        new(WA.Medica1, "Medica", AKind.Gcd, Range: 0, CastMs: 2000, Mp: 900, HealSpell: true),
        new(WA.Medica3, "Medica III", AKind.Gcd, Range: 0, CastMs: 2000, Mp: 1000, HealSpell: true),
        new(WA.Cure, "Cure", AKind.Gcd, Range: 30, CastMs: 1500, Mp: 400, PartyTarget: true, HealSpell: true),
        new(WA.Cure2, "Cure II", AKind.Gcd, Range: 30, CastMs: 2000, Mp: 1000, PartyTarget: true, HealSpell: true),
        new(WA.Cure3, "Cure III", AKind.Gcd, Range: 30, CastMs: 2000, Mp: 1500, PartyTarget: true, HealSpell: true),
        new(WA.Regen, "Regen", AKind.Gcd, Range: 30, Mp: 400, PartyTarget: true, HealSpell: true),
        new(WA.Raise, "Raise", AKind.Gcd, Range: 30, CastMs: 8000, Mp: 2400, PartyTarget: true),
        new(WA.Esuna, "Esuna", AKind.Gcd, Range: 30, CastMs: 1000, Mp: 400, PartyTarget: true),
        new(WA.PoM, "Presence of Mind", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(WA.Assize, "Assize", AKind.Ogcd, Range: 20, CooldownMs: 40000, Potency: 400, SelfAoe: true),
        new(WA.ThinAir, "Thin Air", AKind.Ogcd, Range: 0, CooldownMs: 60000, Charges: 2),
        new(WA.Tetragrammaton, "Tetragrammaton", AKind.Ogcd, Range: 30, CooldownMs: 60000, Charges: 2, PartyTarget: true),
        new(WA.Benison, "Divine Benison", AKind.Ogcd, Range: 30, CooldownMs: 30000, Charges: 2, PartyTarget: true),
        new(WA.Aquaveil, "Aquaveil", AKind.Ogcd, Range: 30, CooldownMs: 60000, PartyTarget: true),
        new(WA.Benediction, "Benediction", AKind.Ogcd, Range: 30, CooldownMs: 180000, PartyTarget: true),
        new(WA.Asylum, "Asylum", AKind.Ogcd, Range: 0, CooldownMs: 90000),
        new(WA.Bell, "Liturgy of the Bell", AKind.Ogcd, Range: 0, CooldownMs: 180000),
        new(WA.Temperance, "Temperance", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(WA.Caress, "Divine Caress", AKind.Ogcd, Range: 0, CooldownMs: 1000),
        new(WA.Plenary, "Plenary Indulgence", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(WA.Lucid, "Lucid Dreaming", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(WA.Swiftcast, "Swiftcast", AKind.Ogcd, Range: 0, CooldownMs: 40000),
        new(WA.Surecast, "Surecast", AKind.Ogcd, Range: 0, CooldownMs: 120000),
        new(WA.Rescue, "Rescue", AKind.Ogcd, Range: 30, CooldownMs: 120000, PartyTarget: true),
        new(WA.Sprint, "Sprint", AKind.Ogcd, Range: 0, CooldownMs: 60000),
        new(WA.Wait, "Wait", AKind.Wait),
    }.ToDictionary(d => d.Id);

    public static readonly HashSet<string> GcdNames = Defs.Values.Where(d => d.Kind == AKind.Gcd).Select(d => d.Name).ToHashSet();

    public WhmSim(World w, int gcdMs)
    {
        W = w;
        GcdMs = gcdMs;
        foreach (var d in Defs.Values.Where(d => d.CooldownMs > 0)) cds[d.Id] = new SamCd(d.CooldownMs, d.Charges);
    }

    // ---------------- state helpers ----------------

    public bool Has(Actor a, uint status) => st.TryGetValue((a.Id, status), out var s) && s.Expire > T;
    public bool Has(uint status) => Has(W.Me, status);
    public double Left(Actor a, uint status) => st.TryGetValue((a.Id, status), out var s) && s.Expire > T ? (s.Expire - T) / 1000.0 : 0;
    public int Stacks(Actor a, uint status) => st.TryGetValue((a.Id, status), out var s) && s.Expire > T ? s.Stacks : 0;
    private void Give(Actor a, uint status, int ms, int stacks = 0) => st[(a.Id, status)] = new SimStatus { Expire = T + ms, Stacks = stacks };
    private void Drop(Actor a, uint status) => st.Remove((a.Id, status));
    public bool HasAny(Actor a, uint status) => Has(a, status) || DotOn(a, status) || W.OthersHave(a, status, T);
    private bool DotOn(Actor a, uint status) => status == WS.Dia && dots.TryGetValue(a.Id, out var d) && d.Expire > T;
    public bool Shielded(Actor a) => WS.OtherShields.Any(s => W.OthersHave(a, s, T));

    public bool PoMUp => Has(WS.PoM);
    public int RecastMs => PoMUp ? (int)Math.Round(GcdMs * 0.8) : GcdMs;
    public int CastTime(WDef d) => d.CastMs == 0 ? 0 : (int)Math.Round(d.CastMs * (GcdMs / 2500.0) * (PoMUp ? 0.8 : 1));

    public Actor? Target => SamSim.TargetAt(W, T);
    /// <summary> Party mitigations' reach at each press: (time, action, party members in its radius, living, rule). </summary>
    public readonly List<(int T, string What, int Reached, int Alive, string Rule)> ReachLog = [];
    public double Dist(Actor? t) => t is null ? 99 : W.Dist(W.Me, t, T);
    private double AllyDist(Actor p) => System.Numerics.Vector2.Distance(W.Pos(p, T), W.Pos(W.Me, T));

    public IEnumerable<Actor> LivingParty(bool inRange = true) =>
        W.Party.Where(p => !W.Dead(p, T) && (!inRange || p.Id == W.Me.Id || AllyDist(p) <= 30));

    /// <summary> 2026-10-29: HP % the rules see. Null: the logged HP (as before). WhmCommands --cfhp: the tanks' counterfactual
    /// HP from WhmTankModel (closed loop: the rules react to the HP they produce). </summary>
    public Func<Actor, int, double>? HpView;
    public double Hp(Actor p) => HpView is null ? W.HpPct(p, T) : HpView(p, T);

    public Actor? HealTarget => LivingParty().MinBy(p => Hp(p));

    public Actor? TT => W.BossTarget(Target, T) is { } tt && !W.Dead(tt, T) ? tt : null;

    public double DiaLeft(Actor? t) => t is not null && dots.TryGetValue(t.Id, out var d) && d.Expire > T ? (d.Expire - T) / 1000.0 : 0;

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
        WA.Stone1 or WA.Stone2 or WA.Stone3 or WA.Stone4 or WA.Glare1 => WA.Glare3,
        WA.Aero or WA.Aero2 => WA.Dia,
        WA.Holy => WA.Holy3,
        WA.Medica2 => WA.Medica3,
        WA.Bell when Has(WS.Bell) => WA.BellRecast,
        _ => id,
    };

    private bool MpOk(WDef d) => d.Mp <= Mp || Has(WS.ThinAir);

    /// <summary> Wrath's ActionReady: learned, a charge or the GCD, and the game's own usability (MP, Lilies, Sacred Sight, Divine Grace). </summary>
    public bool ActionReady(uint id)
    {
        if (id == 0) return false;
        if (id >= 1_000_000) return true;
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (W.Dead(W.Me, T)) return false;
        var usable = id switch
        {
            WA.Glare4 => Has(WS.SacredSight),
            WA.Misery => Blood >= 3,
            WA.Rapture or WA.Solace => Lily >= 1,
            WA.Caress => Has(WS.DivineGrace),
            _ => MpOk(d),
        };
        if (!usable) return false;
        if (d.Kind == AKind.Gcd) return true;
        return !cds.TryGetValue(id, out var cd) || cd.Charges(T) >= 1;
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
                RuleTarget.Dps => Lowest(Others(p => !World.IsTank(p) && !World.IsHealer(p))),
                RuleTarget.TargetsTarget => TT is { } tt && Ok(tt) ? tt : null,
                RuleTarget.Self => Ok(W.Me) ? W.Me : null,
                RuleTarget.Raise => W.Party.FirstOrDefault(p => W.Dead(p, T)),
                RuleTarget.CastTarget => CastTargetAt(spec.Cast ?? "", p => Ok(p)),
                _ => null,
            };
            if (c is not null) { pendingTarget = c; return action; }
        }
        return 0;
    }

    /// <summary> castTarget(Name): the living party member the latest cast of that name (started by now) was aimed at. </summary>
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
            if (Cast is { } c) { Log.Add(new SimEvent(T, $"(cast lost: died) {c.Def.Name}", "", c.Rule)); Cast = null; Gcds--; }
            // Death clears your statuses, your DoT and the Lily gauge (the guide: "You lose all accumulated Healing Lilies upon death").
            foreach (var k in st.Keys.Where(k => k.Item1 == me.Id).ToList()) st.Remove(k);
            dots.Clear();
            EndBell(T);
            Lily = 0; Blood = 0; LilyTimerMs = 0;
            Log.Add(new SimEvent(T, "DIED", "", ""));
        }
        wasDead = dead;
        if (dead && T >= 0) DeadMs += dt;

        if (W.Moving(T)) { if (movingSince == int.MinValue) movingSince = T; lastMovingAt = T; }
        else movingSince = int.MinValue;

        // Lilies: one every 20 s in combat (timer counts up, as Wrath reads gauge.LilyTimer), stopped at 3.
        if (T >= 0 && !dead)
        {
            if (Lily < 3) { LilyTimerMs += dt; if (LilyTimerMs >= 20000) { Lily++; LilyTimerMs -= 20000; } }
            if (Lily >= 3) { LilyTimerMs = 0; LilyCappedMs += dt; }
        }
        if (T % 500 == 0) GaugeLog.Add((T, Lily, Blood));
        // MP: 200 every 3 s, 550 more under Lucid Dreaming.
        if (T >= mpTickAt) { mpTickAt += 3000; if (!dead) Mp = Math.Min(10000, Mp + 200 + (Has(WS.Lucid) ? 550 : 0)); }

        // HoT ticks (Medica III, Regen, Divine Aura) on living targets.
        for (var i = pendingTicks.Count - 1; i >= 0; i--)
        {
            var pt = pendingTicks[i];
            if (pt.T > T) continue;
            pendingTicks.RemoveAt(i);
            if (!W.Dead(pt.Target, pt.T)) Heals.Add(pt);
        }

        // Dia ticks while the target can be hit.
        foreach (var (tid, d) in dots.ToList())
        {
            var next = d.Next;
            while (next <= T && next <= d.Expire)
            {
                if (d.Target.IsPresent(next) && d.Target.IsTargetable(next) && next >= 0) Damage.Add((next, "Dia (tick)", 75, d.Mult));
                next += 3000;
            }
            if (next > d.Expire) dots.Remove(tid); else dots[tid] = d with { Next = next };
        }

        // Asylum ticks: 100 potency every 3 s to whoever stands in it (10 y), +10% healing received inside.
        if (asylumUntil != int.MinValue && T >= asylumNext && asylumNext <= asylumUntil)
        {
            foreach (var p in W.Party.Where(p => !W.Dead(p, T) && System.Numerics.Vector2.Distance(W.Pos(p, T), asylumPos) <= 10))
                Heals.Add((T, p, 110, "Asylum"));
            asylumNext += 3000;
            if (asylumNext > asylumUntil) asylumUntil = int.MinValue;
        }

        // Hits on the party: shields (Divine Caress, Divine Benison) break on a non-auto hit or the second auto; the
        // Bell heals on each hit on the White Mage (once a second).
        while (hitIndex < W.HitsOnParty.Count && W.HitsOnParty[hitIndex].T <= T)
        {
            var (ht, tgt, auto, _, _) = W.HitsOnParty[hitIndex++];
            foreach (var sh in new[] { WS.CaressShield, WS.Benison })
                // a barrier takes effect about 0.6 s after the press (Metrics.LandMs; the guide: "a slight delay before being applied")
                if (st.TryGetValue((tgt.Id, sh), out var s) && s.Expire > ht && ht >= s.Expire - (sh == WS.CaressShield ? 10000 : 15000) + Metrics.LandMs)
                {
                    s.Hits++;
                    if (!auto || s.Hits >= 2) EndShield(tgt, sh, s, ht, "broken");
                }
            if (tgt.Id == me.Id && ht >= 0 && bellUntil > ht && Stacks(me, WS.Bell) > 0 && ht - bellLastProc >= 1000)
            {
                bellLastProc = ht;
                var s = st[(me.Id, WS.Bell)];
                s.Stacks--;
                foreach (var p in W.Party.Where(p => !W.Dead(p, ht) && System.Numerics.Vector2.Distance(W.Pos(p, ht), bellPos) <= 20))
                    Heals.Add((ht, p, 400, "Liturgy of the Bell"));
                if (s.Stacks <= 0) { st.Remove((me.Id, WS.Bell)); EndBell(ht); }
            }
        }
        // Shields that ran out; the Bell running out heals 200 per stack left.
        foreach (var k in st.Where(kv => (kv.Key.Item2 is WS.CaressShield or WS.Benison) && kv.Value.Expire <= T).Select(kv => kv.Key).ToList())
            if (W.Party.FirstOrDefault(p => p.Id == k.Item1) is { } who) EndShield(who, k.Item2, st[k], st[k].Expire, "expired");
        if (bellUntil != int.MinValue && T >= bellUntil)
        {
            var left = st.TryGetValue((me.Id, WS.Bell), out var b) ? b.Stacks : 0;
            if (left > 0)
                foreach (var p in W.Party.Where(p => !W.Dead(p, T) && System.Numerics.Vector2.Distance(W.Pos(p, T), bellPos) <= 20))
                    Heals.Add((T, p, 200 * left, "Liturgy of the Bell (end)"));
            st.Remove((me.Id, WS.Bell));
            EndBell(T);
        }

        var incap = SamSim.Incapacitated(W, T);
        if (Cast is { } cast)
        {
            var reason = incap is not null ? $"interrupted ({incap})" : W.Moving(T) ? "moved" : null;
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

        // Accounting while the fight is on. Time incapacitated is downtime.
        var tgtNow = Target;
        if (T >= 0 && !dead && tgtNow is not null && incap is not null) IncapMs += dt;
        if (T >= 0 && !dead && tgtNow is not null && incap is null)
        {
            var phase = W.R.PhaseAt(T);
            var p = PhaseMs.GetValueOrDefault(phase);
            p.Avail += dt; AvailMs += dt;
            if (Cast is not null || T < GcdReadyAt) { p.Busy += dt; BusyMs += dt; }
            if (DiaLeft(tgtNow) > 0) { p.Dia += dt; DiaUpMs += dt; }
            PhaseMs[phase] = p;
        }
    }

    /// <summary> Liturgy of the Bell periods: (press, end). </summary>
    public readonly List<(int From, int To)> BellLog = new();

    private void EndBell(int at)
    {
        if (bellUntil != int.MinValue && BellLog.Count > 0 && BellLog[^1].To == int.MaxValue) BellLog[^1] = (BellLog[^1].From, at);
        bellUntil = int.MinValue;
    }

    /// <summary> At the pull's end: shields still up go to the log (so coverage queries see them). </summary>
    public void Finish()
    {
        foreach (var k in st.Where(kv => kv.Key.Item2 is WS.CaressShield or WS.Benison).Select(kv => kv.Key).ToList())
            if (W.Party.FirstOrDefault(p => p.Id == k.Item1) is { } who)
            {
                var s = st[k];
                st.Remove(k);
                ShieldLog.Add((who, k.Item2, s.Expire - (k.Item2 == WS.CaressShield ? 10000 : 15000), Math.Min(s.Expire, T), "pull ended"));
            }
        if (BellLog.Count > 0 && BellLog[^1].To == int.MaxValue) BellLog[^1] = (BellLog[^1].From, Math.Min(T, bellUntil == int.MinValue ? T : bellUntil));
    }

    private void EndShield(Actor who, uint status, SimStatus s, int at, string how)
    {
        st.Remove((who.Id, status));
        var from = s.Expire - (status == WS.CaressShield ? 10000 : 15000);
        ShieldLog.Add((who, status, from, at, how));
        // Divine Caress: Divine Aura, 200 potency every 3 s for 15 s, when the barrier ends.
        if (status == WS.CaressShield)
            for (var k = 1; k <= 5; k++) pendingTicks.Add((at + 3000 * k, who, 200, "Divine Aura"));
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
            Console.WriteLine($"   trace {SageSim.F(T)} -> {(Defs.TryGetValue(a, out var dd) ? dd.Name : a.ToString())} ({rule}) gcdRem={Math.Max(0, GcdReadyAt - T)} lock={Math.Max(0, LockUntil - T)} cast={(Cast is null ? "-" : Cast.Value.Def.Name)} moving={W.Moving(T)} tgt={Target?.Name ?? "none"} dist={Dist(Target):0.0} ready={ActionReady(a)} mp={Mp} lily={Lily}@{LilyTimerMs} blood={Blood} dia={DiaLeft(Target):0.0} raid={W.RaidBuffs(Target, T).Count} raidIn={RaidIn():0}");
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
            if (d.PartyTarget && (target is null || !target.IsPlayer || W.Dead(target, T) || AllyDist(target) > d.Range)) return;
            Ogcds.Add((T, d.Name, rule, Math.Max(0, GcdReadyAt - T)));
            RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
            Land(d, d.PartyTarget ? target : Target, rule);
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
        if (d.PartyTarget && (target is null || !target.IsPlayer || (d.Id != WA.Raise && W.Dead(target, T)) || AllyDist(target) > d.Range)) { AddIdle($"no target: {d.Name}", rule); return; }
        var castMs = CastTime(d);
        var swift = castMs > 0 && Has(WS.Swiftcast);
        if (castMs > 0 && !swift && W.Moving(T)) { AddIdle($"moving, cast held: {d.Name}", rule); return; }
        GcdReadyAt = T + RecastMs;
        LastGcdAt = T;
        Weaves = 0;
        Gcds++;
        RuleFires[rule] = RuleFires.GetValueOrDefault(rule) + 1;
        if (castMs > 0 && !swift)
        {
            Cast = (d, T, T + castMs, target, rule, false);
            Log.Add(new SimEvent(T, $"cast> {d.Name}", target is { IsPlayer: true } && target.Id != W.Me.Id ? target.Name : "", rule));
        }
        else
        {
            if (swift) Drop(W.Me, WS.Swiftcast);
            LockUntil = T + 600;
            Land(d, target, rule);
        }
    }

    private double HealMult(WDef d, Actor tgt) => (d.HealSpell && Has(WS.Temperance) ? 1.2 : 1);

    private void Land(WDef d, Actor? target, string rule)
    {
        var me = W.Me;
        lastUse[d.Id] = T;
        if (cds.TryGetValue(d.Id, out var cd)) cd.Use(T);
        if (d.Mp > 0)
        {
            if (Has(WS.ThinAir)) Drop(me, WS.ThinAir);
            else Mp -= d.Mp;   // taken when the cast completes; a cancelled cast costs nothing
        }
        if (T >= 0) MinMp = Math.Min(MinMp, Mp);
        var note = "";
        void Heal(Actor? tgt, double pot, string src) { if (tgt is not null && !W.Dead(tgt, T)) Heals.Add((T, tgt, pot * HealMult(d, tgt), src)); }
        IEnumerable<Actor> Near(double r) => LivingParty(false).Where(p => AllyDist(p) <= r);
        double Conf(Actor p) => Has(p, WS.Confession) ? 200 : 0;
        // damage, prepull casts landing in the last 3 s of the countdown count at the pull
        void Hit(Actor? tgt, double pot, string src)
        {
            if (tgt is null || tgt.IsPlayer || T < -3000) return;
            Damage.Add((Math.Max(0, T), src, pot, W.RaidBuffs(tgt, T).Mult));
        }
        // other enemies near the target (Misery 50% less, Glare IV 40% less) or near you (Assize, Holy III: full)
        IEnumerable<Actor> OtherEnemies(Actor? around, double r) => W.Bosses.Where(b => b != around && b.IsPresent(T) && b.IsTargetable(T) && !b.IsHidden(T)
            && (around is null ? W.Dist(W.Me, b, T) <= r : W.Dist(around, b, T) <= r));
        switch (d.Id)
        {
            case WA.Glare3: Hit(target, 310, d.Name); break;
            case WA.Glare4:
            {
                Hit(target, 640, d.Name);
                foreach (var o in OtherEnemies(target, 5)) Hit(o, 384, d.Name + " (2nd target)");
                var s = st[(me.Id, WS.SacredSight)];
                if (--s.Stacks <= 0) Drop(me, WS.SacredSight);
                break;
            }
            case WA.Misery:
                Hit(target, 1240, d.Name);
                foreach (var o in OtherEnemies(target, 5)) Hit(o, 620, d.Name + " (2nd target)");
                Blood = 0;
                break;
            case WA.Holy3: if (Target is { } ht && Dist(ht) <= 8) Hit(ht, 150, d.Name); break;
            case WA.Dia:
                Hit(target, 75, d.Name);
                if (target is not null && !target.IsPlayer)
                {
                    if (dots.TryGetValue(target.Id, out var old)) note = $"refreshed with {(old.Expire - T) / 1000.0:0.0}s left";
                    dots[target.Id] = (T + 30000, T + 1500, W.RaidBuffs(target, T).Mult, target, T);
                    DotApplications.Add(T);
                }
                break;
            case WA.Rapture or WA.Solace:
                if (Lily >= 3) LilyTimerMs = 0;
                Lily--;
                if (Blood >= 3) { BloodWasted++; if (T >= 0) Anomalies.Add($"{SageSim.F(T)} {d.Name} with three Blood Lilies ({rule})"); }
                else Blood++;
                if (d.Id == WA.Rapture) foreach (var p in Near(20)) Heal(p, 400 + Conf(p), d.Name);
                else Heal(target, 800, d.Name);
                break;
            case WA.Medica1: foreach (var p in Near(20)) Heal(p, 400 + Conf(p), d.Name); break;
            case WA.Medica3:
                foreach (var p in Near(20))
                {
                    Heal(p, 250 + Conf(p), d.Name);
                    Give(p, WS.Medica3, 15000);
                    for (var k = 1; k <= 5; k++) pendingTicks.Add((T + 3000 * k, p, 175 * HealMult(d, p), "Medica III (regen)"));
                }
                break;
            case WA.Cure: Heal(target, 500, d.Name); break;
            case WA.Cure2: Heal(target, 800, d.Name); break;
            case WA.Cure3:
                if (target is not null)
                    foreach (var p in LivingParty(false).Where(p => System.Numerics.Vector2.Distance(W.Pos(p, T), W.Pos(target, T)) <= 10)) Heal(p, 600 + Conf(p), d.Name);
                break;
            case WA.Regen:
                if (target is not null)
                {
                    Give(target, WS.Regen, 18000);
                    for (var k = 1; k <= 6; k++) pendingTicks.Add((T + 3000 * k, target, 250 * HealMult(d, target), "Regen"));
                }
                break;
            case WA.PoM: Give(me, WS.PoM, 15000); Give(me, WS.SacredSight, 30000, 3); break;
            case WA.Assize:
                if (Target is { } at && Dist(at) <= 20) Hit(at, 400, d.Name);
                foreach (var o in OtherEnemies(null, 20).Where(o => o != Target)) Hit(o, 400, d.Name + " (2nd target)");
                foreach (var p in Near(20)) Heal(p, 400, d.Name);
                Mp = Math.Min(10000, Mp + 500);
                break;
            case WA.ThinAir: Give(me, WS.ThinAir, 12000); break;
            case WA.Swiftcast: Give(me, WS.Swiftcast, 10000); break;
            case WA.Lucid: Give(me, WS.Lucid, 21000); break;
            case WA.Tetragrammaton: Heal(target, 700, d.Name); note = target is null ? "" : $"{target.Name} at {Hp(target):0}%"; break;
            case WA.Benediction: Heal(target, 10000, d.Name); break;
            case WA.Benison:
                if (target is not null) { Give(target, WS.Benison, 15000); note = $"{target.Name} ({World.RoleOf(target)})"; }
                break;
            case WA.Aquaveil:
                if (target is not null) { Give(target, WS.Aquaveil, 8000); note = $"{target.Name} ({World.RoleOf(target)})"; }
                break;
            case WA.Temperance:
                Give(me, WS.Temperance, 20000);
                Give(me, WS.DivineGrace, 30000);
                foreach (var p in LivingParty(false).Where(p => AllyDist(p) <= 50)) Give(p, WS.TemperanceParty, 20000);
                break;
            case WA.Caress:
                ReachLog.Add((T, d.Name, Near(30).Count(), LivingParty(false).Count(), rule));
                Drop(me, WS.DivineGrace);
                foreach (var p in Near(30))
                {
                    if (st.TryGetValue((p.Id, WS.CaressShield), out var old)) EndShield(p, WS.CaressShield, old, T, "replaced");
                    Give(p, WS.CaressShield, 10000);
                }
                break;
            case WA.Plenary: ReachLog.Add((T, d.Name, Near(30).Count(), LivingParty(false).Count(), rule)); foreach (var p in Near(30)) Give(p, WS.Confession, 10000); break;
            case WA.Bell:
                ReachLog.Add((T, d.Name, Near(20).Count(), LivingParty(false).Count(), rule));
                bellPos = W.Pos(me, T); bellUntil = T + 20000; bellLastProc = int.MinValue; BellLog.Add((T, int.MaxValue));
                Give(me, WS.Bell, 20000, 5);
                break;
            case WA.Asylum:
                asylumPos = W.Pos(me, T); asylumUntil = T + 24000; asylumNext = T + 3000;
                foreach (var p in LivingParty(false).Where(p => AllyDist(p) <= 10)) Heals.Add((T, p, 100, "Asylum"));
                break;
            case WA.Raise: note = target?.Name ?? ""; break;
        }
        Log.Add(new SimEvent(T, d.Name, target is null || target.Id == me.Id || !target.IsPlayer ? "" : target.Name, rule, note));
    }

    public static string F(int ms) => SageSim.F(ms);

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
        id = OriginalHook(id);
        if (!Defs.TryGetValue(id, out var d)) return false;
        if (d.Range == 0 || d.PartyTarget) return true;
        var t = Target;
        return t is not null && Dist(t) <= d.Range;
    }

    // ---------------- gauge fields (Dalamud's WHMGauge) ----------------

    public Func<double>? Gauge(string field) => field.ToLowerInvariant() switch
    {
        "lily" => () => Lily,
        "bloodlily" => () => Blood,
        // Elapsed ms towards the next Lily (0..20000), as WrathCombo's WHM code reads it (20000 - LilyTimer = time to the next).
        "lilytimer" => () => LilyTimerMs,
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

        F("ready", Act, 1, a => { var id = a[0].Id; return () => B(ActionReady(OriginalHook(id))); });
        F("cd", Act, 1, a => { var id = a[0].Id; return () => Cd(id); });
        F("charges", Act, 1, a => { var id = a[0].Id; return () => Charges(id); });
        F("chargeCd", Act, 1, a => { var id = a[0].Id; return () => ChargeCd(id); });
        F("justUsed", [ParamKind.Action, ParamKind.Number], 1, a => { var id = a[0].Id; var s = a[1].Value; return () => B(JustUsed(id, s?.Invoke() ?? 3)); });
        F("inRange", Act, 1, a => { var id = a[0].Id; return () => B(InRange(id)); });
        F("combo", Act, 1, _ => () => 0);
        F("enemies", Act, 1, _ => () => Target is null ? 0 : 1);
        F("hook", Act, 1, a => { var id = a[0].Id; return () => OriginalHook(id); });
        F("buff", St, 1, a => { var id = a[0].Id; return () => B(Has(id)); });
        F("buffTime", St, 1, a => { var id = a[0].Id; return () => Left(W.Me, id); });
        F("buffStacks", St, 1, a => { var id = a[0].Id; return () => Stacks(W.Me, id); });
        F("buffAny", St, 1, a => { var id = a[0].Id; return () => B(Has(id) || W.OthersHave(W.Me, id, T)); });
        F("buffAnyTime", St, 1, a => { var id = a[0].Id; return () => Math.Max(Left(W.Me, id), W.OthersTime(W.Me, id, T)); });
        F("debuff", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(id == WS.Dia ? DiaLeft(t) > 0 : Has(t, id)) : 0; });
        F("debuffTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? (id == WS.Dia ? DiaLeft(t) : Left(t, id)) : 0; });
        F("debuffAny", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? B(HasAny(t, id)) : 0; });
        F("debuffAnyTime", St, 1, a => { var id = a[0].Id; return () => Target is { } t ? Math.Max(id == WS.Dia ? DiaLeft(t) : Left(t, id), W.OthersTime(t, id, T)) : 0; });
        Val("gcd", () => RecastMs / 1000.0);
        Val("remainingGcd", () => Math.Max(0, GcdReadyAt - T) / 1000.0);
        Val("canWeave", () => B(CanWeave()));
        Val("moving", () => B(W.Moving(T)));
        Val("stoodStill", () => StoodStill);
        Val("movingTime", () => movingSince == int.MinValue ? 0 : (T - movingSince) / 1000.0);
        Val("sinceGcd", () => LastGcdAt == int.MinValue ? 99 : (T - LastGcdAt) / 1000.0);
        Val("inMelee", () => Target is { } t ? B(Dist(t) <= 3) : 0);
        Val("distance", () => Target is { } t ? Dist(t) : 0);
        foreach (var n in new[] { "rear", "flank", "front", "needsPositionals", "comboTimer", "comboAction", "raidwide", "stackMarker", "tankbuster", "canEsuna", "healCleansable" })
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
