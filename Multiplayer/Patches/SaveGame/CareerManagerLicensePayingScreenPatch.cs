using DV.ServicePenalty.UI;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data.RPCs;
using UnityEngine;

namespace Multiplayer.Patches.SaveGame;

[HarmonyPatch(typeof(CareerManagerLicensePayingScreen), nameof(CareerManagerLicensePayingScreen.HandleInputAction))]
public static class CareerManagerLicensePayingScreenPatch
{
    private static bool Prefix(CareerManagerLicensePayingScreen __instance, InputAction input)
    {
        if (input != InputAction.Confirm || NetworkLifecycle.Instance.IsHost())
            return true;

        // Vanilla logs its own error for this state; nothing for us to send.
        if (!__instance.IsJobLicense && !__instance.IsGeneralLicense)
            return true;

        // Buy() consumes the cash deposited in the register and plays its own rejection
        // sound when there is too little. Vanilla only breaks out of Confirm in that case,
        // so stopping here matches it; handing back would just sound the rejection twice.
        if (!__instance.cashReg.Buy())
            return false;

        bool isJobLicense = __instance.IsJobLicense;
        string licenseId = isJobLicense ? __instance.jobLicenseToBuy.id : __instance.generalLicenseToBuy.id;

        // Vanilla grants the license here. On a client only the server may do that, so we
        // hold the screen until it answers rather than reporting a sale that never happened.
        RpcTicket ticket = RpcManager.Instance
            .CreateTicket(Mathf.Max(NetworkLifecycle.Instance.Client.RPC_Timeout, 2f))
            .OnResolve(response =>
            {
                if (response is not LicensePurchaseResponse purchase)
                {
                    Refuse(__instance, Locale.CAREER_MANAGER__LICENSE_REFUSED);
                    return;
                }

                if (purchase.Response == LicensePurchaseResponse.ResponseType.Success)
                {
                    // The license and the new balance arrive as their own packets.
                    if (__instance != null)
                        __instance.screenSwitcher.SetActiveDisplay(__instance.licensesScreen);
                    return;
                }

                Refuse(__instance, purchase.Response switch
                {
                    LicensePurchaseResponse.ResponseType.OutstandingDebts => Locale.CAREER_MANAGER__LICENSE_REFUSED_DEBTS,
                    LicensePurchaseResponse.ResponseType.InsufficientFunds => Locale.CAREER_MANAGER__LICENSE_REFUSED_FUNDS,
                    _ => Locale.CAREER_MANAGER__LICENSE_REFUSED
                });
            })
            .OnTimeout(() => Refuse(__instance, Locale.CAREER_MANAGER__LICENSE_REFUSED_TIMEOUT));

        NetworkLifecycle.Instance.Client.SendLicensePurchaseRequest(ticket.TicketId, licenseId, isJobLicense);
        return false;
    }

    // The server returns the money it refused to take, so the balance corrects itself. All
    // that is left is telling the player why, on the screen they are already looking at.
    private static void Refuse(CareerManagerLicensePayingScreen screen, string reason)
    {
        if (screen == null)
            return;

        screen.insertWallet.text = reason;
    }
}
