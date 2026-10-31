using RaidReplay.Core.Indexing;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

/// <summary>
///     2026-10-29 tank-care audit for a logged White Mage (log only; TankAudit.cs is the Sage's): every moment a tank's logged
///     HP fell under 30% or the tank died, with what was hitting it, the tank's own cooldowns, and what the White Mage had
///     available (Divine Benison and Tetragrammaton charges, Benediction, Aquaveil, Temperance, rebuilt from its presses;
///     Lilies rebuilt from 3 at the pull, one every 20 s under 3, one per Solace or Rapture), and the GCDs it pressed.
///   whmaudit &lt;log&gt; whm|N,M [--me=HEX] [--csv=FILE]
/// </summary>
internal static class WhmTankAudit
{
    private static readonly HashSet<string> TankMit =
    [
        "Rampart", "Dark Mind", "Shadow Wall", "Shadowed Vigil", "The Blackest Night", "Oblation", "Living Dead", "Walking Dead",
        "Undead Rebirth", "Nebula", "Great Nebula", "Camouflage", "Heart of Corundum", "Clarity of Corundum", "Catharsis of Corundum",
        "Brutal Shell", "Superbolide", "Aurora", "Sentinel", "Guardian", "Hallowed Ground", "Holy Sheltron", "Intervention",
        "Knight's Resolve", "Knight's Benediction", "Bulwark", "Vengeance", "Damnation", "Holmgang", "Bloodwhetting", "Raw Intuition",
        "Nascent Glint", "Stem the Flow", "Stem the Tide", "Thrill of Battle", "Equilibrium", "Reprisal", "Dark Missionary",
        "Heart of Light", "Passage of Arms", "Divine Veil", "Shake It Off", "Arm's Length", "Kerachole", "Taurochole", "Haima",
        "Panhaima", "Holosakos", "Eukrasian Diagnosis", "Eukrasian Prognosis", "Krasis", "Divine Benison", "Aquaveil", "Temperance",
        "Asylum", "Liturgy of the Bell", "Divine Caress", "Shield Samba", "Feint", "Addle", "Troubadour", "Tactician", "Magick Barrier",
        "Confession", "Regen", "Medica III", "Divine Aura",
    ];

    public static int Main(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index, string csv)
    {
        var lines = new List<string>();
        foreach (var s in WhmCommands.Select(index(log), spec, meId))
        {
            var r = PullLoader.Load(s, null, registry);
            var w = new World(r, meId);
            foreach (var l in Run(s, r, w, meId)) { Console.WriteLine(l.Text); lines.Add(l.Tsv); }
        }
        if (csv.Length > 0) File.WriteAllLines(csv, lines);
        return 0;
    }

    /// <summary>
    ///     whmp3 &lt;log&gt; &lt;pulls&gt; --me=HEX: the player's presses in Phase 3's two-boss stretch (Bowels of Agony, Implosion, Umbra Smash
    ///     segments), by action and target role, with the share of that time each tank had the player's Regen.
    /// </summary>
    public static int P3(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var tot = new Dictionary<string, int>(); double totMin = 0; var regenUp = new List<double>();
        foreach (var s in WhmCommands.Select(index(log), spec, meId))
        {
            var r = PullLoader.Load(s, null, registry);
            var w = new World(r, meId);
            var segs = r.Phases.Where(ph => ph.IsSegment && ph.Name is "Bowels of Agony" or "Implosion" or "Umbra Smash + Ultima Blaster").ToList();
            if (segs.Count == 0) continue;
            int s0 = segs.Min(v => v.StartMs), s1 = Math.Min(segs.Max(v => v.EndMs), r.EndMs);
            var mine = r.Actions.Where(a => a.Source.Id == meId && a.T >= s0 && a.T < s1).GroupBy(a => a.Sequence != 0 ? (long)a.Sequence : ((long)a.ActionId << 32) + a.T).Select(g => g.First()).OrderBy(a => a.T).ToList();
            var c = new Dictionary<string, int>();
            foreach (var a in mine)
            {
                var tg = a.PrimaryTarget;
                var k = a.Name + (tg is { IsPlayer: true } && tg.Id != meId ? (World.IsTank(tg) ? "->tank" : "->" + World.RoleOf(tg)) : "");
                c[k] = c.GetValueOrDefault(k) + 1; tot[k] = tot.GetValueOrDefault(k) + 1;
            }
            totMin += (s1 - s0) / 60000.0;
            var tanks = w.Party.Where(World.IsTank).ToList();
            var ups = tanks.Select(tk => 100.0 * r.Statuses.Where(st => st.Source?.Id == meId && st.Target.Id == tk.Id && st.Name == "Regen")
                .Sum(st => Math.Max(0, Math.Min(st.EndMs, s1) - Math.Max(st.StartMs, s0))) / Math.Max(1, s1 - s0)).ToList();
            regenUp.AddRange(ups);
            Console.WriteLine($"#{s.Ordinal} {SageSim.F(s0)}-{SageSim.F(s1)} {(w.Dead(w.Me, s1 - 1) ? "(WHM dead by the end) " : "")}Regen up on tanks {string.Join("/", ups.Select(u => $"{u:0}%"))}: " + string.Join(", ", c.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
        }
        Console.WriteLine($"TOTAL {totMin:0.0} min: " + string.Join(", ", tot.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value} ({kv.Value / Math.Max(0.01, totMin):0.0}/min)")));
        if (regenUp.Count > 0) Console.WriteLine($"Regen uptime on tanks: mean {regenUp.Average():0}%");
        return 0;
    }

    public static List<(string Text, string Tsv)> Run(PullSummary s, PullReplay r, World w, uint meId)
    {
        var res = new List<(string, string)>();
        var me = w.Me;
        var tanks = w.Party.Where(World.IsTank).ToList();
        var coHealer = w.Party.FirstOrDefault(p => World.IsHealer(p) && p.Id != meId);
        var mine = r.Actions.Where(a => a.Source.Id == meId).GroupBy(a => a.Sequence != 0 ? (long)a.Sequence : ((long)a.ActionId << 32) + a.T)
            .Select(g => g.First()).OrderBy(a => a.T).ToList();
        var presses = mine.Select(a => (a.T, a.Name)).ToList();
        (int Lily, int Blood) GaugeAt(int t)
        {
            int lily = 3, blood = 3, timer = 0, last = 0;
            foreach (var a in mine.Where(a => a.T >= 0 && a.T <= t))
            {
                Adv(a.T);
                if (a.Name is "Afflatus Solace" or "Afflatus Rapture") { lily = Math.Max(0, lily - 1); blood = Math.Min(3, blood + 1); }
                if (a.Name == "Afflatus Misery") blood = 0;
            }
            Adv(t);
            return (lily, blood);
            void Adv(int to)
            {
                var dt = to - last; last = to;
                if (lily >= 3) { timer = 0; return; }
                timer += dt;
                while (timer >= 20000 && lily < 3) { lily++; timer -= 20000; }
                if (lily >= 3) timer = 0;
            }
        }
        var deathsAll = r.Deaths.OrderBy(d => d.T).ToList();
        foreach (var tank in tanks)
        {
            var max = tank.MaxHp;
            if (max <= 0) continue;
            var hits = r.Actions.Where(a => !a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet)
                .SelectMany(a => a.Hits.Where(h => h.Target.Id == tank.Id && h.Damage > 0).Select(h => (a.T, a.Name, Dmg: (double)h.Damage))).OrderBy(x => x.T).ToList();
            var eps = new List<(int Start, int MinT, double Min, int End, bool Died)>();
            (int Start, int MinT, double Min)? cur = null;
            var tDeath = deathsAll.Where(d => d.Victim.Id == tank.Id).Select(d => d.T).ToList();
            for (var t = 0; t <= r.EndMs; t += 200)
            {
                if (w.Dead(tank, t))
                {
                    if (cur is { } c0) { eps.Add((c0.Start, c0.MinT, c0.Min, t, tDeath.Any(d => d <= t && d >= c0.Start - 1000))); cur = null; }
                    continue;
                }
                var hp = 100.0 * tank.Hp.At(t, max) / max;
                if (cur is null && hp < 30 && hp > 0) cur = (t, t, hp);
                else if (cur is { } c)
                {
                    if (hp < c.Min && hp > 0) cur = (c.Start, t, hp);
                    if (hp > 50) { eps.Add((c.Start, c.MinT, c.Min, t, false)); cur = null; }
                }
            }
            if (cur is { } ce) eps.Add((ce.Start, ce.MinT, ce.Min, r.EndMs, false));
            foreach (var d in deathsAll.Where(d => d.Victim.Id == tank.Id))
                if (!eps.Any(e => e.Died && Math.Abs(e.End - d.T) <= 1500))
                    eps.Add((d.T, d.T, 0, d.T, true));
            foreach (var e in eps.OrderBy(e => e.Start))
            {
                var at = e.Died ? (deathsAll.FirstOrDefault(d => d.Victim.Id == tank.Id && Math.Abs(d.T - e.End) <= 1500)?.T ?? e.End) : e.MinT;
                var death = e.Died ? deathsAll.FirstOrDefault(d => d.Victim.Id == tank.Id && Math.Abs(d.T - at) <= 1500) : null;
                var oneShot = e.Died && e.Min == 0 && e.Start == e.End;
                var wipe = deathsAll.Count(d => d.T >= at - 2000 && d.T <= at + 8000) >= 5;
                var w0 = at - 10000;
                var win = hits.Where(h => h.T >= w0 && h.T <= at).ToList();
                double autoK = win.Where(h => h.Name is "auto-attack" or "attack").Sum(h => h.Dmg) / 1000, otherK = win.Where(h => h.Name is not ("auto-attack" or "attack")).Sum(h => h.Dmg) / 1000;
                var hitStr = string.Join(" ", win.Where(h => h.Dmg >= 0.1 * max).Select(h => $"{SageSim.F(h.T)}:{(h.Name == "auto-attack" ? "auto" : h.Name)}:{100 * h.Dmg / max:0}"));
                var own = r.Statuses.Where(st => st.Target.Id == tank.Id && TankMit.Contains(st.Name) && st.StartMs <= at - 3000 && st.EndMs > at - 3000)
                    .Select(st => st.Name).Distinct().OrderBy(x => x);
                var whmDead = w.Dead(me, at - 3000);
                var g = GaugeAt(at - 3000);
                var ready = whmDead ? "WHM DEAD" : WhmTankMetrics.ReadyAt(presses, at - 3000) + $",Lily{g.Lily},Blood{g.Blood}";
                var gcds = mine.Where(a => a.T >= w0 && a.T <= at && WhmSim.GcdNames.Contains(a.Name))
                    .Select(a => a.Name.Replace("Afflatus ", "").Replace("Glare III", "Glare") + (a.PrimaryTarget is { IsPlayer: true } pt && pt.Id != meId ? "->" + (pt.Id == tank.Id ? "T" : World.RoleOf(pt)) : ""));
                var og = mine.Where(a => a.T >= w0 && a.T <= at && a.Name is "Divine Benison" or "Aquaveil" or "Tetragrammaton" or "Benediction"
                    && (a.PrimaryTarget?.Id == tank.Id || a.Hits.Any(h => h.Target.Id == tank.Id))).Select(a => a.Name);
                double myHeal = 0, coHeal = 0;
                foreach (var a in r.Actions.Where(a => a.T >= w0 && a.T <= at && (a.Source.Id == meId || coHealer is not null && a.Source.Id == coHealer.Id)))
                    foreach (var h in a.Hits.Where(h => h.Target.Id == tank.Id && h.Kind == HitKind.Heal))
                        if (a.Source.Id == meId) myHeal += h.Heal; else coHeal += h.Heal;
                foreach (var tk in r.Ticks.Where(tk => tk.IsHeal && tk.Target.Id == tank.Id && tk.T >= w0 && tk.T <= at))
                    if (tk.Source?.Id == meId) myHeal += tk.Amount; else if (coHealer is not null && tk.Source?.Id == coHealer.Id) coHeal += tk.Amount;
                var phase = TankMetrics.PhaseOf(r, at); var seg = TankMetrics.SegmentOf(r, at);
                var date = $"{new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm}";
                var job = TankAudit.JobName(tank.Job);
                var text = $"#{s.Ordinal} {date} {phase} {SageSim.F(at),7} [{seg}] {tank.Name} {job} min {e.Min:0}%{(e.Died ? (oneShot ? " ONE-SHOT" : " DIED") : "")}{(wipe ? " (wipe)" : "")} | 10s dmg autos {autoK:0}k other {otherK:0}k | own: {string.Join(",", own)} | ready {ready} | GCDs {string.Join(" ", gcds)} | oGCDs on tank {string.Join(",", og)} | heals 10s: WHM {myHeal / 1000:0}k co {coHeal / 1000:0}k | hits {hitStr}{(death is not null ? " | KB " + death.Cause : "")}";
                var tsv = string.Join("\t", s.Ordinal, date, phase, seg, at, tank.Name, job, e.Min.ToString("0.0"), e.Died, oneShot, wipe, autoK.ToString("0"), otherK.ToString("0"), string.Join(",", own), ready, string.Join(" ", gcds), string.Join(",", og), myHeal.ToString("0"), coHeal.ToString("0"), hitStr, death?.Cause ?? "");
                res.Add((text, tsv));
            }
        }
        return res;
    }
}
