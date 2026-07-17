using DV.InventorySystem;
using DV.Teleporters;
using DV.UI;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data.RPCs;
using Multiplayer.Utils;
using UnityEngine;

namespace Multiplayer.Patches.Train;

/// <summary>
/// Vanilla pays for a fast travel out of the local Inventory. On a client that is only a mirror
/// of the wallet the host keeps, so the trip cost nothing and the next balance packet handed the
/// money straight back (B11). Ask the host instead: it works out the fare from where this player
/// really is, charges it, and only then do we travel.
/// </summary>
[HarmonyPatch(typeof(FastTravelController), nameof(FastTravelController.OnFastTravelRequested))]
public static class FastTravelController_OnFastTravelRequested_Patch
{
    private static bool Prefix(FastTravelController __instance, bool withLoco)
    {
        // The host's Inventory *is* the authoritative wallet, so vanilla is already right.
        if (NetworkLifecycle.Instance.IsHost())
            return true;

        // Travelling with a loco is off for clients (see the button patch below), so anything
        // arriving here with it set did not come from the interface.
        if (withLoco)
            return false;

        FastTravelDestination marker = __instance.lastMarkerClicked;

        if (marker == null)
            return false;

        // Vanilla's own gate - licences, derailment, feature flags. None of it is money, so it
        // is honest to ask locally before troubling the host.
        FastTravelData ftData = FastTravelController.ExtractFastTravelData(marker, PlayerManager.Car);

        if (!ftData.CanTravelWithoutLoco)
        {
            Multiplayer.LogDebug(() => $"FastTravel refused locally for '{marker.MarkerName}'");
            __instance.OnTeleportDenied();
            return false;
        }

        RpcTicket ticket = RpcManager.Instance
            .CreateTicket(Mathf.Max(NetworkLifecycle.Instance.Client.RPC_Timeout, 2f))
            .OnResolve(response =>
            {
                if (response is FastTravelResponse fare && fare.Response == FastTravelResponse.ResponseType.Success)
                {
                    Multiplayer.Log($"[Diag] Money: fast travel to '{marker.MarkerName}' charged ${fare.Price} by the host");

                    //the host already took the money and is sending the new balance
                    if (fare.Price > 0)
                        __instance.moneyRemovedSound?.Play2D();

                    SingletonBehaviour<CoroutineManager>.Instance.StartCoroutine(
                        __instance.FastTravel(marker, __instance.FastTravelWithoutLocomotive, ftData.isDestinationWithinSameTrainset, ftData.fastTravelDuration));
                    return;
                }

                Multiplayer.LogWarning($"Fast travel to '{marker.MarkerName}' refused: {(response as FastTravelResponse)?.Response.ToString() ?? "no answer"}");
                __instance.OnTeleportDenied();
            })
            .OnTimeout(() =>
            {
                Multiplayer.LogWarning($"Fast travel to '{marker.MarkerName}' timed out");
                __instance.OnTeleportDenied();
            });

        NetworkLifecycle.Instance.Client.SendFastTravelRequest(ticket.TicketId, marker.MarkerName);
        return false;
    }
}

[HarmonyPatch(typeof(FastTravelUIController))]
public static class FastTravelUIControllerPatch
{
    [HarmonyPatch(typeof(FastTravelUIController), nameof(FastTravelUIController.RefreshInterface))]
    private static void Postfix(FastTravelUIController __instance)
    {
        // If the host is playing alone, don't disable the fast travel with loco button
        if (NetworkLifecycle.Instance.IsHost() && NetworkLifecycle.Instance.Server.PlayerCount == 1)
            return;

        if (__instance?.fastTravelWithLocoButton != null)
        {
            __instance.fastTravelWithLocoButton.ToggleInteractable(false);
        }
    }
}
