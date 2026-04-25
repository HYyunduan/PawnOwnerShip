using System;
using System.Runtime.CompilerServices;
using Verse;

namespace PawnOwnership
{
    public static class PawnOwnershipExtension
    {
        private static OwnershipWorldComponent Tracker
            => Find.World.GetComponent<OwnershipWorldComponent>();

        public static void SetOwner(this Pawn pawn, string playerId)
        {
            if (pawn == null) return;
            Tracker.SetOwner(pawn.thingIDNumber, playerId);
        }

        public static string GetOwner(this Pawn pawn)
        {
            if (pawn == null) return null;
            return Tracker.GetOwner(pawn.thingIDNumber);
        }
    }
}