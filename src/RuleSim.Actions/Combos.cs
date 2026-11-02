namespace RuleSim.Actions;

// The linear combo: the sheet's ActionCombo names the action each step follows. Combos are recorded
// against base ids, while a max-level player presses the upgraded ones.
internal static class Combos
{
    public const float Window = 30f;

    // upgraded id → the base id the sheet's combos name.
    private static readonly Dictionary<uint, uint> Upgrades = new()
    {
        [16479] = 75,     // Raiden Thrust ← True Thrust
        [25770] = 86,     // Draconian Fury ← Doom Spike
        [36963] = 7477,   // Gyofu ← Hakaze
        [25780] = 7483,   // Fuko ← Fuga
        [7411] = 2866,    // Heated Split Shot ← Split Shot
        [36954] = 78,     // Lance Barrage ← Vorpal Thrust
        [36955] = 87,     // Spiral Blow ← Disembowel
        [25771] = 84,     // Heavens' Thrust ← Full Thrust
        [25772] = 88,     // Chaotic Spring ← Chaos Thrust
        [7412] = 2868,    // Heated Slug Shot ← Slug Shot
    };

    private static uint Base(uint id) => Upgrades.TryGetValue(id, out var b) ? b : id;

    // A second step an action also follows, besides the one the sheet names.
    private static readonly Dictionary<uint, uint> AlsoFollows = new()
    {
        [36952] = 3556,   // Drakesbane after Wheeling Thrust
    };

    // comboFree: every step counts as continuing the combo (Meikyo Shisui).
    public static ComboRole RoleOf(IActionSheet sheet, JobState state, uint actionId, bool comboFree = false)
    {
        var prerequisite = sheet.Action(actionId)?.ComboFrom ?? 0;
        if (prerequisite != 0)
        {
            if (comboFree) return ComboRole.Continuation;
            var follows = state.ComboTimer > 0f && (Base(state.ComboAction) == Base(prerequisite)
                || AlsoFollows.TryGetValue(actionId, out var other) && Base(state.ComboAction) == other);
            return follows ? ComboRole.Continuation : ComboRole.None;
        }
        return sheet.IsComboPrerequisite(Base(actionId)) ? ComboRole.Starter : ComboRole.None;
    }

    // Another GCD breaks the combo unless the sheet says it preserves it.
    public static void Advance(ResolveContext ctx)
    {
        if (ctx.ComboRole != ComboRole.None)
        {
            ctx.State.ComboAction = ctx.ActionId;
            ctx.State.ComboTimer = Window;
        }
        else if (ctx.Info is { IsGcd: true, PreservesCombo: false })
        {
            ctx.State.ComboAction = 0;
            ctx.State.ComboTimer = 0f;
        }
    }

    public static void Tick(JobState state, float dt)
    {
        if (state.ComboTimer <= 0f) return;
        state.ComboTimer -= dt;
        if (state.ComboTimer > 0f) return;
        state.ComboTimer = 0f;
        state.ComboAction = 0;
    }
}
