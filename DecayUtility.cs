using RimWorld;
using Verse;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KjellnersPersistentMaps
{
    public struct OfflineDecayContext
    {
        public Map map;
        public int startTick;
        public int ticksPassed;
        public int tileId;
        public float rainfall;
        public bool hasFreezeThawCycles; // computed once by BuildDecayContext, shared by all decay passes

        public float YearsPassed        => ticksPassed / (60000f * 60f);
        public float NormalizedRainfall => Math.Max(0f, Math.Min(1f, rainfall / 4000f));
    }

    public static class DecayUtility
    {
        // -------------------------
        // Per-thing decay
        // -------------------------

        // Called for every saved thing after restore. Handles rot, item outdoor deterioration,
        // and building structural decay. Buildings are separated from items so each uses the
        // appropriate rate formula.
        public static void ApplyDecay(Thing thing, OfflineDecayContext context)
        {
            if (thing.Destroyed)
                return;

            // ROT — step through elapsed time at hourly intervals to capture temperature variation
            var rotComp = thing.TryGetComp<CompRottable>();
            if (rotComp != null)
            {
                float rot = rotComp.RotProgress;
                int endTick = context.startTick + context.ticksPassed;
                const int hourStep = 2500;
                var rotProps = rotComp.PropsRot;

                for (int tick = context.startTick; tick < endTick; tick += hourStep)
                {
                    int step = Math.Min(hourStep, endTick - tick);
                    float temp = GenTemperature.GetTemperatureFromSeasonAtTile(tick, context.tileId)
                                 + GenTemperature.OffsetFromSunCycle(tick, context.tileId);
                    rot += GenTemperature.RotRateAtTemperature(temp) * step;

                    if (rot >= rotProps.TicksToRotStart)
                    {
                        thing.Destroy(DestroyMode.Vanish);
                        return;
                    }
                }

                rotComp.RotProgress = rot;
            }

            // OUTDOOR ITEM DETERIORATION (items only — buildings use ApplyBuildingDecay below)
            if (!thing.Destroyed && !(thing is Building) && !context.map.roofGrid.Roofed(thing.Position))
            {
                float intervals = context.ticksPassed / 250f;
                float rainFactor = 0.5f + 1.5f * context.NormalizedRainfall;
                int damage = (int)Math.Round(intervals * 0.015f * rainFactor);

                if (damage > 0)
                    thing.TakeDamage(new DamageInfo(DamageDefOf.Deterioration, damage));
            }

            // BUILDING STRUCTURAL DECAY
            if (!thing.Destroyed && thing is Building building)
                ApplyBuildingDecay(building, context);
        }

        private static void ApplyBuildingDecay(Building building, OfflineDecayContext context)
        {
            if (building.def.IsFrame || building.def.IsBlueprint) return;
            if (building.def.building?.isNaturalRock == true) return;

            float yearsPassed = context.YearsPassed;
            if (yearsPassed <= 0f) return;

            bool roofed = context.map.roofGrid.Roofed(building.Position);

            // Material-based rate: % of max HP lost per year when fully exposed
            float materialFactor = MaterialDecayFactor(building);

            // Roofing strongly protects against weathering
            float exposureFactor = roofed ? 0.08f : 1.0f;

            // Rain accelerates decay of exposed structures; irrelevant for roofed
            float rainFactor = roofed ? 1.0f : 0.5f + 1.5f * context.NormalizedRainfall;

            // Freeze-thaw cycles crack masonry and loosen foundations
            float freezeFactor = context.hasFreezeThawCycles ? 1.4f : 1.0f;

            float damagePercent = yearsPassed * materialFactor * exposureFactor * rainFactor * freezeFactor;
            int damage = (int)Math.Round(building.MaxHitPoints * damagePercent);

            if (damage > 0)
                building.TakeDamage(new DamageInfo(DamageDefOf.Deterioration, damage));
        }

        // % of max HP lost per year when fully exposed to weather
        private static float MaterialDecayFactor(Building building)
        {
            if (building.Stuff == null)
                return 0.04f; // abstract/no-stuff buildings (lights, consoles, etc.)

            var cats = building.Stuff.stuffProps?.categories;
            if (cats == null) return 0.04f;

            if (cats.Contains(StuffCategoryDefOf.Woody))    return 0.15f; // wood rots quickly
            if (cats.Contains(StuffCategoryDefOf.Metallic)) return 0.07f; // metal rusts
            if (cats.Contains(StuffCategoryDefOf.Stony))    return 0.02f; // stone is very durable

            return 0.05f; // fallback (e.g. modded stuff categories)
        }

        // -------------------------
        // Floor decay
        // -------------------------

        // Iterates every map cell and probabilistically removes constructed floors that are
        // outdoors and exposed to rain and frost. The chance per cell compounds over time so
        // short absences leave most floors intact while decade-long absences erode them heavily.
        // Called after ApplyTerrainData (so floors are already restored) and before structural
        // failure events (so collapse can then strip whatever passive decay left behind).
        public static void ApplyFloorDecay(Map map, OfflineDecayContext context)
        {
            float yearsPassed = context.YearsPassed;
            if (yearsPassed <= 0f) return;

            float rainMod   = 0.5f + 1.5f * context.NormalizedRainfall;
            float freezeMod = context.hasFreezeThawCycles ? 1.5f : 1.0f;

            // Base chance per unroofed constructed-floor cell per year to revert to natural terrain.
            // 6% × modifiers means high-rain freeze-thaw tiles lose ~20-25% of exposed floors/yr.
            const float baseChancePerYear = 0.06f;
            float chancePerYear = baseChancePerYear * rainMod * freezeMod;

            // Cumulative probability: 1 - (1 - p)^years
            float totalChance = 1f - (float)Math.Pow(1.0 - chancePerYear, yearsPassed);

            foreach (IntVec3 cell in map.AllCells)
            {
                if (map.roofGrid.Roofed(cell)) continue;                    // roofed floors are protected
                if (map.terrainGrid.UnderTerrainAt(cell) == null) continue; // no constructed floor here

                if (Rand.Value < totalChance)
                    map.terrainGrid.RemoveTopLayer(cell, doLeavings: false);
            }
        }

        // -------------------------
        // Structural failure events
        // -------------------------

        // Second decay pass: rolls for discrete structural collapse events — a localized
        // cluster of buildings takes heavy damage, simulating roof cave-ins, foundation
        // failures, or ice-heave collapses. Floors within the blast zone are also stripped.
        // Frequency and severity scale with time, rainfall, roofing coverage, and freeze-thaw.
        public static void SimulateStructuralFailures(Map map, OfflineDecayContext context)
        {
            var candidates = map.listerThings.AllThings
                .OfType<Building>()
                .Where(b => !b.Destroyed && b.def.building?.isNaturalRock != true)
                .ToList();

            if (candidates.Count == 0) return;

            int eventCount = CountStructuralEvents(context, candidates);
            if (eventCount == 0) return;

            for (int i = 0; i < eventCount; i++)
                ApplyOneStructuralFailure(map, context, candidates);

            KLog.Message($"[PersistentMaps] Applied {eventCount} structural failure event(s) for tile {context.tileId}");
        }

        private static int CountStructuralEvents(OfflineDecayContext context, List<Building> candidates)
        {
            const float minimumYears = 0.25f; // no events before ~15 in-game days
            const float baseMtbDays  = 300f;  // ~one event per 10 months at baseline

            float yearsPassed = context.YearsPassed;
            if (yearsPassed < minimumYears) return 0;

            // High rain → lower MTB → more frequent events
            float rainMod = 0.4f + 0.6f * (1f - context.NormalizedRainfall); // 1.0 dry → 0.4 very wet

            // Freeze-thaw shortens time between failures
            float freezeMod = context.hasFreezeThawCycles ? 0.65f : 1.0f;

            // Mostly unroofed → more exposure → more frequent events
            int roofedCount = candidates.Count(b => context.map.roofGrid.Roofed(b.Position));
            float roofedFraction = roofedCount / (float)candidates.Count;
            float roofMod = 0.5f + roofedFraction; // 0.5 all-unroofed → 1.5 all-roofed

            float adjustedMtbTicks = baseMtbDays * rainMod * freezeMod * roofMod * 60000f;
            float expected = context.ticksPassed / adjustedMtbTicks;

            int count = (int)expected;
            if (Rand.Value < (expected - count)) count++; // probabilistic remainder

            return Math.Min(count, 8); // cap so extreme absences don't obliterate a map
        }

        // Power-curve falloff: 1.0 at the centre, 0.0 at the radius edge.
        // Exponent 1.8 concentrates damage strongly toward the epicenter.
        private static float PowerFalloff(float dist, float radius) =>
            (float)Math.Pow(1.0 - dist / radius, 1.8);

        private static void ApplyOneStructuralFailure(Map map, OfflineDecayContext context, List<Building> candidates)
        {
            float yearsPassed = context.YearsPassed;

            // Exponential ramp: at 1yr≈0.18, 3yr≈0.45, 5yr≈0.63, 10yr≈0.86
            float timeSeverityScale = 1f - (float)Math.Exp(-yearsPassed / 5.0);

            var alive = candidates.Where(b => !b.Destroyed).ToList();
            if (alive.Count == 0) return;

            // Prefer unroofed buildings as epicenters; fall back to any surviving building
            Building epicenter = alive
                .OrderBy(_ => Rand.Value)
                .FirstOrDefault(b => !context.map.roofGrid.Roofed(b.Position))
                ?? alive.RandomElement();

            IntVec3 center = epicenter.Position;
            int radius = Rand.RangeInclusive(2, 6);

            bool epicenterExposed = !context.map.roofGrid.Roofed(center);
            float exposureScale = epicenterExposed ? 1.0f : 0.55f;

            // At epicenter: damage scales from 20% (fresh) to 75% (long-abandoned) of max HP
            float baseDamageAtCenter = 0.20f + (0.75f - 0.20f) * timeSeverityScale;

            // --- Building damage ---
            foreach (Thing t in map.listerThings.AllThings.ToList())
            {
                if (t.Destroyed || !(t is Building b)) continue;
                if (b.def.building?.isNaturalRock == true) continue;

                float dist = center.DistanceTo(b.Position);
                if (dist > radius) continue;

                float falloff = PowerFalloff(dist, radius);

                float damagePercent = baseDamageAtCenter * falloff * exposureScale;

                // Roofed buildings within the blast zone take reduced collateral damage
                if (context.map.roofGrid.Roofed(b.Position))
                    damagePercent *= 0.35f;

                int damage = (int)Math.Round(b.MaxHitPoints * damagePercent);
                if (damage > 0)
                    b.TakeDamage(new DamageInfo(DamageDefOf.Deterioration, damage));
            }

            // --- Floor damage in the collapse zone ---
            // Debris from the collapse tears up constructed floors. Chance scales with
            // proximity to epicenter and how long the site has been abandoned.
            foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, (float)radius, useCenter: true))
            {
                if (!cell.InBounds(map)) continue;
                if (map.terrainGrid.UnderTerrainAt(cell) == null) continue; // no constructed floor

                float dist    = center.DistanceTo(cell);
                float falloff = PowerFalloff(dist, radius);

                float destroyChance = falloff * timeSeverityScale * 0.85f * exposureScale;

                if (Rand.Value < destroyChance)
                    map.terrainGrid.RemoveTopLayer(cell, doLeavings: false);
            }
        }

        // -------------------------
        // Shared helpers
        // -------------------------

        // Samples the tile's temperature at monthly intervals to detect freeze-thaw cycles
        // (temperature crossing 0°C). Computed once and cached in OfflineDecayContext.
        public static bool ComputeFreezeThawCycles(OfflineDecayContext context)
        {
            const int samples      = 12;
            const int ticksPerYear = 60000 * 60;
            float min = float.MaxValue, max = float.MinValue;

            for (int i = 0; i < samples; i++)
            {
                int tick = context.startTick + (i * ticksPerYear / samples);
                float temp = GenTemperature.GetTemperatureFromSeasonAtTile(tick, context.tileId);
                if (temp < min) min = temp;
                if (temp > max) max = temp;
            }

            return min < 0f && max > 0f;
        }
    }
}
