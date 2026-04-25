using System;
using System.Runtime.CompilerServices;
using Verse;

namespace PawnOwnership
{
    public class PawnOwnershipData : IExposable
    {
        public string owningPlayerId = null;

        public void ExposeData()
        {
            Scribe_Values.Look(ref owningPlayerId, "owningPlayerId", null);
        }
    }

    public static class PawnOwnershipExtension
    {
        private static readonly ConditionalWeakTable<Pawn, PawnOwnershipData> ownershipTable =
            new ConditionalWeakTable<Pawn, PawnOwnershipData>();

        public static void SetOwner(this Pawn pawn, string playerId)
        {
            if (pawn == null) return;
            var data = ownershipTable.GetOrCreateValue(pawn);
            data.owningPlayerId = playerId;
        }

        public static string GetOwner(this Pawn pawn)
        {
            if (pawn == null) return null;
            if (ownershipTable.TryGetValue(pawn, out var data))
                return data.owningPlayerId;
            return null;
        }
    }
}
