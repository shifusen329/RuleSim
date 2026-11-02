using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions.Jobs;

// The Astrosign deck: three play slots and the Minor Arcana slot (4 bits each), and which draw comes
// next (Astral 0, Umbral 1). The play buttons become the held card's own action, so both ids count.
internal static class Astrologian
{
    private const int Balance = 1, Bole = 2, Arrow = 3, Spear = 4, Ewer = 5, Spire = 6, Lord = 7, Lady = 8;
    private const int AstralNext = 0, UmbralNext = 1;

    public static void OnAction(ResolveContext ctx)
    {
        var g = ctx.Gauge;
        switch (ctx.ActionId)
        {
            // UNVERIFIED: CurrentDraw is the draw shown next.
            case 37017:   // Astral Draw
                AstCards.Write(g, Pack(Balance, Arrow, Spire, Lord));
                AstCurrentDraw.Write(g, UmbralNext);
                break;
            case 37018:   // Umbral Draw
                AstCards.Write(g, Pack(Spear, Bole, Ewer, Lady));
                AstCurrentDraw.Write(g, AstralNext);
                break;
            case 37019 or 37023 or 37026: ClearSlot(g, 0); break;   // Play I, The Balance, The Spear
            case 37020 or 37024 or 37027: ClearSlot(g, 1); break;   // Play II, The Arrow, The Bole
            case 37021 or 37025 or 37028: ClearSlot(g, 2); break;   // Play III, The Spire, The Ewer
            case 37022 or 7444 or 7445: ClearSlot(g, 3); break;     // Minor Arcana, Lord / Lady of Crowns
        }
    }

    private static int Pack(int slot0, int slot1, int slot2, int arcana) => slot0 | (slot1 << 4) | (slot2 << 8) | (arcana << 12);

    private static void ClearSlot(byte[] g, int slot) => AstCards.Write(g, AstCards.Read(g) & ~(0xF << (4 * slot)));
}
