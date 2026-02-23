using RimWorld;
using RimWorld.Planet;
using Verse;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KjellnersPersistentMaps
{
    public static class PersistentMapSerializer
    {
        public static bool IsRestoring = false;

        // -------------------------
        // Save
        // -------------------------

        public static void SaveMap(Map map, PlanetTile tile)
        {
            string folder = GetPersistentFolder();
            if (string.IsNullOrEmpty(folder))
                return;

            string file = Path.Combine(folder, $"Tile_{tile.tileId}.xml");

            try
            {
                var data = new PersistentMapData();
                data.abandonedAtTick = Find.TickManager.TicksGame;

                SerializeGridData(map, data);

                var wc = Find.World.GetComponent<WorldComponent_PersistentMaps>();
                var record = wc.GetOrCreate(tile.tileId);
                record.parkedPawns.Clear();
                record.cryoPawns.Clear();

                int cryoCount = ExtractCryoColonists(map, record);
                if (cryoCount > 0)
                    KLog.Message($"[PersistentMaps] Moved {cryoCount} cryo colonists to WorldPawns(KeepForever) for tile {tile.tileId}");

                // Build savedThings while everything is still spawned.
                // Caskets are empty (cryo colonists already moved to WorldPawns above).
                // Wildlife is included because ShouldPersistThing allows non-humanlike pawns.
                data.savedThings = map.listerThings.AllThings
                    .Where(PersistentMapData.ShouldPersistThing)
                    .ToList();

                Scribe.saver.InitSaving(file, "PersistentMap");
                Scribe.mode = LoadSaveMode.Saving;
                Scribe_Deep.Look(ref data, "MapData");
                SaveOptInComponents(map);
                Scribe.saver.FinalizeSaving();

                // DeSpawn wildlife and clear faction in one pass after saving.
                // DeSpawn removes them from map.mapPawns before DeinitAndRemoveMap runs
                // (prevents pawnsDead ID collisions on next restore).
                // SetFaction(null) prevents RimWorld's faction cleanup from parking the
                // DeSpawned animals in WorldPawns (which would also cause ID collisions).
                int wildlifeCount = DeSpawnAndClearWildlife(data.savedThings);
                KLog.Message($"[PersistentMaps] Deep-saving {wildlifeCount} wildlife for tile {tile.tileId}");

                KLog.Message($"[PersistentMaps] Saved persistent map to {file}");
            }
            catch (Exception e)
            {
                KLog.Error($"[PersistentMaps] Failed saving map: {e}");
            }
            finally
            {
                Scribe.mode = LoadSaveMode.Inactive;
            }
        }

        // Serializes all grid layers (terrain, roof, snow, pollution, fog) into data.
        private static void SerializeGridData(Map map, PersistentMapData data)
        {
            data.terrainData = MapSerializeUtility.SerializeUshort(
                map, c => (ushort)map.terrainGrid.TerrainAt(c).shortHash);

            data.roofData = MapSerializeUtility.SerializeUshort(
                map, c => (ushort)(map.roofGrid.RoofAt(c)?.shortHash ?? 0));

            data.snowData = MapSerializeUtility.SerializeByte(
                map, c => (byte)map.snowGrid.GetDepth(c));

            if (ModsConfig.BiotechActive && map.pollutionGrid != null)
                data.pollutionData = MapSerializeUtility.SerializeByte(
                    map, c => map.pollutionGrid.IsPolluted(c) ? (byte)1 : (byte)0);

            data.fogData = MapSerializeUtility.SerializeByte(
                map, c => map.fogGrid.IsFogged(c) ? (byte)1 : (byte)0);
        }

        // Pulls player colonists out of cryptosleep caskets → WorldPawns(KeepForever)
        // so they live in the main .rws save (faction, relations, ideo roles preserved).
        // Their casket position is recorded for re-insertion on restore.
        // Must run BEFORE building savedThings so caskets are empty in our XML.
        private static int ExtractCryoColonists(Map map, TileRecord record)
        {
            int count = 0;
            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (!(t is Building_Casket casket)) continue;
                ThingOwner held = casket.GetDirectlyHeldThings();
                for (int i = held.Count - 1; i >= 0; i--)
                {
                    if (!(held[i] is Pawn cp) || cp.Faction != Faction.OfPlayer) continue;
                    held.Remove(cp);
                    Find.WorldPawns.PassToWorld(cp, PawnDiscardDecideMode.KeepForever);
                    record.cryoPawns.Add(new CryoPawnRecord { pawn = cp, position = casket.Position });
                    count++;
                }
            }
            return count;
        }

        // DeSpawns wildlife and clears their faction in a single pass after XML is written.
        // Safe to run after FinalizeSaving: pawn data is already captured; DeinitAndRemoveMap
        // hasn't run yet (it fires when our Prefix returns to vanilla Abandon).
        private static int DeSpawnAndClearWildlife(List<Thing> savedThings)
        {
            int count = 0;
            foreach (Thing t in savedThings)
            {
                if (!(t is Pawn wp) || wp.RaceProps.Humanlike) continue;
                wp.DeSpawn(DestroyMode.Vanish);
                if (wp.Faction != null) wp.SetFaction(null);
                count++;
            }
            return count;
        }

        // -------------------------
        // Load
        // -------------------------

        public static void LoadSavedMap(Map map, PlanetTile tile)
        {
            string folder = GetPersistentFolder();
            if (string.IsNullOrEmpty(folder))
                return;

            string file = Path.Combine(folder, $"Tile_{tile.tileId}.xml");
            if (!File.Exists(file))
                return;

            try
            {
                IsRestoring = true;

                WipeMapgenThings(map);

                Scribe.loader.InitLoading(file);
                Scribe.mode = LoadSaveMode.LoadingVars;
                PersistentMapData data = null;
                Scribe_Deep.Look(ref data, "MapData");

                if (data == null)
                {
                    KLog.Error("[PersistentMaps] Loaded data is null.");
                    return;
                }

                // Must be called before FinalizeScribeLoadState so curXmlParent is still valid.
                LoadOptInComponents(map);

                ApplyTerrainData(map, data);

                // Register live game objects as cross-ref targets so references in our
                // fragment XML (faction, ideo, master, etc.) resolve into the live game.
                RefInjector.PreRegisterActiveGame();

                // Replicate the XML cursor teardown FinalizeLoading() would do, without
                // triggering its monolithic resolve+init sequence. This lets us insert
                // RefInjector between LoadingVars and ResolveAllCrossReferences.
                FinalizeScribeLoadState();

                KLog.Message($"[PersistentMaps] crossReferencingExposables count: {Scribe.loader.crossRefs.crossReferencingExposables.Count}");
                Scribe.loader.crossRefs.ResolveAllCrossReferences();

                Scribe.mode = LoadSaveMode.PostLoadInit;
                Scribe.loader.initer.DoAllPostLoadInits();
                Scribe.mode = LoadSaveMode.Inactive;

                var context = BuildDecayContext(data, map, tile);

                RemoveWorldPawnGhosts(data.savedThings);
                SpawnSavedThings(map, data.savedThings);
                RestoreWorldComponentPawns(map, tile);
                ApplyMapGridData(map, data, context);

                KLog.Message($"[PersistentMaps] Applied saved data for tile {tile}");
            }
            catch (Exception e)
            {
                KLog.Error($"[PersistentMaps] Failed applying saved data: {e}");
            }
            finally
            {
                IsRestoring = false;
                Scribe.mode = LoadSaveMode.Inactive;
            }
        }

        // Destroys or DeSpawns mapgen things that our XML will replace.
        // Pawns are DeSpawned (not Destroyed) to avoid pawnsDead pollution.
        // Casket inner pawns are drained before Destroy to prevent ClearAndDestroyContents
        // from sending world-pawn IDs to pawnsDead.
        private static void WipeMapgenThings(Map map)
        {
            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (!PersistentMapData.ShouldPersistThing(t)) continue;

                if (t is Pawn wp)
                {
                    wp.DeSpawn(DestroyMode.Vanish);
                    continue;
                }

                if (t is Building_Casket casket)
                {
                    ThingOwner held = casket.GetDirectlyHeldThings();
                    while (held.Count > 0)
                        held.Remove(held[0]);
                }

                t.Destroy(DestroyMode.Vanish);
            }
        }

        private static void ApplyTerrainData(Map map, PersistentMapData data)
        {
            if (data.terrainData == null) return;
            MapSerializeUtility.LoadUshort(data.terrainData, map, (c, val) =>
            {
                TerrainDef def = DefDatabase<TerrainDef>.GetByShortHash(val);
                if (def != null) map.terrainGrid.SetTerrain(c, def);
            });
        }

        private static void FinalizeScribeLoadState()
        {
            Scribe.loader.curXmlParent = null;
            Scribe.loader.curParent = null;
            Scribe.loader.curPathRelToParent = null;
        }

        private static OfflineDecayContext BuildDecayContext(PersistentMapData data, Map map, PlanetTile tile)
        {
            return new OfflineDecayContext
            {
                map = map,
                startTick = data.abandonedAtTick,
                ticksPassed = Math.Max(0, Find.TickManager.TicksGame - data.abandonedAtTick),
                rainfall = Find.WorldGrid[tile.tileId].rainfall,
                tileId = tile.tileId
            };
        }

        // Removes WorldPawns ghost copies of deep-saved wildlife.
        // DeSpawned animals may drift into WorldPawns between save and restore via
        // faction cleanup; the SetFaction fix in SaveMap prevents this going forward,
        // but this handles any ghosts left by earlier saves.
        private static void RemoveWorldPawnGhosts(List<Thing> savedThings)
        {
            foreach (Thing t in savedThings)
            {
                if (!(t is Pawn savedPawn)) continue;
                string savedId = savedPawn.GetUniqueLoadID();
                Pawn ghost = Find.WorldPawns.AllPawnsAliveOrDead
                    .FirstOrDefault(p => p != savedPawn && p.GetUniqueLoadID() == savedId);
                if (ghost != null)
                    Find.WorldPawns.RemovePawn(ghost);
            }
        }

        private static void SpawnSavedThings(Map map, List<Thing> savedThings)
        {
            foreach (Thing t in savedThings)
            {
                // Drain any existing casket at this position before GenSpawn wipes it.
                // Handles the edge case where an opened ancient danger casket was saved
                // empty but mapgen placed a new casket (with a world pawn) at the same
                // spot. Draining first prevents ClearAndDestroyContents → pawnsDead.
                if (t is Building_Casket)
                {
                    foreach (Thing existing in map.thingGrid.ThingsListAt(t.Position).ToList())
                    {
                        if (existing is Building_Casket existingCasket)
                        {
                            ThingOwner held = existingCasket.GetDirectlyHeldThings();
                            while (held.Count > 0)
                                held.Remove(held[0]);
                        }
                    }
                }

                GenSpawn.Spawn(t, t.Position, map, t.Rotation, WipeMode.Vanish, respawningAfterLoad: true);
            }
        }

        private static void RestoreWorldComponentPawns(Map map, PlanetTile tile)
        {
            var wc = Find.World.GetComponent<WorldComponent_PersistentMaps>();
            var record = wc?.TryGet(tile.tileId);
            if (record == null) return;

            // Legacy: saves before wildlife deep-save parked animals in WorldPawns.
            int legacyRestored = 0;
            foreach (Pawn p in record.parkedPawns.ToList())
            {
                if (p == null || p.Destroyed) continue;
                Find.WorldPawns.RemovePawn(p);
                IntVec3 pos = p.Position;
                if (!pos.InBounds(map) || !pos.Standable(map))
                    pos = RCellFinder.RandomAnimalSpawnCell_MapGen(map);
                GenSpawn.Spawn(p, pos, map, p.Rotation, WipeMode.Vanish, respawningAfterLoad: true);
                legacyRestored++;
            }
            if (legacyRestored > 0)
                KLog.Message($"[PersistentMaps] (legacy) Restored {legacyRestored} parkedPawns from WorldPawns on tile {tile.tileId}");

            // Re-insert cryo colonists into their caskets.
            int cryoRestored = 0;
            foreach (CryoPawnRecord cryo in record.cryoPawns.ToList())
            {
                if (cryo?.pawn == null || cryo.pawn.Destroyed) continue;
                Find.WorldPawns.RemovePawn(cryo.pawn);

                Building_Casket target = map.thingGrid.ThingsListAt(cryo.position)
                    .OfType<Building_Casket>().FirstOrDefault();

                if (target != null && target.TryAcceptThing(cryo.pawn, allowSpecialEffects: false))
                {
                    // WorldPawns/abandon cycle may have stripped player faction.
                    if (cryo.pawn.Faction != Faction.OfPlayer)
                        cryo.pawn.SetFaction(Faction.OfPlayer);
                    cryoRestored++;
                }
                else
                {
                    KLog.Warning($"[PersistentMaps] No casket at {cryo.position} for {cryo.pawn.Name?.ToStringFull}; spawning free");
                    IntVec3 fallback = cryo.position.Standable(map) ? cryo.position : RCellFinder.RandomAnimalSpawnCell_MapGen(map);
                    GenSpawn.Spawn(cryo.pawn, fallback, map, Rot4.North);
                }
            }
            KLog.Message($"[PersistentMaps] Restored {cryoRestored} cryo colonists into caskets on tile {tile.tileId}");

            wc.Release(tile.tileId);
        }

        // Applies roofs, decay, snow, pollution, and fog after things are spawned.
        private static void ApplyMapGridData(Map map, PersistentMapData data, OfflineDecayContext context)
        {
            if (data.roofData != null)
                ApplyRoofs(map, data);

            foreach (Thing t in map.listerThings.AllThings)
            {
                if (!PersistentMapData.ShouldPersistThing(t)) continue;
                DecayUtility.ApplyDecay(t, context);
            }

            if (data.snowData != null)
                MapSerializeUtility.LoadByte(data.snowData, map,
                    (c, val) => map.snowGrid.SetDepth(c, val));

            if (ModsConfig.BiotechActive && map.pollutionGrid != null && data.pollutionData != null)
                MapSerializeUtility.LoadByte(data.pollutionData, map,
                    (c, val) => map.pollutionGrid.SetPolluted(c, val == 1, silent: true));

            if (data.fogData != null)
            {
                map.fogGrid.Refog(new CellRect(0, 0, map.Size.x, map.Size.z));
                MapSerializeUtility.LoadByte(data.fogData, map, (c, val) =>
                {
                    if (val == 0) map.fogGrid.Unfog(c);
                });
            }
        }

        private static void ApplyRoofs(Map map, PersistentMapData data)
        {
            MapSerializeUtility.LoadUshort(data.roofData, map, (c, val) =>
            {
                if (val == 0) { map.roofGrid.SetRoof(c, null); return; }
                RoofDef def = DefDatabase<RoofDef>.GetByShortHash(val);
                if (def != null) map.roofGrid.SetRoof(c, def);
            });
            RebuildRoofSupport(map);
        }

        private static void RebuildRoofSupport(Map map)
        {
            typeof(RoofGrid)
                .GetMethod("ResolveAllRoofSupport", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(map.roofGrid, null);
            map.roofCollapseBuffer.Clear();
        }

        // -------------------------
        // Helpers
        // -------------------------

        public static bool PersistentFileExists(PlanetTile tile)
        {
            string folder = GetPersistentFolder();
            if (string.IsNullOrEmpty(folder)) return false;
            return File.Exists(Path.Combine(folder, $"Tile_{tile.tileId}.xml"));
        }

        private static string GetPersistentFolder()
        {
            if (Current.Game == null)
            {
                KLog.Error("[PersistentMaps] Current.Game is null.");
                return null;
            }

            var comp = Current.Game.GetComponent<GameComponent_PersistentMaps>();
            if (comp == null)
            {
                KLog.Error("[PersistentMaps] GameComponent_PersistentMaps not found.");
                return null;
            }

            if (string.IsNullOrEmpty(comp.persistentId))
            {
                KLog.Error("[PersistentMaps] persistentId is null or empty.");
                return null;
            }

            string folder = Path.Combine(
                GenFilePaths.SaveDataFolderPath,
                "PersistentMaps",
                comp.persistentId);

            Directory.CreateDirectory(folder);
            return folder;
        }

        // -------------------------
        // Opt-in MapComponent serialization
        // -------------------------

        // Saves each IPersistableMapComponent as a sibling node alongside <MapData> in our XML.
        // Must be called after Scribe_Deep.Look(ref data, "MapData") and before FinalizeSaving.
        private static void SaveOptInComponents(Map map)
        {
            var comps = map.components
                .Where(c => c is IPersistableMapComponent)
                .ToList();

            foreach (MapComponent comp in comps)
            {
                string key = SanitizeTypeName(comp.GetType().FullName);
                var wrapper = new ComponentWrapper(comp);
                Scribe_Deep.Look(ref wrapper, key);
            }

            if (comps.Count > 0)
                KLog.Message($"[PersistentMaps] Saved {comps.Count} opt-in MapComponent(s)");
        }

        // Loads each IPersistableMapComponent from a sibling node in our XML into the
        // matching live instance on the fresh map.  Must be called after
        // Scribe_Deep.Look(ref data, "MapData") and before FinalizeScribeLoadState so that
        // curXmlParent still points at the <PersistentMap> root element.
        //
        // Wrappers are added to crossReferencingExposables and initer by Scribe_Deep, so
        // cross-refs resolve and PostLoadInit fires automatically in the existing restore flow.
        private static void LoadOptInComponents(Map map)
        {
            int count = 0;
            foreach (MapComponent comp in map.components)
            {
                if (!(comp is IPersistableMapComponent)) continue;
                string key = SanitizeTypeName(comp.GetType().FullName);

                // Inject the live instance so ComponentWrapper's parameterless ctor picks it up.
                ComponentWrapper.SetPending(comp);
                ComponentWrapper wrapper = null;
                Scribe_Deep.Look(ref wrapper, key);
                // Clear any unconsumed pending: if the XML node was absent (mod added after this
                // save was made), Scribe_Deep never called new ComponentWrapper(), leaving
                // _pending set and potentially corrupting the next iteration.
                ComponentWrapper.SetPending(null);

                if (wrapper != null) count++;
            }

            if (count > 0)
                KLog.Message($"[PersistentMaps] Loaded {count} opt-in MapComponent(s)");
        }

        // Converts a type's FullName to a valid XML element name.
        // Replaces '+' (nested class separator) and '.' (namespace separator) with '_'.
        private static string SanitizeTypeName(string fullName) =>
            fullName.Replace('+', '_').Replace('.', '_');

        // Thin IExposable wrapper that delegates to a live MapComponent.
        // Scribe_Deep.Look creates instances via new T() during loading; the static _pending
        // field is set immediately before the call so the ctor can capture the live component.
        private class ComponentWrapper : IExposable
        {
            [ThreadStatic]
            private static MapComponent _pending;

            internal static void SetPending(MapComponent comp) => _pending = comp;

            private readonly MapComponent _comp;

            // Used by Scribe_Deep in loading mode (Activator.CreateInstance → parameterless ctor).
            public ComponentWrapper() { _comp = _pending; _pending = null; }

            // Used directly on the save side to avoid relying on the static.
            public ComponentWrapper(MapComponent comp) { _comp = comp; }

            public void ExposeData() => _comp?.ExposeData();
        }
    }
}
