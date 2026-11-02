namespace RuleSim.Actions;

// Gauge fields by their offset on the client's per-job gauge struct.
internal static class Gauges
{
    public static readonly ResourceGauge GnbCartridge = new(0x08, 3);
    // Bloodfest's cartridges go past the usual cap.
    public static readonly ResourceGauge GnbBloodfestCartridge = new(0x08, 6);
    // The step the Gnashing Fang (1, 2) and Reign of Beasts (3, 4) button swaps read; 0 ends either.
    public static readonly ResourceGauge GnbComboStep = new(0x0C, 4);

    public static readonly ResourceGauge PldOath = new(0x08, 100);
    // Confiteor→Blade of Faith→Truth→Valor: step 0→1→2→3.
    public static readonly ResourceGauge PldConfiteorStep = new(0x0C, 3);

    public static readonly ResourceGauge DrkBlood = new(0x08, 100);
    // Scarlet Delirium→Comeuppance→Torcleaver: step 0→1→2.
    public static readonly ResourceGauge DrkDeliriumStep = new(0x10, 2, size: 2);

    public static readonly ResourceGauge WarBeast = new(0x08, 100);
    public static readonly ResourceGauge DrgFocus = new(0x0C, 2);
    public static readonly ResourceGauge NinNinki = new(0x08, 100);
    public static readonly ResourceGauge NinKazematoi = new(0x0A, 5);

    public static readonly ResourceGauge RprSoul = new(0x08, 100);
    public static readonly ResourceGauge RprShroud = new(0x09, 100);
    public static readonly ResourceGauge RprLemureShroud = new(0x0C, 5);
    public static readonly ResourceGauge RprVoidShroud = new(0x0D, 8);

    public static readonly ResourceGauge SamKenki = new(0x0B, 100);
    public static readonly ResourceGauge SamMeditation = new(0x0C, 3);
    public static readonly GaugeField SamKaeshi = new(0x0A);
    public static readonly GaugeField SamSen = new(0x0D);

    public static readonly ResourceGauge MchHeat = new(0x0C, 100);
    public static readonly ResourceGauge MchBattery = new(0x0D, 100);

    public static readonly ResourceGauge RdmWhite = new(0x08, 100);
    public static readonly ResourceGauge RdmBlack = new(0x09, 100);
    // At 3 the Verthunder/Veraero buttons become Verflare/Verholy, which spend all 3.
    public static readonly ResourceGauge RdmManaStacks = new(0x0A, 3);

    public static readonly ResourceGauge SchAetherflow = new(0x08, 3);

    public static readonly ResourceGauge VprRattlingCoil = new(0x08, 3);
    public static readonly ResourceGauge VprAnguineTribute = new(0x09, 5);
    public static readonly ResourceGauge VprSerpentOffering = new(0x0A, 100);
    public static readonly GaugeField VprDreadCombo = new(0x0B);
    public static readonly GaugeField VprDreadTimer = new(0x0C, size: 2);
    public static readonly GaugeField VprSerpentCombo = new(0x10);

    public static readonly GaugeField WhmLilyTimer = new(0x0A, size: 2, signed: true);
    public static readonly ResourceGauge WhmLily = new(0x0C, 3);
    public static readonly ResourceGauge WhmBloodLily = new(0x0D, 3);

    public static readonly ResourceGauge DncFeathers = new(0x08, 4);
    public static readonly ResourceGauge DncEsprit = new(0x09, 100);
    public static readonly GaugeField DncStep0 = new(0x0A);
    public static readonly GaugeField DncStepIndex = new(0x0E);

    public static readonly GaugeField SgeAddersgallTimer = new(0x08, size: 2, signed: true);
    public static readonly ResourceGauge SgeAddersgall = new(0x0A, 3);
    public static readonly ResourceGauge SgeAddersting = new(0x0B, 3);

    public static readonly ResourceGauge MnkChakra = new(0x08, 5);
    public static readonly GaugeField MnkBeastChakra1 = new(0x09);
    public static readonly GaugeField MnkBeastChakra2 = new(0x0A);
    public static readonly GaugeField MnkBeastChakra3 = new(0x0B);
    public static readonly GaugeField MnkBeastChakraStacks = new(0x0C);
    public static readonly GaugeField MnkNadi = new(0x0D);
    public static readonly GaugeField MnkBlitzTimer = new(0x0E, size: 2);

    public static readonly ResourceGauge BrdSoulVoice = new(0x0D, 100);

    public static readonly ResourceGauge PctPalette = new(0x08, 100);
    public static readonly ResourceGauge PctPaint = new(0x0A, 5);
    public static readonly GaugeField PctCanvas = new(0x0B);
    public static readonly GaugeField PctCreature = new(0x0C);

    public static readonly GaugeField BlmEnochianTimer = new(0x08, size: 2, signed: true);
    public static readonly GaugeField BlmStance = new(0x0A, signed: true);
    public static readonly GaugeField BlmUmbralHearts = new(0x0B);
    public static readonly ResourceGauge BlmPolyglot = new(0x0C, 3);
    // Bit 0 Enochian, bit 1 Paradox, bits 2-4 Astral Soul stacks.
    public static readonly GaugeField BlmFlags = new(0x0D);

    public static readonly GaugeField SmnSummonTimer = new(0x08, size: 2);
    public static readonly GaugeField SmnAttunementTimer = new(0x0A, size: 2);
    // (count << 2) | type.
    public static readonly GaugeField SmnAttunement = new(0x0E);
    public static readonly GaugeField SmnAetherFlags = new(0x0F);

    // Three 4-bit play slots and the Minor Arcana slot, low nibble first.
    public static readonly GaugeField AstCards = new(0x08, size: 2);
    public static readonly GaugeField AstCurrentDraw = new(0x0A);
}
