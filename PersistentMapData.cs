using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;
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

        public List<Thing> savedThings;

        // Lords that owned non-player non-humanlike pawns (mech cluster lords, etc.).
        // Saved in the same XML document as savedThings so ownedPawns cross-refs resolve.
        public List<Lord> savedLords;

        public int abandonedAtTick;

        public void ExposeData()
        {
            DataExposeUtility.LookByteArray(ref terrainData, "terrainData");
            DataExposeUtility.LookByteArray(ref roofData, "roofData");
            DataExposeUtility.LookByteArray(ref snowData, "snowData");
            DataExposeUtility.LookByteArray(ref pollutionData, "pollutionData");
            DataExposeUtility.LookByteArray(ref fogData, "fogData");

            Scribe_Collections.Look(ref savedThings, "savedThings", LookMode.Deep);
            Scribe_Collections.Look(ref savedLords, "savedLords", LookMode.Deep);

            Scribe_Values.Look(ref abandonedAtTick, "abandonedAtTick", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                savedThings ??= new List<Thing>();
                savedLords  ??= new List<Lord>();
            }
        }

        public static bool ShouldPersistThing(Thing t)
        {
            if (t.Destroyed) return false;
            if (t is Pawn p)
            {
                if (p.RaceProps.Humanlike) return false; // transient NPCs; colonists leave with caravan
                // Non-humanlike world pawns (Megascarab bodyguards etc.) stay in WorldPawns
                // even while spawned; deep-saving them would cause an ID collision on restore.
                if (Find.WorldPawns.GetSituation(p) != WorldPawnSituation.None) return false;
            }
            // Corpses are saved (all types: player, enemy, mech, animal, in grave or standalone).
            // Inner-pawn cross-refs resolve via RefInjector. Any WorldPawns ID collision for
            // the inner pawn is cleaned up by RemoveWorldPawnGhosts before loading.
            if (t is Building_Casket casket)
            {
                // ExtractCryoColonists and ExtractCasketNonPlayerPawns run before this
                // filter, so caskets should be empty by the time we reach here.
                // Safety net: skip any casket still holding a world-pawn occupant;
                // deep-saving a world-pawn would cause an ID collision on restore.
                ThingOwner held = casket.GetDirectlyHeldThings();
                for (int i = 0; i < held.Count; i++)
                {
                    if (held[i] is Pawn ip)
                    {
                        if (Find.WorldPawns.GetSituation(ip) != WorldPawnSituation.None) return false;
                    }
                }
            }
            if (t.def.IsBlueprint) return false;
            if (t.def.category == ThingCategory.Mote) return false;
            if (t.def.category == ThingCategory.Ethereal) return false;
            if (t is Skyfaller) return false;
            if (t is Projectile) return false;
            // listerThings.AllThings only contains spawned things, so this should always be true.
            // Defensive guard in case ShouldPersistThing is ever called from a non-listerThings source.
            if (!t.Spawned) return false;
            // Monolith excluded: should regenerate naturally on new tiles rather than being
            // frozen in our save. All other non-destroyable things (geysers etc.) ARE saved.
            if (t.def.defName == "VoidMonolith") return false;

            return true;
        }

    }
}