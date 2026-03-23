using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mlie;
using RimWorld;
using UnityEngine;
using Verse;

namespace RandomFactions;

public class RandomFactionsMod : Mod
{
    public const string RandomCategoryName = "Random";
    private const string XenopatchCategoryName = "Xenopatch";

    // exclude specific xenotypes from specific FactionDefs
    private static readonly Dictionary<string, HashSet<string>> BlockedPairs = new()
    {
        { "OutlanderRough", ["Pigskin"] },
        { "OutlanderCivil", ["Papago", "Hunterphage"] },
        { "Pirate", ["Waster", "Yttakin"] },
        { "TribeSavage", ["Impid", "Starjack"] },
        { "TribeRough", ["Neanderthal", "Starjack"] },
        { "TribeCivil", ["Starjack"] },
        { "TribeCannibal", ["Starjack"] },
        { "NudistTribe", ["Starjack"] }
    };

    private static string currentVersion;

    private static readonly Dictionary<(string baseFactionDefName, string xenotype), string> XenotypeFactionOverrides =
        new()
        {
            { ("TribeRough", "Neanderthal"), "TribeRoughNeanderthal" },
            { ("Pirate", "Yttakin"), "PirateYttakin" },
            { ("TribeSavage", "Impid"), "TribeSavageImpid" },
            { ("OutlanderRough", "Pigskin"), "OutlanderRoughPig" },
            { ("PirateRough", "Waster"), "PirateWaster" },
            { ("Sanguophage", "Sanguophage"), "Sanguophages" },
            { ("OutlanderCivil", "Papago"), "OutlanderPapou" },
            { ("OutlanderCivil", "Hunterphage"), "HuntersCovenant" }
        };

    // Xenotype faction creation - now with filtering
    // Easy to extend to allow mod options to blacklist, etc.

    // Xenotypes that should NEVER be used to create new xenotype factions
    public static readonly HashSet<string> GloballyBlockedXenotypes =
    [
        "Baseliner", // handled by the base Defs
        "Highmate"
    ];

    // don't make xenotype factions with these, leads to nonsensical "waster savage impid tribe", etc.
    public static readonly HashSet<string> HardExcludedFactionDefs =
    [
        "TribeRoughNeanderthal",
        "PirateYttakin",
        "TribeSavageImpid",
        "OutlanderRoughPig",
        "PirateWaster",
        "Sanguophages",
        "TradersGuild",
        "Empire",
        //modded regular faction
        "EVA_Faction",
        //modded xenotype faction (this is really going to need to become a mod option)
        "HuntersCovenant", // Hunterphage
        "OutlanderPapou"
    ];

    // don't disable these factions by default, as they are used in various DLC quests and scenarios, disabling breaks functionality
    private static readonly HashSet<string> NotZeroedFactionDefs = new(StringComparer.OrdinalIgnoreCase) { "Empire", "Insect", "TradersGuild" };

    public static RandomFactionsSettings SettingsInstance;

    public readonly ModLogger Logger;

    private readonly Dictionary<string, FactionDef> patchedXenotypeFactions = new();
    private readonly Dictionary<FactionDef, int> randCountRecord = new();
    private readonly Dictionary<FactionDef, int> zeroCountRecord = new();

    public RandomFactionsMod(ModContentPack content) : base(content)
    {
        currentVersion = VersionFromManifest.GetVersionFromModMetaData(content.ModMetaData);
        Logger = new ModLogger("RandomFactionsMod");
        SettingsInstance = GetSettings<RandomFactionsSettings>();

        Logger.Trace("RandomFactionsMod constructed");

        // Run DefsLoaded logic asynchronously so all defs are available
        LongEventHandler.QueueLongEvent(DefsLoaded, "RandomFactions:LoadingDefs", false, null);
    }

    //public static string XenoFactionDefName(XenotypeDef xdef, FactionDef fdef)
    //{
    // Unique name by concatenating the xenotype name and the faction name
    //    if (xdef == null) throw new ArgumentNullException(nameof(xdef));
    //    if (fdef == null) throw new ArgumentNullException(nameof(fdef));

    //    return $"{xdef.defName}_{fdef.defName}";
    //}


    private static string BaseFactionKey(FactionDef def)
    {
        // Use exact defName for key, not substring
        return def.defName;
    }


    private static FactionDef cloneDef(FactionDef def)
    {
        var cpy = new FactionDef();
        reflectionCopy(def, cpy);
        cpy.debugRandomId = (ushort)(def.debugRandomId + 1);
        return cpy;
    }

    private void createXenoFactions()
    {
        Logger.Trace("Starting xenotype faction creation...");

        var newDefs = new List<FactionDef>();
        var violenceCapableXenotypes = getViolenceCapableXenotypes();

        Logger.Trace($"Found {violenceCapableXenotypes.Count} violence-capable xenotypes.");

        foreach (var def in DefDatabase<FactionDef>.AllDefs)
        {
            if (HardExcludedFactionDefs.Contains(def.defName))
            {
                Logger.Trace($"Skipping hard-excluded faction: {def.defName}");
                continue;
            }

            if (!IsXenotypePatchable(def))
            {
                Logger.Trace(
                    $"Skipping non-patchable faction: {def.defName} (hidden={def.hidden}, maxConfig={def.maxConfigurableAtWorldCreation}, isPlayer={def.isPlayer})");
                continue;
            }

            Logger.Trace($"Processing patchable faction: {def.defName}");

            foreach (var xenotypeDef in violenceCapableXenotypes)
            {
                // GLOBAL xenotype exclusion
                if (GloballyBlockedXenotypes.Contains(xenotypeDef.defName))
                {
                    Logger.Trace($" - Globally blocked xenotype '{xenotypeDef.defName}' — skipping.");
                    continue;
                }

                // PAIR exclusion (e.g., OutlanderRough → no Pigskin)
                if (BlockedPairs.TryGetValue(def.defName, out var blockedXenos))
                {
                    if (blockedXenos.Contains(xenotypeDef.defName))
                    {
                        Logger.Trace(
                            $" - Blocked pair: faction '{def.defName}' cannot use xenotype '{xenotypeDef.defName}'");
                        continue;
                    }
                }

                Logger.Trace($" - Applying xenotype: {xenotypeDef.defName}");

                // *** NEW LOGIC: Determine final defName before cloning ***
                var newName = XenoFactionDefName(xenotypeDef, def);

                // null means: override exists → skip generation
                if (newName == null)
                {
                    Logger.Trace(
                        $"   - Skipping creation: existing overridden faction already defined for ({xenotypeDef.defName} + {def.defName})");
                    continue;
                }

                // Check for duplicates early (safety)
                if (DefDatabase<FactionDef>.GetNamedSilentFail(newName) != null)
                {
                    Logger.Warning($"   - WARNING: Attempted to create duplicate faction def '{newName}'. Skipping.");
                    continue;
                }

                // Clone & apply
                var defCopy = cloneDef(def);
                defCopy.defName = newName;
                defCopy.categoryTag = XenopatchCategoryName;
                defCopy.label = $"{xenotypeDef.label} {defCopy.label}";

                // Create XenotypeSet
                var xenoChance = new XenotypeChance(xenotypeDef, 1f);
                var xenotypeChances = new List<XenotypeChance> { xenoChance };
                var newXenoSet = new XenotypeSet();

                foreach (var field in typeof(XenotypeSet).GetFields(
                             BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!field.FieldType.IsAssignableFrom(xenotypeChances.GetType()))
                    {
                        continue;
                    }

                    field.SetValue(newXenoSet, xenotypeChances);
                    Logger.Trace($"   - Set XenotypeSet field '{field.Name}' for {defCopy.defName}");
                }

                defCopy.xenotypeSet = newXenoSet;
                defCopy.maxConfigurableAtWorldCreation = 0;
                defCopy.hidden = true;

                Logger.Trace($"   - Created xenotype faction def: {defCopy.defName}");

                newDefs.Add(defCopy);
            }
        }

        // Add to database
        foreach (var def in newDefs)
        {
            patchedXenotypeFactions[def.defName] = def;
            DefDatabase<FactionDef>.Add(def);
        }

        Logger.Trace($"Created {newDefs.Count} xenotype faction defs (after filtering).");
    }

    private void DefsLoaded()
    {
        Logger.Trace("DefsLoaded: initializing settings and generating xeno factions");

        if (SettingsInstance.removeOtherFactions)
        {
            zeroCountFactionDefs();
        }

        if (ModsConfig.BiotechActive)
        {
            createXenoFactions();
        }

        // Apply faction count settings
        // The XML defines them as 4 / 2 / 2 / 1 (Any / Civil / Rough / Pirate), this allows user changable Faction counts in settings.
        applyFactionCountSettings();

        Logger.Trace("DefsLoaded complete");
    }


    private static List<XenotypeDef> getViolenceCapableXenotypes()
    {
        return DefDatabase<XenotypeDef>.AllDefs
            .Where(x =>
            {
                //To prevent replacing baseliners with baseliners
                if (x == XenotypeDefOf.Baseliner)
                {
                    return false;
                }

                if (x.genes == null)
                {
                    return true;
                }

                var combinedDisabled = WorkTags.None;
                foreach (var gene in x.genes)
                {
                    combinedDisabled |= gene.disabledWorkTags;
                }

                return (combinedDisabled & WorkTags.Violent) == 0;
            })
            .ToList();
    }

    private static void reflectionCopy(object a, object b)
    {
        foreach (var field in a.GetType()
                     .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            field.SetValue(b, field.GetValue(a));
        }
    }

    private void SettingsChanged()
    {
        if (SettingsInstance.removeOtherFactions)
        {
            zeroCountFactionDefs();
        }
        else
        {
            undoZeroCountFactionDefs();
        }
    }

    private void undoZeroCountFactionDefs()
    {
        foreach (var def in zeroCountRecord.Keys)
        {
            def.startingCountAtWorldCreation = zeroCountRecord[def];
        }

        foreach (var def in DefDatabase<FactionDef>.AllDefs)
        {
            if (!RandomCategoryName.EqualsIgnoreCase(def.categoryTag))
            {
                continue;
            }

            randCountRecord[def] = def.startingCountAtWorldCreation;
            def.startingCountAtWorldCreation = 0;
        }
    }

    private void zeroCountFactionDefs()
    {
        foreach (var def in DefDatabase<FactionDef>.AllDefs)
        {
            if (def.hidden || def.isPlayer || RandomCategoryName.EqualsIgnoreCase(def.categoryTag) || NotZeroedFactionDefs.Contains(def.defName))
            {
                continue;
            }

            zeroCountRecord[def] = def.startingCountAtWorldCreation;
            def.startingCountAtWorldCreation = 0;
        }

        foreach (var def in randCountRecord.Keys)
        {
            def.startingCountAtWorldCreation = randCountRecord[def];
        }
    }

    /// <summary>
    /// Apply faction count settings to override XML values
    /// </summary>
    private void applyFactionCountSettings()
    {
        Logger.Trace("Applying faction count settings...");

        // Get the faction defs by their defNames
        var randomFactionDef = DefDatabase<FactionDef>.GetNamedSilentFail("RF_RandomFaction");
        var randomTradeFactionDef = DefDatabase<FactionDef>.GetNamedSilentFail("RF_RandomTradeFaction");
        var randomRoughFactionDef = DefDatabase<FactionDef>.GetNamedSilentFail("RF_RandomRoughFaction");
        var randomPirateFactionDef = DefDatabase<FactionDef>.GetNamedSilentFail("RF_RandomPirateFaction");        

        // Apply settings if the defs exist
        if (randomFactionDef != null)
        {
            randomFactionDef.startingCountAtWorldCreation = SettingsInstance.randomFactionCount;
            Logger.Trace($"Set RF_RandomFaction count to: {SettingsInstance.randomFactionCount}");
        } else {
            Logger.Warning("RF_RandomFaction def not found. Cannot apply randomFactionCount setting.");
        }

        if (randomRoughFactionDef != null)
        {
            randomRoughFactionDef.startingCountAtWorldCreation = SettingsInstance.randomRoughFactionCount;
            Logger.Trace($"Set RF_RandomRoughFaction count to: {SettingsInstance.randomRoughFactionCount}");
        } else {        
            Logger.Warning("RF_RandomRoughFaction def not found. Cannot apply randomRoughFactionCount setting.");
        }

        if (randomPirateFactionDef != null)
        {
            randomPirateFactionDef.startingCountAtWorldCreation = SettingsInstance.randomPirateFactionCount;
            Logger.Trace($"Set RF_RandomPirateFaction count to: {SettingsInstance.randomPirateFactionCount}");
        } else {
            Logger.Warning("RF_RandomPirateFaction def not found. Cannot apply randomPirateFactionCount setting.");
        }

        if (randomTradeFactionDef != null)
        {
            randomTradeFactionDef.startingCountAtWorldCreation = SettingsInstance.randomTradeFactionCount; 
            Logger.Trace($"Set RF_RandomTradeFaction count to: {SettingsInstance.randomTradeFactionCount}");
        } else {
            Logger.Warning("RF_RandomTradeFaction def not found. Cannot apply randomTradeFactionCount setting.");
        }

        Logger.Trace("Faction count settings applied.");
    }

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var listing = new Listing_Standard();
        listing.Begin(inRect);

        listing.CheckboxLabeled(
            "RaFa.reorganiseFactions".Translate(),
            ref SettingsInstance.removeOtherFactions,
            "RaFa.reorganiseFactionsTT".Translate());

        listing.CheckboxLabeled(
            "RaFa.allowDuplicates".Translate(),
            ref SettingsInstance.allowDuplicates,
            "RaFa.allowDuplicatesTT".Translate());

        listing.Label("RaFa.xenotypePercent".Translate() + ": " + SettingsInstance.xenoPercent);
        SettingsInstance.xenoPercent = (int)listing.Slider(SettingsInstance.xenoPercent, 0f, 100f);

        listing.Gap();

        listing.Label("RaFa.randomFactionCount".Translate() + ": " + SettingsInstance.randomFactionCount);
        SettingsInstance.randomFactionCount = (int)listing.Slider(SettingsInstance.randomFactionCount, 0f, 10f);

        listing.Label("RaFa.randomTradeFactionCount".Translate() + ": " + SettingsInstance.randomTradeFactionCount);
        SettingsInstance.randomTradeFactionCount = (int)listing.Slider(SettingsInstance.randomTradeFactionCount, 0f, 10f);

        listing.Label("RaFa.randomRoughFactionCount".Translate() + ": " + SettingsInstance.randomRoughFactionCount);
        SettingsInstance.randomRoughFactionCount = (int)listing.Slider(SettingsInstance.randomRoughFactionCount, 0f, 10f);

        listing.Label("RaFa.randomPirateFactionCount".Translate() + ": " + SettingsInstance.randomPirateFactionCount);
        SettingsInstance.randomPirateFactionCount = (int)listing.Slider(SettingsInstance.randomPirateFactionCount, 0f, 10f);

        listing.Gap();

        listing.CheckboxLabeled(
            "RaFa.verboseLogging".Translate(),
            ref SettingsInstance.verboseLogging,
            "RaFa.verboseLoggingTT".Translate());

        if (currentVersion != null)
        {
            listing.Gap();
            GUI.contentColor = Color.gray;
            listing.Label("RaFa.currentModVersion".Translate(currentVersion));
            GUI.contentColor = Color.white;
        }

        listing.End();

        base.DoSettingsWindowContents(inRect);
    }

    public static bool IsXenotypePatchable(FactionDef def)
    {
        // NEVER patch xenotype-only defs (vanilla or modded)
        if (RandomFactionGenerator.XenotypeOnlyFactionDefNames.Contains(def.defName))
        {
            return false;
        }

        // Don’t patch player or hidden defs
        if (def.isPlayer || def.hidden)
        {
            return false;
        }

        // Don’t patch factions already generated by this mod
        return !RandomCategoryName.EqualsIgnoreCase(def.categoryTag);
        // Otherwise OK
    }

    public override string SettingsCategory()
    {
        return "RaFa.ModName".Translate();
    }

    public override void WriteSettings()
    {
        base.WriteSettings();
        SettingsChanged();
    }

    public static string XenoFactionDefName(XenotypeDef xdef, FactionDef fdef)
    {
        if (xdef == null)
        {
            throw new ArgumentNullException(nameof(xdef));
        }

        if (fdef == null)
        {
            throw new ArgumentNullException(nameof(fdef));
        }

        var xenotype = xdef.defName;
        var baseKey = BaseFactionKey(fdef);

        // Check for override-based canonical factions
        if (!XenotypeFactionOverrides.TryGetValue((baseKey, xenotype), out var mapped))
        {
            return $"{xenotype}_{fdef.defName}";
        }

        // If the mapped def exists already — DO NOT CREATE A DUPLICATE
        return DefDatabase<FactionDef>.GetNamedSilentFail(mapped) != null
            ?
            // Signal caller: "skip creation"
            null
            : mapped;

        // Default generated name
    }
}
