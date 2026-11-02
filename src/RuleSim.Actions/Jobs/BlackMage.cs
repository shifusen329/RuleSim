using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions.Jobs;

// The Astral Fire / Umbral Ice stance (+1..+3 / -1..-3), Umbral Hearts, Astral Soul, the 15 s
// Enochian countdown and the Paradox marker. Polyglot is in Rules.
internal static class BlackMage
{
    private const uint Fire = 141, FireII = 147, HighFireII = 25794, FireIII = 152, FireIV = 3577;
    private const uint Blizzard = 142, BlizzardII = 25793, HighBlizzardII = 25795, BlizzardIII = 154, BlizzardIV = 3576;
    private const uint Freeze = 159, Flare = 162, FlareStar = 36989, Despair = 16505;
    private const uint UmbralSoul = 16506, Transpose = 149, Manafont = 158, Paradox = 25797;
    private const ushort Firestarter = 165;

    private const int EnochianBit = 1, ParadoxBit = 2, SoulShift = 2, SoulMask = 0x1C;
    private const int EnochianMs = 15000;

    public static void OnAction(ResolveContext ctx)
    {
        var g = ctx.Gauge;
        var stance = BlmStance.Read(g);
        switch (ctx.ActionId)
        {
            // UNVERIFIED: Fire from Umbral Ice lands on Astral Fire I rather than dropping to neutral.
            case Fire:
                SetStance(g, stance <= 0 ? 1 : Math.Min(stance + 1, 3));
                ConsumeHearts(g, 1);
                break;
            case FireII or HighFireII:
                SetStance(g, 3);
                ConsumeHearts(g, 1);
                break;
            case FireIII or Despair:
                SetStance(g, 3);
                break;
            case Flare:
                SetStance(g, 3);
                ConsumeHearts(g, 3);
                SetSoul(g, Soul(g) + 3);
                break;
            case FireIV:
                Refresh(g);
                SetSoul(g, Soul(g) + 1);
                break;
            case FlareStar:
                Refresh(g);
                SetSoul(g, 0);
                break;
            case Manafont:
                SetStance(g, 3);
                BlmUmbralHearts.Write(g, 3);
                SetFlag(g, ParadoxBit, true);
                break;
            // UNVERIFIED: Blizzard from Astral Fire lands on Umbral Ice I rather than dropping to neutral.
            case Blizzard:
                SetStance(g, stance >= 0 ? -1 : Math.Max(stance - 1, -3));
                TryArmParadox(g);
                break;
            case BlizzardII or HighBlizzardII or BlizzardIII:
                SetStance(g, -3);
                TryArmParadox(g);
                break;
            case BlizzardIV or Freeze:
                Refresh(g);
                BlmUmbralHearts.Write(g, 3);
                TryArmParadox(g);
                break;
            case UmbralSoul:
                SetStance(g, Math.Max(stance - 1, -3));
                BlmUmbralHearts.Write(g, Math.Min(BlmUmbralHearts.Read(g) + 1, 3));
                TryArmParadox(g);
                break;
            case Transpose:
                if (stance > 0) SetStance(g, -1);
                else if (stance < 0) SetStance(g, 1);
                break;
            case Paradox:
                Refresh(g);
                SetFlag(g, ParadoxBit, false);
                if (stance > 0) ctx.Grant(ctx.Caster, Firestarter, 0, 30f);
                break;
        }
    }

    // UNVERIFIED: the countdown holds under Umbral Ice; only Astral Fire drains it here.
    public static void Tick(JobState state, float dt)
    {
        var g = state.Gauge;
        var timer = BlmEnochianTimer.Read(g);
        if (timer <= 0 || BlmStance.Read(g) < 0) return;
        timer -= (int)(dt * 1000f);
        if (timer > 0)
        {
            BlmEnochianTimer.Write(g, timer);
            return;
        }
        BlmEnochianTimer.Write(g, 0);
        BlmStance.Write(g, 0);
        BlmUmbralHearts.Write(g, 0);
        BlmFlags.Write(g, 0);
    }

    private static void SetStance(byte[] g, int stance)
    {
        BlmStance.Write(g, stance);
        SetFlag(g, EnochianBit, true);
        BlmEnochianTimer.Write(g, EnochianMs);
    }

    private static void Refresh(byte[] g) => BlmEnochianTimer.Write(g, EnochianMs);

    private static void ConsumeHearts(byte[] g, int n) => BlmUmbralHearts.Write(g, Math.Max(BlmUmbralHearts.Read(g) - n, 0));

    private static int Soul(byte[] g) => (BlmFlags.Read(g) & SoulMask) >> SoulShift;

    private static void SetSoul(byte[] g, int stacks)
        => BlmFlags.Write(g, (BlmFlags.Read(g) & ~SoulMask) | (Math.Clamp(stacks, 0, 6) << SoulShift));

    private static void SetFlag(byte[] g, int bit, bool on)
        => BlmFlags.Write(g, on ? BlmFlags.Read(g) | bit : BlmFlags.Read(g) & ~bit);

    // UNVERIFIED: armed on reaching Umbral Ice III with 3 Umbral Hearts, and by Manafont; it carries
    // into Astral Fire until Paradox is cast.
    private static void TryArmParadox(byte[] g)
    {
        if (BlmStance.Read(g) == -3 && BlmUmbralHearts.Read(g) == 3) SetFlag(g, ParadoxBit, true);
    }
}
