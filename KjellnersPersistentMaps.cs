using RimWorld;
using RimWorld.Planet;
using Verse;
using HarmonyLib;
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace KjellnersPersistentMaps
{
    // Stick a UUID into the save file to identify this save and tie it to external serialized map data
    public class GameComponent_PersistentMaps : GameComponent
    {
        public string persistentId;

        // When loading a save the UUID will be generated and then overwritten by Scribe in LoadingVars mode
        public GameComponent_PersistentMaps(Game game)
        {
            persistentId = Guid.NewGuid().ToString();
            KLog.Message($"[PersistentMaps] Generated persistent ID: {persistentId}");
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref persistentId, "persistentId");
        }
    }

    // Harmony Initialization
    [StaticConstructorOnStartup]
    public static class PersistentMapsInit
    {
        static PersistentMapsInit()
        {
            var harmony = new Harmony("kjellner.persistentmaps");
            harmony.PatchAll();

            KLog.Message("[PersistentMaps] All patches applied.");
        }
    }

    // Allow settling on AbandonedSettlement tiles
    [HarmonyPatch(typeof(TileFinder), nameof(TileFinder.IsValidTileForNewSettlement))]
    public static class Patch_IsValidTileForNewSettlement
    {
        public static void Postfix(
            PlanetTile tile,
            StringBuilder reason,
            bool forGravship,
            ref bool __result)
        {
            if (__result)
                return;

            var abandoned = Find.WorldObjects.WorldObjectAt<AbandonedSettlement>(tile);

            if (abandoned != null)
            {
                KLog.Message($"[PersistentMaps] Allowing settlement on abandoned tile {tile.tileId}");

                __result = true;

                if (reason != null)
                    reason.Length = 0;
            }
        }
    }

    // Remove AbandonedSettlement before settling
    [HarmonyPatch(typeof(SettleInEmptyTileUtility), nameof(SettleInEmptyTileUtility.Settle))]
    public static class Patch_SettleInEmptyTileUtility_Settle
    {
        public static void Prefix(Caravan caravan)
        {
            PlanetTile tile = caravan.Tile;

            var abandoned = Find.WorldObjects.WorldObjectAt<AbandonedSettlement>(tile);

            if (abandoned != null)
            {
                KLog.Message("[PersistentMaps] Removing abandoned settlement before settling.");
                Find.WorldObjects.Remove(abandoned);
            }
        }
    }

    // Serialize map before abandonment
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.Abandon))]
    public static class Patch_MapParent_Abandon
    {
        public static void Prefix(MapParent __instance, bool wasGravshipLaunch)
        {
            Map map = __instance.Map;

            if (map == null)
                return;

            if (!map.IsPlayerHome)
                return;

            KLog.Message($"[PersistentMaps] Serializing map before abandonment. Tile: {__instance.Tile.tileId}");

            PersistentMapSerializer.SaveMap(map, __instance.Tile);
        }
    }

    // Inject map loading logic
    [HarmonyPatch(
        typeof(GetOrGenerateMapUtility),
        nameof(GetOrGenerateMapUtility.GetOrGenerateMap),
        new Type[]
        {
            typeof(PlanetTile),
            typeof(IntVec3),
            typeof(WorldObjectDef),
            typeof(IEnumerable<GenStepWithParams>),
            typeof(bool)
        }
    )]
    public static class Patch_GetOrGenerateMap
    {
        public static void Postfix(
            PlanetTile tile,
            ref Map __result)
        {
            if (__result == null)
                return;

            if (!PersistentMapSerializer.PersistentFileExists(tile))
                return;

            KLog.Message($"[PersistentMaps] Applying persistent data to tile {tile}");

            Map map = __result;

            LongEventHandler.ExecuteWhenFinished(() =>
            {
                PersistentMapSerializer.LoadSavedMap(map, tile);
            });
        }
    }

    [HarmonyPatch(typeof(RoofCollapseCellsFinder), "CheckAndRemoveCollpsingRoofs")]
    public static class Patch_DisableRoofCollapseDuringLoad
    {
        public static bool Prefix()
        {
            if (PersistentMapSerializer.IsRestoring)
                return false; // Skip collapse check

            return true;
        }
    }

    // Block area revealed when rebuilding fog
    [HarmonyPatch(typeof(FogGrid), "NotifyAreaRevealed")]
    public static class Patch_DisableAreaRevealLettersDuringLoad
    {
        public static bool Prefix()
        {
            if (PersistentMapSerializer.IsRestoring)
                return false; // Skip sending letters

            return true;
        }
    }

    // Persistent Map Serializer
    public static class PersistentMapSerializer
    {
        public static bool IsRestoring = false;

        public static void SaveMap(Map map, PlanetTile tile)
        {
            string folder = GetPersistentFolder();
            if (string.IsNullOrEmpty(folder))
                return;

            string file = Path.Combine(folder, $"Tile_{tile.tileId}.xml");

            try
            {
                // -----------------------------
                // Build lightweight persistent data
                // -----------------------------
                PersistentMapData data = new PersistentMapData();

                // Save when this was abandoned so we can calculate a diff and decay stuff
                data.abandonedAtTick = Find.TickManager.TicksGame;

                // Terrain
                data.terrainData = MapSerializeUtility.SerializeUshort(
                    map,
                    c => (ushort)map.terrainGrid.TerrainAt(c).shortHash
                );

                // Roof
                data.roofData = MapSerializeUtility.SerializeUshort(
                    map,
                    c => (ushort)(map.roofGrid.RoofAt(c)?.shortHash ?? 0)
                );


                // Snow
                data.snowData = MapSerializeUtility.SerializeByte(
                    map,
                    c => (byte)map.snowGrid.GetDepth(c)
                );

                // Pollution (if Biotech)
                if (ModsConfig.BiotechActive && map.pollutionGrid != null)
                {
                    data.pollutionData = MapSerializeUtility.SerializeByte(
                        map,
                        c => map.pollutionGrid.IsPolluted(c) ? (byte)1 : (byte)0
                    );
                }

                // Fog
                data.fogData = MapSerializeUtility.SerializeByte(
                    map,
                    c => map.fogGrid.IsFogged(c) ? (byte)1 : (byte)0
                );

                // Items
                // We filter what we save to avoid crossref issues
                data.items = new List<PersistentItemData>();

                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (!PersistentItemData.IsSafePersistentItem(t))
                        continue;

                    // Deal with food rot
                    float rot = 0f;

                    var rotComp = t.TryGetComp<CompRottable>();
                    if (rotComp != null)
                    {
                        rot = rotComp.RotProgress;
                    }

                    data.items.Add(new PersistentItemData
                    {
                        defName = t.def.defName,
                        stuffDefName = t.Stuff?.defName,
                        position = t.Position,
                        stackCount = t.stackCount,
                        hitPoints = t.HitPoints,
                        rotProgress = rot
                    });
                }

                // Plants
                data.plants = new List<PersistentPlantData>();

                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (t.def.category != ThingCategory.Plant)
                        continue;

                    Plant plant = t as Plant;
                    if (plant == null)
                        continue;

                    // Optional: skip plants that shouldn't persist
                    if (plant.Destroyed || plant.Position.IsValid == false)
                        continue;

                    data.plants.Add(new PersistentPlantData
                    {
                        defName = plant.def.defName,
                        position = plant.Position,
                        growth = plant.Growth,
                        hitPoints = plant.HitPoints
                    });
                }


                // Buildings
                data.buildings = new List<PersistentBuildingData>();

                // Make sure to mirror this logic in load otherwise bad things happen mate
                foreach (Thing t in map.listerThings.AllThings)
                {
                    if (!PersistentBuildingData.IsSafePersistentBuilding(t))
                        continue;

                    data.buildings.Add(new PersistentBuildingData
                    {
                        defName = t.def.defName,
                        stuffDefName = t.Stuff?.defName,
                        factionDefName = t.Faction?.def.defName,
                        position = t.Position,
                        rotation = t.Rotation.AsInt,
                        hitPoints = t.HitPoints
                    });
                }



                // -----------------------------
                // Save to XML
                // -----------------------------
                Scribe.saver.InitSaving(file, "PersistentMap");
                Scribe.mode = LoadSaveMode.Saving;

                Scribe_Deep.Look(ref data, "MapData");

                Scribe.saver.FinalizeSaving();

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
    
        public static void LoadSavedMap(Map map, PlanetTile tile)
        {
            // Mod stores data outside the regular save files
            string folder = GetPersistentFolder();
            if (string.IsNullOrEmpty(folder))
                return;

            string file = Path.Combine(folder, $"Tile_{tile.tileId}.xml");

            if (!File.Exists(file))
                return;

            try
            {
                PersistentMapData data = null;

                // -----------------------------
                // Load persistent data object
                // -----------------------------
                Scribe.loader.InitLoading(file);
                Scribe.mode = LoadSaveMode.LoadingVars;

                Scribe_Deep.Look(ref data, "MapData");

                Scribe.loader.FinalizeLoading();

                if (data == null)
                {
                    KLog.Error("[PersistentMaps] Loaded data is null.");
                    return;
                }

                // Calculate time passed for decay
                int ticksPassed = Find.TickManager.TicksGame - data.abandonedAtTick;
                if (ticksPassed < 0)
                    ticksPassed = 0;

                int currentTick = Find.TickManager.TicksGame;
                int abandonedTick = data.abandonedAtTick;
                
                // Configure decay context for this map
                float rainfall = Find.WorldGrid[tile.tileId].rainfall;

                // BUILD DECAY CONTEXT
                var context = new OfflineDecayContext
                {
                    map = map,
                    ticksPassed = ticksPassed,
                    rainfall = rainfall,
                    tileId = tile.tileId
                };
                
                // Roofs & fog stuff
                IsRestoring = true;

                // -----------------------------
                // Apply Terrain
                // -----------------------------
                if (data.terrainData != null)
                {
                    MapSerializeUtility.LoadUshort(
                        data.terrainData,
                        map,
                        (c, val) =>
                        {
                            TerrainDef def = DefDatabase<TerrainDef>.GetByShortHash(val);
                            if (def != null)
                                map.terrainGrid.SetTerrain(c, def);
                        });
                }

                // Buildings, need to go before to avoid roof collapse (maybe)
                if (data.buildings != null)
                {
                    // wipe mapgen stuff that we load
                    foreach (Thing thing in map.listerThings.AllThings.ToList())
                    {
                        if (!PersistentBuildingData.IsSafePersistentBuilding(thing))
                            continue;

                        thing.Destroy(DestroyMode.Vanish);
                    }

                    // load
                    foreach (PersistentBuildingData b in data.buildings)
                    {
                        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(b.defName);
                        if (def == null)
                            continue;

                        ThingDef stuff = null;
                        if (!string.IsNullOrEmpty(b.stuffDefName))
                            stuff = DefDatabase<ThingDef>.GetNamedSilentFail(b.stuffDefName);

                        Thing thing = ThingMaker.MakeThing(def, stuff);

                        thing.HitPoints = b.hitPoints < 1
                            ? 1
                            : (b.hitPoints > thing.MaxHitPoints ? thing.MaxHitPoints : b.hitPoints);
                        thing.Rotation = new Rot4(b.rotation);


                        if (!string.IsNullOrEmpty(b.factionDefName))
                        {
                            FactionDef fDef = DefDatabase<FactionDef>.GetNamedSilentFail(b.factionDefName);
                            if (fDef != null)
                            {
                                Faction faction = Find.FactionManager.FirstFactionOfDef(fDef);
                                if (faction != null)
                                    thing.SetFaction(faction);
                            }
                        }

                        GenSpawn.Spawn(
                                thing,
                                b.position,
                                map,
                                thing.Rotation,
                                WipeMode.Vanish,
                                respawningAfterLoad: true
                            );

                    }
                }
                // -------------------------------------------------
                // APPLY ROOFS EARLY (before items & decay)
                // -------------------------------------------------
                if (data.roofData != null)
                {
                    ApplyRoofs(map, data);
                }

                // Items
                // Wipe mapgen
                foreach (Thing t in map.listerThings.AllThings.ToList())
                {
                    if (!PersistentItemData.IsSafePersistentItem(t))
                        continue;

                    t.Destroy(DestroyMode.Vanish);
                }

                // Rebuild
                if (data.items != null)
                {
                    foreach (var i in data.items)
                    {
                        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(i.defName);
                        if (def == null)
                            continue;

                        var decay = DecayUtility.ApplyOfflineDecay(context, i);

                        if (!decay.shouldSpawn)
                            continue;

                        ThingDef stuff = null;
                        if (!string.IsNullOrEmpty(i.stuffDefName))
                            stuff = DefDatabase<ThingDef>.GetNamedSilentFail(i.stuffDefName);

                        Thing thing = ThingMaker.MakeThing(def, stuff);

                        thing.stackCount = i.stackCount;
                        thing.HitPoints = Math.Max(1, Math.Min(thing.MaxHitPoints, decay.resultingHp));

                        GenSpawn.Spawn(
                            thing,
                            i.position,
                            map,
                            Rot4.North,
                            WipeMode.Vanish,
                            respawningAfterLoad: true);

                        // -------------------------------------------------
                        // Apply rot progress AFTER spawn
                        // -------------------------------------------------
                        var rotComp = thing.TryGetComp<CompRottable>();
                        if (rotComp != null)
                        {
                            // IMPORTANT:
                            // resultingRotProgress already includes:
                            // saved rot + offline seasonal rot
                            rotComp.RotProgress = decay.resultingRotProgress;
                        }
                    }
                }

                // Plants
                // Wipe mapgen
                foreach (Thing t in map.listerThings.AllThings.ToList())
                {
                    if (t.def.category == ThingCategory.Plant)
                    {
                        t.Destroy(DestroyMode.Vanish);
                    }
                }

                // Rebuild
                if (data.plants != null)
                {
                    foreach (var p in data.plants)
                    {
                        ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(p.defName);
                        if (def == null)
                            continue;

                        Plant plant = ThingMaker.MakeThing(def) as Plant;
                        if (plant == null)
                            continue;

                        plant.Growth = p.growth;
                        plant.HitPoints = Math.Max(1, Math.Min(p.hitPoints, plant.MaxHitPoints));

                        GenSpawn.Spawn(plant, p.position, map, Rot4.North, WipeMode.Vanish, respawningAfterLoad: true);
                    }
                }

                // -----------------------------
                // Apply Snow
                // -----------------------------
                if (data.snowData != null)
                {
                    MapSerializeUtility.LoadByte(
                        data.snowData,
                        map,
                        (c, val) =>
                        {
                            map.snowGrid.SetDepth(c, val);
                        });
                }

                // -----------------------------
                // Apply Pollution (Biotech)
                // -----------------------------
                if (ModsConfig.BiotechActive && map.pollutionGrid != null && data.pollutionData != null)
                {
                    MapSerializeUtility.LoadByte(
                        data.pollutionData,
                        map,
                        (c, val) =>
                        {
                            map.pollutionGrid.SetPolluted(c, val == 1, silent: true);
                        });
                }


                // Clean out any pawns generated by mapgen, could maybe be blocked instead
                foreach (Pawn pawn in map.mapPawns.AllPawns.ToList())
                {
                    if (pawn.Faction == null)
                        continue;

                    if (pawn.Faction == Faction.OfPlayer)
                        continue;

                    if (pawn.Faction.HostileTo(Faction.OfPlayer))
                        pawn.Destroy();
                }

                // Restore any saved pawns
                // TODO: Implement
                
                // Fog
                if (data.fogData != null)
                {
                    // First fog everything using Refog
                    map.fogGrid.Refog(new CellRect(0, 0, map.Size.x, map.Size.z));

                    MapSerializeUtility.LoadByte(
                        data.fogData,
                        map,
                        (c, val) =>
                        {
                            if (val == 0)
                            {
                                // 0 = was unfogged
                                map.fogGrid.Unfog(c);
                            }
                        });
                }

                IsRestoring = false;
                KLog.Message($"[PersistentMaps] Applied saved data for tile {tile}");
            }
            catch (Exception e)
            {
                KLog.Error($"[PersistentMaps] Failed applying saved data: {e}");
                Scribe.mode = LoadSaveMode.Inactive;
            }
        }

        private static void ApplyRoofs(Map map, PersistentMapData data)
        {
            if (data.roofData == null)
                return;

            MapSerializeUtility.LoadUshort(
                data.roofData,
                map,
                (c, val) =>
                {
                    if (val == 0)
                    {
                        map.roofGrid.SetRoof(c, null);
                        return;
                    }

                    RoofDef def = DefDatabase<RoofDef>.GetByShortHash(val);
                    if (def != null)
                        map.roofGrid.SetRoof(c, def);
                });

            // Critical: rebuild roof support
            RebuildRoofSupport(map);
        }

        private static void RebuildRoofSupport(Map map)
        {
            var method = typeof(RoofGrid)
                .GetMethod("ResolveAllRoofSupport",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            if (method != null)
            {
                method.Invoke(map.roofGrid, null);
            }

            map.roofCollapseBuffer.Clear();
        }

        
        public static bool PersistentFileExists(PlanetTile tile)
        {
            string folder = GetPersistentFolder();
            string file = Path.Combine(folder, $"Tile_{tile.tileId}.xml");
            return File.Exists(file);
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
                comp.persistentId
            );

            Directory.CreateDirectory(folder);

            return folder;
        }


    }
}
