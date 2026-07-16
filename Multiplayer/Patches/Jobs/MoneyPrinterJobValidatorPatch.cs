using DV.Logic.Job;
using HarmonyLib;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data;

namespace Multiplayer.Patches.Jobs;

[HarmonyPatch(typeof(MoneyPrinterJobValidator))]
public static class MoneyPrinterJobValidator_Patch
{
    [HarmonyPatch(nameof(MoneyPrinterJobValidator.PrintPayment))]
    [HarmonyPrefix]
    private static bool PrintPayment(Job job)
    {
        // A client's booklet is validated by the server, so vanilla would print the wage as
        // banknotes in the host's world, where item sync cannot show them to anyone else and
        // only the host could pick them up. Pay the submitter's wallet instead.
        if (!NetworkLifecycle.Instance.IsHost() || NetworkLifecycle.Instance.Server.IsSinglePlayer)
            return true;

        ServerPlayer submitter = JobValidator_Patch.JobSubmitter;

        if (submitter == null)
        {
            Multiplayer.LogWarning($"PrintPayment() No submitter for job {job?.ID}; leaving the payout to vanilla");
            return true;
        }

        float wage = job.GetWageForTheJob();
        submitter.AddMoney(wage);
        Multiplayer.Log($"[Diag] Pay: job {job?.ID} paid ${wage} straight into {submitter.Username}'s wallet (now ${submitter.Money}). No banknotes printed");

        return false;
    }
}
