using RimWorld;
using RimWorld.Planet;
using Verse;
using HarmonyLib;
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace KjellnersPersistentMaps
{
    public class PersistentMapData : IExposable
    {
        public byte[] terrainData;
        public byte[] roofData;
        public byte[] snowData;
        public byte[] pollutionData;
        public byte[] fogData;

        public List<PersistentBuildingData> buildings;
        public List<PersistentItemData> items;
        public List<PersistentPlantData> plants;

        public int abandonedAtTick;

        public void ExposeData()
        {
            DataExposeUtility.LookByteArray(ref terrainData, "terrainData");
            DataExposeUtility.LookByteArray(ref roofData, "roofData");
            DataExposeUtility.LookByteArray(ref snowData, "snowData");
            DataExposeUtility.LookByteArray(ref pollutionData, "pollutionData");
            DataExposeUtility.LookByteArray(ref fogData, "fogData");

            Scribe_Collections.Look(ref buildings, "buildings", LookMode.Deep);
            Scribe_Collections.Look(ref items, "items", LookMode.Deep);
            Scribe_Collections.Look(ref plants, "plants", LookMode.Deep);

            Scribe_Values.Look(ref abandonedAtTick, "abandonedAtTick", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit && buildings == null)
                buildings = new List<PersistentBuildingData>();
            
        }
    }

    public class PersistentBuildingData : IExposable
    {
        public string defName;
        public string stuffDefName;
        public string factionDefName;

        public IntVec3 position;
        public int rotation;
        public int hitPoints;

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref stuffDefName, "stuffDefName");
            Scribe_Values.Look(ref factionDefName, "factionDefName");

            Scribe_Values.Look(ref position, "position");
            Scribe_Values.Look(ref rotation, "rotation");
            Scribe_Values.Look(ref hitPoints, "hitPoints");
        }

        public static bool IsSafePersistentBuilding(Thing t)
        {
            if (t.def.category != ThingCategory.Building)
                return false;

            if (!t.def.destroyable)
                return false;

            if (t.def.IsBlueprint || t.def.IsFrame)
                return false;

            return true;
        }

    }

    public class PersistentItemData : IExposable
    {
        public string defName;
        public string stuffDefName;
        public IntVec3 position;
        public int stackCount;
        public int hitPoints;

        public float rotProgress;

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref stuffDefName, "stuffDefName");
            Scribe_Values.Look(ref position, "position");
            Scribe_Values.Look(ref stackCount, "stackCount");
            Scribe_Values.Look(ref hitPoints, "hitPoints");

            Scribe_Values.Look(ref rotProgress, "rotProgress", 0f);
        }
        
        public static bool IsSafePersistentItem(Thing t)
        {
            if (t.def.category != ThingCategory.Item)
                return false;

            if (!t.def.destroyable)
                return false;

            if (t is Pawn)
                return false;

            if (t is Corpse)
                return false;

            if (t is MinifiedThing)
                return false;

            if (t.def.IsBlueprint || t.def.IsFrame)
                return false;

            return true;
        }

    }

    public class PersistentPlantData : IExposable
    {
        public string defName;
        public IntVec3 position;
        public float growth;
        public int hitPoints;

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref position, "position");
            Scribe_Values.Look(ref growth, "growth");
            Scribe_Values.Look(ref hitPoints, "hitPoints");
        }
    }

}