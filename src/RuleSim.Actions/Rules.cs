using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions;

// What each action does, as the server resolves it: effects per action id, the statuses an action
// spends, gauge costs from the sheet, and the gauges that change on a timer.
public static class Rules
{
    internal static readonly Dictionary<uint, IEffect[]> Actions = new()
    {
        // GNB
        [16145] = [Combo(Gauge(GnbCartridge, 1)), SetGauge(GnbComboStep, 0)],   // Solid Barrel (+ breaks Gnashing Fang)
        [16149] = [Combo(Gauge(GnbCartridge, 1)), SetGauge(GnbComboStep, 0)],   // Demon Slaughter (+ breaks)
        [16164] = [Gauge(GnbBloodfestCartridge, 3), Status(3840, 30f)],   // Bloodfest → 3 cartridges + Ready to Reign
        // Gnashing Fang route (step 0→1→2→0) + the Ready-to status that lights the Continuation:
        [16146] = [Gauge(GnbComboStep, 1), Status(1842, 10f)],   // Gnashing Fang → Ready to Rip
        [16147] = [Gauge(GnbComboStep, 1), Status(1843, 10f)],   // Savage Claw → Ready to Tear
        [16150] = [SetGauge(GnbComboStep, 0), Status(1844, 10f)],  // Wicked Talon → Ready to Gouge
        // GNB's other combos break Gnashing Fang (can't run two at once):
        [16137] = [SetGauge(GnbComboStep, 0)],   // Keen Edge
        [16139] = [SetGauge(GnbComboStep, 0)],   // Brutal Shell
        [16141] = [SetGauge(GnbComboStep, 0)],   // Demon Slice
        // Reign of Beasts → Noble Blood → Lion Heart runs on the same step, from 3:
        [36937] = [SetGauge(GnbComboStep, 3)],   // Reign of Beasts
        [36938] = [SetGauge(GnbComboStep, 4)],   // Noble Blood
        [36939] = [SetGauge(GnbComboStep, 0)],   // Lion Heart
        // buffs / Continuation procs:
        [16138] = [Status(1831, 20f), Status(3886, 30f)],   // No Mercy (+ Ready to Break)
        [16162] = [Status(2686, 10f)],                      // Burst Strike → Ready to Blast
        [16163] = [Status(3839, 10f)],                      // Fated Circle → Ready to Raze
        // mitigation:
        [16152] = [Status(1836, 10f)],                      // Superbolide
        [16148] = [Status(3838, 15f)], [36935] = [Status(3838, 15f)],   // Nebula / Great Nebula → Great Nebula
        [16140] = [Status(1832, 20f)],                      // Camouflage
        [16160] = [TargetStatus(1839, 15f)],                // Heart of Light
        [16161] = [TargetStatus(2683, 8f), TargetStatus(2684, 4f), TargetStatus(2685, 20f)],   // Heart of Stone → Heart of Corundum + Clarity + Catharsis
        [25758] = [TargetStatus(2683, 8f), TargetStatus(2684, 4f), TargetStatus(2685, 20f)],   // Heart of Corundum

        // Tank role
        [7531] = [Status(1191, 20f)],   // Rampart
        [7535] = [EnemyStatus(1193, 15f)],   // Reprisal

        // Melee / caster role
        [7549] = [EnemyStatus(1195, 15f)],   // Feint
        [7560] = [EnemyStatus(1203, 15f)],   // Addle

        // Tank LB3
        [199]   = [TargetStatus(196, 8f)],    // Last Bastion
        [4240]  = [TargetStatus(863, 8f)],    // Land Waker
        [4241]  = [TargetStatus(864, 8f)],    // Dark Force
        [17105] = [TargetStatus(1931, 8f)],   // Gunmetal Soul

        // WAR
        [37] = [Combo(Gauge(WarBeast, 10))],   // Maim
        [42] = [Combo(Gauge(WarBeast, 20))],   // Storm's Path
        [45] = [Combo(Gauge(WarBeast, 10), Extend(2677, 30f, 60f, grantIfMissing: true))],   // Storm's Eye (+ 30s Surging Tempest)
        [52] = [Gauge(WarBeast, 50), Status(1897, 30f)],          // Infuriate (+ Nascent Chaos)
        [16462] = [Combo(Extend(2677, 30f, 60f, grantIfMissing: true), Gauge(WarBeast, 20))],  // Mythril Tempest → 30s Surging Tempest + 20 Beast
        [7389]  = [Status(1177, 15f, 3), Status(2624, 30f), Extend(2677, 10f, 60f)],  // Inner Release (3 stk) + Primal Rend Ready, +10s Surging Tempest
        [25753] = [Status(3834, 20f)],         // Primal Rend → Primal Ruination Ready
        // mitigation:
        [43]    = [Status(409, 10f)],          // Holmgang
        [44]    = [Status(3832, 15f)], [36923] = [Status(3832, 15f)],   // Vengeance / Damnation → Damnation
        [40]    = [Status(87, 10f)],           // Thrill of Battle
        [7388]  = [TargetStatus(1457, 30f), TargetStatus(2108, 15f)],   // Shake It Off (+ Over Time)
        [3551]  = [Status(2678, 8f), Status(2679, 4f), Status(2680, 20f)],   // Raw Intuition → Bloodwhetting + Stem the Flow + Stem the Tide
        [25751] = [Status(2678, 8f), Status(2679, 4f), Status(2680, 20f)],   // Bloodwhetting
        [16464] = [Status(1857, 8f), TargetStatus(1858, 8f), TargetStatus(2679, 4f), TargetStatus(2680, 20f)],   // Nascent Flash; Nascent Glint, Stem the Flow and Stem the Tide on the target

        // DRK
        [3632]  = [Combo(Gauge(DrkBlood, 20))],   // Souleater
        [16468] = [Combo(Gauge(DrkBlood, 20))],   // Stalwart Soul
        [7390]  = [Status(3836, 15f, 3), Status(742, 15f, 3)], // Delirium (3 stk) + Blood Weapon (3 stk)
        [16472] = [Status(3837, 30f)],            // Living Shadow → Scorn
        [7393]  = [TargetStatus(1178, 7f)],   // The Blackest Night
        // mitigation:
        [3638]  = [Status(810, 10f)],             // Living Dead
        [3636]  = [Status(3835, 15f), Status(3902, 20f)], [36927] = [Status(3835, 15f), Status(3902, 20f)],   // Shadow Wall / Shadowed Vigil (+ Vigilant)
        [3634]  = [Status(746, 10f)],             // Dark Mind
        [16471] = [TargetStatus(1894, 15f)],      // Dark Missionary
        [25754] = [TargetStatus(2682, 10f)],      // Oblation
        // Scarlet Delirium route (under Delirium): step drives Scarlet Delirium→Comeuppance→Torcleaver swap.
        [36928] = [Gauge(DrkDeliriumStep, 1)],    // Scarlet Delirium → step 1 (Comeuppance)
        [36929] = [Gauge(DrkDeliriumStep, 1)],    // Comeuppance → step 2 (Torcleaver)
        [36930] = [Gauge(DrkDeliriumStep, -2)],   // Torcleaver → ends route

        // DRG
        [16479] = [Gauge(DrgFocus, 1)],   // Raiden Thrust
        [25770] = [Gauge(DrgFocus, 1)],   // Draconian Fury
        [85]    = [Status(1864, 20f)],   // Lance Charge
        [3557]  = [TargetStatus(786, 20f)],    // Battle Litany, on the party around
        [83]    = [Status(116, 5f)],     // Life Surge
        [36955] = [Combo(Status(2720, 30f))], [87] = [Combo(Status(2720, 30f))], [7397] = [Combo(Status(2720, 30f))], // Spiral Blow/Disembowel/Sonic Thrust → Power Surge (combo bonus)
        [36952] = [Combo(Status(1863, 30f))], [16477] = [Combo(Status(1863, 30f))],  // Drakesbane/Coerthan Torment → Draconian Fire (combo bonus)
        [16478] = [Status(1243, 15f)], [92] = [Status(1243, 15f)],     // High Jump/Jump → Dive Ready
        [94]    = [Status(1870, 15f)],   // Elusive Jump → Enhanced Piercing Talon
        [96]    = [Status(3845, 30f)],   // Dragonfire Dive → Dragon's Flight
        [3555]  = [Status(3844, 20f)],  // Geirskogul → Nastrond Ready
        [16480] = [Status(3846, 20f)],   // Stardiver → Starcross Ready

        // NIN
        [2240]  = [Gauge(NinNinki, 5)],          // Spinning Edge
        [2242]  = [Combo(Gauge(NinNinki, 5))],   // Gust Slash
        [2247]  = [Gauge(NinNinki, 5)],          // Throwing Dagger
        [2254]  = [Gauge(NinNinki, 5)],          // Death Blossom
        [2255]  = [Combo(Gauge(NinNinki, 15)), Gauge(NinKazematoi, -1)],  // Aeolian Edge (spends 1 Kazematoi)
        [3563]  = [Combo(Gauge(NinNinki, 15), Gauge(NinKazematoi, 2))],   // Armor Crush (+2 Kazematoi, combo)
        [16488] = [Combo(Gauge(NinNinki, 5))],   // Hakke Mujinsatsu
        [16489] = [Gauge(NinNinki, 50), Status(2689, 30f)], // Meisui (+ Meisui status)
        [25774] = [Gauge(NinNinki, 10)],         // Phantom Kamaitachi
        [25777] = [Gauge(NinNinki, 5)],          // Forked Raiju
        [25778] = [Gauge(NinNinki, 5)],          // Fleeting Raiju
        [36957] = [Gauge(NinNinki, 40), Status(3850, 30f)], // Dokumori (+ Higi)
        [2264]  = [Status(497, 15f)],                     // Kassatsu
        [7403]  = [Status(1186, 6f), Status(3851, 30f)],  // Ten Chi Jin + Tenri Jindo Ready
        [2267]  = [Status(2690, 30f, 3)],                 // Raiton → Raiju Ready (3 stk)
        [16493] = [Status(1954, 30f, 5), Status(2723, 45f)], // Bunshin (5 stk) + Phantom Kamaitachi Ready
        [2271]  = [Status(3848, 20f)], [18881] = [Status(3848, 20f)],   // Suiton (and its Ten Chi Jin form) → Shadow Walker
        [18877] = [Status(2690, 30f, 3)],                 // Raiton under Ten Chi Jin → Raiju Ready

        // RPR
        [24373] = [Gauge(RprSoul, 10)],          // Slice
        [24374] = [Combo(Gauge(RprSoul, 10))],   // Waxing Slice
        [24375] = [Combo(Gauge(RprSoul, 10))],   // Infernal Slice
        [24376] = [Gauge(RprSoul, 10)],          // Spinning Scythe
        [24377] = [Combo(Gauge(RprSoul, 10))],   // Nightmare Scythe
        [24378] = [Gauge(RprSoul, 10)],          // Shadow of Death
        [24379] = [Gauge(RprSoul, 10)],          // Whorl of Death
        [24380] = [Gauge(RprSoul, 50)],          // Soul Slice
        [24381] = [Gauge(RprSoul, 50)],          // Soul Scythe
        [24386] = [Gauge(RprSoul, 10)],          // Harpe
        [24388] = [Gauge(RprSoul, 10)],          // Harvest Moon
        [24382] = [Gauge(RprShroud, 10), Status(2589, 60f)],  // Gibbet (+ Enhanced Gallows)
        [24383] = [Gauge(RprShroud, 10), Status(2588, 60f)],  // Gallows (+ Enhanced Gibbet)
        [24384] = [Gauge(RprShroud, 10)],        // Guillotine
        [36970] = [Gauge(RprShroud, 10), Status(2589, 60f)],  // Executioner's Gibbet (+ Enhanced Gallows)
        [36971] = [Gauge(RprShroud, 10), Status(2588, 60f)],  // Executioner's Gallows (+ Enhanced Gibbet)
        [36972] = [Gauge(RprShroud, 10)],        // Executioner's Guillotine
        // status grants:
        [24389] = [Status(2587, 30f)], [24392] = [Status(2587, 30f)], [24390] = [Status(2587, 30f)], [24391] = [Status(2587, 30f)], // → Soul Reaver
        [24395] = [Gauge(RprVoidShroud, 1), Status(2591, 30f)], [24396] = [Gauge(RprVoidShroud, 1), Status(2590, 30f)], // Void/Cross Reaping → +1 Void Shroud + Enhanced Cross/Void
        [24397] = [Gauge(RprVoidShroud, 1)],   // Grim Reaping → +1 Void Shroud
        [24394] = [Gauge(RprLemureShroud, 5), Status(2593, 30f), Status(3857, 30f)],  // Enshroud → 5 Lemure Shroud + Enshrouded + Oblatio
        [24393] = [Status(3858, 30f, 2)],                  // Gluttony → Executioner (2 stk)
        [24387] = [Status(2594, 0f)],                      // Soulsow (no-expiry)
        [24401] = [Status(2845, 10f), Status(2595, 10f)], [24402] = [Status(2845, 10f), Status(2595, 10f)], // Ingress/Egress → Enhanced Harpe + Threshold
        [24385] = [Status(3905, 30f), Status(3859, 30f)],  // Plentiful Harvest → Ideal Host + Perfectio Occulta
        [24398] = [IfCasterHad(3859, Status(3860, 30f)), SetGauge(RprLemureShroud, 0), SetGauge(RprVoidShroud, 0)],   // Communio → Perfectio Parata after Plentiful Harvest; ends Enshroud
        [24405] = [TargetStatus(2599, 20f), Status(2972, 6f)],   // Arcane Circle on the party around + Bloodsown Circle

        // SAM — Kenki, Meditation and statuses; Sen and Kaeshi in Samurai
        [36963] = [Gauge(SamKenki, 5)],          // Gyofu (upgrades from Hakaze)
        [7478]  = [Combo(Gauge(SamKenki, 5), Status(1298, 40f))],   // Jinpu (+ Fugetsu)
        [7479]  = [Combo(Gauge(SamKenki, 5), Status(1299, 40f))],   // Shifu (+ Fuka)
        [7480]  = [Combo(Gauge(SamKenki, 15))],  // Yukikaze
        [7481]  = [Combo(Gauge(SamKenki, 10), Status(1298, 40f))],  // Gekko (+ Fugetsu)
        [7482]  = [Combo(Gauge(SamKenki, 10), Status(1299, 40f))],  // Kasha (+ Fuka)
        [25780] = [Gauge(SamKenki, 10)],         // Fuko (upgrades from Fuga)
        [7484]  = [Combo(Gauge(SamKenki, 10), Status(1298, 40f))],  // Mangetsu (+ Fugetsu)
        [7485]  = [Combo(Gauge(SamKenki, 10), Status(1299, 40f))],  // Oka (+ Fuka)
        [7486]  = [Gauge(SamKenki, 10)],         // Enpi
        [16482] = [Gauge(SamKenki, 50), Status(2959, 30f), Status(3855, 30f)], // Ikishoten (+ Ogi Namikiri Ready + Zanshin Ready)
        [7493]  = [Status(1236, 15f)],           // Hissatsu: Yaten → Enhanced Enpi
        [7499]  = [Status(1233, 20f, 3), Status(3856, 30f)], // Meikyo Shisui (3 stk) + Tendo
        // Iaijutsu → the Tsubame-gaeshi status its Kaeshi costs + Meditation:
        [7488]  = [Status(3852, 30f), Gauge(SamMeditation, 1)], [7487] = [Status(4216, 30f), Gauge(SamMeditation, 1)],   // Tenka Goken, Midare Setsugekka
        [36965] = [Status(4217, 30f), Gauge(SamMeditation, 1)], [36966] = [Status(4218, 30f), Gauge(SamMeditation, 1)], // Tendo Goken, Tendo Setsugekka
        [7489]  = [Gauge(SamMeditation, 1)], [25781] = [Gauge(SamMeditation, 1)], // Higanbana / Ogi Namikiri → +1 Meditation

        // MCH — max-level (Heated) ids; their combo prerequisites are the base ids (see Combos)
        [7411]  = [Gauge(MchHeat, 5)],                                 // Heated Split Shot
        [7412]  = [Combo(Gauge(MchHeat, 5))],                          // Heated Slug Shot
        [7413]  = [Combo(Gauge(MchHeat, 5), Gauge(MchBattery, 10))],   // Heated Clean Shot
        [2870]  = [Gauge(MchHeat, 5)],                                 // Spread Shot
        [25786] = [Gauge(MchHeat, 10)],                               // Scattergun
        [16500] = [Gauge(MchBattery, 20)],                            // Air Anchor
        [25788] = [Gauge(MchBattery, 20), Status(3865, 30f)],         // Chain Saw (+ Excavator Ready)
        [36981] = [Gauge(MchBattery, 20)],                            // Excavator
        [2876]  = [Status(851, 5f)],                                  // Reassemble → Reassembled
        [17209] = [Status(2688, 10f, 5)],                             // Hypercharge → Overheated (5 stk)
        [7414]  = [Status(3864, 30f), Status(3866, 30f)],             // Barrel Stabilizer → Hypercharged + Full Metal Machinist
        [2878]  = [Status(1946, 10f)],                                // Wildfire → self-status (enables Detonator)
        [16889] = [TargetStatus(1951, 15f)],                          // Tactician
        [2887]  = [EnemyStatus(860, 10f)],                            // Dismantle

        // RDM — all flat: mana is "Additional Effect", never combo-gated
        [37004] = [Gauge(RdmWhite, 2), Gauge(RdmBlack, 2)],   // Jolt III
        [37006] = [Gauge(RdmWhite, 3), Gauge(RdmBlack, 3)],   // Grand Impact
        [16526] = [Gauge(RdmWhite, 3), Gauge(RdmBlack, 3)],   // Impact
        [7511]  = [Gauge(RdmWhite, 5)],                       // Verstone
        [7510]  = [Gauge(RdmBlack, 5)],                       // Verfire
        [25856] = [Gauge(RdmWhite, 6), Random(0.5f, Status(1235, 30f))],    // Veraero III (+ Verstone Ready, 50%; guaranteed under Acceleration not modelled)
        [25855] = [Gauge(RdmBlack, 6), Random(0.5f, Status(1234, 30f))],    // Verthunder III (+ Verfire Ready, 50%; guaranteed under Acceleration not modelled)
        [16525] = [Gauge(RdmWhite, 7)],                       // Veraero II
        [16524] = [Gauge(RdmBlack, 7)],                       // Verthunder II
        [7526]  = [Gauge(RdmWhite, 11), Gauge(RdmManaStacks, -3), Random(0.2f, Status(1235, 30f))],   // Verholy (+11 W, -3 stacks, 20% Verstone Ready)
        [7525]  = [Gauge(RdmBlack, 11), Gauge(RdmManaStacks, -3), Random(0.2f, Status(1234, 30f))],   // Verflare (+11 B, -3 stacks, 20% Verfire Ready)
        [16530] = [Gauge(RdmWhite, 4), Gauge(RdmBlack, 4)],   // Scorch
        [25858] = [Gauge(RdmWhite, 4), Gauge(RdmBlack, 4)],   // Resolution
        [7505]  = [Random(0.5f, Status(1234, 30f))],   // Verthunder → Verfire Ready (50%)
        [7507]  = [Random(0.5f, Status(1235, 30f))],   // Veraero → Verstone Ready (50%)
        [7518]  = [Status(1238, 20f), Status(3877, 30f)],  // Acceleration + Grand Impact Ready
        [7520]  = [Status(1239, 20f), OthersStatus(1297, 20f), Status(3876, 30f)],  // Embolden (the party's own version on the others) + Thorned Flourish
        [7521]  = [Status(1971, 30f), Status(3875, 30f, 3), Status(3878, 30f)], // Manafication → range + Magicked Swordplay (3 stk) + Prefulgence Ready
        [25857] = [TargetStatus(2707, 10f)],                  // Magick Barrier
        // Enchanted melee each grant 1 Mana Stack (at 3, Verthunder/Veraero → Verflare/Verholy):
        [7527] = [Gauge(RdmManaStacks, 1)], [7528] = [Gauge(RdmManaStacks, 1)], [7529] = [Gauge(RdmManaStacks, 1)], // Enchanted Riposte/Zwerchhau/Redoublement
        [7530] = [Gauge(RdmManaStacks, 1)], [37002] = [Gauge(RdmManaStacks, 1)], [37003] = [Gauge(RdmManaStacks, 1)], // Enchanted Moulinet/Deux/Trois
        // TODO: unbalanced mana

        // SCH — Aetherflow only; the fairy gauge fills passively (not modelled)
        [166]  = [Gauge(SchAetherflow, 3)],   // Aetherflow
        [3587] = [Gauge(SchAetherflow, 3)],   // Dissipation
        [7436]  = [Status(3882, 30f)],        // Chain Stratagem → Impact Imminent
        [16542] = [Status(1896, 15f)],        // Recitation
        [37014] = [Status(3884, 20f)],   // Seraphism → self-status (Manifestation/Accession/Seraphic Halo swaps)
        [37015] = [TargetStatus(297, 30f)],   // Manifestation → Galvanize
        [3585]  = [PartyStatus(297, 30f, 30f, shieldScale: 2.45f)],   // Deployment Tactics → a spread crit Adloquium's Galvanize on the party
        [37016] = [TargetStatus(297, 30f)],                 // Accession → Galvanize
        [186]   = [TargetStatus(297, 30f)], [37013] = [TargetStatus(297, 30f)],   // Succor / Concitation → Galvanize
        [25868] = [TargetStatus(2711, 20f)],                // Expedient → Desperate Measures
        [188]   = [GroundZone(299, 15f, 15f)],              // Sacred Soil
        [16545] = [PartyStatus(1917, 30f, 30f)], [16546] = [PartyStatus(1917, 30f, 30f)],   // Summon Seraph / Consolation → Seraphic Veil
        [16538] = [PartyStatus(317, 20f, 30f)],             // Fey Illumination

        // VPR
        [34620] = [Gauge(VprRattlingCoil, 1)],      // Vicewinder
        [34623] = [Gauge(VprRattlingCoil, 1)],      // Vicepit
        [34647] = [Gauge(VprRattlingCoil, 1), Status(3671, 30f)],   // Serpent's Ire (+ Ready to Reawaken)
        [34610] = [Gauge(VprSerpentOffering, 10), Status(3647, 60f)],  // Flanksting Strike (+ Hindstung Venom)
        [34611] = [Gauge(VprSerpentOffering, 10), Status(3648, 60f)],  // Flanksbane Fang (+ Hindsbane Venom)
        [34612] = [Gauge(VprSerpentOffering, 10), Status(3646, 60f)],  // Hindsting Strike (+ Flanksbane Venom)
        [34613] = [Gauge(VprSerpentOffering, 10), Status(3645, 60f)],  // Hindsbane Fang (+ Flankstung Venom)
        [34618] = [Gauge(VprSerpentOffering, 10), Status(3650, 60f)],  // Jagged Maw (+ Grimskin's Venom)
        [34619] = [Gauge(VprSerpentOffering, 10), Status(3649, 60f)],  // Bloodied Maw (+ Grimhunter's Venom)
        [34621] = [Gauge(VprSerpentOffering, 5), Status(3668, 40f), Status(3657, 30f)],   // Hunter's Coil (+ Hunter's Instinct + Hunter's Venom)
        [34622] = [Gauge(VprSerpentOffering, 5), Status(3669, 40f), Status(3658, 30f)],   // Swiftskin's Coil (+ Swiftscaled + Swiftskin's Venom)
        [34624] = [Gauge(VprSerpentOffering, 5), Status(3668, 40f), Status(3659, 30f)],   // Hunter's Den (+ Hunter's Instinct + Fellhunter's Venom)
        [34625] = [Gauge(VprSerpentOffering, 5), Status(3669, 40f), Status(3660, 30f)],   // Swiftskin's Den (+ Swiftscaled + Fellskin's Venom)
        [34626] = [Gauge(VprAnguineTribute, 5), Status(3670, 30f)],  // Reawaken (+ Reawakened)
        [34606] = [Status(3772, 60f)], [34614] = [Status(3772, 60f)],  // Steel Fangs/Maw → Honed Reavers
        [34607] = [Status(3672, 60f)], [34615] = [Status(3672, 60f)],  // Reaving Fangs/Maw → Honed Steel
        [34608] = [Status(3668, 40f)], [34616] = [Status(3668, 40f)],  // Hunter's Sting/Bite → Hunter's Instinct
        [34609] = [Status(3669, 40f)], [34617] = [Status(3669, 40f)],  // Swiftskin's Sting/Bite → Swiftscaled
        [34633] = [Status(3665, 60f)], [34644] = [Status(3666, 60f)],  // Uncoiled Fury/Twinfang → Poised

        // WHM — Blood Lily only; casting these needs a Lily (passive gauge, not fed here)
        [16531] = [Gauge(WhmBloodLily, 1)],   // Afflatus Solace
        [16534] = [Gauge(WhmBloodLily, 1)],   // Afflatus Rapture
        [136]   = [Status(157, 15f), Status(3879, 30f, 3)],   // Presence of Mind → self-haste + Sacred Sight (3 stk)
        [16536] = [Status(3881, 30f), Status(1872, 20f), PartyStatus(1873, 20f, 50f)],   // Temperance → Divine Grace (enables Divine Caress) + the party's 10%
        [37011] = [TargetStatus(3903, 10f)],   // Divine Caress
        [7433]  = [TargetStatus(1219, 10f)],   // Plenary Indulgence → Confession
        [120]   = [Random(0.15f, Status(155, 15f))],   // Cure → Freecure (15%)

        // DNC — Feathers only (50% procs); Esprit is buff-gated, not modelled
        // Esprit +5 per own damage GCD (base Esprit-trait rate; dance-partner contribution not modelled), +50 from Tillana.
        [15991] = [Random(0.5f, Gauge(DncFeathers, 1)), Gauge(DncEsprit, 5)],   // Reverse Cascade
        [15992] = [Random(0.5f, Gauge(DncFeathers, 1)), Gauge(DncEsprit, 5)],   // Fountainfall
        [15995] = [Random(0.5f, Gauge(DncFeathers, 1)), Gauge(DncEsprit, 5)],   // Rising Windmill
        [15996] = [Random(0.5f, Gauge(DncFeathers, 1)), Gauge(DncEsprit, 5)],   // Bloodshower
        [25790] = [Gauge(DncEsprit, 50)],   // Tillana → +50 Esprit
        [15989] = [Random(0.5f, Status(2693, 30f)), Gauge(DncEsprit, 5)], [15993] = [Random(0.5f, Status(2693, 30f)), Gauge(DncEsprit, 5)],   // Cascade/Windmill → Silken Symmetry (50%)
        [15990] = [Combo(Random(0.5f, Status(2694, 30f))), Gauge(DncEsprit, 5)], [15994] = [Combo(Random(0.5f, Status(2694, 30f))), Gauge(DncEsprit, 5)],   // Fountain/Bladeshower → Silken Flow (50%, combo bonus)
        [16007] = [Random(0.5f, Status(1820, 30f))], [16008] = [Random(0.5f, Status(1820, 30f))],   // Fan Dance I/II → Threefold Fan Dance (50%)
        [16011] = [Status(1825, 20f), Status(2700, 20f)],               // Devilment + Flourishing Starfall
        [16013] = [Status(3017, 30f), Status(3018, 30f), Status(1820, 30f), Status(2699, 30f), Status(3868, 30f)], // Flourish (5 procs)
        [16012] = [TargetStatus(1826, 15f)],                            // Shield Samba
        [16014] = [Status(1827, 15f)],                                  // Improvisation (Rising Rhythm builds in Dancer.Tick)
        [25789] = [ScaleByStacks(2696, [1f, 1.2f, 1.4f, 1.6f, 2f], TargetStatus(2697, 30f))],   // Improvised Finish: 5/6/7/8/10% by Rising Rhythm
        [15997] = [Status(1818, 15f)], [15998] = [Status(1819, 15f)],   // Standard/Technical Step
        [16003] = [Status(3867, 30f)],                                  // Standard Finish with no step → Last Dance Ready only
        [16191] = [Status(1821, 60f), Status(3867, 30f)], [16192] = [Status(1821, 60f), Status(3867, 30f)],   // Single/Double Standard Finish
        [16004] = [Status(1822, 20f), Status(2698, 30f), Status(3869, 30f)], // Technical Finish → dmg + Flourishing Finish + Dance of Dawn Ready
        // Technical Finish's party buff comes from its follow-up action (FollowUps).
        [16193] = [Status(2698, 30f), Status(3869, 30f)], [16194] = [Status(2698, 30f), Status(3869, 30f)],   // Single/Double Technical Finish
        [16195] = [Status(2698, 30f), Status(3869, 30f)], [16196] = [Status(2698, 30f), Status(3869, 30f)],   // Triple/Quadruple Technical Finish
        [33215] = [TargetStatus(1822, 20f)], [33216] = [TargetStatus(1822, 20f)], [33217] = [TargetStatus(1822, 20f)], [33218] = [TargetStatus(1822, 20f)],   // ...the follow-ups
        [36984] = [Status(1821, 60f), Status(3867, 30f)],               // Finishing Move → Standard Finish + Last Dance Ready

        // PLD
        [20]    = [Status(76, 20f), Status(3847, 30f)],      // Fight or Flight → dmg buff + Goring Blade Ready
        [3539]  = [Combo(Status(1902, 30f), Status(2673, 30f))],    // Royal Authority → Atonement Ready + Divine Might (combo bonus)
        [16457] = [Combo(Status(2673, 30f))],                       // Prominence → Divine Might (combo bonus)
        [7383]  = [Status(1368, 30f, 4), Status(3019, 30f)], // Requiescat (4 stk) + Confiteor Ready
        [36921] = [Status(1368, 30f, 4), Status(3019, 30f)], // Imperator (Lv100 upgrade)
        [16460] = [Status(3827, 30f)],                       // Atonement → Supplication Ready
        [36918] = [Status(3828, 30f)],                       // Supplication → Sepulchre Ready
        // Confiteor route: step drives the Confiteor→Blade of Faith→Truth→Valor button swap.
        [16459] = [Gauge(PldConfiteorStep, 1)],              // Confiteor → step 1 (Blade of Faith)
        [25748] = [Gauge(PldConfiteorStep, 1)],              // Blade of Faith → step 2 (Blade of Truth)
        [25749] = [Gauge(PldConfiteorStep, 1)],              // Blade of Truth → step 3 (Blade of Valor)
        [25750] = [Status(3831, 30f), Gauge(PldConfiteorStep, -3)], // Blade of Valor → Blade of Honor Ready (+ ends route)
        // mitigation:
        [30]    = [Status(82, 10f)],                         // Hallowed Ground
        [17]    = [Status(3829, 15f), Status(3830, 15f)], [36920] = [Status(3829, 15f), Status(3830, 15f)],   // Sentinel / Guardian → Guardian + Guardian's Will
        [22]    = [Status(77, 10f)],                         // Bulwark
        [3540]  = [TargetStatus(1362, 30f)],                 // Divine Veil
        [7382]  = [TargetStatus(1174, 8f), TargetStatus(2675, 4f), TargetStatus(2676, 12f)],   // Intervention (+ Knight's Resolve + Knight's Benediction on the target)
        [7385]  = [Status(1175, 18f)],                       // Passage of Arms
        [3542]  = [Status(2674, 8f), Status(2675, 4f), Status(2676, 12f)],   // Sheltron → Holy Sheltron + Knight's Resolve + Knight's Benediction
        [25746] = [Status(2674, 8f), Status(2675, 4f), Status(2676, 12f)],   // Holy Sheltron

        // MNK
        [7395]  = [Status(1181, 20f), Status(3843, 20f)],   // Riddle of Fire + Fire's Rumination
        [25766] = [Status(2687, 15f), Status(3842, 15f)],   // Riddle of Wind + Wind's Rumination
        [7396]  = [TargetStatus(1185, 20f), TargetStatus(1182, 20f)],   // Brotherhood + Meditative Brotherhood, on the party around
        [69]    = [Status(110, 20f, 3)],                    // Perfect Balance (3 stk)
        [4262]  = [Status(2513, 30f)],                      // Form Shift → Formless Fist
        [36950] = [Status(2513, 30f)],                      // Fire's Reply → Formless Fist
        [25764] = [Status(2513, 30f)],                      // Masterful Blitz → Formless Fist
        [25765] = [Status(2513, 30f)], [25768] = [Status(2513, 30f)], [25769] = [Status(2513, 30f)], [36948] = [Status(2513, 30f)], // resolved blitz finishers → Formless Fist
        [7394]  = [Status(3841, 30f)],   // Riddle of Earth → Earth's Rumination
        [36940] = [Gauge(MnkChakra, 1)], [36941] = [Gauge(MnkChakra, 1)], [36942] = [Gauge(MnkChakra, 1)], [36943] = [Gauge(MnkChakra, 1)], // Meditation opens 1 Chakra
        // Forms; none while Perfect Balance is up.
        [56] = [Form(107)], [66] = [Form(107)], [36947] = [Form(107)], [70] = [Form(107)],                       // Opo-opo Form
        [53] = [Form(108)], [74] = [Form(108)], [36945] = [Form(108)], [62] = [Form(108)], [25767] = [Form(108)], // Raptor Form
        [54] = [Form(109)], [61] = [Form(109)], [36946] = [Form(109)], [16473] = [Form(109)],                    // Coeurl Form

        // BRD
        [16495] = [Random(0.35f, Status(3861, 30f))],     // Burst Shot → Hawk's Eye (35%)
        [3560]  = [Random(0.35f, Status(3861, 30f))],     // Iron Jaws → Hawk's Eye (35%)
        [7406]  = [Random(0.35f, Status(3861, 30f))], [7407] = [Random(0.35f, Status(3861, 30f))], [25783] = [Random(0.35f, Status(3861, 30f))], // Caustic Bite/Stormbite/Ladonsbite → Hawk's Eye (35%)
        [107]   = [Status(128, 10f), Status(3862, 30f)],  // Barrage → Barrage + Resonant Arrow Ready
        [16496] = [Status(2692, 10f)],                    // Apex Arrow → Blast Arrow Ready
        [25785] = [Status(2722, 20f), Status(3863, 30f)], // Radiant Finale → Radiant Finale + Radiant Encore Ready
        [101]   = [Status(125, 20f)],                     // Raging Strikes
        [118]   = [TargetStatus(141, 20f)],               // Battle Voice, on the party around
        [114]   = [Status(2217, 45f)],                    // Mage's Ballad
        [116]   = [Status(2218, 45f)],                    // Army's Paeon
        [3559]  = [Status(2216, 45f)],                    // the Wanderer's Minuet
        [7405]  = [TargetStatus(1934, 15f)],              // Troubadour

        // BLM
        [141]  = [Random(0.4f, Status(165, 30f)), Status(3870, 0f)],   // Fire → Firestarter (40%) + Thunderhead (stance entry)
        [3573] = [Status(737, 20f)],   // Ley Lines
        [158]  = [Status(3870, 0f)],  // Manafont → Thunderhead
        [142]  = [Status(3870, 0f)], [149]  = [Status(3870, 0f)], [152] = [Status(3870, 0f)], [154] = [Status(3870, 0f)], // Blizzard / Transpose / Fire III / Blizzard III → Thunderhead (stance entry)
        [25796] = [Gauge(BlmPolyglot, 1)],  // Amplifier → +1 Polyglot (spent by Foul/Xenoglossy via CostGauges)

        // SMN
        [25801] = [TargetStatus(2703, 20f), Status(3873, 30f)],  // Searing Light on the party around + Ruby's Glimmer
        [36992] = [Status(3874, 30f)],                     // Summon Solar Bahamut → Refulgent Lux
        [16508] = [Status(2701, 60f)],                     // Energy Drain → Further Ruin
        [16510] = [Status(2701, 60f)],                     // Energy Siphon → Further Ruin
        [25838] = [Status(2724, 0f)],                      // Summon Ifrit II → Ifrit's Favor
        [25840] = [Status(2725, 0f)],                      // Summon Garuda II → Garuda's Favor
        [25824] = [Status(2853, 0f)],                      // Topaz Rite → Titan's Favor
        [25833] = [Status(2853, 0f)],                      // Topaz Catastrophe → Titan's Favor
        [25835] = [Status(4403, 0f)],                      // Crimson Cyclone → Crimson Strike Ready
        [25831] = [Status(3229, 15f)],                     // Summon Phoenix → Firebird Trance
        [7427]  = [Status(3228, 15f)],                     // Summon Bahamut → Dreadwyrm Trance
        [3581]  = [Status(3228, 15f)],                     // Dreadwyrm Trance

        // AST
        [16552] = [TargetStatus(1878, 20f), Status(3893, 30f)],  // Divination on the party around + Divining
        [3606]  = [Status(841, 15f)],                      // Lightspeed
        [16559] = [Status(3895, 30f), Status(1892, 20f)],   // Neutral Sect → Suntouched (enables Sun Sign) + Neutral Sect (Helios barriers)
        [37031] = [TargetStatus(3896, 15f)],   // Sun Sign
        [37030] = [IfCasterHas(1892, TargetStatus(1921, 30f))], [3601] = [IfCasterHas(1892, TargetStatus(1921, 30f))],   // Helios Conjunction / Aspected Helios → barrier under Neutral Sect
        [3613]  = [TargetStatus(849, 10f)],    // Collective Unconscious
        [25874] = [TargetStatus(2718, 15f)],   // Macrocosmos on the party around (enables Microcosmos)
        [3594]  = [Random(0.15f, Status(815, 15f))],   // Benefic → Enhanced Benefic II (15%)
        [7439]  = [Status(1224, 10f)],   // Earthly Star → Earthly Dominance (enables Stellar Detonation, first 10s)

        // PCT
        [34650] = [Status(3675, 30f)], [34656] = [Status(3675, 30f)], [34653] = [Status(3675, 30f)], [34659] = [Status(3675, 30f)], // → Aetherhues
        [34651] = [Status(3676, 30f)], [34657] = [Status(3676, 30f)], [34654] = [Status(3676, 30f)], [34660] = [Status(3676, 30f)], // → Aetherhues II
        [34652] = [Gauge(PctPalette, 25), Gauge(PctPaint, 1)], [34658] = [Gauge(PctPalette, 25), Gauge(PctPaint, 1)], // Water in Blue (I/II): +25 palette, +1 paint
        [34655] = [Gauge(PctPaint, 1)], [34661] = [Gauge(PctPaint, 1)], [34688] = [Gauge(PctPaint, 1)], // Thunder in Magenta (I/II) + Rainbow Drip: +1 paint
        [34662] = [Gauge(PctPaint, -1)], [34663] = [Gauge(PctPaint, -1), Status(3679, 30f)],   // Holy in White / Comet in Black: spend 1 paint; Comet → Rainbow Bright
        [34683] = [Status(3674, 0f, 3), Status(3691, 0f)],  // Subtractive Palette (3 stk) + Monochrome Tones
        [34674] = [Status(3680, 30f, 3)],                   // Striking Muse → Hammer Time (3 stk)
        [34675] = [TargetStatus(3685, 20f), Status(3690, 30f), Status(3688, 30f, 5), Status(3681, 20f)], // Starry Muse on the party around, and its suite
        [34685] = [Status(3686, 10f)],                      // Tempera Coat → its own barrier, which also enables Tempera Grassa
        [34686] = [TargetStatus(3687, 10f)],                // Tempera Grassa (spends the Coat, see StatusClearedOnAction)

        // SGE
        [24290] = [Status(2606, 0f)],                     // Eukrasia (toggle, no-expiry)
        [37035] = [Status(3898, 20f), TargetStatus(3899, 20f)], // Philosophia + Eudaimonia on the party around
        [24298] = [TargetStatus(2618, 15f)],              // Kerachole
        [24300] = [Status(2611, 30f)],                    // Zoe
        [37034] = [Boost(2611, 1.5f, TargetStatus(2609, 30f))], [24292] = [Boost(2611, 1.5f, TargetStatus(2609, 30f))],   // Eukrasian Prognosis II / I (Zoe: x1.5)
        [24310] = [TargetStatus(3003, 20f), TargetStatus(3365, 30f)],   // Holos + Holosakos
        [24301] = [TargetRemove(2607, 2609)],                           // Pepsis spends the Eukrasian barriers
        [24311] = [TargetStatus(2613, 15f), OthersStatus(2643, 15f, 5)], // Panhaima + Panhaimatinon (5 stk) on the others
        [24309] = [Gauge(SgeAddersgall, 1)],              // Rhizomata → +1 Addersgall (passive fill in TimedGauges)
    };

    // What one landed auto-attack swing does, per job.
    internal static readonly Dictionary<Job, IEffect[]> AutoAttacks = new()
    {
        [Job.Paladin] = [Gauge(PldOath, 5)],   // Oath Mastery
    };

    // What a player holds when a scenario starts: phases start mid-fight, with resources the
    // server would have built up.
    internal static readonly Dictionary<Job, IEffect[]> StartingResources = new()
    {
        [Job.Paladin] = [SetGauge(PldOath, 100)],
    };

    // status → the actions that spend it (one stack, or the whole status when it has none).
    // Spending runs before the action's own effects, so an action can re-grant what it spent.
    internal static readonly Dictionary<ushort, IActionPredicate[]> StatusClearedOnAction = new()
    {
        [1842] = [ActionId(16156), AnyWeaponskill()],   // Ready to Rip:   Jugular Rip   or any WS
        [1843] = [ActionId(16157), AnyWeaponskill()],   // Ready to Tear:  Abdomen Tear  or any WS
        [1844] = [ActionId(16158), AnyWeaponskill()],   // Ready to Gouge: Eye Gouge     or any WS
        [2686] = [ActionId(25759), AnyWeaponskill()],   // Ready to Blast: Hypervelocity or any WS
        [3839] = [ActionId(36936), AnyWeaponskill()],   // Ready to Raze:  Fated Brand   or any WS
        [3840] = [ActionId(36937)],                     // Ready to Reign: Reign of Beasts
        [3886] = [ActionId(16153)],                     // Ready to Break: Sonic Break
        // PLD
        [3847] = [ActionId(3538)], [1902] = [ActionId(16460)], [2673] = [ActionId(7384), ActionId(16458)],
        [3827] = [ActionId(36918)], [3828] = [ActionId(36919)], [3019] = [ActionId(16459)], [3831] = [ActionId(36922)],
        // WAR
        [1177] = [ActionId(3549), ActionId(3550)], [1897] = [ActionId(16465), ActionId(16463)],
        [2624] = [ActionId(25753)], [3834] = [ActionId(36925)],
        [87] = [ActionId(7388)], [3832] = [ActionId(7388)], [2678] = [ActionId(7388)],   // Shake It Off dispels Thrill of Battle / Damnation / Bloodwhetting
        // DRK
        [3836] = [ActionId(36928), ActionId(36929), ActionId(36930), ActionId(36931)], [742] = [AnyGcd()],
        [3837] = [ActionId(36932)],
        // DRG
        [116] = [AnyWeaponskill()], [1863] = [ActionId(16479), ActionId(25770)], [1243] = [ActionId(7399)],
        [1870] = [ActionId(90)], [3845] = [ActionId(36953)], [3844] = [ActionId(7400)], [3846] = [ActionId(36956)],
        // MNK
        [2513] = [AnyWeaponskill()], [3843] = [ActionId(36950)], [3842] = [ActionId(36949)], [3841] = [ActionId(36944)],
        // NIN
        [3851] = [ActionId(36961)], [2690] = [ActionId(25777), ActionId(25778)],   // Raiju Ready: Forked / Fleeting Raiju
        [1186] = [ActionId(18879), ActionId(18880), ActionId(18881)],   // Ten Chi Jin: its third ninjutsu
        [2689] = [ActionId(7402), ActionId(36960)],
        [2723] = [ActionId(25774)], [3850] = [ActionId(36959), ActionId(36960)], [3848] = [ActionId(36958), ActionId(2258), ActionId(16489)],
        // SAM
        [1236] = [ActionId(7486)], [2959] = [ActionId(25781)], [3855] = [ActionId(36964)], [3856] = [ActionId(36966), ActionId(36965)],
        [1233] = [ActionId(7477), ActionId(36963), ActionId(7478), ActionId(7479), ActionId(7480), ActionId(7481), ActionId(7482), ActionId(7483), ActionId(25780), ActionId(7484), ActionId(7485)],   // Meikyo Shisui: the combo weaponskills
        [3852] = [ActionId(16485)], [4216] = [ActionId(16486)], [4217] = [ActionId(36967)], [4218] = [ActionId(36968)],   // each Kaeshi's Tsubame-gaeshi
        // RPR
        [2587] = [ActionId(24382), ActionId(24383), ActionId(24384)], [2588] = [ActionId(24382), ActionId(36970)],
        [2589] = [ActionId(24383), ActionId(36971)], [2590] = [ActionId(24395), ActionId(24398)], [2591] = [ActionId(24396), ActionId(24398)],
        [2593] = [ActionId(24398)], [3857] = [ActionId(36969)], [3858] = [ActionId(36970), ActionId(36971), ActionId(36972)],
        [2594] = [ActionId(24388)], [2845] = [ActionId(24386)], [2595] = [ActionId(24403)], [3905] = [ActionId(24394)],
        [3859] = [ActionId(24398)], [3860] = [ActionId(36973)],
        // RDM
        [1234] = [ActionId(7510)], [1235] = [ActionId(7511)], [1238] = [ActionId(25855), ActionId(25856), ActionId(16526)],
        [3877] = [ActionId(37006)], [3876] = [ActionId(37005)], [3878] = [ActionId(37007)],
        // MCH
        [851] = [AnyWeaponskill()], [2688] = [ActionId(36978), ActionId(16497)],
        [3864] = [ActionId(17209)], [3866] = [ActionId(36982)], [3865] = [ActionId(36981)], [1946] = [ActionId(16766)], // Wildfire consumed by Detonator
        // BRD
        [3861] = [ActionId(7409), ActionId(16494), ActionId(36974)], [128] = [ActionId(7409), ActionId(16494), ActionId(36974)],
        [3862] = [ActionId(36976)], [2692] = [ActionId(25784)], [3863] = [ActionId(36977)],
        // BLM
        [165] = [ActionId(152)], [3870] = [ActionId(36986), ActionId(36987), ActionId(153), ActionId(7420), ActionId(144), ActionId(7447)],
        // SMN
        [2701] = [ActionId(7426)], [3873] = [ActionId(36991)], [3874] = [ActionId(36997)],
        [2724] = [ActionId(25835)], [2725] = [ActionId(25837)], [2853] = [ActionId(25836)], [4403] = [ActionId(25885)],
        // AST
        [3893] = [ActionId(37029)],
        [3895] = [ActionId(37031)], [2718] = [ActionId(25875)], [815] = [ActionId(3610)], [1224] = [ActionId(8324)], // Sun Sign / Microcosmos / Benefic II / Stellar Detonation consumers
        // DNC
        [2693] = [ActionId(15991), ActionId(15995)], [2694] = [ActionId(15992), ActionId(15996)],
        [3017] = [ActionId(15991), ActionId(15995)], [3018] = [ActionId(15992), ActionId(15996)],
        [1820] = [ActionId(16009)], [2699] = [ActionId(25791)], [2700] = [ActionId(25792)], [2698] = [ActionId(25790)],
        [3867] = [ActionId(36983)], [3868] = [ActionId(36984)], [3869] = [ActionId(36985)],
        [1827] = [ActionId(25789)],   // Improvisation: ended by Improvised Finish (which reads Rising Rhythm, not this)
        // A finish executes as the variant for the steps danced: 16191/16192, 16193-16196.
        [1818] = [ActionId(16003), ActionId(16191), ActionId(16192)],
        [1819] = [ActionId(16004), ActionId(16193), ActionId(16194), ActionId(16195), ActionId(16196)],
        // WHM / SCH / SGE
        [3879] = [ActionId(37009)],
        [3881] = [ActionId(37011)],   // Divine Grace: consumed by Divine Caress
        [155]  = [ActionId(135)],     // Freecure: consumed by Cure II

        [3882] = [ActionId(37012)], [1896] = [ActionId(185), ActionId(37013), ActionId(3583), ActionId(7434), ActionId(37016)],
        [2606] = [ActionId(24314), ActionId(37032), ActionId(24291), ActionId(24292), ActionId(24293), ActionId(24308), ActionId(37034)],
        // VPR
        [3772] = [ActionId(34607), ActionId(34615)], [3672] = [ActionId(34606), ActionId(34614)],
        [3645] = [ActionId(34610)], [3646] = [ActionId(34611)], [3647] = [ActionId(34612)], [3648] = [ActionId(34613)],
        [3649] = [ActionId(34618)], [3650] = [ActionId(34619)], [3657] = [ActionId(34636)], [3658] = [ActionId(34637)],
        [3659] = [ActionId(34638)], [3660] = [ActionId(34639)], [3665] = [ActionId(34644)], [3666] = [ActionId(34645)],
        [3670] = [ActionId(34631)], [3671] = [ActionId(34626)],
        // PCT
        [3675] = [ActionId(34651), ActionId(34657), ActionId(34654), ActionId(34660)],
        [3676] = [ActionId(34652), ActionId(34658), ActionId(34655), ActionId(34661)],
        [3674] = [ActionId(34653), ActionId(34659), ActionId(34654), ActionId(34660), ActionId(34655), ActionId(34661)],
        [3691] = [ActionId(34663)], [3690] = [ActionId(34683)], [3680] = [ActionId(34678), ActionId(34679), ActionId(34680)],
        [3681] = [ActionId(34681)], [3679] = [ActionId(34688)],  [3686] = [ActionId(34686)],
        // stack-decrement / any-GCD consumers (rely on the AddStatus(-1) consume above):
        [1368] = [ActionId(7384), ActionId(16458), ActionId(16459), ActionId(25748), ActionId(25749), ActionId(25750)], // Requiescat: empowered casts / Blades
        [1954] = [AnyWeaponskill()],   // Bunshin: one stack per weaponskill
        [3875] = [ActionId(7527), ActionId(7528), ActionId(7529), ActionId(7530), ActionId(37002), ActionId(37003)],  // Magicked Swordplay: enchanted melee
        [110]  = [AnyWeaponskill()],   // Perfect Balance: one stack per weaponskill
        [3688] = [AnyGcd()],           // Hyperphantasia: one stack per pictomancy GCD
    };

    // (job, Action sheet PrimaryCostType) → the gauges an action spends; PrimaryCostValue is the
    // amount. Cost-type ids aren't unique across jobs (30 is Aetherflow for SCH and SMN), so the key
    // includes the job, and only the job that owns each scalar gauge is listed. Non-scalar costs
    // (Sen, Nadi, canvas, attunement) and non-gauge ones (MP, statuses, cards) are absent.
    internal static readonly Dictionary<(Job Job, uint CostType), ResourceGauge[]> CostGauges = new()
    {
        [(Job.Warrior, 22u)] = [WarBeast], [(Job.DarkKnight, 25u)] = [DrkBlood], [(Job.Ninja, 27u)] = [NinNinki], [(Job.Monk, 28u)] = [MnkChakra],
        [(Job.Scholar, 30u)] = [SchAetherflow], [(Job.Samurai, 39u)] = [SamKenki], [(Job.RedMage, 43u)] = [RdmWhite, RdmBlack],  // RDM: N of each colour
        [(Job.Dancer, 53u)] = [DncFeathers], [(Job.Dancer, 54u)] = [DncEsprit], [(Job.Gunbreaker, 55u)] = [GnbCartridge],
        [(Job.WhiteMage, 56u)] = [WhmBloodLily], [(Job.WhiteMage, 57u)] = [WhmLily], [(Job.Bard, 59u)] = [BrdSoulVoice],
        [(Job.Machinist, 61u)] = [MchHeat], [(Job.Machinist, 62u)] = [MchBattery], [(Job.Samurai, 63u)] = [SamMeditation],
        [(Job.Reaper, 64u)] = [RprSoul], [(Job.Reaper, 65u)] = [RprShroud], [(Job.Reaper, 66u)] = [RprLemureShroud], [(Job.Reaper, 67u)] = [RprVoidShroud],
        [(Job.Sage, 68u)] = [SgeAddersgall], [(Job.Sage, 69u)] = [SgeAddersting], [(Job.Dragoon, 75u)] = [DrgFocus],
        [(Job.Viper, 87u)] = [VprRattlingCoil], [(Job.Viper, 88u)] = [VprSerpentOffering], [(Job.Viper, 89u)] = [VprAnguineTribute], [(Job.Viper, 90u)] = [VprAnguineTribute], [(Job.Pictomancer, 91u)] = [PctPalette],
        [(Job.BlackMage, 23u)] = [BlmPolyglot],   // Foul / Xenoglossy spend 1 Polyglot
        [(Job.Paladin, 41u)] = [PldOath],         // Sheltron / Holy Sheltron / Intervention / Cover
    };

    internal static readonly TimedGauge[] TimedGauges =
    [
        new(Job.WhiteMage, WhmLily, 20f, decay: false, timerField: WhmLilyTimer),        // Healing Lily: +1 every 20s
        new(Job.Sage, SgeAddersgall, 20f, decay: false, timerField: SgeAddersgallTimer), // Addersgall: +1 every 20s
        new(Job.Gunbreaker, GnbComboStep, 30f, decay: true),    // Gnashing Fang combo: resets to 0 after 30s (no visual timer field)
        new(Job.Paladin, PldConfiteorStep, 30f, decay: true), // Confiteor route: safety reset if abandoned mid-combo
        new(Job.DarkKnight, DrkDeliriumStep, 30f, decay: true),  // Scarlet Delirium route: safety reset if abandoned mid-combo
        new(Job.BlackMage, BlmPolyglot, 30f, decay: false),     // Polyglot: +1/30s (real gen needs active Enochian; approximated as always-on in-sim)
    ];

    // An action the server answers with a second one of its own, this long after.
    internal static readonly Dictionary<uint, (uint ActionId, float Delay)> FollowUps = new()
    {
        [16193] = (33215, 0.6f), [16194] = (33216, 0.6f), [16195] = (33217, 0.6f), [16196] = (33218, 0.6f),   // Technical Finish → its party buff
    };

    // action → the status that makes it free, spent instead of its gauge cost.
    internal static readonly Dictionary<uint, ushort> CostWaivedBy = new()
    {
        [34626] = 3671,   // Reawaken under Ready to Reawaken
    };

    // The animation lock the server sends where it isn't the usual 0.6 s.
    internal static readonly Dictionary<uint, float> AnimationLocks = new()
    {
        // Dashes, jumps and backsteps:
        [92] = 0.8f, [94] = 0.8f, [96] = 0.8f, [112] = 0.8f, [155] = 0.8f, [2262] = 0.8f, [7419] = 0.8f, [7493] = 0.8f,
        [7515] = 0.8f, [16478] = 0.8f, [24401] = 0.8f, [24403] = 0.8f, [34684] = 0.8f, [44898] = 0.8f, [24295] = 0.7f, [37008] = 0.7f,
        [2259] = 0.35f, [2261] = 0.35f, [2263] = 0.35f, [18805] = 0.35f, [18806] = 0.35f, [18807] = 0.35f,   // Mudras
        [16146] = 0.7f, [16147] = 0.5f, [16150] = 0.77f,   // Gnashing Fang, Savage Claw, Wicked Talon
        [25753] = 1.15f, [16480] = 1.5f, [25835] = 0.75f,   // Primal Rend, Stardiver, Crimson Cyclone
        [199] = 3.86f, [4240] = 3.86f, [4241] = 3.86f, [17105] = 3.86f,   // Tank limit break III
        [33215] = 0.1f, [33216] = 0.1f, [33217] = 0.1f, [33218] = 0.1f, [27524] = 0.1f, [34682] = 0.11f,   // follow-ups
    };

    public static bool Knows(uint actionId) => Actions.ContainsKey(actionId);

    public static IEnumerable<uint> ActionIds => Actions.Keys;

    // Every status the action grants, wherever it lands, with its duration.
    public static IReadOnlyList<(ushort StatusId, float Duration)> GrantsOf(uint actionId)
        => Actions.TryGetValue(actionId, out var effects) ? effects.SelectMany(e => e.Grants).ToList() : [];

    // The statuses the action needs on its caster first (Divine Grace for Divine Caress).
    public static IReadOnlyList<ushort> StatusesSpentBy(uint actionId)
        => StatusClearedOnAction.Where(kv => kv.Value.Any(p => p is ActionIdPredicate id && id.Id == actionId))
                                .Select(kv => kv.Key).ToList();

    private static IEffect Gauge(ResourceGauge gauge, int amount) => new GaugeEffect(gauge, amount);
    private static IEffect SetGauge(ResourceGauge gauge, int value) => new SetGaugeEffect(gauge, value);
    private static IEffect Status(ushort status, float duration, int stacks = 0) => new StatusEffect(status, duration, stacks);
    private static IEffect TargetStatus(ushort status, float duration, int stacks = 0, float shieldScale = 1f) => new TargetStatusEffect(status, duration, stacks, shieldScale);
    private static IEffect EnemyStatus(ushort status, float duration, int stacks = 0) => new EnemyStatusEffect(status, duration, stacks);
    private static IEffect PartyStatus(ushort status, float duration, float range, float shieldScale = 1f) => new PartyStatusEffect(status, duration, range, shieldScale);
    private static IEffect OthersStatus(ushort status, float duration, int stacks = 0) => new OthersStatusEffect(status, duration, stacks);
    private static IEffect TargetRemove(params ushort[] statuses) => new TargetRemoveEffect(statuses);
    private static IEffect Extend(ushort status, float seconds, float cap, bool grantIfMissing = false) => new ExtendEffect(status, seconds, cap, grantIfMissing);
    private static IEffect Form(ushort status) => new IfCasterLackedEffect(110, [Status(status, 30f)]);
    private static IEffect IfCasterHad(ushort status, params IEffect[] inner) => new IfCasterHadEffect(status, inner);
    private static IEffect GroundZone(ushort status, float radius, float duration) => new GroundZoneEffect(status, radius, duration);
    private static IEffect IfCasterHas(ushort status, params IEffect[] inner) => new IfCasterHasEffect(status, inner);
    private static IEffect Boost(ushort status, float factor, params IEffect[] inner) => new BoostEffect(status, factor, inner);
    private static IEffect ScaleByStacks(ushort status, float[] scaleByStacks, params IEffect[] inner) => new ScaleByStacksEffect(status, scaleByStacks, inner);
    private static IEffect Combo(params IEffect[] inner) => new ComboEffect(inner);
    private static IEffect Random(float chance, params IEffect[] inner) => new RandomEffect(chance, inner);

    private static IActionPredicate ActionId(uint id) => new ActionIdPredicate(id);
    private static IActionPredicate AnyWeaponskill() => new AnyWeaponskillPredicate();
    private static IActionPredicate AnyGcd() => new AnyGcdPredicate();
}
