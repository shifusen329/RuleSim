using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions.Jobs;

// The demi-summon trance (SummonTimer and the Phoenix/Solar bits), the elemental attunement (type,
// count and timer) and the two Aetherflow bits. Favors and trances are statuses in Rules.
internal static class Summoner
{
    // UNVERIFIED: Ifrit 1, Titan 2, Garuda 3.
    private const int Fire = 1, Earth = 2, Wind = 3;
    private const int Aetherflow1 = 1, Aetherflow2 = 2, Aetherflow = 3, PhoenixPrimed = 4, SolarPrimed = 12;
    private const int SummonState = PhoenixPrimed | 8;

    public static void OnAction(ResolveContext ctx)
    {
        var g = ctx.Gauge;
        switch (ctx.ActionId)
        {
            case 7427 or 3581: Trance(g, 0); break;           // Summon Bahamut / Dreadwyrm Trance
            case 25831: Trance(g, PhoenixPrimed); break;      // Summon Phoenix
            case 36992: Trance(g, SolarPrimed); break;        // Summon Solar Bahamut (UNVERIFIED: both bits)

            case 25805 or 25838: Attune(g, Fire, 2); break;   // Summon Ifrit / Ifrit II
            case 25806 or 25839: Attune(g, Earth, 4); break;  // Summon Titan / Titan II
            case 25807 or 25840: Attune(g, Wind, 4); break;   // Summon Garuda / Garuda II

            case 25883 or 25884                               // Gemshine / Precious Brilliance
                or 25808 or 25809 or 25810 or 25811 or 25812 or 25813
                or 25814 or 25815 or 25816 or 25817 or 25818 or 25819
                or 25823 or 25824 or 25825 or 25827 or 25828 or 25829
                or 25832 or 25833 or 25834:
                SpendAttunement(g);
                break;

            // UNVERIFIED: the Aetherflow1/2 bits are Summoner's two Aetherflow stacks.
            case 16508 or 16510: SmnAetherFlags.Write(g, SmnAetherFlags.Read(g) | Aetherflow); break;   // Energy Drain / Siphon
            case 181 or 3578 or 36990: SpendAetherflow(g); break;                                        // Fester / Painflare / Necrotize
        }
    }

    public static void Tick(JobState state, float dt)
    {
        var g = state.Gauge;
        var step = (int)(dt * 1000f);
        var summon = SmnSummonTimer.Read(g);
        if (summon > 0)
        {
            var t = summon - step;
            if (t <= 0)
            {
                SmnSummonTimer.Write(g, 0);
                SmnAetherFlags.Write(g, SmnAetherFlags.Read(g) & ~SummonState);
            }
            else SmnSummonTimer.Write(g, t);
        }
        var attune = SmnAttunementTimer.Read(g);
        if (attune > 0)
        {
            var t = attune - step;
            if (t <= 0)
            {
                SmnAttunementTimer.Write(g, 0);
                SmnAttunement.Write(g, 0);
            }
            else SmnAttunementTimer.Write(g, t);
        }
    }

    private static void Trance(byte[] g, int state)
    {
        SmnSummonTimer.Write(g, 15000);
        SmnAetherFlags.Write(g, (SmnAetherFlags.Read(g) & ~SummonState) | state);
    }

    private static void Attune(byte[] g, int type, int count)
    {
        SmnAttunement.Write(g, (count << 2) | type);
        SmnAttunementTimer.Write(g, 30000);
    }

    private static void SpendAttunement(byte[] g)
    {
        var packed = SmnAttunement.Read(g);
        var count = packed >> 2;
        if (count <= 0) return;
        count--;
        if (count == 0)
        {
            SmnAttunement.Write(g, 0);
            SmnAttunementTimer.Write(g, 0);
        }
        else SmnAttunement.Write(g, (count << 2) | (packed & 3));
    }

    private static void SpendAetherflow(byte[] g)
    {
        var flags = SmnAetherFlags.Read(g);
        SmnAetherFlags.Write(g, (flags & Aetherflow2) != 0 ? flags & ~Aetherflow2 : flags & ~Aetherflow1);
    }
}
