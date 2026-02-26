using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
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
                record.casketPawns.Clear();
                record.playerAnimalPawns.Clear();
                record.worldCreaturePawns.Clear();

                ExtractAllCasketOccupants(map, record);

                int animalCount = ExtractPlayerAnimals(map, record);
                if (animalCount > 0)
                    KLog.Message($"[PersistentMaps] Moved {animalCount} player animals to WorldPawns(KeepForever) for tile {tile.tileId}");

                int creatureCount = ExtractNonPlayerWorldPawnCreatures(map, record);
                if (creatureCount > 0)
                    KLog.Message($"[PersistentMaps] Deep-saved {creatureCount} WorldPawn creatures to WorldComponent for tile {tile.tileId}");

                // Collect lords that own non-player non-humanlike pawns (mech cluster lords etc.).
                // Saved in the same XML document as savedThings so ownedPawns cross-refs resolve.
                data.savedLords = map.lordManager.lords
                    .Where(l => l.ownedPawns.Any(p => p != null && p.Spawned && !p.RaceProps.Humanlike && p.Faction != Faction.OfPlayer))
                    .ToList();
                if (data.savedLords.Count > 0)
                    KLog.Message($"[PersistentMaps] Saving {data.savedLords.Count} lord(s) with their pawns for tile {tile.tileId}");

                // Build savedThings while everything is still spawned.
                // Caskets are empty (all occupants extracted to WorldComponent above).
                // Wild animals are included; player animals have been DeSpawned above.
                // Faction mechs (owned by savedLords) are included here too.
                data.savedThings = map.listerThings.AllThings
                    .Where(PersistentMapData.ShouldPersistThing)
                    .ToList();

                // Corpse.innerContainer uses LookMode.Reference — the InnerPawn is NOT
                // deep-saved in our XML, only referenced by load ID. The normal GC
                // protection ("CorpseExists") disappears when DeinitAndRemoveMap destroys
                // the Corpse. Adding InnerPawns to ForcefullyKeptPawns keeps them in
                // WorldPawns.pawnsDead so the cross-reference resolves on restore.
                ProtectCorpseInnerPawns(data.savedThings);

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
                DeSpawnAndClearWildlife(data.savedThings);

                // DeSpawn lord-owned faction mechs after XML is written.
                // Their data is already captured in the XML; DeSpawn removes them from
                // mapPawns so DeinitAndRemoveMap won't Destroy them into pawnsDead.
                // Unlike wildlife we do NOT clear faction — mechs keep theirs for proper
                // behaviour on restore.
                foreach (Lord lord in data.savedLords)
                    foreach (Pawn p in lord.ownedPawns.ToList())
                        if (p != null && p.Spawned)
                            p.DeSpawn(DestroyMode.Vanish);

                LogSaveStats(file, data, record);
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

        // Logs a two-part breakdown after FinalizeSaving:
        //   Disk  — the tile XML file size, with raw grid array sizes and thing count.
        //   RAM   — what the WorldComponent is holding in memory while the tile is absent:
        //     · WorldPawns refs: cryo/casket/player-animal pawns whose data lives in the
        //       main .rws (WorldPawns.pawnsAlive); WorldComp holds a reference + position only.
        //     · Owned pawns: worldCreaturePawns are NOT in WorldPawns; the full Pawn
        //       objects live exclusively in the WorldComponent (net new heap).
        //     · GC-protected: corpse InnerPawns pinned in ForcefullyKeptPawns so
        //       WorldPawnGC does not collect them while the tile is unloaded.
        //       Their data is in WorldPawns.pawnsDead (in the main .rws); only the
        //       HashSet reference is added by us.
        // Size estimates are obtained by deep-serializing each pawn group to a temp file
        // (same code path as the real save) and measuring the resulting file size.
        private static void LogSaveStats(string file, PersistentMapData data, TileRecord record)
        {
            // --- disk ---
            long xmlBytes = new FileInfo(file).Length;

            // Raw in-memory sizes of the grid byte arrays.
            // On disk they are base64-encoded (~4/3 larger than these numbers).
            long gridBytes =
                (data.terrainData?.LongLength   ?? 0) +
                (data.roofData?.LongLength       ?? 0) +
                (data.snowData?.LongLength       ?? 0) +
                (data.pollutionData?.LongLength  ?? 0) +
                (data.fogData?.LongLength        ?? 0);

            int thingCount = data.savedThings?.Count ?? 0;

            // --- RAM: WorldPawns-backed (data in main .rws, we hold ref + position) ---
            var wpPawns = record.cryoPawns.Select(r => r.pawn)
                .Concat(record.casketPawns.Select(r => r.pawn))
                .Concat(record.playerAnimalPawns.Select(r => r.pawn))
                .Concat(record.parkedPawns)
                .Where(p => p != null).ToList();
            long wpBytes = EstimatePawnsSerialized(wpPawns);

            // --- RAM: fully owned by WorldComponent (NOT in WorldPawns) ---
            var ownedPawns = record.worldCreaturePawns
                .Where(r => r?.pawn != null).Select(r => r.pawn).ToList();
            long ownedBytes = EstimatePawnsSerialized(ownedPawns);

            // --- RAM: GC-protected InnerPawns (in WorldPawns.pawnsDead, just a HashSet ref) ---
            var innerPawnList = data.savedThings != null
                ? CorpseInnerPawns(data.savedThings).ToList()
                : new List<Pawn>();
            long innerBytes = EstimatePawnsSerialized(innerPawnList);

            int cryo     = record.cryoPawns.Count;
            int casket   = record.casketPawns.Count;
            int animals  = record.playerAnimalPawns.Count;
            int parked   = record.parkedPawns.Count;
            string wpDetail = $"cryo={cryo} casket={casket} animals={animals}" +
                              (parked > 0 ? $" legacy={parked}" : "");

            long ramBytes = wpBytes + ownedBytes + innerBytes;

            KLog.Message(
                $"[PersistentMaps] Tile save breakdown:" +
                $"\n  Disk : {FormatBytes(xmlBytes)} — grids (raw)={FormatBytes(gridBytes)}, things={thingCount}" +
                $"\n  RAM  : {FormatBytes(ramBytes)} — WorldPawns refs={wpPawns.Count} ({wpDetail}) ~{FormatBytes(wpBytes)}" +
                $" | owned pawn objects={ownedPawns.Count} ~{FormatBytes(ownedBytes)}" +
                $" | GC-protected InnerPawns={innerPawnList.Count} ~{FormatBytes(innerBytes)}");
        }

        // Estimates the serialized XML size of a list of pawns by deep-saving them to a
        // temp file (same code path as the real save) and measuring the resulting file size.
        // Called after FinalizeSaving so Scribe.mode is Inactive and safe to reuse.
        // The temp file is deleted immediately after measuring.
        private static long EstimatePawnsSerialized(List<Pawn> pawns)
        {
            if (pawns == null || pawns.Count == 0) return 0;
            string tempPath = null;
            try
            {
                tempPath = Path.GetTempFileName();
                Scribe.saver.InitSaving(tempPath, "est");
                Scribe.mode = LoadSaveMode.Saving;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var p = pawns[i];
                    Scribe_Deep.Look(ref p, $"p{i}");
                }
                Scribe.saver.FinalizeSaving();
                return new FileInfo(tempPath).Length;
            }
            catch (Exception e)
            {
                KLog.Warning($"[PersistentMaps] EstimatePawnsSerialized failed: {e.Message}");
                return 0;
            }
            finally
            {
                Scribe.mode = LoadSaveMode.Inactive;
                if (tempPath != null)
                    try { File.Delete(tempPath); } catch { }
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
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

        // Extracts all pawn occupants from every cryptosleep casket in a single pass:
        //   · Player-faction pawns → record.cryoPawns  (re-inserted into caskets on restore)
        //   · All other pawns      → record.casketPawns (ancient soldiers, slaves, etc.)
        // Both groups go to WorldPawns(KeepForever) so faction/relations survive in the main .rws.
        // Must run BEFORE building savedThings so all caskets are empty in our XML.
        private static void ExtractAllCasketOccupants(Map map, TileRecord record)
        {
            int cryoCount = 0, casketCount = 0;
            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (!(t is Building_Casket casket)) continue;
                ThingOwner held = casket.GetDirectlyHeldThings();
                for (int i = held.Count - 1; i >= 0; i--)
                {
                    if (!(held[i] is Pawn cp)) continue;
                    held.Remove(cp);
                    if (Find.WorldPawns.GetSituation(cp) == WorldPawnSituation.None)
                        Find.WorldPawns.PassToWorld(cp, PawnDiscardDecideMode.KeepForever);
                    var rec = new CryoPawnRecord { pawn = cp, position = casket.Position };
                    if (cp.Faction == Faction.OfPlayer) { record.cryoPawns.Add(rec);   cryoCount++; }
                    else                                { record.casketPawns.Add(rec); casketCount++; }
                }
            }
            if (cryoCount   > 0) KLog.Message($"[PersistentMaps] Moved {cryoCount} cryo colonists to WorldPawns(KeepForever) for tile {record.tileId}");
            if (casketCount > 0) KLog.Message($"[PersistentMaps] Moved {casketCount} ancient casket occupants to WorldPawns(KeepForever) for tile {record.tileId}");
        }

        // Extracts non-humanlike pawns from the map before savedThings is built:
        //   - Player faction (tamed animals, player mechs): WorldPawns(KeepForever) with
        //     saved position so they can be re-spawned at the right place on restore.
        //   - Friendly/enemy faction: returned to WorldPawns, no position recorded
        //     (transient visitors — faction manages them from there).
        //   - Wild/natural (null faction): left in listerThings → deep-saved in tile XML.
        private static int ExtractPlayerAnimals(Map map, TileRecord record)
        {
            int playerCount = 0;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.RaceProps.Humanlike) continue;

                if (p.Faction == Faction.OfPlayer)
                {
                    // Player animals / mechs: extract with position for restore.
                    IntVec3 pos = p.Position;
                    p.DeSpawn(DestroyMode.Vanish);
                    Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever);
                    record.playerAnimalPawns.Add(new CryoPawnRecord { pawn = p, position = pos });
                    playerCount++;
                }
                // Wild animals (null faction, or already a WorldPawn) → deep-saved in XML.
                // Faction mechs (non-null faction, GetSituation==None, not player) stay on the
                // map too — their lord is collected separately and saved alongside savedThings.
            }
            return playerCount;
        }

        // Extracts non-humanlike WorldPawn creatures (ancient danger mechs/insects, mech
        // cluster pawns) from the map. These pawns have GetSituation != None while spawned
        // but have no cross-references from the main save, so they are safe to deep-save
        // in WorldComponent (not WorldPawns). Removing them from WorldPawns before
        // savedThings is built prevents ID collision on restore.
        // Must run BEFORE building savedThings so they are not included in tile XML.
        private static int ExtractNonPlayerWorldPawnCreatures(Map map, TileRecord record)
        {
            int count = 0;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p.RaceProps.Humanlike) continue;
                if (p.Faction == Faction.OfPlayer) continue;  // handled by ExtractPlayerAnimals
                if (Find.WorldPawns.GetSituation(p) == WorldPawnSituation.None) continue; // wild/non-WorldPawn: handled elsewhere

                // Non-humanlike WorldPawn (mech, insect, etc.): deep-save in WorldComponent.
                IntVec3 pos = p.Position;
                p.DeSpawn(DestroyMode.Vanish);
                Find.WorldPawns.RemovePawn(p);
                record.worldCreaturePawns.Add(new DeepPawnRecord { pawn = p, position = pos });
                count++;
            }
            return count;
        }

        // DeSpawns wildlife and clears their faction in a single pass after XML is written.
        // Safe to run after FinalizeSaving: pawn data is already captured; DeinitAndRemoveMap
        // hasn't run yet (it fires when our Prefix returns to vanilla Abandon).
        private static void DeSpawnAndClearWildlife(List<Thing> savedThings)
        {
            foreach (Thing t in savedThings)
            {
                if (!(t is Pawn wp) || wp.RaceProps.Humanlike) continue;
                wp.DeSpawn(DestroyMode.Vanish);
                if (wp.Faction != null) wp.SetFaction(null);
            }
        }

        // Yields the InnerPawn of every Corpse in savedThings — both standalone corpses
        // and corpses held inside graves/sarcophagi (Building_Casket). Used by the
        // Protect/Unprotect pair and by LogSaveStats to avoid duplicating the iteration.
        private static IEnumerable<Pawn> CorpseInnerPawns(IEnumerable<Thing> savedThings)
        {
            foreach (Thing t in savedThings)
            {
                if (t is Corpse corpse && corpse.InnerPawn != null)
                {
                    yield return corpse.InnerPawn;
                }
                else if (t is Building_Casket casket)
                {
                    ThingOwner held = casket.GetDirectlyHeldThings();
                    for (int i = 0; i < held.Count; i++)
                        if (held[i] is Corpse gc && gc.InnerPawn != null)
                            yield return gc.InnerPawn;
                }
            }
        }

        // Adds InnerPawns of all saved corpses (standalone and inside graves/sarcophagi)
        // to WorldPawns.ForcefullyKeptPawns. Called before XML is written so the pawns
        // survive WorldPawnGC while the tile is absent.
        private static void ProtectCorpseInnerPawns(List<Thing> savedThings)
        {
            var forced = Find.WorldPawns.ForcefullyKeptPawns;
            foreach (Pawn p in CorpseInnerPawns(savedThings))
                forced.Add(p);
        }

        // Removes InnerPawns of restored corpses from ForcefullyKeptPawns.
        // Called after SpawnSavedThings — the corpses are back on the map so
        // WorldPawnGC's "CorpseExists" check protects them naturally from here on.
        private static void UnprotectCorpseInnerPawns(List<Thing> savedThings)
        {
            var forced = Find.WorldPawns.ForcefullyKeptPawns;
            foreach (Pawn p in CorpseInnerPawns(savedThings))
                forced.Remove(p);
        }

        // Returns the first Building_Casket at the given map position, or null.
        private static Building_Casket CasketAtPosition(IntVec3 pos, Map map) =>
            map.thingGrid.ThingsListAt(pos).OfType<Building_Casket>().FirstOrDefault();

        // Removes all contents from a casket without triggering Destroy side-effects.
        private static void DrainCasket(Building_Casket casket)
        {
            ThingOwner held = casket.GetDirectlyHeldThings();
            while (held.Count > 0)
                held.Remove(held[0]);
        }

        // Spawns a pawn at pos; falls back to a random animal cell if pos is out-of-bounds
        // or not standable. Used for animal/creature restores that tracked a map position.
        private static void SpawnAtSavedPosition(Pawn p, IntVec3 pos, Map map)
        {
            if (!pos.InBounds(map) || !pos.Standable(map))
            {
                IntVec3 original = pos;
                // Try a nearby standable cell first; only fall back to a random map cell
                // if nothing is found within range (e.g. the saved position is now walled in).
                if (!pos.InBounds(map) || !CellFinder.TryFindRandomCellNear(pos, map, 8, c => c.Standable(map), out pos))
                {
                    pos = RCellFinder.RandomAnimalSpawnCell_MapGen(map);
                    KLog.Message($"[PersistentMaps] SpawnAtSavedPosition: {p.LabelShort} saved={original} inBounds={original.InBounds(map)} standable={original.InBounds(map) && original.Standable(map)} → fallback random={pos}");
                }
                else
                {
                    KLog.Message($"[PersistentMaps] SpawnAtSavedPosition: {p.LabelShort} saved={original} not standable → nearby={pos}");
                }
            }
            GenSpawn.Spawn(p, pos, map, p.Rotation, WipeMode.Vanish, respawningAfterLoad: true);
            KLog.Message($"[PersistentMaps] SpawnAtSavedPosition: {p.LabelShort} intended={pos} actual={p.Position} spawned={p.Spawned}");
        }

        // Spawns a pawn at preferredPos if standable, otherwise at a random animal cell.
        // Uses Rot4.North; intended for casket occupants that failed re-insertion.
        private static void SpawnFreeFallback(Pawn p, IntVec3 preferredPos, Map map)
        {
            IntVec3 pos = preferredPos.Standable(map) ? preferredPos : RCellFinder.RandomAnimalSpawnCell_MapGen(map);
            GenSpawn.Spawn(p, pos, map, Rot4.North);
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

                // Remove WorldPawn ghost copies of deep-saved creatures BEFORE RefInjector
                // runs. Non-humanlike pawns (e.g. Megascarabs) can drift into WorldPawns
                // between save and restore; if RefInjector finds them there it tries to
                // register the same ID that our XML already registered → "id already used".
                // Clearing the ghosts first lets RefInjector skip those IDs entirely.
                RemoveWorldPawnGhosts(data.savedThings);

                // Set lordManager on saved lords NOW — before DoAllPostLoadInits() runs.
                // Lord.PostLoadInit() accesses lord.Map (= lordManager?.map) and will
                // NullReference if lordManager is still null at that point.
                if (data.savedLords != null)
                    foreach (Lord lord in data.savedLords)
                        if (lord != null) lord.lordManager = map.lordManager;

                // Register live game objects as cross-ref targets so references in our
                // fragment XML (faction, ideo, master, etc.) resolve into the live game.
                RefInjector.PreRegisterActiveGame();

                // Replicate the XML cursor teardown FinalizeLoading() would do, without
                // triggering its monolithic resolve+init sequence. This lets us insert
                // RefInjector between LoadingVars and ResolveAllCrossReferences.
                FinalizeScribeLoadState();

                Scribe.loader.crossRefs.ResolveAllCrossReferences();

                Scribe.mode = LoadSaveMode.PostLoadInit;
                Scribe.loader.initer.DoAllPostLoadInits();
                Scribe.mode = LoadSaveMode.Inactive;

                var context = BuildDecayContext(data, map, tile);

                SpawnSavedThings(map, data.savedThings);
                // Corpses are back on the map; WorldPawnGC's "CorpseExists" check now
                // protects their InnerPawns. Remove the ForcefullyKept entries we added
                // in SaveMap so they don't accumulate across multiple abandon/settle cycles.
                UnprotectCorpseInnerPawns(data.savedThings);

                // Restore mech cluster lords (and similar). The lords were deep-saved in the
                // same XML document as savedThings, so ownedPawns cross-refs already resolved.
                // Setting lordManager and adding to lords lets the lord tick normally and gives
                // the mechs valid job targets — preventing the (0,0,0) teleport on first tick.
                if (data.savedLords != null && data.savedLords.Count > 0)
                {
                    foreach (Lord lord in data.savedLords)
                    {
                        if (lord == null) continue;
                        // lordManager was already set before PostLoadInits; just add + register.
                        map.lordManager.lords.Add(lord);
                        Find.SignalManager.RegisterReceiver(lord);
                    }
                    KLog.Message($"[PersistentMaps] Restored {data.savedLords.Count} lord(s) for tile {tile.tileId}");
                }

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
                    DrainCasket(casket);

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
            var context = new OfflineDecayContext
            {
                map        = map,
                startTick  = data.abandonedAtTick,
                ticksPassed = Math.Max(0, Find.TickManager.TicksGame - data.abandonedAtTick),
                rainfall   = Find.WorldGrid[tile.tileId].rainfall,
                tileId     = tile.tileId
            };
            // Computed once here so both ApplyDecay and SimulateStructuralFailures share the result.
            context.hasFreezeThawCycles = DecayUtility.ComputeFreezeThawCycles(context);
            return context;
        }

        // Removes WorldPawns ghost copies of non-humanlike pawns that are deep-saved in
        // our tile XML, to prevent "Id already used" collisions in LoadedObjectDirectory.
        //
        // Only direct Pawn entries (wildlife) are handled here. Wildlife is deep-saved
        // (full pawn data in our XML), so if the same pawn also drifted into WorldPawns
        // after DeSpawn, loading would try to register the same ID twice.
        //
        // NOTE: Corpse.InnerPawn is stored as LookMode.Reference (not deep-saved in our
        // XML), so InnerPawns must NOT be removed from WorldPawns.pawnsDead here — they
        // are the authoritative data that the Corpse's cross-reference must resolve against.
        // GC protection for InnerPawns is handled by ProtectCorpseInnerPawns / ForcefullyKeptPawns.
        private static void RemoveWorldPawnGhosts(List<Thing> savedThings)
        {
            foreach (Thing t in savedThings)
            {
                if (t is Pawn savedPawn)
                    RemoveGhost(savedPawn);
            }
        }

        private static void RemoveGhost(Pawn loadedPawn)
        {
            string loadId = loadedPawn.GetUniqueLoadID();
            Pawn ghost = Find.WorldPawns.AllPawnsAliveOrDead
                .FirstOrDefault(p => p != loadedPawn && p.GetUniqueLoadID() == loadId);
            if (ghost != null)
                Find.WorldPawns.RemovePawn(ghost);
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
                        if (existing is Building_Casket existingCasket)
                            DrainCasket(existingCasket);
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
                SpawnAtSavedPosition(p, p.Position, map);
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

                Building_Casket target = CasketAtPosition(cryo.position, map);
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
                    SpawnFreeFallback(cryo.pawn, cryo.position, map);
                }
            }
            if (cryoRestored > 0)
                KLog.Message($"[PersistentMaps] Restored {cryoRestored} cryo colonists into caskets on tile {tile.tileId}");

            // Re-insert non-player casket occupants (ancient soldiers etc.) into their caskets.
            int casketRestored = 0;
            foreach (CryoPawnRecord cr in record.casketPawns.ToList())
            {
                if (cr?.pawn == null || cr.pawn.Destroyed) continue;
                if (Find.WorldPawns.GetSituation(cr.pawn) != WorldPawnSituation.None)
                    Find.WorldPawns.RemovePawn(cr.pawn);

                Building_Casket target = CasketAtPosition(cr.position, map);
                if (target != null && target.TryAcceptThing(cr.pawn, allowSpecialEffects: false))
                {
                    casketRestored++;
                }
                else
                {
                    KLog.Warning($"[PersistentMaps] No casket at {cr.position} for ancient pawn {cr.pawn.Name?.ToStringFull}; spawning free");
                    SpawnFreeFallback(cr.pawn, cr.position, map);
                }
            }
            if (casketRestored > 0)
                KLog.Message($"[PersistentMaps] Restored {casketRestored} ancient casket occupants on tile {tile.tileId}");

            // Re-spawn player animals at their saved positions.
            int animalRestored = 0;
            foreach (CryoPawnRecord ar in record.playerAnimalPawns.ToList())
            {
                if (ar?.pawn == null || ar.pawn.Destroyed) continue;
                if (Find.WorldPawns.GetSituation(ar.pawn) != WorldPawnSituation.None)
                    Find.WorldPawns.RemovePawn(ar.pawn);

                // WorldPawns discard cycle may have stripped player faction; restore it.
                if (ar.pawn.Faction != Faction.OfPlayer)
                    ar.pawn.SetFaction(Faction.OfPlayer);

                SpawnAtSavedPosition(ar.pawn, ar.position, map);
                animalRestored++;
            }
            if (animalRestored > 0)
                KLog.Message($"[PersistentMaps] Restored {animalRestored} player animals on tile {tile.tileId}");

            // Re-spawn WorldPawn creatures (ancient danger mechs/insects, mech cluster pawns)
            // at their saved positions. These were deep-saved in WorldComponent (not WorldPawns).
            // Stop all jobs after spawning: their saved jobs reference a lord/targets that no
            // longer exist, which would cause them to teleport to (0,0,0) on the first AI tick.
            int creatureRestored = 0;
            foreach (DeepPawnRecord cr in record.worldCreaturePawns.ToList())
            {
                if (cr?.pawn == null || cr.pawn.Destroyed) continue;
                SpawnAtSavedPosition(cr.pawn, cr.position, map);
                if (cr.pawn.Spawned)
                {
                    cr.pawn.jobs?.StopAll();
                    cr.pawn.mindState.duty = null;
                }
                creatureRestored++;
            }
            if (creatureRestored > 0)
                KLog.Message($"[PersistentMaps] Restored {creatureRestored} WorldPawn creatures on tile {tile.tileId}");

            wc.Release(tile.tileId);
        }

        // Applies roofs, decay, snow, pollution, and fog after things are spawned.
        private static void ApplyMapGridData(Map map, PersistentMapData data, OfflineDecayContext context)
        {
            if (data.roofData != null)
                ApplyRoofs(map, data);

            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (!PersistentMapData.ShouldPersistThing(t)) continue;
                DecayUtility.ApplyDecay(t, context);
            }

            // Floor passive decay — erodes constructed floors on unroofed cells over time.
            // Runs after terrain is restored (terrainGrid already has floors) and before
            // structural failures so collapse events can then strip whatever passive decay left.
            DecayUtility.ApplyFloorDecay(map, context);

            // Second pass: discrete structural failure events (localized collapse clusters).
            // Runs after per-thing decay so buildings already have weathering damage applied.
            // ApplyOneStructuralFailure also strips floors in each blast zone.
            DecayUtility.SimulateStructuralFailures(map, context);

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
