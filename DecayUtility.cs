using RimWorld;
using RimWorld.Planet;
using Verse;
using UnityEngine;
using System;

namespace KjellnersPersistentMaps
{
    public struct OfflineDecayResult
    {
        public bool shouldSpawn;
        public int resultingHp;
        public float resultingRotProgress;
    }

    public struct OfflineDecayContext
    {
        public Map map;
        public int startTick;
        public int ticksPassed;
        public int tileId;
        public float rainfall;
    }

    public static class DecayUtility
    {
        public static OfflineDecayResult ApplyOfflineDecay(
            OfflineDecayContext context,
            PersistentItemData itemData)
        {
            OfflineDecayResult result = new OfflineDecayResult
            {
                shouldSpawn = true,
                resultingHp = itemData.hitPoints,
                resultingRotProgress = 0f
            };

            ThingDef def = DefDatabase<ThingDef>
                .GetNamedSilentFail(itemData.defName);

            if (def == null)
            {
                result.shouldSpawn = false;
                return result;
            }

            bool roofed = context.map.roofGrid.Roofed(itemData.position);

            // FOOD ROT 
            if (IsRottingFood(def))
            {
                var rotProps = def.GetCompProperties<CompProperties_Rottable>();

                float rotProgress =
                    CalculateOfflineRot(def, itemData, context);

                result.resultingRotProgress = rotProgress;

                if (rotProgress >= rotProps.TicksToRotStart)
                {
                    result.shouldSpawn = false;
                    return result;
                }
            }

            // OUTDOOR DAMAGE 
            if (!roofed)
            {
                int hpLoss = CalculateOutdoorDeterioration(context);
                result.resultingHp -= hpLoss;
            }

            if (result.resultingHp <= 0)
            {
                result.shouldSpawn = false;
            }

            return result;
        }

        private static bool IsRottingFood(ThingDef def)
        {
            return def.IsIngestible &&
                   def.GetCompProperties<CompProperties_Rottable>() != null;
        }

        private static float CalculateOfflineRot(
            ThingDef def,
            PersistentItemData itemData,
            OfflineDecayContext context)
        {
            var rotProps = def.GetCompProperties<CompProperties_Rottable>();
            if (rotProps == null)
                return itemData.rotProgress;

            float rot = itemData.rotProgress;

            int startTick = context.startTick;
            int endTick = startTick + context.ticksPassed;

            const int hourStep = 2500; // 1 in-game hour

            for (int tick = startTick; tick < endTick; tick += hourStep)
            {
                int remaining = endTick - tick;
                int step = remaining < hourStep ? remaining : hourStep;

                // Seasonal temperature
                float temp =
                    GenTemperature.GetTemperatureFromSeasonAtTile(
                        tick,
                        context.tileId);

                // Add day/night swing
                temp += GenTemperature.OffsetFromSunCycle(
                    tick,
                    context.tileId);

                float rotRate = GenTemperature.RotRateAtTemperature(temp);

                // Vanilla 1.6 rot math
                rot += rotRate * step;

                // Early exit if fully rotten
                if (rot >= rotProps.TicksToRotStart)
                    return rot;
            }

            return rot;
        }

        private static int CalculateOutdoorDeterioration(
            OfflineDecayContext context)
        {
            float deteriorationIntervals =
                context.ticksPassed / 250f;

            // Clamp 0..1 manually
            float normalizedRain =
                Math.Max(0f, Math.Min(1f, context.rainfall / 4000f));

            // Manual Lerp between 0.5 and 2.0
            float rainFactor =
                0.5f + (2f - 0.5f) * normalizedRain;

            float expectedDamage =
                deteriorationIntervals * 0.015f * rainFactor;

            return (int)Math.Round(expectedDamage);
        }
    }
}