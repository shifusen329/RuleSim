namespace RuleSim.Actions;

// One action being resolved. Status changes are recorded, not applied: reads see them through an
// overlay, so a later step sees what an earlier one spent or granted.
internal sealed class ResolveContext
{
    private readonly Dictionary<(ulong Actor, ushort Status), StatusView?> overlay = [];
    private readonly byte[] gaugeBefore;

    public ResolveContext(IWorld world, IActionSheet sheet, IActor caster, JobState state, uint actionId, ulong targetId, Random rng, List<StatusChange> changes)
    {
        World = world;
        Sheet = sheet;
        Caster = caster;
        State = state;
        ActionId = actionId;
        TargetId = targetId;
        Rng = rng;
        Changes = changes;
        Info = sheet.Action(actionId);
        gaugeBefore = (byte[])state.Gauge.Clone();
    }

    public IWorld World { get; }
    public IActionSheet Sheet { get; }
    public IActor Caster { get; }
    public JobState State { get; }
    public uint ActionId { get; }
    public ulong TargetId { get; }
    public ActionInfo? Info { get; }
    public Random Rng { get; }
    public List<StatusChange> Changes { get; }
    public byte[] Gauge => State.Gauge;

    public ComboRole ComboRole { get; init; }

    // Multiplies the barriers granted while it's set (Zoe, a spread crit Adloquium, Rising Rhythm).
    public float ShieldScale { get; set; } = 1f;

    // Who the action reached, in order.
    public List<ulong> Hit { get; } = [];
    public List<GroundZone> Zones { get; } = [];
    public List<string> Warnings { get; } = [];

    public bool GaugeChanged => !gaugeBefore.AsSpan().SequenceEqual(State.Gauge);

    public StatusView? Find(IActor actor, ushort status)
    {
        if (overlay.TryGetValue((actor.Id, status), out var seen)) return seen;
        foreach (var s in actor.Statuses)
            if (s.Id == status && s.Source == 0) return s;
        return null;
    }

    public bool Has(IActor actor, ushort status) => Find(actor, status) != null;

    public void Grant(IActor target, ushort status, int param, float duration, float shieldScale = 1f)
    {
        overlay[(target.Id, status)] = new StatusView(status, (ushort)param, duration);
        Changes.Add(new StatusChange(target.Id, StatusChangeKind.Grant, status, (ushort)param, duration, shieldScale));
        if (!Hit.Contains(target.Id)) Hit.Add(target.Id);
    }

    public void AddStack(IActor target, ushort status, float duration)
    {
        var current = Find(target, status);
        overlay[(target.Id, status)] = new StatusView(status, (ushort)((current?.Param ?? 0) + 1), duration);
        Changes.Add(new StatusChange(target.Id, StatusChangeKind.AddStack, status, Duration: duration));
    }

    public void Consume(IActor target, ushort status)
    {
        if (Find(target, status) is not { } current) return;
        overlay[(target.Id, status)] = current.Param > 1 ? current with { Param = (ushort)(current.Param - 1) } : null;
        Changes.Add(new StatusChange(target.Id, StatusChangeKind.Consume, status));
    }

    public void Remove(IActor target, ushort status)
    {
        if (Find(target, status) == null) return;
        overlay[(target.Id, status)] = null;
        Changes.Add(new StatusChange(target.Id, StatusChangeKind.Remove, status));
    }
}
