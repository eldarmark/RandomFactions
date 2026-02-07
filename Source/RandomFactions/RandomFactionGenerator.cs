using System;
using System.Collections.Generic;
using System.Linq;
using RandomFactions.filters;
using RimWorld;
using Verse;

namespace RandomFactions;

public class RandomFactionGenerator
{
    public static readonly HashSet<string> XenotypeOnlyFactionDefNames =
    [
        "TribeRoughNeanderthal",
        "PirateYttakin",
        "TribeSavageImpid",
        "OutlanderRoughPig",
        "PirateWaster",
        "Sanguophages",
        // modded xenotype factions
        "OutlanderPapou",
        "HuntersCovenant"
    ];

    private readonly List<FactionDef> definedFactionDefs = [];
    private readonly bool hasBiotech;
    private readonly ModLogger modLogger;
    private readonly string[] modOffBooksFactionDefNames;
    private readonly int percentXeno;
    private readonly Random prng;

    // ========================
    // Core replacement logic
    // ========================

    private string newName;

    public RandomFactionGenerator(int percentXenoFaction, IEnumerable<FactionDef> allFactionDefs,
        string[] offBooksFactionDefNames, bool hasBiotechExpansion, ModLogger logger)
    {
        modLogger = logger;
        percentXeno = percentXenoFaction;
        hasBiotech = hasBiotechExpansion;
        modOffBooksFactionDefNames = offBooksFactionDefNames;

        modLogger.Trace($"RandomFactionGenerator created with percentXeno = {percentXeno}");

        // Seeded PRNG for reproducible world generation
        var seeder = new Random(Find.World.ConstantRandSeed);
        var seedBuffer = new byte[4];
        seeder.NextBytes(seedBuffer);
        var seed = BitConverter.ToInt32(seedBuffer, 0);
        prng = new Random(seed);

        // Build base pool, excluding xenotype-only defs
        foreach (var def in allFactionDefs)
        {
            if (def.categoryTag.EqualsIgnoreCase(RandomFactionsMod.RandomCategoryName))
            {
                continue;
            }

            if (XenotypeOnlyFactionDefNames.Contains(def.defName))
            {
                modLogger.Trace($"Excluding xenotype-only faction def from base pool: {def.defName}");
                continue;
            }

            definedFactionDefs.Add(def);
        }

        modLogger.Trace($"RandomFactionGenerator constructed with {definedFactionDefs.Count} base faction defs.");
    }

    private static int DefaultGoodwill(FactionDef def)
    {
        if (def.categoryTag.EqualsIgnoreCase(RandomFactionsMod.RandomCategoryName))
        {
            return 0;
        }

        if (def.permanentEnemy)
        {
            return -100;
        }

        if (def.naturalEnemy)
        {
            return -80;
        }

        return 0;
    }

    private static int DefaultGoodwill(Faction fac)
    {
        if (fac.def.categoryTag.EqualsIgnoreCase(RandomFactionsMod.RandomCategoryName))
        {
            return 0;
        }

        if (fac.def.permanentEnemy)
        {
            return -100;
        }

        if (fac.def.naturalEnemy)
        {
            return -80;
        }

        return fac.NaturalGoodwill;
    }

    private static List<FactionRelation> DefaultRelations(FactionDef targetDef, IEnumerable<Faction> allFactions)
    {
        var list = new List<FactionRelation>();
        foreach (var fac in allFactions)
        {
            if (fac.IsPlayer)
            {
                continue;
            }

            var gw = Math.Min(DefaultGoodwill(targetDef), DefaultGoodwill(fac));
            var kind = FactionRelationKind.Neutral;
            if (gw <= -80)
            {
                kind = FactionRelationKind.Hostile;
            }
            else if (gw >= 80)
            {
                kind = FactionRelationKind.Ally;
            }

            list.Add(new FactionRelation(fac, kind) { baseGoodwill = gw });
        }

        return list;
    }

    // ========================
    // Draw a faction def (pre-created only)
    // ========================

    private FactionDef DrawRandomFactionDef(List<FactionDef> factionDefs, IEnumerable<Faction> existingFactions)
    {
        if (factionDefs.Count == 0)
        {
            return null;
        }

        // Pick base faction respecting maxConfigurableAtWorldCreation
        FactionDef baseDef = null;
        var existingArray = existingFactions.ToArray();
        var limit = 100;
        while (--limit > 0)
        {
            baseDef = factionDefs[prng.Next(factionDefs.Count)];
            var count = existingArray.Count(f => f.def == baseDef);
            if (baseDef.maxConfigurableAtWorldCreation <= 0 || count < baseDef.maxConfigurableAtWorldCreation)
            {
                break;
            }
        }

        if (baseDef == null)
        {
            modLogger.Error("DrawRandomFactionDef failed to select a baseDef.");
            return null;
        }

        modLogger.Trace($"Selected base faction def: {baseDef.defName}");

        // Xenotype check
        if (!hasBiotech || !RandomFactionsMod.IsXenotypePatchable(baseDef))
        {
            return baseDef;
        }

        // Respect HardExcludedFactionDefs: never apply xenotype
        if (RandomFactionsMod.HardExcludedFactionDefs.Contains(baseDef.defName))
        {
            modLogger.Trace($"Skipping xenotype: base faction '{baseDef.defName}' is hard-excluded.");
            return baseDef;
        }

        var chanceRoll = prng.NextDouble();
        modLogger.Trace($"Xenotype roll: {chanceRoll:F2} vs chance {percentXeno / 100.0:F2}");

        if (chanceRoll > percentXeno / 100.0)
        {
            modLogger.Trace("Skipping xenotype: Roll failed.");
            return baseDef;
        }

        modLogger.Trace("Roll succeeds, choosing xenotype faction.");

        // Pick a xenotype that exists
        var xenotypeCandidates = DefDatabase<XenotypeDef>.AllDefsListForReading
            .Where(x => !RandomFactionsMod.GloballyBlockedXenotypes.Contains(x.defName))
            .ToList();

        if (xenotypeCandidates.Count == 0)
        {
            return baseDef;
        }

        foreach (var xenotypeDef in xenotypeCandidates.InRandomOrder(prng))
        {
            var xenoDefName = RandomFactionsMod.XenoFactionDefName(xenotypeDef, baseDef);
            if (xenoDefName == null)
            {
                continue;
            }

            var xenoDef = DefDatabase<FactionDef>.GetNamedSilentFail(xenoDefName);
            if (xenoDef == null)
            {
                continue;
            }

            xenoDef.maxConfigurableAtWorldCreation = Math.Max(1, xenoDef.maxConfigurableAtWorldCreation);
            xenoDef.hidden = false;
            modLogger.Trace($"Selected xenotype faction: {xenoDef.defName} replacing {baseDef.defName}");
            return xenoDef;
        }

        return baseDef;
    }

    private static FactionDefFilter duplicateFilter(IEnumerable<Faction> existingFactions, bool allowDuplicates)
    {
        if (allowDuplicates)
        {
            return new FactionDefNoOpFilter();
        }

        var names = existingFactions.Select(f => f.def.defName).ToArray();
        return new FactionDefNameFilter(false, names);
    }


    // ========================
    // Generate faction with relations
    // ========================

    private Faction GenerateFactionFromDef(FactionDef def, IEnumerable<Faction> allFactions)
    {
        try
        {
            return FactionGenerator.NewGeneratedFactionWithRelations(def, DefaultRelations(def, allFactions));
        }
        catch (Exception ex)
        {
            modLogger.Error($"Exception generating faction from def {def.defName}: {ex}");
            return null;
        }
    }

    private Faction GenerateRandomFaction(List<FactionDef> defs, Faction oldFaction, bool allowDuplicates,
        params FactionDefFilter[] filters)
    {
        var existingFactions = Find.World.factionManager.AllFactions.ToList();
        modLogger.Trace(
            $"Generating random faction to replace {oldFaction?.def.defName ?? "null"}, allowDuplicates={allowDuplicates}. Existing factions: {existingFactions.Count}");

        var failedDefs = new HashSet<FactionDef>();
        var attempt = 0;

        while (true)
        {
            attempt++;

            var filterDefs = FactionDefFilter.FilterFactionDefs(defs, filters).ToList();
            modLogger.Trace($"Attempt {attempt}: {filterDefs.Count} defs after filtering.");

            if (filterDefs.Count == 0)
            {
                modLogger.Error("Filter collapse: no faction defs survived filtering.");
                return null;
            }

            if (failedDefs.Count > 0)
            {
                filterDefs.RemoveAll(fd => failedDefs.Contains(fd));
                if (filterDefs.Count == 0)
                {
                    modLogger.Error("All candidate defs previously failed — aborting generation.");
                    return null;
                }
            }

            var chosenDef = DrawRandomFactionDef(filterDefs, existingFactions);
            if (chosenDef == null)
            {
                modLogger.Error("DrawRandomFactionDef returned null — aborting generation.");
                return null;
            }

            var faction = GenerateFactionFromDef(chosenDef, existingFactions);
            if (faction != null)
            {
                modLogger.Trace($"Successfully generated faction: {faction.Name} ({faction.def.defName})");
                return faction;
            }

            failedDefs.Add(chosenDef);

            if (attempt <= 20)
            {
                continue;
            }

            modLogger.Error("Exceeded 20 attempts to generate a faction; returning null.");
            return null;
        }
    }

    private Faction RandomEnemyFaction(Faction oldFaction, bool allowDuplicates)
    {
        return GenerateRandomFaction(definedFactionDefs, oldFaction, allowDuplicates,
            new PlayerFactionDefFilter(false),
            new HiddenFactionDefFilter(false),
            new FactionDefNameFilter(false, modOffBooksFactionDefNames),
            new PermanentEnemyFactionDefFilter(true),
            duplicateFilter(Find.World.factionManager.AllFactions, allowDuplicates));
    }

    private Faction RandomNamedFaction(Faction oldFaction, bool allowDuplicates, params string[] nameList)
    {
        return GenerateRandomFaction(definedFactionDefs, oldFaction, allowDuplicates,
            new PlayerFactionDefFilter(false),
            new FactionDefNameFilter(false, modOffBooksFactionDefNames),
            new FactionDefNameFilter(nameList),
            duplicateFilter(Find.World.factionManager.AllFactions, allowDuplicates));
    }

    private Faction RandomNeutralFaction(Faction oldFaction, bool allowDuplicates)
    {
        return GenerateRandomFaction(definedFactionDefs, oldFaction, allowDuplicates,
            new PlayerFactionDefFilter(false),
            new HiddenFactionDefFilter(false),
            new FactionDefNameFilter(false, modOffBooksFactionDefNames),
            new PermanentEnemyFactionDefFilter(false),
            new NaturalEnemyFactionDefFilter(false),
            duplicateFilter(Find.World.factionManager.AllFactions, allowDuplicates));
    }

    // ========================
    // Random faction selection
    // ========================

    private Faction RandomNpcFaction(Faction oldFaction, bool allowDuplicates)
    {
        return GenerateRandomFaction(definedFactionDefs, oldFaction, allowDuplicates,
            new PlayerFactionDefFilter(false),
            new HiddenFactionDefFilter(false),
            new FactionDefNameFilter(false, modOffBooksFactionDefNames),
            duplicateFilter(Find.World.factionManager.AllFactions, allowDuplicates));
    }

    private Faction RandomRoughFaction(Faction oldFaction, bool allowDuplicates)
    {
        return GenerateRandomFaction(definedFactionDefs, oldFaction, allowDuplicates,
            new PlayerFactionDefFilter(false),
            new HiddenFactionDefFilter(false),
            new FactionDefNameFilter(false, modOffBooksFactionDefNames),
            new PermanentEnemyFactionDefFilter(false),
            new NaturalEnemyFactionDefFilter(true),
            duplicateFilter(Find.World.factionManager.AllFactions, allowDuplicates));
    }

    private void ReplaceFactionInternal(Faction oldFaction, Faction newFaction)
    {
        if (oldFaction == null)
        {
            modLogger.Warning("ReplaceFactionInternal called with null oldFaction. Aborting.");
            return;
        }

        if (newFaction == null)
        {
            modLogger.Warning(
                $"Failed to generate a new faction to replace {oldFaction.Name} ({oldFaction.def.defName}). Retaining old faction.");
            return;
        }

        //Because I can't resist

        //shoulda exposed this to XML, maybe next time :)
        var newSpecialFactionNames = new List<string> { "Zal's Irregulars", "Mlie's Marauders", "Wolf's Dragoons" };

        // Only apply to Pirate factions
        if (newFaction.def.defName.Contains("Pirate"))
        {
            // 1% chance roll
            if (Rand.Value < 0.01f)
            {
                var chosenName = newSpecialFactionNames.RandomElement();
                modLogger.Trace($"Renaming faction {newFaction.Name} to {chosenName} - 1% chance");
                newFaction.Name = chosenName;
            }
        }


        modLogger.Trace(
            $"Replacing faction {oldFaction.Name} ({oldFaction.def.defName}) with {newFaction.Name} ({newFaction.def.defName})");

        // Reassign settlements
        HashSet<string> usedNames = [];

        //shoulda exposed this to XML, maybe next time :)
        var newSpecialSettlementNames = new List<string>
        {
            "Zal's Rest",
            "Discovery Park",
            "Serenity Valley",
            "Thunderdome",
            "Vo Wacune",
            "Hadley's Hope",
            "Dehra Dun",
            "Cave of Ridges",
            "Hotel California",
            "Paradise City",
            "Shangri-La",
            "Outside-the-Asylum",
            "Sietch Tabr",
            "Night City",
            "The Hermitage",
            "Pointe du Lac",
            "Hill Valley",
            "Lankhmar",
            "Strana Mechty",
            "Onn"
        };
        var reassignedCount = 0;
        foreach (var stl in Find.WorldObjects.Settlements)
        {
            if (stl.Faction != oldFaction)
            {
                continue;
            }

            stl.SetFaction(newFaction);
            reassignedCount++;

            if (Rand.Value < 0.01f)
            {
                newName = newSpecialSettlementNames.RandomElement();
                modLogger.Trace($"Using special name - {newName} - 1% chance");
            }
            else
            {
                newName = SettlementNameUtility.GenerateSettlementNameForFaction(newFaction.def, usedNames);
            }

            if (newName.NullOrEmpty())
            {
                continue;
            }

            modLogger.Trace($"Renaming settlement '{stl.Label}' → '{newName}' for faction {newFaction.Name}");
            stl.Name = newName;
            usedNames.Add(newName);
        }

        modLogger.Trace($"Total settlements reassigned from {oldFaction.Name} to {newFaction.Name}: {reassignedCount}");

        // Mark old faction defeated & hidden
        oldFaction.defeated = true;
        oldFaction.def.hidden = true;
        modLogger.Trace($"Old faction {oldFaction.Name} marked defeated and hidden.");

        // Add new faction if not already present
        if (Find.World.factionManager.AllFactions.Contains(newFaction))
        {
            return;
        }

        Find.World.factionManager.Add(newFaction);
        modLogger.Trace($"New faction {newFaction.Name} added to the world.");
    }

    public void ReplaceWithRandomNamedFaction(Faction faction, bool allowDuplicates, params string[] validDefNames)
    {
        ReplaceFactionInternal(faction, RandomNamedFaction(faction, allowDuplicates, validDefNames));
    }

    public void ReplaceWithRandomNonHiddenEnemyFaction(Faction faction, bool allowDuplicates)
    {
        ReplaceFactionInternal(faction, RandomEnemyFaction(faction, allowDuplicates));
    }

    // ========================
    // Public replacement methods
    // ========================

    public void ReplaceWithRandomNonHiddenFaction(Faction faction, bool allowDuplicates)
    {
        ReplaceFactionInternal(faction, RandomNpcFaction(faction, allowDuplicates));
    }

    public void ReplaceWithRandomNonHiddenTraderFaction(Faction faction, bool allowDuplicates)
    {
        ReplaceFactionInternal(faction, RandomNeutralFaction(faction, allowDuplicates));
    }

    public void ReplaceWithRandomNonHiddenWarlordFaction(Faction faction, bool allowDuplicates)
    {
        ReplaceFactionInternal(faction, RandomRoughFaction(faction, allowDuplicates));
    }

    private static class SettlementNameUtility
    {
        public static string GenerateSettlementNameForFaction(FactionDef def, HashSet<string> usedNames)
        {
            return def?.settlementNameMaker == null
                ? null
                : NameGenerator.GenerateName(def.settlementNameMaker, usedNames);
        }
    }
}