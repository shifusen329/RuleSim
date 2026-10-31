using RaidReplay.Core.Model;

/// <summary>
///     2026-10-28 tank-care audit (log only): every moment a tank's logged HP fell under 30% or the tank died, with the
///     phase, what was hitting it, the tank's own cooldowns, and what the real Sage had available (Addersgall rebuilt from
///     its 20 s timer and the Sage's spends, Kardia's holder, the oGCD heals off cooldown, the GCDs it pressed).
/// </summary>
internal static class TankAudit
{
    public sealed record Episode(int Pull, string Date, string Phase, string Segment, int T, string Tank, string Job, double MinPct, bool Died,
        bool OneShot, bool WipeDeath, string Hits, double AutoK, double OtherK, string OwnMit, string Kardia, int Gall, string Ready,
        string SageGcds, string SageOgcdsOnTank, double SageHealK, double CoHealK, string KillingBlow);

    private static readonly Dictionary<string, int> Cd = new()
    {
        ["Haima"] = 120, ["Taurochole"] = 45, ["Krasis"] = 60, ["Soteria"] = 60, ["Holos"] = 120, ["Panhaima"] = 120, ["Zoe"] = 90,
        ["Rhizomata"] = 90, ["Kerachole"] = 30, ["Ixochole"] = 30, ["Pepsis"] = 30, ["Physis II"] = 60, ["Pneuma"] = 120,
    };

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
    ];

    public static List<Episode> Run(PullSummary s, PullReplay r, World w, uint meId)
    {
        var res = new List<Episode>();
        var me = w.Me;
        var party = w.Party;
        var tanks = party.Where(World.IsTank).ToList();
        var coHealer = party.FirstOrDefault(p => World.IsHealer(p) && p.Id != meId);
        var mine = r.Actions.Where(a => a.Source.Id == meId).GroupBy(a => a.Sequence != 0 ? (long)a.Sequence : ((long)a.ActionId << 32) + a.T)
            .Select(g => g.First()).OrderBy(a => a.T).ToList();
        // Addersgall: 3 at the pull, one every 20 s below 3 (the timer stops at 3), -1 for each Addersgall spend, +1 Rhizomata.
        var gallAt = new List<(int T, int Gall)>();
        int gall = 3, timer = 0, last = int.MinValue;
        void Adv(int t)
        {
            if (last == int.MinValue) { last = t; return; }
            var dt = t - last; last = t;
            if (gall >= 3) { timer = 0; return; }
            timer += dt;
            while (timer >= 20000 && gall < 3) { gall++; timer -= 20000; }
            if (gall >= 3) timer = 0;
        }
        Adv(-30000);
        foreach (var a in mine)
        {
            Adv(a.T);
            if (a.Name is "Kerachole" or "Ixochole" or "Taurochole" or "Druochole") gall = Math.Max(0, gall - 1);
            if (a.Name == "Rhizomata") gall = Math.Min(3, gall + 1);
            gallAt.Add((a.T, gall));
        }
        int GallAt(int t)
        {
            // re-run the timer from the last spend before t
            var g = 3; var tm = 0; var l = -30000;
            foreach (var a in mine)
            {
                if (a.T > t) break;
                var dt = a.T - l; l = a.T;
                if (g < 3) { tm += dt; while (tm >= 20000 && g < 3) { g++; tm -= 20000; } if (g >= 3) tm = 0; }
                if (a.Name is "Kerachole" or "Ixochole" or "Taurochole" or "Druochole") g = Math.Max(0, g - 1);
                if (a.Name == "Rhizomata") g = Math.Min(3, g + 1);
            }
            if (g < 3) { tm += t - l; while (tm >= 20000 && g < 3) { g++; tm -= 20000; } }
            return g;
        }
        var kardion = r.Statuses.Where(st => st.Source?.Id == meId && st.Name == "Kardion").ToList();
        string Phase(int t) => r.Phases.Where(p => !p.IsSegment && p.StartMs <= t && t < Math.Max(p.EndMs, p.StartMs + 1)).Select(p => p.Id).FirstOrDefault()
            ?? r.Phases.Where(p => !p.IsSegment && p.StartMs <= t).Select(p => p.Id).LastOrDefault() ?? "?";
        string Segment(int t) => r.Phases.Where(p => p.IsSegment && p.StartMs <= t && t < p.EndMs).Select(p => p.Name).FirstOrDefault() ?? "";
        var deathsAll = r.Deaths.OrderBy(d => d.T).ToList();

        foreach (var tank in tanks)
        {
            var max = tank.MaxHp;
            if (max <= 0) continue;
            var hits = r.Actions.Where(a => !a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet)
                .SelectMany(a => a.Hits.Where(h => h.Target.Id == tank.Id && h.Damage > 0).Select(h => (a.T, a.Name, Dmg: (double)h.Damage, h.HpBefore))).OrderBy(x => x.T).ToList();
            // episodes: HP under 30% until over 50% (or death)
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
            // deaths not preceded by an episode: one-shots
            foreach (var d in deathsAll.Where(d => d.Victim.Id == tank.Id))
                if (!eps.Any(e => e.Died && Math.Abs(e.End - d.T) <= 1500))
                    eps.Add((d.T, d.T, 0, d.T, true));
            foreach (var e in eps.OrderBy(e => e.Start))
            {
                var at = e.Died ? (deathsAll.FirstOrDefault(d => d.Victim.Id == tank.Id && Math.Abs(d.T - e.End) <= 1500)?.T ?? e.End) : e.MinT;
                var death = e.Died ? deathsAll.FirstOrDefault(d => d.Victim.Id == tank.Id && Math.Abs(d.T - at) <= 1500) : null;
                var oneShot = e.Died && e.Min == 0 && e.Start == e.End;
                var wipe = e.Died && deathsAll.Count(d => d.T >= at - 2000 && d.T <= at + 8000) >= 5;
                var w0 = at - 10000;
                var win = hits.Where(h => h.T >= w0 && h.T <= at).ToList();
                double autoK = win.Where(h => h.Name == "auto-attack" || h.Name == "attack").Sum(h => h.Dmg) / 1000, otherK = win.Where(h => h.Name != "auto-attack" && h.Name != "attack").Sum(h => h.Dmg) / 1000;
                var hitStr = string.Join(" ", win.Where(h => h.Dmg >= 0.1 * max).Select(h => $"{SageSim.F(h.T)}:{(h.Name == "auto-attack" ? "auto" : h.Name)}:{100 * h.Dmg / max:0}"));
                var own = r.Statuses.Where(st => st.Target.Id == tank.Id && TankMit.Contains(st.Name) && st.StartMs <= at - 3000 && st.EndMs > at - 3000)
                    .Select(st => st.Name).Distinct().OrderBy(x => x);
                var kh = kardion.FirstOrDefault(k => k.StartMs <= at - 1000 && k.EndMs > at - 1000)?.Target;
                var kstr = kh is null ? "none" : kh.Id == tank.Id ? "THIS" : World.IsTank(kh) ? "other tank" : World.RoleOf(kh);
                var g = GallAt(at - 3000);
                var ready = new List<string>();
                var sageDead = w.Dead(me, at - 3000);
                if (!sageDead)
                    foreach (var (n, cd) in Cd)
                    {
                        var lu = mine.LastOrDefault(a => a.Name == n && a.T <= at - 3000);
                        if (lu is null || at - 3000 - lu.T >= cd * 1000) ready.Add(n);
                    }
                var gcds = mine.Where(a => a.T >= w0 && a.T <= at && a.Name is "Dosis III" or "Eukrasian Dosis III" or "Phlegma III" or "Toxikon II"
                    or "Dyskrasia II" or "Pneuma" or "Diagnosis" or "Eukrasian Diagnosis" or "Prognosis" or "Eukrasian Prognosis II" or "Egeiro" or "Eukrasian Dyskrasia")
                    .Select(a => a.Name.Replace("Eukrasian ", "E.").Replace(" III", "").Replace(" II", "") + (a.PrimaryTarget is { IsPlayer: true } pt && pt.Id != meId ? "->" + (pt.Id == tank.Id ? "T" : World.RoleOf(pt)) : ""));
                var ogcds = mine.Where(a => a.T >= w0 && a.T <= at && a.Name is "Haima" or "Taurochole" or "Druochole" or "Krasis" or "Soteria" or "Kardia"
                    && (a.PrimaryTarget?.Id == tank.Id || a.Hits.Any(h => h.Target.Id == tank.Id) || a.Name == "Soteria")).Select(a => a.Name);
                double sageHeal = 0, coHeal = 0;
                foreach (var a in r.Actions.Where(a => a.T >= w0 && a.T <= at && (a.Source.Id == meId || coHealer is not null && a.Source.Id == coHealer.Id)))
                    foreach (var h in a.Hits.Where(h => h.Target.Id == tank.Id && h.Kind == HitKind.Heal))
                        if (a.Source.Id == meId) sageHeal += h.Heal; else coHeal += h.Heal;
                foreach (var tk in r.Ticks.Where(tk => tk.IsHeal && tk.Target.Id == tank.Id && tk.T >= w0 && tk.T <= at))
                    if (tk.Source?.Id == meId) sageHeal += tk.Amount; else if (coHealer is not null && tk.Source?.Id == coHealer.Id) coHeal += tk.Amount;
                var kb = death is null ? "" : $"{death.Cause}";
                res.Add(new Episode(s.Ordinal, $"{new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm}", Phase(at), Segment(at), at, tank.Name, World.RoleOf(tank) == "tank" ? JobName(tank.Job) : "?",
                    e.Min, e.Died, oneShot, wipe, hitStr, autoK, otherK, string.Join(",", own), kstr, sageDead ? -1 : g, sageDead ? "SAGE DEAD" : string.Join(",", ready.Where(x => x is "Haima" or "Taurochole" or "Krasis" or "Soteria" or "Zoe" or "Rhizomata" or "Pepsis" or "Holos" or "Panhaima")),
                    string.Join(" ", gcds), string.Join(",", ogcds), sageHeal / 1000, coHeal / 1000, kb));
            }
        }
        return res;
    }

    public static string JobName(int j) => j switch { 19 => "PLD", 21 => "WAR", 32 => "DRK", 37 => "GNB", _ => j.ToString() };
}
