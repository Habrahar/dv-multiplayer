using DV.ServicePenalty.UI;
using HarmonyLib;
using Multiplayer.Components.Networking;

namespace Multiplayer.Patches.SaveGame;

[HarmonyPatch(typeof(CareerManagerLicensePayingScreen), nameof(CareerManagerLicensePayingScreen.HandleInputAction))]
public static class CareerManagerLicensePayingScreenPatch
{
    private static bool Prefix(CareerManagerLicensePayingScreen __instance, InputAction input)
    {
        if (input != InputAction.Confirm || NetworkLifecycle.Instance.IsHost())
            return true;

        // The purchase did not go through locally, most often because the player cannot
        // afford it. Hand back to the vanilla handler so it runs its own rejection flow;
        // swallowing this leaves the screen frozen with no feedback at all.
        if (!__instance.cashReg.Buy())
            return true;

        if (__instance.IsJobLicense)
            NetworkLifecycle.Instance.Client.SendLicensePurchaseRequest(__instance.jobLicenseToBuy.id, __instance.IsJobLicense);
        else if (__instance.IsGeneralLicense)
            NetworkLifecycle.Instance.Client.SendLicensePurchaseRequest(__instance.generalLicenseToBuy.id, __instance.IsJobLicense);

        __instance.screenSwitcher.SetActiveDisplay(__instance.licensesScreen);
        return false;
    }
}
