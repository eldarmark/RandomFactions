using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RandomFactions;

public class RandomFactionsWorldComponent : WorldComponent
{
    private bool initialized;

    public RandomFactionsWorldComponent(World world) : base(world)
    {
        initialized = false;
    }

    public override void WorldComponentTick()
    {
        if (initialized)
        {
            return;
        }

        // Make sure the world exists
        if (Find.World == null)
        {
            return;
        }

        initialized = true;

        var mod = LoadedModManager.GetMod<RandomFactionsMod>();
        if (mod == null)
        {
            Log.Error("[RandomFactions] RandomFactionsMod instance not found!");
            return;
        }

        var logger = mod.Logger;
        var settings = RandomFactionsMod.SettingsInstance;

        logger.Trace("World loaded! Running RandomFactionGenerator.");

        // Prepare faction generator
        var allDefs = DefDatabase<FactionDef>.AllDefs;
        var offBooks = Array.Empty<string>(); // replace with your off-books if needed
        var generator = new RandomFactionGenerator(
            settings.xenoPercent,
            allDefs,
            offBooks,
            ModsConfig.BiotechActive,
            logger);

        // Replace all random factions in the world
        foreach (var faction in Find.FactionManager.AllFactions.ToList())
        {
            if (faction.def.categoryTag.EqualsIgnoreCase(RandomFactionsMod.RandomCategoryName) && !faction.defeated)
            {
                generator.ReplaceWithRandomNonHiddenFaction(faction, settings.allowDuplicates);
            }
        }

        logger.Trace("Random faction replacement complete.");
    }
}