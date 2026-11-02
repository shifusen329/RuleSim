using System.Numerics;

namespace RuleSim.Actions;

public enum StatusChangeKind
{
    // Replaces any copy the target holds.
    Grant,
    // One more stack, up to the status's MaxStacks, refreshing the duration.
    AddStack,
    // One stack spent; the status ends at none.
    Consume,
    Remove,
}

public readonly record struct StatusChange(
    ulong Target,
    StatusChangeKind Kind,
    ushort Status,
    ushort Param = 0,
    float Duration = 0f,
    float ShieldScale = 1f);

public enum ComboRole { None, Starter, Continuation }

// A circle on the ground that gives `Status` to whoever stands in it.
public sealed record GroundZone(Vector3 Position, float Radius, ushort Status, float Duration);

// What the server does and sends for one action, in the order it happens.
public sealed class ActionResult
{
    public required uint ActionId { get; init; }
    public required ulong Caster { get; init; }

    public string? Rejected { get; internal set; }

    // Who the ActionEffect is aimed at, and who it lists: the aimed target first.
    public ulong AnimationTarget { get; internal set; }
    public List<ulong> Targets { get; } = [];

    public List<StatusChange> Statuses { get; } = [];
    public List<GroundZone> Zones { get; } = [];

    // How the action took part in a linear combo, before it advanced.
    public ComboRole Combo { get; internal set; }

    // The caster's ActorGauge payload after the action, when it changed.
    public byte[]? Gauge { get; internal set; }

    public float AnimationLock { get; internal set; }

    // A second action the server sends on its own after this one, and how long after.
    public (uint ActionId, float Delay)? FollowUp { get; internal set; }

    public List<string> Warnings { get; } = [];

    // Granted, wherever they landed.
    public IEnumerable<ushort> Granted => Statuses.Where(s => s.Kind == StatusChangeKind.Grant).Select(s => s.Status);
}

// What the server does on its own between actions: timers running out, gauges filling.
public sealed class TickResult
{
    public List<StatusChange> Statuses { get; } = [];
    public byte[]? Gauge { get; internal set; }
}
