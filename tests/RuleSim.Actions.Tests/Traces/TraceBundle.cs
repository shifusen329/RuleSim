using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuleSim.Actions.Tests;

// One action from a real log and everything the server sent for it (written by RuleSim's `traces`).
internal sealed record TraceBundle
{
    public string Log { get; init; } = "";
    public string T { get; init; } = "";
    public string Seq { get; init; } = "";
    public string Caster { get; init; } = "";
    public int CasterJob { get; init; }
    public int? Level { get; init; }
    public bool Owner { get; init; }
    public uint Action { get; init; }
    public float? Lock { get; init; }
    public float? CastTime { get; init; }
    public float[]? CasterPos { get; init; }
    public List<TraceTarget> Targets { get; init; } = [];
    public List<TraceStatus> Gained { get; init; } = [];
    public List<TraceStatus> Lost { get; init; } = [];
    public string? GaugeBefore { get; init; }
    public string? GaugeAfter { get; init; }
    public Dictionary<string, List<TraceStatus>> StatusesBefore { get; init; } = [];
    public List<TraceRecent> Recent { get; init; } = [];
    public List<TraceMember> Party { get; init; } = [];

    public ulong CasterId => Hex(Caster);

    public string Name => $"{Log} {T} {(Job)CasterJob} {Action}";

    public static ulong Hex(string id) => ulong.Parse(id, NumberStyles.HexNumber);

    public static byte[]? Gauge(string? hex)
        => hex is { Length: 32 } ? Convert.FromHexString(hex) : null;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static IEnumerable<TraceBundle> Load(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl").OrderBy(f => f))
            foreach (var line in File.ReadLines(file))
                if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize<TraceBundle>(line, Options) is { } bundle)
                    yield return bundle;
    }
}

internal sealed record TraceTarget
{
    public string Id { get; init; } = "";
    public float[]? Pos { get; init; }
    public List<TraceEffect> Effects { get; init; } = [];
}

internal sealed record TraceEffect
{
    public int Type { get; init; }
}

internal sealed record TraceStatus
{
    public string Target { get; init; } = "";
    public ushort Status { get; init; }
    public ushort Param { get; init; }
    public float? Duration { get; init; }
    public float? Remaining { get; init; }
    public string Source { get; init; } = "";
}

internal sealed record TraceRecent
{
    public uint Action { get; init; }
    public float Ago { get; init; }
}

internal sealed record TraceMember
{
    public string Id { get; init; } = "";
    public int Job { get; init; }
    public float[]? Pos { get; init; }
    public bool Alive { get; init; } = true;
}

// The world just before a logged action, rebuilt from its bundle.
internal sealed class TraceWorld : IWorld
{
    private readonly Dictionary<ulong, FakeActor> actors = [];
    private readonly HashSet<ulong> party = [];

    // enemyStatuses: what only lands on enemies. A non-player the action reached otherwise is a pet,
    // which counts as party. The server's target list says who an AoE caught; enemy hitboxes aren't
    // in the log, so an enemy it lists is taken as in reach.
    public TraceWorld(TraceBundle b, IReadOnlySet<ushort> enemyStatuses)
    {
        foreach (var m in b.Party)
        {
            var actor = Add(TraceBundle.Hex(m.Id), (Job)m.Job, m.Pos);
            actor.IsAlive = m.Alive;
            party.Add(actor.Id);
        }
        Add(b.CasterId, (Job)b.CasterJob, b.CasterPos);
        party.Add(b.CasterId);
        foreach (var t in b.Targets)
        {
            var id = TraceBundle.Hex(t.Id);
            var actor = Add(id, Job.None, t.Pos);
            if (party.Contains(id) || id >> 28 != 4) continue;
            var hostile = t.Effects.Any(e => e.Type == DamageEffect)
                          || b.Gained.Any(g => g.Target == t.Id && enemyStatuses.Contains(g.Status));
            if (hostile) actor.HitboxRadius = 100f;
            else party.Add(id);
        }
        foreach (var (id, statuses) in b.StatusesBefore)
            if (actors.TryGetValue(TraceBundle.Hex(id), out var actor))
                foreach (var s in statuses)
                    actor.StatusList.Add(new StatusView(s.Status, s.Param, s.Remaining ?? 0f, TraceBundle.Hex(s.Source) == actor.Id || IsPlayer(s.Source) ? 0 : TraceBundle.Hex(s.Source)));
    }

    private const int DamageEffect = 3;

    // Statuses players apply are keyed without a source, as the sim keeps them; an enemy's keep theirs.
    private static bool IsPlayer(string id) => id.StartsWith("10", StringComparison.Ordinal);

    private FakeActor Add(ulong id, Job job, float[]? pos)
    {
        if (actors.TryGetValue(id, out var existing)) return existing;
        var actor = new FakeActor(id, job) { Position = pos is { Length: >= 2 } ? new Vector3(pos[0], 0f, pos[1]) : Vector3.Zero };
        actors[id] = actor;
        return actor;
    }

    public IActor? Find(ulong id) => actors.GetValueOrDefault(id);
    public IEnumerable<IActor> Party => actors.Values.Where(a => party.Contains(a.Id) && a.IsAlive);
    public IEnumerable<IActor> Enemies => actors.Values.Where(a => !party.Contains(a.Id) && a.Id >> 28 == 4);

    public bool IsParty(ulong id) => party.Contains(id);
}

// Every proc fires, or none does: what the server sent must lie between the two.
internal sealed class FixedRandom(bool procs, int seed) : Random(seed)
{
    public override float NextSingle() => procs ? 0f : 0.99999f;
}
