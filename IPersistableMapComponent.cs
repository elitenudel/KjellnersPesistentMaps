namespace KjellnersPersistentMaps
{
    /// <summary>
    /// Implement this interface on a MapComponent to opt in to PersistentMaps tile XML serialization.
    ///
    /// When a settlement is abandoned, the component's ExposeData() is called within the same
    /// Scribe saving session as savedThings.  On restore, ExposeData() participates in the same
    /// Scribe loading session, so cross-references work as follows:
    ///
    ///   - Scribe_References to Things in savedThings (buildings, items, plants, wildlife)
    ///     resolve correctly — those objects are in the same loadedObjectDirectory.
    ///   - Scribe_References to world-level objects (factions, ideologies, world pawns) also
    ///     resolve correctly via RefInjector's pre-registration pass.
    ///
    /// Do NOT reference Things that are excluded from savedThings (humanlike pawns, corpses,
    /// etc.) — their IDs are absent from the session and will resolve to null.
    ///
    /// If the mod is removed after a save was made, the component is silently skipped on
    /// restore and the component's fields remain at their freshly-constructed defaults.
    /// </summary>
    public interface IPersistableMapComponent { }
}
