using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Model;

/// <summary>
///     The user's Dancing Mad pulls on one job, from the log index alone (no replay), 2026-11-19 (user: "Retrieve my RPR pulls that have
///     made it through P2"):
///
///   list all|&lt;log&gt; &lt;job&gt; [--past=P2] [--me=HEX,HEX] [--from=YYYYMMDD] [--file=substr] [--all-players]
///
/// --past=P2 keeps the pulls whose furthest phase is after P2 (a phase P3 or later began). Per pull: log and pull number, start,
/// length, outcome, furthest phase and its start, the boss and its HP at the end, deaths (before the wipe), the party's jobs.
/// --all-players lists every player on the job, not only the user.
/// </summary>
internal static class ListCommands
{
    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
    private static readonly string LogsDir = Environment.GetEnvironmentVariable("RR_LOGS") is { Length: > 0 } rrLogs ? rrLogs : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");
    private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaidReplay", "index");

    private static readonly Dictionary<string, byte> Jobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = 19, ["MNK"] = 20, ["WAR"] = 21, ["DRG"] = 22, ["BRD"] = 23, ["WHM"] = 24, ["BLM"] = 25, ["SMN"] = 27, ["SCH"] = 28, ["NIN"] = 30,
        ["MCH"] = 31, ["DRK"] = 32, ["AST"] = 33, ["SAM"] = 34, ["RDM"] = 35, ["GNB"] = 37, ["DNC"] = 38, ["RPR"] = 39, ["SGE"] = 40, ["VPR"] = 41, ["PCT"] = 42,
    };

    public static bool Handles(string[] args) => args.Length > 2 && args[0] == "list";

    /// <summary> The phase number of a phase mark ("P3 Chaos & Exdeath" -> 3); 0 when it has none. </summary>
    private static int PhaseNo(string? name) =>
        name is { Length: > 1 } n && n[0] == 'P' && int.TryParse(new string(n.Skip(1).TakeWhile(char.IsDigit).ToArray()), out var k) ? k : 0;

    public static int Main(string[] args, EncounterRegistry registry)
    {
        A = args;
        if (!Jobs.TryGetValue(args[2], out var job)) { Console.Error.WriteLine($"unknown job {args[2]}"); return 2; }
        var me = Opt("me", "10041112,1001A4A8").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToUInt32(x, 16)).ToHashSet();
        var past = Opt("past", "");
        var pastNo = past == "" ? 0 : PhaseNo(past);
        var allPlayers = A.Contains("--all-players");
        var from = Opt("from", "20260101");
        var only = Opt("file", "");
        var jobNames = Jobs.ToDictionary(kv => kv.Value, kv => kv.Key);
        var logs = args[1] == "all"
            ? Directory.GetFiles(LogsDir, "Network_*.log").Where(f => string.Compare(Path.GetFileNameWithoutExtension(f)[^8..], from, StringComparison.Ordinal) >= 0 && (only == "" || f.Contains(only))).Order().ToList()
            : [File.Exists(args[1]) ? args[1] : Path.Combine(LogsDir, args[1])];

        var rows = new List<(PullSummary S, PartyMember P)>();
        int onJob = 0;
        foreach (var path in logs)
        {
            FileIndex idx;
            try
            {
                var prev = IndexStore.Load(CacheDir, path);
                idx = IndexStore.IndexFile(path, prev, new EncounterObserverFactory(registry), registry.HashFor);
                if (!ReferenceEquals(idx, prev)) IndexStore.Save(CacheDir, idx);
            }
            catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); continue; }
            foreach (var s in idx.Pulls.Where(p => p.ZoneId == 1363 && p.HasDirector))
                foreach (var p in s.Party.Where(x => x.Job == job && (allPlayers || me.Contains(x.Id))))
                {
                    onJob++;
                    if (pastNo == 0 || s.Phases.Any(ph => PhaseNo(ph.Name) > pastNo)) rows.Add((s, p));
                }
        }
        Console.WriteLine($"{rows.Count} of {onJob} Dancing Mad pulls on {args[2].ToUpper()}{(allPlayers ? " (every player)" : " (you)")}" +
                          (pastNo > 0 ? $" got past {past}" : ""));
        Console.WriteLine($"{"log",-28} {"pull",4} {"start",-16} {"length",7} {"outcome",-8} {"furthest phase",-24} {"at",7} {"boss",-12} {"hp",6} {"deaths",6}  party");
        foreach (var (s, p) in rows.OrderBy(r => r.S.StartTicks))
        {
            var furthest = s.Phases.LastOrDefault();
            var at = furthest is null ? "" : TimeSpan.FromTicks(furthest.Ticks - s.StartTicks).ToString(@"m\:ss");
            var party = string.Join(" ", s.Party.Select(x => jobNames.GetValueOrDefault(x.Job, x.Job.ToString())));
            Console.WriteLine($"{Path.GetFileName(s.FilePath),-28} {s.Ordinal,4} {s.StartLocal:MM-dd HH:mm:ss}  {TimeSpan.FromMilliseconds(s.DurationMs):m\\:ss} {s.Outcome,-8} {furthest?.Name ?? "-",-24} {at,7} {s.BossName ?? "-",-12} " +
                              $"{(s.BossHpPct >= 0 ? $"{s.BossHpPct:0.0}%" : "-"),6} {s.DeathsShown,6}  {party}{(allPlayers ? $"  ({p.Name})" : "")}");
        }
        if (rows.Count > 0)
        {
            Console.WriteLine();
            foreach (var g in rows.GroupBy(r => r.S.Phases.LastOrDefault()?.Name ?? "-").OrderBy(g => g.Min(r => PhaseNo(g.Key))).ThenBy(g => g.Key))
                Console.WriteLine($"  furthest {g.Key}: {g.Count()}");
            Console.WriteLine($"  nights: {string.Join(", ", rows.GroupBy(r => r.S.StartLocal.ToString("MM-dd")).Select(g => $"{g.Key} ({g.Count()})"))}");
        }
        return 0;
    }
}
