using System.Numerics;
using RaidReplay.Core.Model;

/// <summary>
///     2026-10-30: AstTankMetrics / AstTankModel, WhmTankMetrics.cs adapted to the Astrologian replay (that file is untouched).
///     The rules' Astrologian: barriers Celestial Intersection (400), Neutral Sect's (Aspected Benefic 750, Helios Conjunction
///     375), the Spire (400), from AstSim.BarrierLog; mitigation Exaltation 10% (8 s, the target), the Bole 10% (15 s), Collective
///     Unconscious 10% (10 s, 30 y), Sun Sign 10% (15 s, 30 y, landing 1.5 s after the press). The replaced player's: the same
///     statuses from the log (and a Sage's or White Mage's, for a stand-in); their HoTs capped at Aspected Benefic 250, Helios
///     Conjunction 175, Opposition 100, Wheel of Fortune 100, the Ewer 200 (+30% crits).
///     The original description follows.
///
///     2026-10-29 tank-care measure for the White Mage replay (TankMetrics.cs is the Sage's; this follows it and leaves it
///     untouched): each tank's HP had the rules played the replaced player (the user's White Mage, or a stand-in for another
///     healer, e.g. another White Mage or the user's Sage). Counterfactual HP = logged HP + Δ, where Δ adds what the rules'
///     White Mage does for that tank and removes what the replaced player did:
///       - heals: the rules' heals on the tank (sim.Heals: potency x HP per potency; Temperance's +20% on heal spells is in the
///         sim), minus the replaced player's effective heals (direct; HoT ticks capped at that player's own regens up then:
///         Regen 250, Medica III 175, Divine Aura 200, Asylum 100, Kerakeia 100, Physis II 130, +30% for crits);
///       - barriers: logged damage is after barriers, so on each hit the replaced player's barriers on the tank (Divine Benison
///         500, Divine Caress 400; a Sage's as in TankMetrics) are taken as used up when damage still got through (or the
///         hit's typical size when it logged 0), the pre-barrier damage rebuilt from that, then the rules' Divine Benison (500,
///         15 s) and Divine Caress (400, 10 s, 30 y) absorb from it;
///       - mitigation: Aquaveil 15% (8 s, the target), Temperance 10% (20 s, 50 y), Plenary Indulgence's Confession 10% (10 s,
///         30 y), a Sage's Kerachole/Taurochole 10% and Holos 10%: the replaced player's off, the rules' on (they multiply);
///       - the other healers' overheal fills a deficit (Δ &lt; 0).
///     Potencies and durations from the game's Action sheet (scratchpad actinfo, 2026-10-29). Δ never lifts the tank over max
///     HP. Not modelled: Asylum's +10% on heals received in it, the co-healer reacting to a lower tank, the tanks' own play.
///     WhmTankModel runs incrementally, so the replay can also show the rules this HP while it runs (--cfhp: closed loop).
///     Also: cf-HP episodes under 30% with what the rules' White Mage had ready then (for the tank-care audit).
/// </summary>
internal static class AstTankMetrics
{
    public sealed record Episode(int Start, int MinT, int End, string Tank, string Phase, string Segment, double CfMin, double LogMin, bool CfDied,
        double AutoK, double OtherK, string Hits, string Ready, string Gcds, string Ogcds);

    private static readonly (string Name, int Recast, int Charges)[] Cds =
    [
        ("Essential Dignity", 40000, 3), ("Celestial Intersection", 30000, 2), ("Exaltation", 60000, 1), ("Collective Unconscious", 60000, 1),
        ("Celestial Opposition", 60000, 1), ("Neutral Sect", 120000, 1), ("Macrocosmos", 180000, 1), ("Earthly Star", 60000, 1), ("Synastry", 120000, 1),
    ];

    /// <summary> Charges of each White Mage cooldown at t, from press times (the rules' log or the logged player's actions). </summary>
    public static string ReadyAt(IEnumerable<(int T, string Name)> presses, int t)
    {
        var parts = new List<string>();
        foreach (var (n, rc, ch) in Cds)
        {
            var cd = new SamCd(rc, ch);
            foreach (var p in presses.Where(p => p.Name == n && p.T <= t).OrderBy(p => p.T)) cd.Use(p.T);
            var c = cd.Charges(t);
            var shortN = n switch { "Essential Dignity" => "ED", "Celestial Intersection" => "CI", "Exaltation" => "Exalt", "Collective Unconscious" => "CU", "Celestial Opposition" => "CO", "Neutral Sect" => "NS", "Macrocosmos" => "Macro", "Earthly Star" => "Star", _ => "Syn" };
            if (c > 0) parts.Add(ch > 1 ? $"{shortN}x{c}" : shortN);
        }
        return string.Join(",", parts);
    }

    /// <summary> The whole pull, after the replay: a fresh model run to the end. </summary>
    public static (TankMetrics.Result Res, List<Episode> Eps) Run(World w, AstSim sim, double h)
    {
        var m = new AstTankModel(w, sim, h, episodes: true);
        m.AdvanceTo(w.R.EndMs);
        return m.Result();
    }
}

internal sealed class AstTankModel
{
    private sealed class Bar
    {
        public int Start, End;
        public double Cap;
        public bool Real, Done;
        public string Name = "";
    }

    private static readonly Dictionary<string, double> RealBarrier = new()
    {
        ["Eukrasian Diagnosis"] = 540, ["Eukrasian Prognosis"] = 360, ["Haima"] = 300, ["Panhaima"] = 200, ["Holosakos"] = 300,
        ["Divine Benison"] = 500, ["Divine Caress"] = 400, ["Intersection"] = 400, ["Neutral Sect"] = 375, ["The Spire"] = 400,
    };

    private sealed class TankState
    {
        public Actor Tank = null!;
        public double Max;
        public readonly List<Bar> Bars = new();
        public readonly List<(int S, int E)> Exalt = new(), Bole = new(), Cu = new(), Sun = new();
        public List<(int StartMs, int EndMs)> Inv = new();
        public List<StatusInterval> MineTank = new();
        public List<(int T, int Kind, double A, double B, string Name)> Ev = new();   // kind: 0 hit (D, hpBefore), 1 real heal (eff), 2 other heal (raw, eff)
        public readonly List<(int T, double A, string Src)> RulesHeals = new();
        public int Ei;
        public double Delta;
        public readonly HashSet<int> Recorded = new();
        public List<DeathEvent> TankDeaths = new();
        public int StopAt = int.MaxValue;
        public readonly Dictionary<string, double[]> PhaseAcc = new();
        public (int Start, int MinT, double Min, double LogMin)? Cur;
    }

    private readonly World w;
    private readonly AstSim sim;
    private readonly double h;
    private readonly bool wantEpisodes;
    private readonly List<TankState> tanks = new();
    private readonly Dictionary<uint, TankState> byId = new();
    private readonly List<DeathEvent> allDeaths;
    private readonly Dictionary<string, double> typical = new();
    private int logIdx, healIdx, barIdx, nextTick;
    private readonly List<TankMetrics.Death> deaths = new();
    private readonly List<TankMetrics.CfDeath> cfDeaths = new();
    private readonly List<AstTankMetrics.Episode> eps = new();
    private double rulesLow, realLow;
    private readonly List<(int T, string Name)> rulesPresses = new();

    public AstTankModel(World w, AstSim sim, double h, bool episodes = false)
    {
        this.w = w; this.sim = sim; this.h = h; wantEpisodes = episodes;
        var r = w.R;
        var me = w.Me;
        allDeaths = r.Deaths.OrderBy(d => d.T).ToList();
        foreach (var g in r.Actions.Where(a => !a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet).SelectMany(a => a.Hits.Where(x => World.IsTank(x.Target) && x.Damage > 0 && x.Kind != HitKind.Heal).Select(x => (a.Name, (double)x.Damage))).GroupBy(x => x.Name))
        {
            var l = g.Select(x => x.Item2).OrderBy(x => x).ToList();
            typical[g.Key] = l[l.Count / 2];
        }
        var mineSt = r.Statuses.Where(s => s.Source?.Id == me.Id).ToList();
        var realHeals = Metrics.RealHealsNamed(w);
        foreach (var tank in w.Party.Where(World.IsTank).Where(t => t.MaxHp > 0))
        {
            var ts = new TankState { Tank = tank, Max = tank.MaxHp };
            ts.MineTank = mineSt.Where(s => s.Target.Id == tank.Id).ToList();
            foreach (var s in ts.MineTank)
                if (RealBarrier.TryGetValue(s.Name, out var cap))
                    ts.Bars.Add(new Bar { Start = s.StartMs, End = s.EndMs + 700, Cap = cap * h, Real = true, Name = s.Name });
            ts.Inv = r.Statuses.Where(s => s.Target.Id == tank.Id && s.StatusId is 82 or 409 or 810 or 811 or 3255 or 1836).Select(s => (s.StartMs, s.EndMs)).ToList();
            var max = ts.Max;
            foreach (var a in r.Actions)
            {
                if (!a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet)
                {
                    foreach (var x in a.Hits.Where(x => x.Target.Id == tank.Id && x.Kind is HitKind.Damage or HitKind.Blocked or HitKind.Parried))
                        ts.Ev.Add((x.T, 0, x.Damage, x.HpBefore, a.Name));
                    continue;
                }
                if (a.Source.Id == me.Id) continue;
                foreach (var x in a.Hits.Where(x => x.Target.Id == tank.Id && x.Kind == HitKind.Heal))
                {
                    var eff = x.HpBefore > 0 ? Math.Min(x.Heal, Math.Max(0, max - x.HpBefore)) : x.Heal;
                    ts.Ev.Add((x.T, 2, x.Heal, eff, a.Name));
                }
            }
            foreach (var tk in r.Ticks.Where(tk => tk.IsHeal && tk.Target.Id == tank.Id && tk.Source?.Id != me.Id))
            {
                var hp = tank.Hp.At(tk.T - 1, (int)max);
                ts.Ev.Add((tk.T, 2, tk.Amount, hp > 0 ? Math.Min(tk.Amount, Math.Max(0, max - hp)) : tk.Amount, "tick"));
            }
            foreach (var x in realHeals.Where(x => x.Target.Id == tank.Id))
            {
                var eff = x.Eff;
                if (x.Src == "HoT")
                    eff = Math.Min(eff, 1.3 * h * ((RealSt(ts, "Kerakeia", x.T) ? 100 : 0) + (RealSt(ts, "Physis II", x.T) ? 130 : 0) + (RealSt(ts, "Regen", x.T) ? 250 : 0)
                        + (RealSt(ts, "Medica III", x.T) ? 175 : 0) + (RealSt(ts, "Divine Aura", x.T) ? 200 : 0) + (RealSt(ts, "Asylum", x.T) ? 100 : 0)
                        + (RealSt(ts, "Aspected Benefic", x.T) ? 250 : 0) + (RealSt(ts, "Helios Conjunction", x.T) ? 175 : 0) + (RealSt(ts, "Opposition", x.T) ? 100 : 0)
                        + (RealSt(ts, "Wheel of Fortune", x.T) ? 100 : 0) + (RealSt(ts, "The Ewer", x.T) ? 200 : 0)));
                ts.Ev.Add((x.T, 1, eff, 0, x.Src));
            }
            ts.Ev.Sort((p, q) => p.T != q.T ? p.T.CompareTo(q.T) : p.Kind.CompareTo(q.Kind));
            ts.TankDeaths = allDeaths.Where(d => d.Victim.Id == tank.Id).ToList();
            tanks.Add(ts);
            byId[tank.Id] = ts;
        }
    }

    // AST_TANK_DEBUG=<tank name prefix>:<from s>:<to s> prints every event on that tank with Δ (in the final metrics pass).
    private static readonly (string Who, int From, int To)? Dbg = Environment.GetEnvironmentVariable("AST_TANK_DEBUG") is { Length: > 0 } v && v.Split(':') is { Length: 3 } x
        ? (x[0], (int)(double.Parse(x[1]) * 1000), (int)(double.Parse(x[2]) * 1000)) : null;
    private static double MitRealDbg(TankState ts, int x) => 1 - (1 - (RealSt(ts, "Kerachole", x) || RealSt(ts, "Taurochole", x) ? 0.1 : 0)) * (1 - (RealSt(ts, "Holos", x) ? 0.1 : 0))
        * (1 - (RealSt(ts, "Aquaveil", x) ? 0.15 : 0)) * (1 - (RealSt(ts, "Temperance", x) ? 0.1 : 0)) * (1 - (RealSt(ts, "Confession", x) ? 0.1 : 0))
        * (1 - (RealSt(ts, "Exaltation", x) ? 0.1 : 0)) * (1 - (RealSt(ts, "The Bole", x) ? 0.1 : 0)) * (1 - (RealSt(ts, "Collective Unconscious", x) ? 0.1 : 0)) * (1 - (RealSt(ts, "Sun Sign", x) ? 0.1 : 0));
    private static bool RealSt(TankState ts, string n, int t) => ts.MineTank.Any(s => s.Name == n && s.StartMs <= t && t < s.EndMs);
    private bool Wipe(int t) => allDeaths.Count(d => d.T >= t - 1000 && d.T <= t + 3000) >= 4;
    private float Center(Actor a, Actor b, int t) => Vector2.Distance(w.Pos(a, t), w.Pos(b, t));
    private static bool In(List<(int S, int E)> l, int t) => l.Any(x => x.S <= t && t < x.E);
    private static bool Invuln(TankState ts, int t) => ts.Inv.Any(x => x.StartMs <= t && t < x.EndMs + 300);

    /// <summary> The rules' presses, barriers and heals since the last call: mitigation windows, barriers and heals per tank. </summary>
    private void Ingest()
    {
        var me = w.Me;
        for (; logIdx < sim.Log.Count; logIdx++)
        {
            var e = sim.Log[logIdx];
            rulesPresses.Add((e.T, e.What));
            var land = e.T + Metrics.LandMs;
            foreach (var ts in tanks)
            {
                var tank = ts.Tank;
                if (w.Dead(tank, e.T)) continue;
                switch (e.What)
                {
                    case "Exaltation" when e.Target == tank.Name: ts.Exalt.Add((land, land + 8000)); break;
                    case "the Bole" when e.Target == tank.Name: ts.Bole.Add((land, land + 15000)); break;
                    case "Collective Unconscious" when Center(me, tank, e.T) <= 30: ts.Cu.Add((land, land + 10000)); break;
                    case "Sun Sign" when Center(me, tank, e.T) <= 30: ts.Sun.Add((e.T + 1500, e.T + 16500)); break;
                }
            }
        }
        for (; barIdx < sim.BarrierLog.Count; barIdx++)
        {
            var b = sim.BarrierLog[barIdx];
            if (!byId.TryGetValue(b.Target.Id, out var ts)) continue;
            var land = b.T + Metrics.LandMs;
            var name = b.Status switch { AS.NSShield => "NS", AS.Intersection => "Intersection", _ => "Spire" };
            foreach (var o in ts.Bars.Where(o => !o.Real && o.Name == name && o.End > land)) o.End = land;   // a new one replaces the old
            ts.Bars.Add(new Bar { Start = land, End = land + 30000, Cap = b.Potency * h, Name = name });
        }
        for (; healIdx < sim.Heals.Count; healIdx++)
        {
            var x = sim.Heals[healIdx];
            if (byId.TryGetValue(x.Target.Id, out var ts)) ts.RulesHeals.Add((x.T, x.Potency * h, x.Src));
        }
    }

    /// <summary> The counterfactual HP % of a tank now (the logged one once the model has lost them, or for anyone else). </summary>
    public double CfPct(Actor a, int t)
    {
        var lp = w.HpPct(a, t);
        if (!byId.TryGetValue(a.Id, out var ts) || w.Dead(a, t) || t >= ts.StopAt || t < 0) return lp;
        var lg = a.Hp.At(t, (int)ts.Max);
        if (lg <= 0) return lp;
        return Math.Clamp(100 * Math.Min(ts.Max, lg + ts.Delta) / ts.Max, 0, 100);
    }

    public void AdvanceTo(int t)
    {
        Ingest();
        while (nextTick <= t)
        {
            foreach (var ts in tanks) Tick(ts, nextTick);
            nextTick += 100;
        }
    }

    private void Tick(TankState ts, int t)
    {
        var r = w.R;
        var tank = ts.Tank;
        var max = ts.Max;
        // this tick's events: the log's, then the rules' heals (as the offline sort by time and kind)
        var batch = new List<(int T, int Kind, double A, double B, string Name)>();
        while (ts.Ei < ts.Ev.Count && ts.Ev[ts.Ei].T <= t) batch.Add(ts.Ev[ts.Ei++]);
        if (ts.RulesHeals.Count > 0)
        {
            for (var i = 0; i < ts.RulesHeals.Count;)
                if (ts.RulesHeals[i].T <= t) { batch.Add((ts.RulesHeals[i].T, 3, ts.RulesHeals[i].A, 0, ts.RulesHeals[i].Src)); ts.RulesHeals.RemoveAt(i); }
                else i++;
        }
        if (batch.Count > 1) batch.Sort((p, q) => p.T != q.T ? p.T.CompareTo(q.T) : p.Kind.CompareTo(q.Kind));
        foreach (var e in batch)
        {
            if (w.Dead(tank, e.T) && e.Kind != 0) continue;
            var d0 = ts.Delta;
            if (Dbg is { } dd && wantEpisodes && tank.Name.StartsWith(dd.Who) && e.T >= dd.From && e.T <= dd.To)
                Console.WriteLine($"     dbg {SageSim.F(e.T)} {tank.Name.Split(' ')[0]} {(e.Kind switch { 0 => "HIT ", 1 => "real", 2 => "othr", _ => "RULE" })} {e.Name,-26} A {e.A / 1000,6:0.0}k B {e.B / 1000,6:0.0}k logged {100 * tank.Hp.At(e.T, (int)max) / max,4:0}% delta {d0 / 1000,6:0.0}k"
                    + (e.Kind == 0 ? $" mitReal {MitRealDbg(ts, e.T):0.00} mitRules {(1 - (1 - (In(ts.Exalt, e.T) ? 0.1 : 0)) * (1 - (In(ts.Bole, e.T) ? 0.1 : 0)) * (1 - (In(ts.Cu, e.T) ? 0.1 : 0)) * (1 - (In(ts.Sun, e.T) ? 0.1 : 0))):0.00} realBars {ts.Bars.Where(b => b.Real && b.Start <= e.T && e.T < b.End).Sum(b => b.Cap) / 1000:0.0}k rulesBars {ts.Bars.Where(b => !b.Real && !b.Done && b.Start <= e.T && e.T < b.End).Sum(b => b.Cap) / 1000:0.0}k" : ""));
            switch (e.Kind)
            {
                case 1:
                    ts.Delta -= e.A;
                    if (tank.Hp.At(e.T, (int)max) < 0.5 * max) realLow += e.A;
                    break;
                case 2:
                {
                    var over = e.A - e.B;
                    if (ts.Delta < 0) ts.Delta = Math.Min(0, ts.Delta + over);
                    ts.Delta = Math.Min(ts.Delta, max - tank.Hp.At(e.T + 1, (int)max));
                    break;
                }
                case 3:
                {
                    var logged = tank.Hp.At(e.T, (int)max);
                    if (logged < 0.5 * max || logged + ts.Delta < 0.5 * max) rulesLow += Math.Min(e.A, Math.Max(0, max - logged - ts.Delta));
                    ts.Delta += e.A;
                    ts.Delta = Math.Min(ts.Delta, max - logged);
                    break;
                }
                case 0:
                {
                    var d = e.A; var hpBefore = e.B;
                    if (Invuln(ts, e.T)) break;
                    var realActive = ts.Bars.Where(b => b.Real && b.Start <= e.T && e.T < b.End && b.Cap > 0).ToList();
                    double aReal = 0;
                    if (realActive.Count > 0)
                    {
                        var avail = realActive.Sum(b => b.Cap);
                        aReal = d > 0 ? avail : Math.Min(avail, typical.GetValueOrDefault(e.Name, 0));
                        var left = aReal;
                        foreach (var b in realActive) { var take = Math.Min(b.Cap, left); b.Cap -= take; left -= take; }
                    }
                    double MitReal(int x) => MitRealDbg(ts, x);
                    double MitRules(int x) => 1 - (1 - (In(ts.Exalt, x) ? 0.1 : 0)) * (1 - (In(ts.Bole, x) ? 0.1 : 0)) * (1 - (In(ts.Cu, x) ? 0.1 : 0)) * (1 - (In(ts.Sun, x) ? 0.1 : 0));
                    var rawPre = (d + aReal) / (1 - MitReal(e.T));
                    var rem = rawPre * (1 - MitRules(e.T));
                    foreach (var b in ts.Bars.Where(b => !b.Real && !b.Done && b.Start <= e.T && e.T < b.End).OrderBy(b => b.End))
                    {
                        var take = Math.Min(b.Cap, rem); b.Cap -= take; rem -= take;
                        if (b.Cap <= 0) b.Done = true;
                    }
                    ts.Delta += d - rem;
                    var loggedAfter = hpBefore - d;
                    ts.Delta = Math.Min(ts.Delta, max - Math.Max(0, loggedAfter));
                    var cfAfter = loggedAfter + ts.Delta;
                    var died = ts.TankDeaths.FirstOrDefault(x => x.T >= e.T - 200 && x.T <= e.T + 4000);
                    if (loggedAfter <= 0 && died is not null && ts.Recorded.Add(died.T))
                    {
                        if (!Wipe(died.T))
                            deaths.Add(new TankMetrics.Death(died.T, tank.Name, TankMetrics.PhaseOf(r, died.T), TankMetrics.SegmentOf(r, died.T), $"{e.Name} {d / 1000:0}k at {100 * hpBefore / max:0}%",
                                100 * cfAfter / max, cfAfter > 0));
                        ts.Delta = 0;
                    }
                    else if (loggedAfter > 0 && cfAfter <= 0 && !w.Dead(tank, e.T))
                    {
                        if (ts.StopAt == int.MaxValue)
                            cfDeaths.Add(new TankMetrics.CfDeath(e.T, tank.Name, TankMetrics.PhaseOf(r, e.T), $"{e.Name} {d / 1000:0}k, logged {100 * loggedAfter / max:0}% after it, Δ {ts.Delta / 1000:0}k"));
                        ts.StopAt = Math.Min(ts.StopAt, e.T);
                    }
                    break;
                }
            }
        }
        if (w.Dead(tank, t)) { Close(ts, t, false); ts.StopAt = int.MaxValue; ts.Delta = 0; return; }
        var lg = tank.Hp.At(t, (int)max);
        if (lg <= 0) return;
        var cf = t >= ts.StopAt ? 0 : Math.Min(max, lg + ts.Delta);
        var cfDeadNow = t >= ts.StopAt;
        var lp = 100 * lg / max; var cp = 100 * cf / max;
        if (wantEpisodes)
        {
            if (cfDeadNow) Close(ts, t, true);
            else if (ts.Cur is null && cp < 30 && !Invuln(ts, t) && !Wipe(t + 2000)) ts.Cur = (t, t, cp, lp);
            else if (ts.Cur is { } c)
            {
                if (cp < c.Min) ts.Cur = (c.Start, t, cp, Math.Min(c.LogMin, lp));
                else if (lp < c.LogMin) ts.Cur = (c.Start, c.MinT, c.Min, lp);
                if (cp > 50) Close(ts, t, false);
            }
        }
        var ph0 = TankMetrics.PhaseOf(r, t);
        foreach (var key in new[] { ph0, ph0 == "p3" ? "p3 " + TankMetrics.SegmentOf(r, t) : "" })
        {
            if (key.Length == 0) continue;
            var acc = ts.PhaseAcc.TryGetValue(key, out var a0) ? a0 : ts.PhaseAcc[key] = [0, 0, 0, 0, 100, 100, 0];
            if (lp < 30) acc[0] += 0.1; if (cp < 30 && !cfDeadNow) acc[1] += 0.1; if (lp < 15) acc[2] += 0.1; if (cp < 15 && !cfDeadNow) acc[3] += 0.1;
            if (cfDeadNow) acc[6] += 0.1;
            acc[4] = Math.Min(acc[4], cp); acc[5] = Math.Min(acc[5], lp);
        }
    }

    private void Close(TankState ts, int end, bool died)
    {
        if (ts.Cur is not { } c) return;
        ts.Cur = null;
        var r = w.R; var tank = ts.Tank; var max = ts.Max;
        var hits = r.Actions.Where(a => !a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet)
            .SelectMany(a => a.Hits.Where(x => x.Target.Id == tank.Id && x.Damage > 0 && x.T >= c.MinT - 10000 && x.T <= c.MinT).Select(x => (x.T, a.Name, D: (double)x.Damage))).ToList();
        var autoK = hits.Where(x => x.Name is "auto-attack" or "attack").Sum(x => x.D) / 1000;
        var otherK = hits.Where(x => x.Name is not ("auto-attack" or "attack")).Sum(x => x.D) / 1000;
        var hitStr = string.Join(" ", hits.Where(x => x.D >= 0.1 * max).Select(x => $"{SageSim.F(x.T)}:{(x.Name == "auto-attack" ? "auto" : x.Name)}:{100 * x.D / max:0}"));
        var ready = AstTankMetrics.ReadyAt(rulesPresses, c.Start) + (w.Dead(w.Me, c.Start) ? ",AST DEAD" : "");
        var gcds = string.Join(" ", sim.Log.Where(e => e.T >= c.MinT - 10000 && e.T <= c.MinT && (AstSim.GcdNames.Contains(e.What) || e.What.StartsWith("cast> ")))
            .Select(e => e.What.Replace("cast> ", ">").Replace("Fall Malefic", "Mal").Replace("Helios Conjunction", "HConj") + (e.Target == tank.Name ? "->T" : e.Target.Length > 0 ? "->o" : "")));
        var og = string.Join(",", sim.Log.Where(e => e.T >= c.Start - 10000 && e.T <= end && !AstSim.GcdNames.Contains(e.What) && (e.Target == tank.Name || e.What is "Collective Unconscious" or "Sun Sign" or "Celestial Opposition" or "Lady of Crowns"))
            .Select(e => $"{e.What.Replace("Celestial ", "").Replace("Collective Unconscious", "CU").Replace("Essential Dignity", "ED")}@{SageSim.F(e.T)}"));
        eps.Add(new AstTankMetrics.Episode(c.Start, c.MinT, end, tank.Name, TankMetrics.PhaseOf(r, c.MinT), TankMetrics.SegmentOf(r, c.MinT), c.Min, c.LogMin, died, autoK, otherK, hitStr, ready, gcds, og));
    }

    public (TankMetrics.Result Res, List<AstTankMetrics.Episode> Eps) Result()
    {
        foreach (var ts in tanks) Close(ts, w.R.EndMs, false);
        var byPhase = new Dictionary<string, (double, double, double, double, double, double, double)>();
        foreach (var ts in tanks)
            foreach (var (ph, a) in ts.PhaseAcc)
            {
                var old = byPhase.TryGetValue(ph, out var o) ? o : (0, 0, 0, 0, 100, 100, 0);
                byPhase[ph] = (old.Item1 + a[0], old.Item2 + a[1], old.Item3 + a[2], old.Item4 + a[3], Math.Min(old.Item5, a[4]), Math.Min(old.Item6, a[5]), old.Item7 + a[6]);
            }
        return (new TankMetrics.Result(deaths, cfDeaths, byPhase, rulesLow, realLow, new List<string>()), eps);
    }
}
