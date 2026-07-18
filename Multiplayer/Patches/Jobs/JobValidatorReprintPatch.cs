using System.Collections;
using System.Collections.Generic;
using DV.CabControls;
using DV.Logic.Job;
using DV.Printers;
using DV.Utils;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using UnityEngine;

namespace Multiplayer.Patches.Jobs;

/// <summary>
/// The "reprint active job booklets" button reprints every booklet in the game
/// (JobBooklet.allExistingJobBooklets), and the host holds a hidden booklet for every client's
/// job too - so pressing it spat everyone's jobs out at the host. Reprint only the presser's own
/// (B33): the booklet's NetworkedJob owner must be this machine's player.
/// </summary>
[HarmonyPatch(typeof(JobValidator), nameof(JobValidator.SummonAllActiveJobBooklets))]
public static class JobValidator_SummonAllActiveJobBooklets_Patch
{
    private static bool Prefix(JobValidator __instance)
    {
        // Single player owns every booklet, so vanilla is already right.
        if (!NetworkLifecycle.Instance.IsHost() && NetworkLifecycle.Instance.Client == null)
            return true;

        if (NetworkLifecycle.Instance.IsHost() && NetworkLifecycle.Instance.Server != null
            && NetworkLifecycle.Instance.Server.IsSinglePlayer)
            return true;

        CoroutineManager.Instance.StartCoroutine(ReprintOwnBooklets(__instance));
        return false;
    }

    private static bool IsMine(JobBooklet booklet)
    {
        if (booklet == null || !booklet.HasJobAssigned())
            return false;

        if (!NetworkedJob.TryGetFromJob(booklet.job, out NetworkedJob netJob))
            return false;

        return netJob.OwnerId == NetworkLifecycle.Instance.Client.PlayerId;
    }

    // Vanilla's SummonAllActiveJobBookletsCoro, narrowed to the local player's own booklets.
    private static IEnumerator ReprintOwnBooklets(JobValidator validator)
    {
        List<JobBooklet> mine = new List<JobBooklet>();

        foreach (JobBooklet booklet in JobBooklet.allExistingJobBooklets)
            if (IsMine(booklet))
                mine.Add(booklet);

        if (mine.Count == 0)
        {
            validator.bookletPrinter.PlayErrorSound();
            yield break;
        }

        yield return null;

        PrinterController printer = validator.bookletPrinter;
        Vector3 up = Vector3.up * 0.02f;
        Vector3 forward = printer.spawnAnchor.forward * 0.55f;
        int i = 0;

        //first pass: pull each booklet out of wherever it is and stage it hidden at the printer
        foreach (JobBooklet booklet in mine)
        {
            if (booklet == null || !booklet.HasJobAssigned())
                continue;

            ItemBase item = booklet.GetComponent<ItemBase>();
            if (item == null)
                continue;

            if (item.SnappableItem?.SnappedTo != null)
                item.SnappableItem.SnappedTo.UnsnapItem(forced: true);

            TrainPhysicsLod.RemoveItemFromAnyCar(item);
            Vector3 position = printer.spawnAnchor.position + up * i + forward;
            StorageController.RemoveItemFromCurrentStorageAndAddToWorld(item, position, printer.spawnAnchor.rotation);
            item.gameObject.SetActive(false);
            i++;
        }

        //second pass: print and reveal them one at a time
        foreach (JobBooklet booklet in mine)
        {
            if (booklet == null || !booklet.HasJobAssigned())
                continue;

            ItemBase item = booklet.GetComponent<ItemBase>();
            if (item == null)
                continue;

            printer.Print(ignoreCooldown: true);
            item.transform.position = printer.spawnAnchor.position;
            item.gameObject.SetActive(true);

            yield return WaitFor.Seconds(1.25f);
        }
    }
}
