// --- FIND THIS SECTION IN Update() ---
if (RouteDB.AutoGather && GatheringAM != null && GatheredItem != null && !Player.InGatheringAnimation)
{
    // ORIGINAL FIX: Added .IsVisible check to prevent state lock
    if (GatheringAM.Base->AtkUnitBase.IsVisible) 
    {
        SetState(State.Gathering);
        GatheringActions.UseNextBestAction(GatheringAM, GatheredItem);
        return;
    }
}

if (RouteDB.AutoGather && GatheringCollectableAM != null && !Player.InGatheringAnimation)
{
    // ORIGINAL FIX: Added .IsVisible check for collectables as well
    if (GatheringCollectableAM.Base->AtkUnitBase.IsVisible)
    {
        SetState(State.Gathering);
        GatheringActions.UseNextBestAction(GatheringCollectableAM);
        return;
    }
}
