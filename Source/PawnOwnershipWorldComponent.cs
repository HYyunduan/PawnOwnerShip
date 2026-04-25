using System;
using Verse;
using RimWorld.Planet;
using System.Collections.Generic;

public class OwnershipWorldComponent : WorldComponent
{
    private Dictionary<int, string> ownershipMap = new Dictionary<int, string>();

    public OwnershipWorldComponent(World world) : base(world) { }

    public void SetOwner(int pawnThingID, string playerId) 
        => ownershipMap[pawnThingID] = playerId;

    public string GetOwner(int pawnThingID) 
        => ownershipMap.TryGetValue(pawnThingID, out string id) ? id : null;

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref ownershipMap, "ownershipMap", LookMode.Value, LookMode.Value);
    }
}