using System;
using Verse;

namespace RandomFactions;

/// <summary>
///     Lightweight logger wrapper for RimWorld mods, similar to HugsLib's ModLogger.
/// </summary>
public class ModLogger
{
    private readonly string modName;

    public ModLogger(string modName)
    {
        this.modName = modName ?? "UnknownMod";
    }

    /// <summary>
    ///     Log an error to the RimWorld log.
    /// </summary>
    public void Error(string message)
    {
        Log.Error($"[{modName}] {message}");
    }

    /// <summary>
    ///     Log an exception.
    /// </summary>
    public void Exception(Exception ex, string context = null)
    {
        var prefix = context != null ? $"[{modName}] [{context}] " : $"[{modName}] ";
        Log.Error($"{prefix}Exception: {ex}");
    }

    /// <summary>
    ///     Log a normal message to the RimWorld log.
    /// </summary>
    public void Message(string message)
    {
        Log.Message($"[{modName}] {message}");
    }

    /// <summary>
    ///     Log a verbose debug message.
    /// </summary>
    public void Trace(string message)
    {
        if (RandomFactionsMod.SettingsInstance.verboseLogging)
        {
            Log.Message($"[{modName}][TRACE] {message}");
        }
    }

    /// <summary>
    ///     Log a warning to the RimWorld log.
    /// </summary>
    public void Warning(string message)
    {
        Log.Warning($"[{modName}] {message}");
    }
}