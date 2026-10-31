using RaidReplay.Core.Model;

/// <summary>
///     2026-10-28 tank-care measure: each tank's HP had the rules played the Sage, from the logged HP.
///     Counterfactual HP = logged HP + Δ, where Δ adds what the rules' Sage does for that tank and removes what the real
///     Sage did:
///       - heals: the rules' heals on the tank (potency x HP per potency, the replay's own Krasis/Physis/Zoe multipliers),
///         Haima/Panhaima expiry heals, minus the real Sage's effective heals (direct, HoT, Kardia ticks);
///       - barriers: logged damage is after barriers (fully absorbed hits log 0), so on each hit the real Sage's barriers
///         on the tank are taken as used up when damage still got through (or the hit's typical size when it logged 0), the
///         pre-Sage damage rebuilt from that, then the rules' barriers (Eukrasian Diagnosis 540, Eukrasian Prognosis II 360,
///         Haima 5 x 300, Panhaima 5 x 200, Holos 300; x1.5 under Zoe) absorb from it;
///       - mitigation: Kerachole or Taurochole (10%, not stacking) and Holos (10%), the real Sage's off, the rules' on;
///       - Krasis: +20% on the other healers' heals on the tank while the rules' Krasis is up (and the real one's removed);
///       - the other healers' overheal fills a deficit (Δ < 0): had the real Sage not healed, that overheal would have landed.
///     Δ never lifts the tank over max HP. Everyone else (the other healer, the tanks' own cooldowns, the tanks' and bosses'
///     behaviour) is as logged: it cannot show a co-healer reacting to a lower tank, nor what happens after a logged death
///     (the boss turns to someone else in the log), only whether the killing blow is survived and by how much.
/// </summary>
internal static class TankMetrics
{
    public sealed record Death(int T, string Tank, string Phase, string Segment, string Cause, double MarginPct, bool Survives);
    public sealed record CfDeath(int T, string Tank, string Phase, string Hit);
    public sealed record Result(List<Death> Deaths, List<CfDeath> CfDeaths, Dictionary<string, (double LogLow30, double CfLow30, double LogLow15, double CfLow15, double CfMin, double LogMin, double CfDead)> ByPhase,
        double RulesHealOnLowTank, double RealHealOnLowTank, List<string> Notes);

    private sealed class Barrier
    {
        public int Start, End;
        public double Cap;   // current barrier
        public int Stacks;   // Haima/Panhaima stacks left after this barrier
        public double Restack, ExpiryHealPerStack;
        public bool Real;
        public string Name = "";
        public bool Done;
    }

    // TANK_DEBUG=<tank name prefix>:<from s>:<to s> prints every event on that tank with Δ.
    private static readonly (string Who, int From, int To)? Dbg = Environment.GetEnvironmentVariable("TANK_DEBUG") is { Length: > 0 } v && v.Split(':') is { Length: 3 } x
        ? (x[0], (int)(double.Parse(x[1]) * 1000), (int)(double.Parse(x[2]) * 1000)) : null;

    public static string PhaseOf(PullReplay r, int t) =>
        r.Phases.Where(p => !p.IsSegment && p.StartMs <= t && t < Math.Max(p.EndMs, p.StartMs + 1)).Select(p => p.Id).FirstOrDefault()
        ?? r.Phases.Where(p => !p.IsSegment && p.StartMs <= t).Select(p => p.Id).LastOrDefault() ?? "?";

    public static string SegmentOf(PullReplay r, int t) => r.Phases.Where(p => p.IsSegment && p.StartMs <= t && t < p.EndMs).Select(p => p.Name).FirstOrDefault() ?? "";

    public static Result Run(World w, SageSim sim, double h)
    {
        var r = w.R;
        var me = w.Me;
        var notes = new List<string>();
        var deaths = new List<Death>();
        var cfDeaths = new List<CfDeath>();
        var byPhase = new Dictionary<string, (double, double, double, double, double, double, double)>();
        double rulesLow = 0, realLow = 0;
        var tanks = w.Party.Where(World.IsTank).Where(t => t.MaxHp > 0).ToList();
        var mineSt = r.Statuses.Where(s => s.Source?.Id == me.Id).ToList();
        var allDeaths = r.Deaths.OrderBy(d => d.T).ToList();
        bool Wipe(int t) => allDeaths.Count(d => d.T >= t - 1000 && d.T <= t + 3000) >= 4;
        // Zoe: a heal GCD pressed while the rules' Zoe was up (pressed in the 30 s before, no heal GCD since) is x1.5.
        var healGcds = new HashSet<string> { "Eukrasian Prognosis II", "Pneuma", "Eukrasian Diagnosis", "Diagnosis", "Prognosis" };
        bool UnderZoe(SimEvent e)
        {
            var z = sim.Log.LastOrDefault(x => x.What == "Zoe" && x.T <= e.T && x.T >= e.T - 30000);
            return z is not null && !sim.Log.Any(x => healGcds.Contains(x.What) && x.T > z.T && x.T < e.T);
        }
        // Typical logged damage of an ability on tanks (for hits a barrier fully absorbed).
        var typical = new Dictionary<string, double>();
        foreach (var g in r.Actions.Where(a => !a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet).SelectMany(a => a.Hits.Where(x => World.IsTank(x.Target) && x.Damage > 0 && x.Kind != HitKind.Heal).Select(x => (a.Name, (double)x.Damage))).GroupBy(x => x.Name))
        {
            var l = g.Select(x => x.Item2).OrderBy(x => x).ToList();
            typical[g.Key] = l[l.Count / 2];
        }

        foreach (var tank in tanks)
        {
            var max = (double)tank.MaxHp;
            // ---- barriers ----
            var bars = new List<Barrier>();
            foreach (var e in sim.Log)
            {
                var land = e.T + Metrics.LandMs;
                var zoe = UnderZoe(e) ? 1.5 : 1;
                switch (e.What)
                {
                    case "Eukrasian Diagnosis" when e.Target == tank.Name:
                        bars.RemoveAll(b => !b.Real && b.Name is "EDiag" or "EProg" && b.End > land);   // replaces either
                        bars.Add(new Barrier { Start = land, End = land + 30000, Cap = 540 * h * zoe, Name = "EDiag" });
                        break;
                    case "Eukrasian Prognosis II" when w.Dist(me, tank, e.T) <= 20.5 && !w.Dead(tank, e.T):
                        if (bars.Any(b => !b.Real && b.Name == "EDiag" && b.Start <= land && b.End > land && !b.Done)) break;   // won't stack over E.Diag
                        bars.RemoveAll(b => !b.Real && b.Name == "EProg" && b.End > land);
                        bars.Add(new Barrier { Start = land, End = land + 30000, Cap = 360 * h * zoe, Name = "EProg" });
                        break;
                    case "Haima" when e.Target == tank.Name:
                        bars.Add(new Barrier { Start = land, End = land + 15000, Cap = 300 * h, Stacks = 5, Restack = 300 * h, ExpiryHealPerStack = 150 * h, Name = "Haima" });
                        break;
                    case "Panhaima" when w.Dist(me, tank, e.T) <= 30 && !w.Dead(tank, e.T):
                        bars.Add(new Barrier { Start = land, End = land + 15000, Cap = 200 * h, Stacks = 5, Restack = 200 * h, ExpiryHealPerStack = 100 * h, Name = "Panhaima" });
                        break;
                    case "Holos" when w.Dist(me, tank, e.T) <= 30 && !w.Dead(tank, e.T):
                        bars.Add(new Barrier { Start = land, End = land + 30000, Cap = 300 * h, Name = "Holosakos" });
                        break;
                }
            }
            foreach (var s in mineSt.Where(s => s.Target.Id == tank.Id))
            {
                double cap = s.Name switch { "Eukrasian Diagnosis" => 540, "Eukrasian Prognosis" => 360, "Haima" => 300, "Panhaima" => 200, "Holosakos" => 300, _ => 0 };
                if (cap > 0) bars.Add(new Barrier { Start = s.StartMs, End = s.EndMs + 700, Cap = cap * h, Real = true, Name = s.Name });
            }
            // ---- mitigation windows ----
            var rulesKT = new List<(int S, int E)>(); var rulesHolos = new List<(int S, int E)>(); var rulesKrasis = new List<(int S, int E)>();
            foreach (var e in sim.Log)
            {
                var land = e.T + Metrics.LandMs;
                if (e.What == "Kerachole" && w.Dist(me, tank, e.T) <= 30) rulesKT.Add((land, land + 15000));
                if (e.What == "Taurochole" && e.Target == tank.Name) rulesKT.Add((land, land + 15000));
                if (e.What == "Holos" && w.Dist(me, tank, e.T) <= 30) rulesHolos.Add((land, land + 20000));
                if (e.What == "Krasis" && e.Target == tank.Name) rulesKrasis.Add((land, land + 10000));
            }
            bool In(List<(int S, int E)> l, int t) => l.Any(x => x.S <= t && t < x.E);
            var inv = r.Statuses.Where(s => s.Target.Id == tank.Id && s.StatusId is 82 or 409 or 810 or 811 or 3255 or 1836).Select(s => (s.StartMs, s.EndMs)).ToList();
            bool Invuln(int t) => inv.Any(x => x.StartMs <= t && t < x.EndMs + 300);
            bool RealSt(string n, int t) => mineSt.Any(s => s.Target.Id == tank.Id && s.Name == n && s.StartMs <= t && t < s.EndMs);
            double MitReal(int t) => 1 - (1 - (RealSt("Kerachole", t) || RealSt("Taurochole", t) ? 0.1 : 0)) * (1 - (RealSt("Holos", t) ? 0.1 : 0));
            double MitRules(int t) => 1 - (1 - (In(rulesKT, t) ? 0.1 : 0)) * (1 - (In(rulesHolos, t) ? 0.1 : 0));

            // ---- events ----
            var ev = new List<(int T, int Kind, double A, double B, double C, string Name)>();   // kind: 0 hit (D, hpBefore), 1 real heal (eff), 2 other heal (raw, eff), 3 rules heal (raw)
            foreach (var a in r.Actions)
            {
                if (!a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet)
                {
                    foreach (var x in a.Hits.Where(x => x.Target.Id == tank.Id && x.Kind is HitKind.Damage or HitKind.Blocked or HitKind.Parried))
                        ev.Add((x.T, 0, x.Damage, x.HpBefore, 0, a.Name));
                    continue;
                }
                if (a.Source.Id == me.Id) continue;   // the real Sage's heals come from RealHealsNamed below
                foreach (var x in a.Hits.Where(x => x.Target.Id == tank.Id && x.Kind == HitKind.Heal))
                {
                    var eff = x.HpBefore > 0 ? Math.Min(x.Heal, Math.Max(0, max - x.HpBefore)) : x.Heal;
                    ev.Add((x.T, 2, x.Heal, eff, 0, a.Name));
                }
            }
            foreach (var tk in r.Ticks.Where(tk => tk.IsHeal && tk.Target.Id == tank.Id && tk.Source?.Id != me.Id))
            {
                var hp = tank.Hp.At(tk.T - 1, (int)max);
                ev.Add((tk.T, 2, tk.Amount, hp > 0 ? Math.Min(tk.Amount, Math.Max(0, max - hp)) : tk.Amount, 0, "tick"));
            }
            // HoT ticks come combined per target (one tick for every regen on it, credited to one source): the real Sage's
            // share is capped at its own regens up then (Kerachole's Kerakeia 100, Physis II 130; +30% for crits). Without
            // the cap the Sage was credited with the other healer's regens (31k ticks under one 10k Kerakeia).
            foreach (var x in Metrics.RealHealsNamed(w).Where(x => x.Target.Id == tank.Id))
            {
                var eff = x.Eff;
                if (x.Src == "HoT")
                    eff = Math.Min(eff, 1.3 * h * ((RealSt("Kerakeia", x.T) ? 100 : 0) + (RealSt("Physis II", x.T) ? 130 : 0)));
                ev.Add((x.T, 1, eff, 0, 0, x.Src));
            }
            foreach (var x in sim.Heals.Where(x => x.Target.Id == tank.Id)) ev.Add((x.T, 3, x.Potency * h, 0, 0, x.Src));
            ev.Sort((p, q) => p.T != q.T ? p.T.CompareTo(q.T) : p.Kind.CompareTo(q.Kind));

            double delta = 0;
            var recorded = new HashSet<int>();
            var tankDeaths = allDeaths.Where(d => d.Victim.Id == tank.Id).ToList();
            int ei = 0;
            var stopAt = int.MaxValue;   // a counterfactual death: stop tracking
            var phaseAcc = new Dictionary<string, double[]>();
            double[] Acc(string ph) => phaseAcc.TryGetValue(ph, out var a) ? a : phaseAcc[ph] = [0, 0, 0, 0, 100, 100, 0];
            int lastExpiryCheck = 0;
            void Cap(int t, double logged) => delta = Math.Min(delta, max - logged);
            for (var t = 0; t <= r.EndMs; t += 100)
            {
                // barrier expiries (rules): Haima/Panhaima expiry heals
                foreach (var b in bars.Where(b => !b.Real && !b.Done && b.End <= t && b.End > lastExpiryCheck))
                {
                    b.Done = true;
                    if (b.ExpiryHealPerStack > 0 && !w.Dead(tank, b.End))
                    {
                        delta += b.ExpiryHealPerStack * b.Stacks;
                        Cap(t, tank.Hp.At(t, (int)max));
                    }
                }
                lastExpiryCheck = t;
                while (ei < ev.Count && ev[ei].T <= t)
                {
                    var e = ev[ei++];
                    if (w.Dead(tank, e.T) && e.Kind != 0) continue;
                    var dbg = Dbg is { } dd && tank.Name.StartsWith(dd.Who) && e.T >= dd.From && e.T <= dd.To;
                    var d0 = delta;
                    if (dbg) Console.WriteLine($"     dbg {SageSim.F(e.T)} {tank.Name.Split(' ')[0]} kind {(e.Kind switch { 0 => "HIT ", 1 => "real", 2 => "othr", _ => "RULE" })} {e.Name,-24} A {e.A / 1000,6:0.0}k B {e.B / 1000,6:0.0}k logged {100 * tank.Hp.At(e.T, (int)max) / max,4:0}% delta before {d0 / 1000,6:0.0}k");
                    switch (e.Kind)
                    {
                        case 1:
                            delta -= e.A;
                            if (tank.Hp.At(e.T, (int)max) < 0.5 * max) realLow += e.A;
                            break;
                        case 2:
                        {
                            var raw = e.A; var eff = e.B;
                            var realKr = RealSt("Krasis", e.T);
                            var baseRaw = realKr ? raw / 1.2 : raw;
                            if (realKr) delta -= Math.Min(eff, raw - baseRaw);
                            if (In(rulesKrasis, e.T)) delta += 0.2 * baseRaw;
                            var over = raw - eff;
                            if (delta < 0) delta = Math.Min(0, delta + over);
                            Cap(e.T, tank.Hp.At(e.T + 1, (int)max));
                            break;
                        }
                        case 3:
                        {
                            var logged = tank.Hp.At(e.T, (int)max);
                            if (logged < 0.5 * max || logged + delta < 0.5 * max) rulesLow += Math.Min(e.A, Math.Max(0, max - logged - delta));
                            delta += e.A;
                            Cap(e.T, logged);
                            break;
                        }
                        case 0:
                        {
                            var d = e.A; var hpBefore = e.B;
                            // Under an invulnerability (Hallowed Ground, Holmgang, Living Dead, Walking Dead, Undead Rebirth,
                            // Superbolide) the hit's size says nothing about what the rules would change.
                            if (Invuln(e.T)) break;
                            var realActive = bars.Where(b => b.Real && b.Start <= e.T && e.T < b.End && b.Cap > 0).ToList();
                            double aReal = 0;
                            if (realActive.Count > 0)
                            {
                                var avail = realActive.Sum(b => b.Cap);
                                aReal = d > 0 ? avail : Math.Min(avail, typical.GetValueOrDefault(e.Name, 0));
                                var left = aReal;
                                foreach (var b in realActive) { var take = Math.Min(b.Cap, left); b.Cap -= take; left -= take; }
                            }
                            var rawPre = (d + aReal) / (1 - MitReal(e.T));
                            var dmgRules = rawPre * (1 - MitRules(e.T));
                            var rulesActive = bars.Where(b => !b.Real && !b.Done && b.Start <= e.T && e.T < b.End)
                                .OrderBy(b => b.Name is "EDiag" or "EProg" ? 0 : b.Name == "Haima" ? 1 : 2).ToList();
                            var rem = dmgRules;
                            foreach (var b in rulesActive)
                            {
                                while (rem > 0 && (b.Cap > 0 || b.Stacks > 0))
                                {
                                    if (b.Cap <= 0) { b.Stacks--; b.Cap = b.Restack; }
                                    var take = Math.Min(b.Cap, rem); b.Cap -= take; rem -= take;
                                }
                                if (b.Cap <= 0 && b.Stacks <= 0) b.Done = true;
                            }
                            delta += d - rem;
                            var loggedAfter = hpBefore - d;
                            Cap(e.T, Math.Max(0, loggedAfter));
                            var cfAfter = loggedAfter + delta;
                            var died = tankDeaths.FirstOrDefault(x => x.T >= e.T - 200 && x.T <= e.T + 4000);
                            if (loggedAfter <= 0 && died is not null && recorded.Add(died.T))
                            {
                                if (!Wipe(died.T))
                                    deaths.Add(new Death(died.T, tank.Name, PhaseOf(r, died.T), SegmentOf(r, died.T), $"{e.Name} {d / 1000:0}k at {100 * hpBefore / max:0}%",
                                        100 * cfAfter / max, cfAfter > 0));
                                delta = 0;   // the log has nothing more for this tank until a raise; start again from it
                            }
                            else if (loggedAfter > 0 && cfAfter <= 0 && !w.Dead(tank, e.T))
                            {
                                if (stopAt == int.MaxValue)
                                    cfDeaths.Add(new CfDeath(e.T, tank.Name, PhaseOf(r, e.T), $"{e.Name} {d / 1000:0}k, logged {100 * loggedAfter / max:0}% after it, Δ {delta / 1000:0}k"));
                                stopAt = Math.Min(stopAt, e.T);
                            }
                            break;
                        }
                    }
                }
                if (w.Dead(tank, t)) { stopAt = int.MaxValue; delta = 0; continue; }
                var lg = tank.Hp.At(t, (int)max);
                if (lg <= 0) continue;
                // After a counterfactual death the rules' tank counts as dead (0%) until the logged one dies or the pull ends.
                var cf = t >= stopAt ? 0 : Math.Min(max, lg + delta);
                var cfDeadNow = t >= stopAt;
                var lp = 100 * lg / max; var cp = 100 * cf / max;
                var ph0 = PhaseOf(r, t);
                foreach (var key in new[] { ph0, ph0 == "p3" ? "p3 " + SegmentOf(r, t) : "" })
                {
                    if (key.Length == 0) continue;
                    var acc = Acc(key);
                    if (lp < 30) acc[0] += 0.1; if (cp < 30 && !cfDeadNow) acc[1] += 0.1; if (lp < 15) acc[2] += 0.1; if (cp < 15 && !cfDeadNow) acc[3] += 0.1;
                    if (cfDeadNow) acc[6] += 0.1;
                    acc[4] = Math.Min(acc[4], cp); acc[5] = Math.Min(acc[5], lp);
                }
            }
            foreach (var (ph, a) in phaseAcc)
            {
                var old = byPhase.TryGetValue(ph, out var o) ? o : (0, 0, 0, 0, 100, 100, 0);
                byPhase[ph] = (old.Item1 + a[0], old.Item2 + a[1], old.Item3 + a[2], old.Item4 + a[3], Math.Min(old.Item5, a[4]), Math.Min(old.Item6, a[5]), old.Item7 + a[6]);
            }
        }
        return new Result(deaths, cfDeaths, byPhase, rulesLow, realLow, notes);
    }
}
