using Verse;

namespace RandomFactions;

public class RandomFactionsSettings : ModSettings
{
    public bool allowDuplicates;
    public bool removeOtherFactions = true;
    public bool verboseLogging;
    public int xenoPercent = 15;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref removeOtherFactions, "removeOtherFactions", true);
        Scribe_Values.Look(ref allowDuplicates, "allowDuplicates");
        Scribe_Values.Look(ref xenoPercent, "xenoPercent", 15);
        Scribe_Values.Look(ref verboseLogging, "verboseLogging");
    }
}