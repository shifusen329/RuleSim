using System.Numerics;

namespace RuleSim.Actions;

// ClassJob sheet row ids.
public enum Job : byte
{
    None = 0,
    Paladin = 19,
    Monk = 20,
    Warrior = 21,
    Dragoon = 22,
    Bard = 23,
    WhiteMage = 24,
    BlackMage = 25,
    Summoner = 27,
    Scholar = 28,
    Ninja = 30,
    Machinist = 31,
    DarkKnight = 32,
    Astrologian = 33,
    Samurai = 34,
    RedMage = 35,
    Gunbreaker = 37,
    Dancer = 38,
    Reaper = 39,
    Sage = 40,
    Viper = 41,
    Pictomancer = 42,
}

public static class ActionCategory
{
    public const byte Spell = 2;
    public const byte Weaponskill = 3;
    public const byte Ability = 4;
}

public static class CastType
{
    public const byte SingleTarget = 1;
    public const byte Circle = 2;
}

// The Action sheet columns resolution reads.
public sealed record ActionInfo(
    uint Id,
    byte Category,
    byte CastType,
    float EffectRange,
    bool CanTargetSelf,
    bool CanTargetParty,
    float CastSeconds = 0f,
    float RecastSeconds = 0f,
    uint ComboFrom = 0,
    bool PreservesCombo = false,
    byte PrimaryCostType = 0,
    ushort PrimaryCostValue = 0)
{
    public bool IsGcd => Category is ActionCategory.Spell or ActionCategory.Weaponskill;
}

public interface IActionSheet
{
    ActionInfo? Action(uint id);

    // Some other action's ActionCombo names it.
    bool IsComboPrerequisite(uint id);
}

// Source 0 is a status the holder carries once, whoever applied it.
public readonly record struct StatusView(ushort Id, ushort Param, float Remaining, ulong Source = 0);

public interface IActor
{
    ulong Id { get; }
    Job Job { get; }
    bool IsAlive { get; }
    Vector3 Position { get; }
    float HitboxRadius { get; }
    IReadOnlyList<StatusView> Statuses { get; }
}

public interface IWorld
{
    IActor? Find(ulong id);
    IEnumerable<IActor> Party { get; }
    IEnumerable<IActor> Enemies { get; }
}

public sealed record ActionRequest(ulong Caster, uint ActionId, ulong Target);
