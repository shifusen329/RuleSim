using System.Globalization;
using System.Numerics;

namespace RuleSim.Actions.Tests;

// The player-action rows of the Action sheet, from Data/actions.csv.
internal sealed class CsvActionSheet : IActionSheet
{
    private static readonly Lazy<CsvActionSheet> Shared = new(() => new CsvActionSheet(Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", "actions.csv")));

    private readonly Dictionary<uint, ActionInfo> rows = [];
    private readonly HashSet<uint> prerequisites = [];

    public static CsvActionSheet Instance => Shared.Value;

    private CsvActionSheet(string path)
    {
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split(',');
            var info = new ActionInfo(
                Id: uint.Parse(f[0]),
                Category: byte.Parse(f[1]),
                CastType: byte.Parse(f[2]),
                EffectRange: float.Parse(f[3], CultureInfo.InvariantCulture),
                CanTargetSelf: f[4] == "1",
                CanTargetParty: f[5] == "1",
                CastSeconds: int.Parse(f[6]) / 10f,
                RecastSeconds: int.Parse(f[7]) / 10f,
                ComboFrom: uint.Parse(f[8]),
                PreservesCombo: f[9] == "1",
                PrimaryCostType: byte.Parse(f[10]),
                PrimaryCostValue: ushort.Parse(f[11]));
            rows[info.Id] = info;
            if (info.ComboFrom != 0) prerequisites.Add(info.ComboFrom);
        }
    }

    public ActionInfo? Action(uint id) => rows.GetValueOrDefault(id);
    public bool IsComboPrerequisite(uint id) => prerequisites.Contains(id);
}

internal sealed class FakeActor(ulong id, Job job = Job.None) : IActor
{
    public ulong Id => id;
    public Job Job => job;
    public bool IsAlive { get; set; } = true;
    public Vector3 Position { get; set; }
    public float HitboxRadius { get; set; } = 0.5f;
    public List<StatusView> StatusList { get; } = [];
    public IReadOnlyList<StatusView> Statuses => StatusList;

    // Applies what the server decided, as a client's status list would follow it.
    public void Apply(IEnumerable<StatusChange> changes)
    {
        foreach (var c in changes.Where(c => c.Target == id))
        {
            var i = StatusList.FindIndex(s => s.Id == c.Status && s.Source == 0);
            switch (c.Kind)
            {
                case StatusChangeKind.Grant:
                    if (i >= 0) StatusList.RemoveAt(i);
                    StatusList.Add(new StatusView(c.Status, c.Param, c.Duration));
                    break;
                case StatusChangeKind.AddStack:
                    if (i >= 0) StatusList[i] = StatusList[i] with { Param = (ushort)(StatusList[i].Param + 1), Remaining = c.Duration };
                    else StatusList.Add(new StatusView(c.Status, 1, c.Duration));
                    break;
                case StatusChangeKind.Consume when i >= 0:
                    if (StatusList[i].Param > 1) StatusList[i] = StatusList[i] with { Param = (ushort)(StatusList[i].Param - 1) };
                    else StatusList.RemoveAt(i);
                    break;
                case StatusChangeKind.Remove when i >= 0:
                    StatusList.RemoveAt(i);
                    break;
            }
        }
    }

    public bool Has(ushort status) => StatusList.Any(s => s.Id == status);
}

internal sealed class FakeWorld : IWorld
{
    public List<FakeActor> PartyList { get; } = [];
    public List<FakeActor> EnemyList { get; } = [];

    public IActor? Find(ulong id) => PartyList.Concat(EnemyList).FirstOrDefault(a => a.Id == id);
    public IEnumerable<IActor> Party => PartyList;
    public IEnumerable<IActor> Enemies => EnemyList;
}

// One caster pressing actions in a world, applying each result to the actors.
internal sealed class Bench
{
    public const ulong PlayerId = 0x10000001, BossId = 0x40000001;

    public Resolver Resolver { get; } = new(CsvActionSheet.Instance);
    public FakeWorld World { get; } = new();
    public FakeActor Player { get; }
    public FakeActor Boss { get; } = new(BossId);
    public JobState State { get; }
    public Random Rng { get; set; } = new(1);

    public Bench(Job job)
    {
        Player = new FakeActor(PlayerId, job);
        World.PartyList.Add(Player);
        World.EnemyList.Add(Boss);
        State = new JobState(job);
    }

    public ActionResult Press(uint actionId, ulong? target = null)
    {
        var result = Resolver.Resolve(World, new ActionRequest(PlayerId, actionId, target ?? BossId), State, Rng);
        foreach (var actor in World.PartyList.Concat(World.EnemyList)) actor.Apply(result.Statuses);
        return result;
    }

    public TickResult Tick(float dt, bool moving = false)
    {
        var result = Resolver.Tick(World, Player, State, dt, moving, Rng);
        Player.Apply(result.Statuses);
        return result;
    }
}
