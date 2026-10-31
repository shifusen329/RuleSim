// Samurai (level 100) simulator for checking a rule set against the sheet.
// Fixed 2.14 GCD, full uptime, positionals always hit, no movement.
using System.Text.Json;
using WrathCombo.CustomRules;
using static Id;

var rulePath = args[0];
var minutes = args.Length > 1 ? double.Parse(args[1]) : 8.2;
var verbose = args.Contains("-v");
// --down=START:SECONDS[:new]  the target is gone for SECONDS; "new" = it comes back without your debuffs
var downtimes = args.Where(a => a.StartsWith("--down=")).Select(a =>
{
    var p = a["--down=".Length..].Split(':');
    return new Downtime(double.Parse(p[0]), double.Parse(p[1]), p.Length > 2 && p[2] == "new");
}).ToList();

var file = JsonSerializer.Deserialize<RuleSetFile>(File.ReadAllText(rulePath), new JsonSerializerOptions
{
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
})!;

var sim = new Sim(2.14);
// --dist=FILE: "seconds,yalms to the hitbox edge" every 0.1 s (-1 = no target), from a real pull.
if (args.FirstOrDefault(a => a.StartsWith("--dist=")) is { } distArg)
    sim.Distances = File.ReadAllLines(distArg["--dist=".Length..]).Select(l => double.Parse(l.Split(',')[1])).ToArray();
sim.EnpiBreaksCombo = args.Contains("--enpi-breaks-combo");
// --move=FILE: "seconds,speed y/s" every 0.1 s from the real position track. Moving = faster than --move-speed (2 y/s);
// a cast is cancelled if you move within --cast-lock (0.8 s) of its start (both calibrated on the log).
if (args.FirstOrDefault(a => a.StartsWith("--move=")) is { } moveArg)
    sim.Speeds = File.ReadAllLines(moveArg["--move=".Length..]).Select(l => double.Parse(l.Split(',')[1])).ToArray();
// --stun=T:L,...  incapacitated (no actions at all) for L s from T.   --away=T:L:D,...  pushed D yalms away for L s from T.
foreach (var a in args.Where(a => a.StartsWith("--stun=")))
    foreach (var w in a["--stun=".Length..].Split(',')) { var x = w.Split(':'); sim.Stuns.Add((double.Parse(x[0]), double.Parse(x[1]))); }
foreach (var a in args.Where(a => a.StartsWith("--away=")))
    foreach (var w in a["--away=".Length..].Split(','))
    {
        var x = w.Split(':');
        double t0 = double.Parse(x[0]), len = double.Parse(x[1]), d = double.Parse(x[2]);
        sim.Distances ??= new double[(int)(minutes * 600) + 2000];
        for (var i = (int)(t0 * 10); i < (int)((t0 + len) * 10) && i < sim.Distances.Length; i++) sim.Distances[i] = d;
    }
if (args.FirstOrDefault(a => a.StartsWith("--cast-lock=")) is { } lockArg) sim.CastLock = double.Parse(lockArg["--cast-lock=".Length..]);
if (args.FirstOrDefault(a => a.StartsWith("--move-speed=")) is { } msArg) sim.MoveSpeed = double.Parse(msArg["--move-speed=".Length..]);
sim.Partner = args.Contains("--partner");
if (args.FirstOrDefault(a => a.StartsWith("--clock-offset=")) is { } co) sim.ClockOffset = double.Parse(co["--clock-offset=".Length..]);
sim.DevilmentStarts = args.FirstOrDefault(a => a.StartsWith("--devil=")) is { } devArg
    ? devArg["--devil=".Length..].Split(',').Select(double.Parse).ToArray()
    : Enumerable.Range(0, 12).Select(k => 2.4 + 120 * k).ToArray();
// --raid=FILE: "seconds,raid buffs up" every 0.1 s, from a real pull. Default: like pull #23,
// three 20 s buffs at 2.0 / 2.6 / 3.8 s past every 2-minute mark.
if (args.FirstOrDefault(a => a.StartsWith("--raid=")) is { } raidArg)
    sim.RaidCounts = File.ReadAllLines(raidArg["--raid=".Length..]).Select(l => int.Parse(l.Split(',')[1])).ToArray();
else
    sim.RaidCounts = Enumerable.Range(0, 20000).Select(i =>
    {
        var m = i / 10.0 % 120;
        return (m >= 2.0 && m < 22.0 ? 1 : 0) + (m >= 2.6 && m < 22.6 ? 1 : 0) + (m >= 3.8 && m < 23.8 ? 1 : 0);
    }).ToArray();
var forced = (args.FirstOrDefault(a => a.StartsWith("--force=")) is { } forceArg
        ? forceArg["--force=".Length..].Split(',').Select(s => new Forced(double.Parse(s)))
        : []).ToList();
var errors = new List<string>();
var set = RuleSetCompiler.Compile(file, "sim", new SimSymbols(sim), _ => sim.Functions(), errors);
if (set is null)
{
    Console.WriteLine("Compile errors:\n  " + string.Join("\n  ", errors));
    return 1;
}

// --probe=T1:T2:define1,define2,...: print those defines at each GCD's first weave slot in [T1, T2].
var probe = args.FirstOrDefault(a => a.StartsWith("--probe=")) is { } probeArg ? probeArg["--probe=".Length..].Split(':') : null;
var probeDefines = probe is null ? [] : set.Defines.Where(d => probe[2].Split(',').Contains(d.Name)).ToList();

(uint Action, string Rule) Evaluate()
{
    var a = set.Evaluate(Gyofu, sim);
    return (a, set.LastFired >= 0 ? set.Rules[set.LastFired].Name : "-");
}

// --replay=file.csv (t,id,name,kenki,sen per line): force the sim through an in-game action list and,
// at each action and each weave window, print what the rules would have chosen. --show=d1,d2 adds defines.
var replaying = false;
if (args.FirstOrDefault(a => a.StartsWith("--replay=")) is { } replayArg)
{
    replaying = true;
    var show = args.FirstOrDefault(a => a.StartsWith("--show=")) is { } sa ? sa["--show=".Length..].Split(',') : [];
    var shown = set.Defines.Where(d => show.Contains(d.Name)).ToList();
    string Defs() => shown.Count == 0 ? "" : "   [" + string.Join(" ", shown.Select(d => $"{d.Name}={d.Value():0.##}")) + "]";
    var entries = File.ReadAllLines(replayArg["--replay=".Length..]).Where(l => l.Length > 0)
        .Select(l => l.Split(',')).Select(x => (T: double.Parse(x[0]), Id: uint.Parse(x[1]), Name: x[2], Kenki: int.Parse(x[3]),
                       Sen: int.Parse(x[4]), Meditation: x.Length > 5 ? int.Parse(x[5]) : -1)).ToList();
    // Actions the sim models; anything else (role actions, Tengentsu, Meditate, items) is skipped.
    uint[] modelled = [Gyofu, Hakaze, Jinpu, Shifu, Yukikaze, Gekko, Kasha, Higanbana, TenkaGoken, TendoGoken,
        MidareSetsugekka, TendoSetsugekka, KaeshiSetsugekka, TendoKaeshiSetsugekka, OgiNamikiri, KaeshiNamikiri,
        MeikyoShisui, Senei, Ikishoten, Zanshin, Shoha, Shinten, Gyoten, Enpi, TrueNorth];
    var skipped = entries.Where(e => !modelled.Contains(e.Id)).GroupBy(e => e.Name).Select(g => $"{g.Key} x{g.Count()}").ToList();
    if (skipped.Count > 0) Console.WriteLine($"skipped (not modelled): {string.Join(", ", skipped)}");
    entries = entries.Where(e => modelled.Contains(e.Id)).ToList();
    var from = args.FirstOrDefault(a => a.StartsWith("--from=")) is { } fa ? double.Parse(fa[7..]) : double.NegativeInfinity;
    var to = args.FirstOrDefault(a => a.StartsWith("--to=")) is { } ta ? double.Parse(ta[5..]) : double.PositiveInfinity;
    var mismatches = 0;
    for (var i = 0; i < entries.Count; i++)
    {
        var e = entries[i];
        // A new target after downtime has no Higanbana on it.
        foreach (var d in downtimes.Where(d => !d.Done && d.NewTarget && e.T >= d.Start + d.Length))
        {
            d.Done = true;
            sim.BanaExpire = double.NegativeInfinity;
        }
        var gcdAction = Sim.IsGcd(e.Id);
        sim.T = e.T;
        // Keep the gauge in step with the game (Tengentsu, Enpi and the like change it outside the model).
        if (sim.CombatStart is not null)
        {
            sim.Kenki = e.Kenki;
            sim.Getsu = (e.Sen & 2) != 0; sim.Setsu = (e.Sen & 1) != 0; sim.Ka = (e.Sen & 4) != 0;
            if (e.Meditation >= 0) sim.Meditation = e.Meditation;
        }
        sim.Weave = !gcdAction;
        var (want, why) = Evaluate();
        var same = want == e.Id;
        if (!same) mismatches++;
        if (e.T >= from && e.T <= to)
            Console.WriteLine($"{e.T,7:0.00} {(gcdAction ? "GCD " : "oGCD")} {e.Name,-26} {(same ? "  ok" : "  ->")} {(same ? $"({why})" : $"{Names.Short(want)} ({why})"),-48} kenki sim {sim.Kenki,3} / game {e.Kenki,3}{Defs()}");
        sim.Use(e.Id, "replay");
        if (gcdAction)
        {
            sim.GcdReadyAt = e.T + sim.Gcd;
            // The weave window after this GCD: did the rules want an oGCD that the game didn't use?
            var next = i + 1 < entries.Count ? entries[i + 1] : default;
            var usedWeave = next != default && !Sim.IsGcd(next.Id);
            var inDowntime = downtimes.Any(d => e.T >= d.Start - sim.Gcd && e.T < d.Start + d.Length);
            if (!usedWeave && !inDowntime)
            {
                sim.T = e.T + (Sim.IsCast(e.Id) ? 1.4 : 0.7);
                sim.Weave = true;
                var (w, wr) = Evaluate();
                if (w is not (0 or Wait) && !Sim.IsGcd(w) && sim.T >= from && sim.T <= to)
                {
                    mismatches++;
                    Console.WriteLine($"{sim.T,7:0.00} oGCD {"(nothing used)",-26}   -> {$"{Names.Short(w)} ({wr})",-48}{Defs()}");
                }
            }
        }
    }
    Console.WriteLine($"{entries.Count} actions, {mismatches} differences from the rules");
}

// Countdown from 16s; the pull is at t = 0.
sim.T = -16;
while (!replaying && sim.CombatStart is null)
{
    var (a, rule) = Evaluate();
    if (a is 0 or Wait) { sim.T += 0.1; continue; }
    if (Sim.IsGcd(a)) { sim.T = Math.Max(sim.T, -0.3); RunGcd(a, rule); break; }
    sim.Use(a, rule);
    sim.T += 0.6;
}

while (!replaying && sim.T < minutes * 60)
{
    if (downtimes.FirstOrDefault(d => !d.Done && sim.T >= d.Start) is { } down)
    {
        down.Done = true;
        down.Enter = sim.Snapshot();
        var expiring = sim.ActiveBuffs().Where(b => b.Left < down.Length).Select(b => $"{Names.Status(b.Id)} ({b.Left:0}s left)");
        down.Lost = string.Join(", ", expiring);
        sim.T += down.Length;
        if (down.NewTarget) sim.BanaExpire = double.NegativeInfinity;
        down.Exit = sim.Snapshot();
        down.ResumeAt = sim.T;
        continue;
    }

    if (sim.StunEnd(sim.T) is { } stunEnd) { sim.IdleTime += stunEnd - sim.T; sim.StunTime += stunEnd - sim.T; sim.T = stunEnd; continue; }
    var (a, rule) = Evaluate();
    // --force=T1,T2,...: the first GCD at or after each time is an Enpi (knocked out of melee for one GCD).
    if (Sim.IsGcd(a) && forced.FirstOrDefault(f => !f.Done && sim.T >= f.T) is { } force)
    {
        force.Done = true;
        (a, rule) = (Enpi, "FORCED Enpi");
    }
    if (a is 0 or Wait || !sim.InRangeNow(a) || Sim.IsCast(a) && sim.Moving)   // casts can't start while moving
    {
        // Nothing usable (or the button's action is out of range): keep pressing.
        if (a is not (0 or Wait)) sim.FailedPresses.Add((sim.T, a));
        sim.IdleTime += 0.1;
        sim.T += 0.1;
        continue;
    }
    if (!Sim.IsGcd(a)) { sim.Use(a, rule); sim.T += 0.6; continue; }
    // Moving during the cast's stationary window cancels it: the casting time is lost and the GCD restarts.
    if (Sim.IsCast(a) && (sim.FirstMoveWithin(sim.T, sim.CastLock) ?? sim.FirstStunWithin(sim.T, sim.CastLock)) is { } cancelAt)
    {
        sim.CancelledCasts.Add((sim.T, a));
        sim.IdleTime += cancelAt - sim.T;
        sim.T = cancelAt;
        continue;
    }
    RunGcd(a, rule);
}

void RunGcd(uint action, string rule)
{
    sim.Weave = false;
    var start = sim.T;
    sim.Use(action, rule);
    sim.GcdReadyAt = start + sim.Gcd;
    // A player spamming the button: try every 50 ms between the end of the
    // GCD's animation (or cast) and the last moment that doesn't clip.
    var offset = Sim.IsCast(action) ? 1.3 : 0.6;
    while (offset <= sim.Gcd - 0.6 + 1e-9)
    {
        sim.T = start + offset;
        sim.Weave = true;
        if (probe is not null && offset == (Sim.IsCast(action) ? 1.3 : 0.6) && start >= double.Parse(probe[0]) && start <= double.Parse(probe[1]))
            Console.WriteLine($"  probe {sim.T,7:0.00}: " + string.Join("  ", probeDefines.Select(d => $"{d.Name}={d.Value():0.##}")));
        if (sim.StunEnd(sim.T) is not null) { offset += 0.05; continue; }
        var (w, wr) = Evaluate();
        if (w is not (0 or Wait) && !Sim.IsGcd(w) && sim.InRangeNow(w))
        {
            sim.Use(w, wr);
            offset += 0.6;
        }
        else
            offset += 0.05;
    }
    sim.Weave = false;
    sim.T = sim.GcdReadyAt;
}

#region Report

var gcds = sim.Log.Where(e => e.Gcd).ToList();
string N(uint id) => Names.Short(id);

if (verbose)
    foreach (var (e, i) in gcds.Select((e, i) => (e, i)))
    {
        var weaves = sim.Log.Where(w => !w.Gcd && w.T > e.T && w.T < e.T + sim.Gcd - 0.01).Select(w => N(w.Id));
        Console.WriteLine($"{i + 1,3} {e.T,7:0.00}  {N(e.Id),-10} {string.Join(" ", weaves),-24} {e.Rule}");
    }

var pass = true;
void Compare(string label, IEnumerable<string> expected, IEnumerable<string> actual)
{
    var exp = expected.ToList();
    var act = actual.Take(exp.Count).ToList();
    var ok = exp.SequenceEqual(act);
    pass &= ok;
    Console.WriteLine($"{(ok ? "MATCH " : "DIFFER")} {label}");
    if (!ok)
    {
        Console.WriteLine($"   sheet: {string.Join(", ", exp)}");
        Console.WriteLine($"   rules: {string.Join(", ", act)}");
    }
}

if (args.Contains("--score"))
{
    // Priority 1: auto-crit GCDs inside each raid window. Priority 2: Higanbana uptime while attackable.
    uint[] autoCrits = [MidareSetsugekka, KaeshiSetsugekka, TendoSetsugekka, TendoKaeshiSetsugekka, OgiNamikiri, KaeshiNamikiri];
    var windows = new List<(double Start, double End)>();
    for (double t = 0, from = -1; t < minutes * 60; t += 0.1)
    {
        var up = sim.RaidBuffs(t) > 0;
        if (up && from < 0) from = t;
        if (!up && from >= 0) { windows.Add((from, t)); from = -1; }
    }
    bool Down(double t) => downtimes.Any(d => t >= d.Start && t < d.Start + d.Length);
    var line = new List<string>();
    var totalCrits = 0;
    foreach (var (ws, we) in windows)
    {
        var crits = gcds.Where(e => e.T >= ws && e.T < we && autoCrits.Contains(e.Id)).ToList();
        var full = gcds.Where(e => e.T >= ws && e.T < we && sim.RaidBuffs(e.T) >= 3 && autoCrits.Contains(e.Id)).Count();
        var senei = sim.Log.Any(e => e.Id == Senei && e.T >= ws && e.T < we);
        totalCrits += crits.Count;
        line.Add($"{ws / 60:0}:{ws % 60:00} {crits.Count} ({full} under all buffs){(senei ? "" : ", no Senei")}");
    }
    var banaTimes = sim.Log.Where(e => e.Id == Higanbana).Select(e => e.T + Sim.DotDelay).ToList();
    double banaUp = 0, attackable = 0;
    for (var t = 0.0; t < minutes * 60; t += 0.1)
    {
        if (Down(t) || banaTimes.Count == 0 || t < banaTimes[0]) continue;
        attackable += 0.1;
        var last = banaTimes.LastOrDefault(b => b <= t);
        var newTargetSince = downtimes.Where(d => d.NewTarget && d.Start + d.Length <= t).Select(d => d.Start).DefaultIfEmpty(-1).Max();
        if (last > newTargetSince && t - last < 60) banaUp += 0.1;
    }
    Console.WriteLine($"P1 auto-crits per raid window: {string.Join(" | ", line)}  (total {totalCrits})");
    Console.WriteLine($"P2 Higanbana uptime: {100 * banaUp / attackable:0.0}% of attackable time after the first application");
    if (sim.Speeds is not null)
        Console.WriteLine($"Casts cancelled by movement: {sim.CancelledCasts.Count}  ({string.Join(", ", sim.CancelledCasts.GroupBy(c => Names.Short(c.Id)).Select(g => $"{g.Key} x{g.Count()}"))})   idle/cancelled time {sim.IdleTime:0.0}s");

    // Expected damage (potency x crit/DH expectation x buffs), the thing both priorities serve.
    // Stats: 27% crit, 1.62x crit damage, 25% DH. Potencies approximate for 7.x; Tendo 1100, Higanbana 1200 from The Balance.
    const double c0 = 0.27, cd = 1.62, d0 = 0.25;
    var potency = new Dictionary<uint, double>
    {
        [Gyofu] = 240, [Hakaze] = 240, [Jinpu] = 300, [Shifu] = 300, [Yukikaze] = 340, [Gekko] = 420, [Kasha] = 420,
        [Higanbana] = 200, [MidareSetsugekka] = 640, [KaeshiSetsugekka] = 640, [TendoSetsugekka] = 1100,
        [TendoKaeshiSetsugekka] = 1100, [OgiNamikiri] = 1000, [KaeshiNamikiri] = 1000, [Enpi] = 100,
        [Shinten] = 250, [Senei] = 800, [Zanshin] = 940, [Shoha] = 640, [Gyoten] = 100,
    };
    double DmgMult(double t) => sim.RaidBuffs(t) switch { 0 => 1, 1 => 1.05, 2 => 1.0815, var n => 1.1356 * Math.Pow(1.05, n - 3) };
    double Expected(uint id, double pot, double t)
    {
        var devil = sim.DevilmentLeft(t) > 0;
        double crit = devil ? 0.2 : 0, dh = devil ? 0.2 : 0;
        var dhm = 1 + (d0 + dh) * 0.25;
        return autoCrits.Contains(id)
            ? pot * DmgMult(t) * cd * (1 + crit * (cd - 1)) * dhm
            : pot * DmgMult(t) * (1 + (c0 + crit) * (cd - 1)) * dhm;
    }
    var end = minutes * 60;
    double damage = 0, dotDamage = 0;
    foreach (var e in sim.Log.Where(e => e.T >= 0 && potency.ContainsKey(e.Id)))
        damage += Expected(e.Id, potency[e.Id], e.T);
    for (var i = 0; i < banaTimes.Count; i++)
    {
        // Ticks of 50 every 3 s, snapshotted at application, until refreshed, the target leaves, or the fight ends.
        var stop = Math.Min(banaTimes[i] + 60, i + 1 < banaTimes.Count ? banaTimes[i + 1] : end);
        var leaves = downtimes.Where(d => d.NewTarget && d.Start > banaTimes[i]).Select(d => d.Start).DefaultIfEmpty(end).Min();
        stop = Math.Min(Math.Min(stop, leaves), end);
        var ticks = Math.Max(0, Math.Floor((stop - banaTimes[i]) / 3));
        dotDamage += Expected(Higanbana, 50 * ticks, banaTimes[i]);
    }
    Console.WriteLine($"Expected damage: {damage + dotDamage:N0} ({(damage + dotDamage) / (end - downtimes.Sum(d => d.Length)):N1} per attackable second){(sim.Partner ? "  [dance partner]" : "")}");
    if (args.Contains("--breakdown"))
    {
        foreach (var g in sim.Log.Where(e => e.T >= 0 && potency.ContainsKey(e.Id)).GroupBy(e => e.Id).OrderByDescending(g => g.Sum(e => Expected(e.Id, potency[e.Id], e.T))))
            Console.WriteLine($"   {N(g.Key),-10} x{g.Count(),3}  {g.Sum(e => Expected(e.Id, potency[e.Id], e.T)),9:N0}");
        Console.WriteLine($"   {"DoT",-10} x{banaTimes.Count,3}  {dotDamage,9:N0}  (ticks wasted to clipping: {banaTimes.Zip(banaTimes.Skip(1), (a, b) => Math.Max(0, a + 60 - b)).Sum():0.0}s)");
        Console.WriteLine($"   Meikyo at {string.Join(", ", sim.Log.Where(e => e.Id == MeikyoShisui && e.T >= 0).Select(e => $"{e.T:0}({e.Rule.Split(' ')[0]})"))}");
    }
    Console.WriteLine($"   Senei held past ready: {string.Join(", ", sim.Log.Where(e => e.Id == Senei).Select(e => e.T).Zip(sim.Log.Where(e => e.Id == Senei).Select(e => e.T).Skip(1), (a, b) => (b - a - 60).ToString("0.0")))}   Kenki wasted {sim.KenkiWasted}");
    return 0;
}

if (args.FirstOrDefault(a => a.StartsWith("--window=")) is { } winArg)
{
    var parts = winArg["--window=".Length..].Split(':').Select(double.Parse).ToArray();
    var (w0, w1) = (parts[0], parts[1]);
    var inWin = gcds.Where(e => e.T >= w0 && e.T < w1).ToList();
    Console.WriteLine($"{"#",3} {"time",7} {"dist",5}  {"GCD",-10} {"woven",-22} rule");
    foreach (var e in inWin)
    {
        var weaves = sim.Log.Where(w => !w.Gcd && w.T > e.T && w.T < e.T + sim.Gcd - 0.01).Select(w => N(w.Id));
        Console.WriteLine($"{gcds.IndexOf(e) + 1,3} {e.T,7:0.00} {sim.Dist(e.T),5:0.0}  {N(e.Id),-10} {string.Join(" ", weaves),-22} {e.Rule}");
    }
    var fails = sim.FailedPresses.Where(f => f.T >= w0 && f.T < w1).ToList();
    Console.WriteLine($"GCDs {inWin.Count}, Enpi {inWin.Count(e => e.Id == Enpi)}, " +
                      $"out-of-range presses {fails.Count / 10.0:0.0}s ({string.Join(", ", fails.GroupBy(f => N(f.Id)).Select(g => $"{g.Key} {g.Count() / 10.0:0.0}s"))})");
    return 0;
}

if (args.Contains("--kenki"))
{
    // Raid buffs: 20 s every 2 min, landing ~6.5 s into the pull. Potion: 30 s at the pull and at 6:00.
    bool InRaid(double t) => (t - 6.5) % 120 is >= 0 and < 20 && t >= 6.5;
    bool InPot(double t) => t is >= 0 and < 30 or >= 362 and < 392;
    (int Kenki, int Count) Sum(uint id, int cost, Func<double, bool> where) =>
        (sim.Log.Count(e => e.Id == id && where(e.T)) * cost, sim.Log.Count(e => e.Id == id && where(e.T)));

    Console.WriteLine($"{"",-10} {"in raid buffs",16} {"in potion",12} {"outside both",14} {"total",8}");
    foreach (var (name, id, cost) in new[] { ("Shinten", Shinten, 25), ("Gyoten", Gyoten, 10), ("Senei", Senei, 25), ("Zanshin", Zanshin, 50) })
    {
        var raid = Sum(id, cost, InRaid);
        var pot = Sum(id, cost, InPot);
        var outside = Sum(id, cost, t => !InRaid(t) && !InPot(t));
        var all = Sum(id, cost, _ => true);
        Console.WriteLine($"{name,-10} {raid.Kenki,8} ({raid.Count,2}x) {pot.Kenki,6} ({pot.Count,2}x) {outside.Kenki,7} ({outside.Count,3}x) {all.Kenki,6}");
    }
    var tenPer = (string label, Func<double, bool> where) =>
        sim.Log.Where(e => where(e.T)).Sum(e => e.Id == Shinten ? 25 : e.Id == Gyoten ? 10 : 0);
    Console.WriteLine($"Kenki on 10-potency spenders (Shinten + Gyoten): raid {tenPer("", InRaid)}, potion {tenPer("", InPot)}, outside {tenPer("", t => !InRaid(t) && !InPot(t))}, left at end {sim.Kenki}");
    Console.WriteLine($"Gyoten timing: {string.Join(", ", sim.Log.Where(e => e.Id == Gyoten).Select(e => $"{e.T:0}{(InRaid(e.T) ? "r" : "")}{(InPot(e.T) ? "p" : "")}"))}");
    Console.Write("Per raid window (Shinten+Gyoten Kenki):");
    for (var w = 6.5; w + 20 < minutes * 60 - 30; w += 120)
    {
        var start = w;
        Console.Write($"  {start / 60:0}m:{tenPer("", t => t >= start && t < start + 20)}");
    }
    Console.WriteLine();
    return 0;
}

foreach (var d in downtimes)
{
    Console.WriteLine($"=== Downtime at {d.Start:0}s for {d.Length:0}s ({(d.NewTarget ? "comes back without your debuffs" : "same target, debuffs kept")})");
    Console.WriteLine($"  going in:  {d.Enter}");
    Console.WriteLine($"  coming out:{d.Exit}");
    Console.WriteLine($"  expired during downtime: {(d.Lost.Length > 0 ? d.Lost : "nothing")}");
    Console.WriteLine("  first GCDs after it:");
    foreach (var (e, i) in gcds.Where(e => e.T >= d.ResumeAt).Take(16).Select((e, i) => (e, i)))
    {
        var weaves = sim.Log.Where(w => !w.Gcd && w.T > e.T && w.T < e.T + sim.Gcd - 0.01).Select(w => N(w.Id));
        Console.WriteLine($"   {i + 1,3} {e.T,7:0.00}  {N(e.Id),-10} {string.Join(" ", weaves),-22} {e.Rule}");
    }
}
if (downtimes.Count > 0)
{
    var seneiTimes = sim.Log.Where(e => e.Id == Senei).Select(e => e.T).ToList();
    Console.WriteLine($"Senei held past ready: {string.Join(", ", seneiTimes.Zip(seneiTimes.Skip(1), (a, b) => (b - a - 60).ToString("0.0")))}");
    var banaTimes = sim.Log.Where(e => e.Id == Higanbana).Select(e => e.T).ToList();
    Console.WriteLine($"Higanbana at: {string.Join(", ", banaTimes.Select(t => t.ToString("0")))}");
    Console.WriteLine($"Meikyo at:    {string.Join(", ", sim.Log.Where(e => e.Id == MeikyoShisui).Select(e => $"{e.T:0}"))}");
    Console.WriteLine($"Kenki wasted: {sim.KenkiWasted}   Meditation wasted: {sim.MeditationWasted}");
    return 0;
}

// Opener: the first 15 GCDs and where the oGCDs went.
Compare("opener GCDs", ["Gekko", "Kasha", "Yukikaze", "TendoS", "TendoK", "Gekko", "Higanbana", "Ogi", "KaeshiN", "Kasha", "Gekko", "Gyofu", "Yukikaze", "TendoS", "TendoK"],
    gcds.Select(e => N(e.Id)));
string WovenAfter(int gcdIndex) =>
    string.Join("+", sim.Log.Where(w => !w.Gcd && w.T > gcds[gcdIndex].T && w.T < gcds[gcdIndex].T + sim.Gcd - 0.01).Select(w => N(w.Id)));
var prepull = sim.Log.Where(e => e.T < 0 && !e.Gcd).Select(e => $"{N(e.Id)}@{e.T:0}");
Console.WriteLine($"       pre-pull: {string.Join(", ", prepull)}   (sheet: Meikyo@-14, TrueNorth@-5)");
Console.WriteLine("       opener oGCDs after GCD n (sheet in brackets):");
string[] sheetWeaves = ["Pot", "Iki", "", "Senei", "Meikyo", "Zanshin", "", "Shoha", "", "Shinten", "Gyoten", "", "Shinten", "", ""];
for (var i = 0; i < 15; i++)
    Console.WriteLine($"         {i + 1,2} {N(gcds[i].Id),-10} {WovenAfter(i),-16} [{sheetWeaves[i]}]");

// Cooldown phase after the opener.
Compare("cooldown phase (GCDs 16-32)",
    ["Gyofu", "Yukikaze", "Gyofu", "Jinpu", "Gekko", "Gyofu", "Shifu", "Kasha", "Midare", "Gyofu", "Yukikaze", "Gyofu", "Jinpu", "Gekko", "Gyofu", "Shifu", "Kasha"],
    gcds.Skip(15).Select(e => N(e.Id)));

// Bursts: the GCDs after each Meikyo in combat.
var meikyos = sim.Log.Where(e => e.Id == MeikyoShisui && e.T > 15).ToList();
string[][] sheet =
[
    ["KaeshiS", "TendoS", "TendoK", "Gekko", "Higanbana", "Kasha", "Gekko"],
    ["KaeshiS", "TendoS", "TendoK", "Gekko", "Higanbana", "Ogi", "KaeshiN", "Kasha", "Gekko"],
    ["Kasha", "KaeshiS", "TendoS", "TendoK", "Gekko", "Higanbana", "Kasha"],
    ["Kasha", "KaeshiS", "TendoS", "TendoK", "Gekko", "Higanbana", "Ogi", "KaeshiN", "Kasha"],
    ["Kasha", "Gekko", "KaeshiS", "TendoS", "TendoK", "Gekko", "Higanbana"],
    ["Kasha", "Gekko", "KaeshiS", "TendoS", "TendoK", "Gekko", "Higanbana", "Ogi", "KaeshiN"],
    ["Gekko", "Kasha", "TendoS", "TendoK", "Gekko"],
    ["KaeshiS", "TendoS", "TendoK", "Gekko", "Higanbana", "Kasha", "Gekko"],
];
string[] labels = ["1 min", "2 min", "3 min", "4 min", "5 min", "6 min", "6 min reset (2nd Meikyo)"];
// "The bursts repeat from this point": 7 min is 1 min again, through the next reset.
var bursts = Math.Max(sheet.Length, meikyos.Count(m => m.T < minutes * 60 - 25));
for (var i = 0; i < bursts; i++)
{
    var cycle = i / 7;
    var label = i % 7 == 6 ? $"{6 + cycle * 6} min reset (2nd Meikyo)" : $"{i % 7 + 1 + cycle * 6} min" + (cycle > 0 ? $" (= {labels[i % 7]})" : "");
    if (i >= meikyos.Count) { Console.WriteLine($"MISSING {label}"); pass = false; continue; }
    var m = meikyos[i];
    var expected = sheet[i % 7];
    var before = gcds.Where(e => e.T < m.T).TakeLast(2).Select(e => N(e.Id));
    Compare($"{label,-34} Meikyo at {m.T,6:0.0}s after [{string.Join(", ", before)}] with {m.SenBefore} Sen  ({m.Rule})",
        expected, gcds.Where(e => e.T > m.T).Select(e => N(e.Id)));
}

// Health metrics.
var seneis = sim.Log.Where(e => e.Id == Senei).Select(e => e.T).ToList();
var drift = seneis.Zip(seneis.Skip(1), (a, b) => b - a - 60).ToList();
var banas = sim.Log.Where(e => e.Id == Higanbana).Select(e => e.T).ToList();
var banaGaps = banas.Zip(banas.Skip(1), (a, b) => Math.Max(0, b - a - 60)).ToList();
Console.WriteLine();
Console.WriteLine($"Senei at:   {string.Join(", ", seneis.Select(t => t.ToString("0.0")))}");
Console.WriteLine($"  held past ready: {string.Join(", ", drift.Select(d => d.ToString("0.0")))}");
Console.WriteLine($"Ikishoten:  {string.Join(", ", sim.Log.Where(e => e.Id == Ikishoten).Select(e => e.T.ToString("0.0")))}");
Console.WriteLine($"Meikyo:     {string.Join(", ", sim.Log.Where(e => e.Id == MeikyoShisui).Select(e => $"{e.T:0.0}({e.Rule.Split(' ')[0]})"))}");
Console.WriteLine($"Higanbana:  {string.Join(", ", banas.Select(t => t.ToString("0.0")))}  (DoT gaps: {string.Join(", ", banaGaps.Select(g => g.ToString("0.0")))})");
Console.WriteLine($"Kenki wasted: {sim.KenkiWasted}   Meditation wasted: {sim.MeditationWasted}   Kenki now: {sim.Kenki}");
Console.WriteLine($"Zanshin: {sim.Log.Count(e => e.Id == Zanshin)}  Shoha: {sim.Log.Count(e => e.Id == Shoha)}  Shinten: {sim.Log.Count(e => e.Id == Shinten)}  Gyoten: {sim.Log.Count(e => e.Id == Gyoten)}  Ogi: {sim.Log.Count(e => e.Id == OgiNamikiri)}");
Console.WriteLine(pass ? "\nAll compared sections match the sheet." : "\nSome sections differ from the sheet (see above).");
return pass ? 0 : 2;

#endregion

static class Id
{
    public const uint
        Hakaze = 7477, Yukikaze = 7480, Gekko = 7481, Enpi = 7486, Jinpu = 7478, Kasha = 7482, Shifu = 7479,
        Higanbana = 7489, TenkaGoken = 7488, MidareSetsugekka = 7487, Shinten = 7490, Senei = 16481,
        MeikyoShisui = 7499, Iaijutsu = 7867, TsubameGaeshi = 16483, Shoha = 16487, Ikishoten = 16482,
        OgiNamikiri = 25781, KaeshiNamikiri = 25782, Gyoten = 7492, KaeshiSetsugekka = 16486,
        TendoGoken = 36965, TendoKaeshiSetsugekka = 36968, Zanshin = 36964, TendoSetsugekka = 36966,
        Gyofu = 36963, TrueNorth = 7546, Wait = 1_000_004;

    // Statuses
    public const uint
        BuffMeikyo = 1233, BuffOgiReady = 2959, BuffFuka = 1299, BuffFugetsu = 1298, BuffTsubameReady = 4216,
        BuffTendoKaeshiReady = 4218, BuffZanshinReady = 3855, BuffTendo = 3856, BuffTrueNorth = 1250,
        DebuffHiganbana = 1228;
}

sealed class Forced(double t)
{
    public readonly double T = t;
    public bool Done;
}

sealed class Downtime(double start, double length, bool newTarget)
{
    public double Start = start, Length = length, ResumeAt;
    public bool NewTarget = newTarget, Done;
    public string Enter = "", Exit = "", Lost = "";
}

static class Names
{
    public static string Status(uint id) => id switch
    {
        BuffMeikyo => "Meikyo", BuffOgiReady => "OgiReady", BuffFuka => "Fuka", BuffFugetsu => "Fugetsu",
        BuffTsubameReady => "held Kaeshi", BuffTendoKaeshiReady => "Tendo Kaeshi", BuffZanshinReady => "ZanshinReady",
        BuffTendo => "Tendo", BuffTrueNorth => "TrueNorth", _ => id.ToString(),
    };

    public static string Short(uint id) => id switch
    {
        Gyofu or Hakaze => "Gyofu", Yukikaze => "Yukikaze", Gekko => "Gekko", Kasha => "Kasha", Jinpu => "Jinpu",
        Shifu => "Shifu", Higanbana => "Higanbana", MidareSetsugekka => "Midare", TendoSetsugekka => "TendoS",
        KaeshiSetsugekka => "KaeshiS", TendoKaeshiSetsugekka => "TendoK", OgiNamikiri => "Ogi",
        KaeshiNamikiri => "KaeshiN", MeikyoShisui => "Meikyo", Senei => "Senei", Ikishoten => "Iki",
        Zanshin => "Zanshin", Shoha => "Shoha", Shinten => "Shinten", Gyoten => "Gyoten", TrueNorth => "TrueNorth",
        Enpi => "Enpi", TenkaGoken => "Tenka", _ => id.ToString(),
    };
}

sealed class SimSymbols(Sim sim) : IRuleSymbols
{
    private static readonly Dictionary<string, uint> Actions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hakaze"] = Hakaze, ["Yukikaze"] = Yukikaze, ["Gekko"] = Gekko, ["Enpi"] = Enpi, ["Jinpu"] = Jinpu,
        ["Kasha"] = Kasha, ["Shifu"] = Shifu, ["Higanbana"] = Higanbana, ["TenkaGoken"] = TenkaGoken,
        ["MidareSetsugekka"] = MidareSetsugekka, ["Shinten"] = Shinten, ["Senei"] = Senei,
        ["MeikyoShisui"] = MeikyoShisui, ["Iaijutsu"] = Iaijutsu, ["TsubameGaeshi"] = TsubameGaeshi,
        ["Shoha"] = Shoha, ["Ikishoten"] = Ikishoten, ["OgiNamikiri"] = OgiNamikiri,
        ["KaeshiNamikiri"] = KaeshiNamikiri, ["Gyoten"] = Gyoten, ["KaeshiSetsugekka"] = KaeshiSetsugekka,
        ["TendoGoken"] = TendoGoken, ["TendoKaeshiSetsugekka"] = TendoKaeshiSetsugekka, ["Zanshin"] = Zanshin,
        ["TendoSetsugekka"] = TendoSetsugekka, ["Gyofu"] = Gyofu, ["TrueNorth"] = TrueNorth, ["Wait"] = Wait,
    };

    private static readonly Dictionary<string, uint> Statuses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MeikyoShisui"] = BuffMeikyo, ["OgiNamikiriReady"] = BuffOgiReady, ["Fuka"] = BuffFuka,
        ["Fugetsu"] = BuffFugetsu, ["TsubameReady"] = BuffTsubameReady,
        ["TendoKaeshiSetsugekkaReady"] = BuffTendoKaeshiReady, ["ZanshinReady"] = BuffZanshinReady,
        ["Tendo"] = BuffTendo, ["TrueNorth"] = BuffTrueNorth, ["Higanbana"] = DebuffHiganbana,
        ["DNC.Buffs.Partner"] = 1824, ["DNC.Buffs.Devilment"] = 1825,
    };

    public bool TryResolveAction(string name, out uint id) => Actions.TryGetValue(name, out id);
    public bool TryResolveStatus(string name, out uint id) => Statuses.TryGetValue(name, out id);
    public bool TryResolveGauge(string field, out Func<double>? getter) => sim.TryGauge(field, out getter);
}

sealed class Cooldown(double recast, int maxCharges)
{
    private int _charges = maxCharges;
    private double _nextAt;

    public int Charges(double t) { Update(t); return _charges; }
    public bool Ready(double t) => Charges(t) > 0;
    public void Use(double t)
    {
        Update(t);
        if (_charges == maxCharges) _nextAt = t + recast;
        _charges--;
    }
    // Like Wrath: time until fully recharged, and until the next charge.
    public double Remaining(double t) { Update(t); return _charges == maxCharges ? 0 : _nextAt - t + (maxCharges - _charges - 1) * recast; }
    public double ChargeRemaining(double t) { Update(t); return _charges == maxCharges ? 0 : _nextAt - t; }
    private void Update(double t)
    {
        while (_charges < maxCharges && t >= _nextAt)
        {
            _charges++;
            if (_charges < maxCharges) _nextAt += recast;
        }
    }
}

sealed class Sim(double gcd) : IRuleRuntime
{
    public readonly record struct Entry(double T, uint Id, bool Gcd, string Rule, int SenBefore);

    public double T;
    public readonly double Gcd = gcd;
    public double GcdReadyAt = double.NegativeInfinity;
    public double? CombatStart;

    // Added when moving into the RuleSim repo: the engine's IRuleRuntime gained Retarget after this sim was written.
    // There is only the one striking-dummy target here, so every target spec resolves to it.
    public uint Retarget(uint action, uint trigger, RuleTargetSpec[] targets) => action;
    public bool Weave;
    public int Kenki, Meditation, KenkiWasted, MeditationWasted, MeikyoStacks;
    public bool Getsu, Setsu, Ka, KaeshiNamikiriReady;
    public uint ComboAction;
    public double ComboExpire = double.NegativeInfinity, BanaExpire = double.NegativeInfinity;
    public readonly List<Entry> Log = [];

    private readonly Dictionary<uint, double> _buffs = [];
    private readonly Dictionary<uint, double> _lastUsed = [];
    private readonly Dictionary<uint, Cooldown> _cds = new()
    {
        // Gyoten: 5 s (the log has two 5.75 s apart).
        [MeikyoShisui] = new(55, 2), [Senei] = new(60, 1), [Ikishoten] = new(120, 1), [Shoha] = new(15, 1),
        [Gyoten] = new(5, 1), [TrueNorth] = new(45, 2), [Shinten] = new(1, 1), [Zanshin] = new(1, 1),
    };

    public int Sen => (Getsu ? 1 : 0) + (Setsu ? 1 : 0) + (Ka ? 1 : 0);

    #region Distance (from a real pull's positions)

    public bool Partner;
    public double[] DevilmentStarts = [];
    public double DevilmentLeft(double t) => !Partner ? 0 : DevilmentStarts.Where(s => t >= s && t < s + 20).Select(s => s + 20 - t).DefaultIfEmpty(0).Max();

    public double[]? Distances;
    public double[]? Speeds;
    public double CastLock = 0.8, MoveSpeed = 2.0;
    public readonly List<(double T, uint Id)> CancelledCasts = [];
    public readonly List<(double Start, double Length)> Stuns = [];
    public double StunTime;
    public double? StunEnd(double t) { foreach (var (s0, l) in Stuns) if (t >= s0 && t < s0 + l) return s0 + l; return null; }
    public double? FirstStunWithin(double from, double window) { foreach (var (s0, l) in Stuns) if (s0 > from && s0 <= from + window) return s0; return null; }

    /// <summary> The first moment you move within (from, from + window], if any. </summary>
    public double? FirstMoveWithin(double from, double window)
    {
        if (Speeds is null) return null;
        for (var t = from + 0.1; t <= from + window + 1e-9; t += 0.1)
            if (MovingAt(t)) return t;
        return null;
    }
    public bool EnpiBreaksCombo;
    public readonly List<(double T, uint Id)> FailedPresses = [];
    public double IdleTime;

    /// <summary> Yalms to the target's hitbox edge; -1 = no target. Always melee without --dist. </summary>
    #region Raid buffs

    public int[] RaidCounts = [];
    public const double DotDelay = 1.4;   // Higanbana's debuff lands 1.4 s after the cast starts (measured in game)
    public double ClockOffset;   // --clock-offset=S: the engine's combat start is S seconds late (the plugin-load bug)
    private double? _raidStart;      // start of the current or last raid window seen
    private bool _inRaid;

    public int RaidBuffs(double t) => t < 0 || RaidCounts.Length == 0 ? 0 : RaidCounts[Math.Min((int)(t * 10), RaidCounts.Length - 1)];

    /// <summary> Seconds until no raid buff is up (buff durations are known in game). </summary>
    public double RaidLeft()
    {
        if (RaidBuffs(T) == 0) return 0;
        var t = T;
        while (RaidBuffs(t) > 0 && t < T + 40) t += 0.1;
        return t - T;
    }

    /// <summary>
    ///     Seconds until the next raid window, predicted the way the engine does: the last window's
    ///     start + 120 s, or 2 s past each 2 minutes of combat before any window has been seen.
    /// </summary>
    public double RaidIn()
    {
        if (CombatStart is not { } start) return 2;
        var up = RaidBuffs(T) > 0;
        if (up && !_inRaid) _raidStart = T;
        _inRaid = up;
        if (up) return 0;
        var next = _raidStart is { } s ? s + 120 : start + ClockOffset + 2 + 120 * Math.Ceiling((T - start - ClockOffset - 2) / 120);
        return Math.Max(0, next - T);
    }

    #endregion

    public double Dist(double t) =>
        Distances is null || t < 0 ? 0 : Distances[Math.Clamp((int)(t * 10), 0, Distances.Length - 1)];

    public bool Moving => MovingAt(T);
    public bool MovingAt(double t) => Speeds is not null
        ? t >= 0 && Speeds[Math.Clamp((int)Math.Round(t * 10), 0, Speeds.Length - 1)] > MoveSpeed
        : Distances is not null && Math.Abs(Dist(t) - Dist(t - 0.5)) > 0.3;

    // Action ranges in yalms (0 = self). Melee weaponskills and Kenki spenders are 3 y.
    public static double Range(uint id) => id switch
    {
        MeikyoShisui or Ikishoten or TrueNorth or Wait => 0,
        Enpi or Gyoten => 20,
        OgiNamikiri or KaeshiNamikiri or Zanshin or Shoha => 8,
        Higanbana or MidareSetsugekka or TendoSetsugekka or TenkaGoken or TendoGoken or KaeshiSetsugekka or TendoKaeshiSetsugekka => 6,
        _ => 3,
    };

    public bool InRange(uint id) => Range(id) == 0 || Dist(T) >= 0 && Dist(T) <= Range(id);
    public bool InRangeNow(uint id) => InRange(id);

    #endregion

    public IEnumerable<(uint Id, double Left)> ActiveBuffs() =>
        _buffs.Where(b => b.Value > T).Select(b => (b.Key, b.Value - T)).ToList();

    public string Snapshot() =>
        $" Sen {(Setsu ? "Setsu " : "")}{(Getsu ? "Getsu " : "")}{(Ka ? "Ka " : "")}({Sen}), Kenki {Kenki}, " +
        $"Higanbana {Math.Max(0, BanaExpire - T):0}s, Meikyo charges {_cds[MeikyoShisui].Charges(T)}, " +
        $"Senei cd {_cds[Senei].Remaining(T):0}s, combo {(ComboExpire > T ? "live" : "dropped")}, " +
        $"buffs: {string.Join(" ", ActiveBuffs().Select(b => $"{Names.Status(b.Id)}={b.Left:0}"))}";
    private bool Has(uint s) => _buffs.TryGetValue(s, out var e) && e > T;
    private double Left(uint s) => Has(s) ? _buffs[s] - T : 0;
    private void Give(uint s, double d) => _buffs[s] = T + d;
    private void Drop(uint s) => _buffs.Remove(s);
    private bool InCombo(uint a) => ComboExpire > T && ComboAction == a;

    public static bool IsGcd(uint id) => id is not (MeikyoShisui or Senei or Ikishoten or Zanshin or Shoha or Shinten or Gyoten or TrueNorth or Wait);
    public static bool IsCast(uint id) => id is Higanbana or MidareSetsugekka or TendoSetsugekka or TenkaGoken or TendoGoken or OgiNamikiri;

    #region IRuleRuntime

    public bool CanWeave() => Weave;

    public uint OriginalHook(uint id) => id switch
    {
        Hakaze => Gyofu,
        Iaijutsu => Sen switch
        {
            1 => Higanbana,
            2 => Has(BuffTendo) ? TendoGoken : TenkaGoken,
            3 => Has(BuffTendo) ? TendoSetsugekka : MidareSetsugekka,
            _ => Iaijutsu,
        },
        TsubameGaeshi => Has(BuffTendoKaeshiReady) ? TendoKaeshiSetsugekka : Has(BuffTsubameReady) ? KaeshiSetsugekka : TsubameGaeshi,
        OgiNamikiri => KaeshiNamikiriReady ? KaeshiNamikiri : OgiNamikiri,
        _ => id,
    };

    public bool ActionReady(uint id) => id switch
    {
        Wait => true,
        MeikyoShisui or Ikishoten or TrueNorth => _cds[id].Ready(T),
        Senei => _cds[id].Ready(T) && Kenki >= 25,
        Shinten => Kenki >= 25,
        Gyoten => _cds[id].Ready(T) && Kenki >= 10,
        Zanshin => Has(BuffZanshinReady) && Kenki >= 50,
        Shoha => _cds[id].Ready(T) && Meditation == 3,
        Higanbana => Sen == 1,
        TenkaGoken => Sen == 2,
        TendoGoken => Sen == 2 && Has(BuffTendo),
        MidareSetsugekka => Sen == 3,
        TendoSetsugekka => Sen == 3 && Has(BuffTendo),
        KaeshiSetsugekka => Has(BuffTsubameReady),
        TendoKaeshiSetsugekka => Has(BuffTendoKaeshiReady),
        OgiNamikiri => Has(BuffOgiReady),
        KaeshiNamikiri => KaeshiNamikiriReady,
        Iaijutsu => Sen > 0,
        TsubameGaeshi => false,
        _ => true,
    };

    #endregion

    public void Use(uint id, string rule)
    {
        Log.Add(new Entry(T, id, IsGcd(id), rule, Sen));
        _lastUsed[id] = T;
        if (IsGcd(id) && CombatStart is null) CombatStart = T;
        if (_cds.TryGetValue(id, out var cd)) cd.Use(T);

        var meikyo = Has(BuffMeikyo);
        if (meikyo && id is Gyofu or Hakaze or Jinpu or Shifu or Yukikaze or Gekko or Kasha)
            if (--MeikyoStacks <= 0) Drop(BuffMeikyo);

        switch (id)
        {
            case Gyofu or Hakaze: AddKenki(5); Combo(id); break;
            case Jinpu: if (InCombo(Gyofu)) { AddKenki(5); Give(BuffFugetsu, 40); Combo(id); } else ComboAction = 0; break;
            case Shifu: if (InCombo(Gyofu)) { AddKenki(5); Give(BuffFuka, 40); Combo(id); } else ComboAction = 0; break;
            case Yukikaze: if (meikyo || InCombo(Gyofu)) { AddKenki(15); Setsu = true; } Combo(id); break;
            case Gekko: if (meikyo || InCombo(Jinpu)) { AddKenki(10); Getsu = true; } if (meikyo) Give(BuffFugetsu, 40); Combo(id); break;   // Fugetsu only under Meikyo (as in game)
            case Kasha: if (meikyo || InCombo(Shifu)) { AddKenki(10); Ka = true; } if (meikyo) Give(BuffFuka, 40); Combo(id); break;   // Fuka only under Meikyo (as in game)
            case Higanbana: BanaExpire = T + DotDelay + 60; ClearSen(); AddMeditation(); break;
            case TenkaGoken or TendoGoken: ClearSen(); AddMeditation(); if (id == TendoGoken) Drop(BuffTendo); break;
            case MidareSetsugekka: ClearSen(); AddMeditation(); Give(BuffTsubameReady, 30); break;
            case TendoSetsugekka: ClearSen(); AddMeditation(); Drop(BuffTendo); Give(BuffTendoKaeshiReady, 30); break;
            case KaeshiSetsugekka: Drop(BuffTsubameReady); break;
            case TendoKaeshiSetsugekka: Drop(BuffTendoKaeshiReady); break;
            case OgiNamikiri: Drop(BuffOgiReady); AddMeditation(); KaeshiNamikiriReady = true; break;
            case KaeshiNamikiri: KaeshiNamikiriReady = false; break;
            case MeikyoShisui: Give(BuffMeikyo, 20); MeikyoStacks = 3; Give(BuffTendo, 30); break;
            case Senei: Kenki -= 25; break;
            case Ikishoten: AddKenki(50); Give(BuffOgiReady, 30); Give(BuffZanshinReady, 30); break;
            case Zanshin: Kenki -= 50; Drop(BuffZanshinReady); break;
            case Shoha: Meditation = 0; break;
            case Shinten: Kenki -= 25; break;
            case Gyoten: Kenki -= 10; break;
            case Enpi: AddKenki(10); if (EnpiBreaksCombo) ComboAction = 0; break;
            case TrueNorth: Give(BuffTrueNorth, 10); break;
        }
    }

    private void Combo(uint id) { ComboAction = id; ComboExpire = T + 30; }
    private void ClearSen() => Getsu = Setsu = Ka = false;
    private void AddKenki(int n)
    {
        var total = Kenki + n;
        if (total > 100) { KenkiWasted += total - 100; total = 100; }
        Kenki = total;
    }
    private void AddMeditation() { if (Meditation >= 3) MeditationWasted++; else Meditation++; }

    /// <summary> The same function names and signatures as RuleBuiltins, backed by the sim. </summary>
    public IReadOnlyDictionary<string, RuleFunction> Functions()
    {
        var t = new Dictionary<string, RuleFunction>(StringComparer.OrdinalIgnoreCase);
        static double B(bool b) => b ? 1 : 0;
        void Add(string n, ParamKind[] p, int req, Func<RuleArg[], Func<double>> bind) => t[n] = new RuleFunction(n, p, req, bind, "");
        void Val(string n, Func<double> f) => Add(n, [], 0, _ => f);
        var A = ParamKind.Action;
        var S = ParamKind.Status;
        var Num = ParamKind.Number;

        Add("ready", [A], 1, a => { var id = a[0].Id; return () => B(ActionReady(id)); });
        Add("cd", [A], 1, a => { var id = a[0].Id; return () => _cds.TryGetValue(id, out var c) ? c.Remaining(T) : Math.Max(0, GcdReadyAt - T); });
        Add("charges", [A], 1, a => { var id = a[0].Id; return () => _cds.TryGetValue(id, out var c) ? c.Charges(T) : 1; });
        Add("chargeCd", [A], 1, a => { var id = a[0].Id; return () => _cds.TryGetValue(id, out var c) ? c.ChargeRemaining(T) : 0; });
        Add("learned", [A], 1, _ => () => 1);
        Add("justUsed", [A, Num], 1, a =>
        {
            var id = a[0].Id;
            var s = a[1].Value ?? (() => 3);
            return () => B(_lastUsed.TryGetValue(id, out var u) && T - u <= s());
        });
        Add("inRange", [A], 1, a => { var id = a[0].Id; return () => B(InRange(id)); });
        Add("combo", [A], 1, a => { var id = a[0].Id; return () => B(InCombo(id)); });
        Add("enemies", [A], 1, _ => () => 1);
        Add("hook", [A], 1, a => { var id = a[0].Id; return () => OriginalHook(id); });
        Add("id", [A], 1, a => { double id = a[0].Id; return () => id; });
        Add("buff", [S], 1, a => { var id = a[0].Id; return () => B(Has(id)); });
        Add("buffTime", [S], 1, a => { var id = a[0].Id; return () => Left(id); });
        Add("buffStacks", [S], 1, a => { var id = a[0].Id; return () => id == BuffMeikyo && Has(id) ? MeikyoStacks : 0; });
        Add("debuff", [S], 1, a => { var id = a[0].Id; return () => B(id == DebuffHiganbana && BanaExpire > T); });
        Add("debuffTime", [S], 1, a => { var id = a[0].Id; return () => id == DebuffHiganbana ? Math.Max(0, BanaExpire - T) : 0; });
        Add("buffAny", [S], 1, a => { var id = a[0].Id; return () => id == 1824 ? B(Partner) : id == 1825 ? B(DevilmentLeft(T) > 0) : B(Has(id)); });
        Add("buffAnyTime", [S], 1, a => { var id = a[0].Id; return () => id == 1825 ? DevilmentLeft(T) : Left(id); });
        Add("debuffAny", [S], 1, a => { var id = a[0].Id; return () => B(id == DebuffHiganbana && BanaExpire > T); });
        Add("gauge", [ParamKind.Gauge], 1, a => a[0].Value!);
        Add("if", [Num, Num, Num], 3, a => { Func<double> c = a[0].Value!, x = a[1].Value!, y = a[2].Value!; return () => RuleCompiler.Truthy(c()) ? x() : y(); });
        Add("min", [Num, Num], 2, a => { Func<double> x = a[0].Value!, y = a[1].Value!; return () => Math.Min(x(), y()); });
        Add("max", [Num, Num], 2, a => { Func<double> x = a[0].Value!, y = a[1].Value!; return () => Math.Max(x(), y()); });

        Val("gcd", () => Gcd);
        Val("remainingGcd", () => Math.Max(0, GcdReadyAt - T));
        Val("canWeave", () => B(Weave));
        Val("inCombat", () => B(CombatStart is not null));
        Val("combatTime", () => CombatStart is { } s ? T - s : 0);
        Val("hasTarget", () => B(Dist(T) >= 0));
        Val("targetHp", () => 100);
        Val("playerHp", () => 100);
        Val("level", () => 100);
        Val("moving", () => B(Moving));
        Val("stoodStill", () => { if (Moving) return 0; var t = T; while (t > T - 99 && !MovingAt(t)) t -= 0.1; return T - t; });
        Val("sinceGcd", () => Log.LastOrDefault(e => e.Gcd) is { Id: not 0 } last ? T - last.T : 99);
        Val("movingTime", () => { if (!Moving) return 0; var t = T; while (t > T - 99 && MovingAt(t)) t -= 0.1; return T - t; });
        Val("inMelee", () => B(Dist(T) >= 0 && Dist(T) <= 3));
        Val("distance", () => Math.Max(0, Dist(T)));
        Val("rear", () => 1);
        Val("flank", () => 0);
        Val("front", () => 0);
        Val("needsPositionals", () => 0);
        Val("boss", () => 1);
        Val("bossEncounter", () => 1);
        Val("comboTimer", () => Math.Max(0, ComboExpire - T));
        Val("comboAction", () => ComboAction);
        Val("countdown", () => CombatStart is null && T < 0 ? -T : 0);
        Val("raidBuffs", () => RaidBuffs(T));
        Val("raidLeft", RaidLeft);
        Val("raidIn", RaidIn);

        return t;
    }

    public bool TryGauge(string field, out Func<double>? getter)
    {
        static double B(bool b) => b ? 1 : 0;
        getter = field.ToLowerInvariant() switch
        {
            "kenki" => () => Kenki,
            "meditationstacks" => () => Meditation,
            "hasgetsu" => () => B(Getsu),
            "hassetsu" => () => B(Setsu),
            "haska" => () => B(Ka),
            "kaeshi" => () => KaeshiNamikiriReady ? 4 : 0,
            "sen" => () => (Setsu ? 1 : 0) | (Getsu ? 2 : 0) | (Ka ? 4 : 0),
            _ => null,
        };
        return getter is not null;
    }
}
