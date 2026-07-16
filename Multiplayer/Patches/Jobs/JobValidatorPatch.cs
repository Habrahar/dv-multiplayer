using System;
using System.Collections;
using DV.Booklets;
using DV.Logic.Job;
using DV.Printers;
using DV.ThingTypes;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Components.Networking.Jobs;
using Multiplayer.Components.Networking.World;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Jobs;
using Multiplayer.Networking.Managers.Server;
using Multiplayer.Networking.Packets.Clientbound.Jobs;
using UnityEngine;

namespace Multiplayer.Patches.Jobs;

[HarmonyPatch(typeof(JobValidator))]
public static class JobValidator_Patch
{
    private const float TIME_OUT = 3f;

    /// <summary>
    /// Who handed in the booklet ValidateJob is currently working on, or null outside such
    /// a call. A client's booklet reaches the validator through the server, so by the time
    /// the payout runs there is nothing left tying the money to a player. Read by
    /// <see cref="MoneyPrinterJobValidator_Patch"/>.
    /// </summary>
    public static ServerPlayer JobSubmitter { get; private set; }

    // Called by the server before it validates a booklet on a client's behalf.
    public static void SetJobSubmitter(ServerPlayer player)
    {
        JobSubmitter = player;
    }

    private static ServerPlayer HostPlayer()
    {
        NetworkServer server = NetworkLifecycle.Instance.Server;
        return server != null && server.TryGetServerPlayer(server.SelfId, out ServerPlayer player) ? player : null;
    }

    [HarmonyPatch(nameof(JobValidator.Start))]
    [HarmonyPostfix]
    private static void Start(JobValidator __instance)
    {
        //Multiplayer.Log($"JobValidator Awake!");
        NetworkedStationController.QueueJobValidator(__instance);
    }


    [HarmonyPatch(nameof(JobValidator.ProcessJobOverview))]
    [HarmonyPrefix]
    private static bool ProcessJobOverview(JobValidator __instance, JobOverview jobOverview, out Job __state)
    {
        // Vanilla destroys the overview on the way out, so remember the job for the postfix.
        __state = jobOverview?.job;

        if(__instance.bookletPrinter.IsOnCooldown)
        {
            __instance.bookletPrinter.PlayErrorSound();
            return false;
        }

        if(!NetworkedJob.TryGetFromJob(jobOverview.job, out NetworkedJob networkedJob) || jobOverview.job.State != JobState.Available)
        {
            NetworkLifecycle.Instance.Client.LogWarning($"Processing JobOverview {jobOverview?.job?.ID} {(networkedJob == null ? "NetworkedJob not found!, " : "")}Job state: {jobOverview?.job?.State}");
            __instance.bookletPrinter.PlayErrorSound();
            jobOverview.DestroyJobOverview();
            return false;
        }

        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.Server.Log($"Processing JobOverview {jobOverview?.job?.ID}");
            networkedJob.JobValidator = __instance;
            return true;
        }

        if (!networkedJob.ValidatorRequestSent)
            SendValidationRequest(__instance, networkedJob, ValidationType.JobOverview);

        return false;
    }

    // The host takes jobs through vanilla, which judges it correctly - they are the host, so
    // the singletons it consults really are theirs. All that is missing is naming the owner,
    // and only once vanilla has agreed to hand it over.
    [HarmonyPatch(nameof(JobValidator.ProcessJobOverview))]
    [HarmonyPostfix]
    private static void ProcessJobOverview_Postfix(Job __state)
    {
        if (__state == null || !NetworkLifecycle.Instance.IsHost())
            return;

        if (__state.State != JobState.InProgress)
            return;

        if (!NetworkedJob.TryGetFromJob(__state, out NetworkedJob networkedJob) || networkedJob.OwnerId != 0)
            return;

        ServerPlayer host = HostPlayer();

        if (host == null)
            return;

        networkedJob.SetOwner(host);
        host.AddTakenJob(networkedJob.NetId);
    }


    [HarmonyPatch(nameof(JobValidator.ValidateJob))]
    [HarmonyPrefix]
    private static bool ValidateJob_Prefix(JobValidator __instance, JobBooklet jobBooklet)
    {
        if (__instance.bookletPrinter.IsOnCooldown)
        {
            __instance.bookletPrinter.PlayErrorSound();
            return false;
        }

        if (!NetworkedJob.TryGetFromJob(jobBooklet.job, out NetworkedJob networkedJob) || jobBooklet.job.State != JobState.InProgress)
        {
            NetworkLifecycle.Instance.Client.LogWarning($"Validating Job {jobBooklet?.job?.ID} {(networkedJob == null ? "NetworkedJob not found!, " : "")}Job state: {jobBooklet?.job?.State}");
            __instance.bookletPrinter.PlayErrorSound();
            jobBooklet.DestroyJobBooklet();
            return false;
        }

        if (NetworkLifecycle.Instance.IsHost())
        {
            NetworkLifecycle.Instance.Server.Log($"Validating Job {jobBooklet?.job?.ID}");
            networkedJob.JobValidator = __instance;

            // The server names the submitter before validating on a client's behalf.
            // Nothing set means the host fed this validator itself.
            JobSubmitter ??= HostPlayer();
            return true;
        }

        if (!networkedJob.ValidatorRequestSent)
            SendValidationRequest(__instance, networkedJob, ValidationType.JobBooklet);

        return false;
    }

    // Runs even when the prefix skipped the original, so the submitter never outlives the
    // call that set it.
    [HarmonyPatch(nameof(JobValidator.ValidateJob))]
    [HarmonyPostfix]
    private static void ValidateJob_Postfix()
    {
        JobSubmitter = null;
    }

    private static void SendValidationRequest(JobValidator validator,NetworkedJob netJob, ValidationType type)
    {
        //find the current station we're at
        if (NetworkedStationController.GetFromJobValidator(validator, out NetworkedStationController networkedStation))
        {
            //Set initial job state parameters
            netJob.ValidatorRequestSent = true;
            netJob.ValidatorResponseReceived = false;
            netJob.ValidationAccepted = false;
            netJob.JobValidator = validator;
            netJob.ValidationType = type;

            NetworkLifecycle.Instance.Client.SendJobValidateRequest(netJob, networkedStation);
            CoroutineManager.Instance.StartCoroutine(AwaitResponse(validator, netJob));
        }
        else
        {
            NetworkLifecycle.Instance.Client.LogError($"Failed to validate {type} for {netJob?.Job?.ID}. NetworkedStation not found!");
            validator.bookletPrinter.PlayErrorSound();
        }
    }
    private static IEnumerator AwaitResponse(JobValidator validator, NetworkedJob networkedJob)
    {
        Multiplayer.LogDebug(() => $"Awaiting validation response for {networkedJob?.Job?.ID}...");

        float timeout = Time.time;

        //Book spawns can take a few seconds, this may be due to how the asset is loaded and rendered
        yield return new WaitUntil
        (
            () =>
            {
                return networkedJob.ValidatorResponseReceived || (Time.time - timeout > TIME_OUT);
            }
        );

        //WaitForSecondsRealtime(Math.Max(4f,(NetworkLifecycle.Instance.Client.Ping * 4f)/1000));

        bool received = networkedJob.ValidatorResponseReceived;
        bool accepted = networkedJob.ValidationAccepted;

        var receivedStr = received ? "received" : "timed out";
        var acceptedStr = accepted ? " Accepted" : " Rejected";

        NetworkLifecycle.Instance.Client.Log($"Job Validation Response {receivedStr} for {networkedJob?.Job?.ID}.{acceptedStr}");

        if (networkedJob == null)
        {
            validator.bookletPrinter.PlayErrorSound();
            yield break;
        }

        if(!received || !accepted)
        {
            PrintRefusal(validator, networkedJob, received);
        }

        networkedJob.ValidatorRequestSent = false;
        networkedJob.ValidatorResponseReceived = false;
        networkedJob.ValidationAccepted = false;
        networkedJob.RefusalReason = ClientboundJobValidateResponsePacket.RefusalReason.Accepted;
    }

    /// <summary>
    /// Prints the same report vanilla prints when it refuses a job itself. Vanilla builds it from
    /// the job, so it names the licences that are missing - we only have to say which case it is.
    /// A refusal we timed out on says nothing, so it keeps the bare error sound.
    /// </summary>
    private static void PrintRefusal(JobValidator validator, NetworkedJob networkedJob, bool received)
    {
        PrinterController printer = validator.bookletPrinter;

        if (!received || networkedJob.Job == null)
        {
            printer.PlayErrorSound();
            return;
        }

        switch (networkedJob.RefusalReason)
        {
            // Vanilla's own mapping (JobValidator.ProcessJobOverview): the same report covers
            // both, and the flag is what tells them apart on the page.
            case ClientboundJobValidateResponsePacket.RefusalReason.LicencesMissing:
                BookletCreator.CreateMissingLicenseReport(networkedJob.Job, true, printer.spawnAnchor.position, printer.spawnAnchor.rotation, WorldMover.OriginShiftParent);
                break;

            case ClientboundJobValidateResponsePacket.RefusalReason.NoFreeSlots:
                BookletCreator.CreateMissingLicenseReport(networkedJob.Job, false, printer.spawnAnchor.position, printer.spawnAnchor.rotation, WorldMover.OriginShiftParent);
                break;

            // Vanilla has no paper for these - it cannot happen on your own: nobody else exists
            // to take the job first.
            default:
                printer.PlayErrorSound();
                return;
        }

        Multiplayer.Log($"[Diag] Jobs: {networkedJob.Job.ID} refused - {networkedJob.RefusalReason}. Printing the report");

        printer.PlayErrorSound();
        printer.Print();
    }
}
