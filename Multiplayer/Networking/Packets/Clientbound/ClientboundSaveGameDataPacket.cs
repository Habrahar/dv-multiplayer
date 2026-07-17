using DV.InventorySystem;
using DV.JObjectExtstensions;
using DV.Logic.Job;
using DV.ServicePenalty;
using DV.UserManagement;
using Multiplayer.Components.Networking;
using Multiplayer.Components.SaveGame;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Multiplayer.Networking.Packets.Clientbound;

public class ClientboundSaveGameDataPacket
{
    public string GameMode { get; set; }
    public string SerializedDifficulty { get; set; }
    public float Money { get; set; }
    public string[] AcquiredGeneralLicenses { get; set; }
    public string[] AcquiredJobLicenses { get; set; }
    public string[] UnlockedGarages { get; set; }
    public Vector3 Position { get; set; }
    public float Rotation { get; set; }

    public bool HasDebt { get; set; }
    // public string[] Debt_existing_locos { get; set; }
    // public string[] Debt_deleted_locos { get; set; }
    // public string[] Debt_existing_jobs { get; set; }
    // public string[] Debt_staged_jobs { get; set; }
    // public string Debt_existing_jobless_cars { get; set; }
    // public string Debt_deleted_jobless_cars { get; set; }
    // public string Debt_insurance { get; set; }

    public PlayerItemSaveData[] PlayerItems { get; set; }

    public float JobManagerTime { get; set; }

    public static ClientboundSaveGameDataPacket CreatePacket(ServerPlayer player)
    {
        Multiplayer.LogDebug(() => $"ClientboundSaveGameDataPacket.CreatePacket() for player (is null: {player == null}) {player?.Username} ({player?.Guid})");
        if (WorldStreamingInit.isLoaded)
            SaveGameManager.Instance.UpdateInternalData();

        SaveGameData data = SaveGameManager.Instance.data;

        JObject difficulty = new();
        DifficultyDataUtils.SetDifficultyToJSON(difficulty, NetworkLifecycle.Instance.Server.Difficulty);

        JObject playerData = NetworkedSaveGameManager.Instance.Server_GetPlayerData(data, player.Guid);

        // Restore the player's own balance before it goes out in the packet below.
        // The host is skipped: it has no saved player data, and its wallet is the
        // local Inventory, which the vanilla save has already restored.
        if (!player.IsHost)
        {
            player.SetMoney(playerData?.GetFloat(SaveGameKeys.Player_money) ?? ServerPlayer.STARTING_MONEY);

            // Null, not empty, for a first-time player: LoadLicenses reads that as "give them
            // the starting set". An empty array is a player who genuinely owns nothing.
            player.LoadLicenses
            (
                playerData?.GetStringArray(SaveGameKeys.Licenses_General),
                playerData?.GetStringArray(SaveGameKeys.Licenses_Jobs)
            );
        }

        Multiplayer.LogDebug(() =>
        {
            string unlockedGen = string.Join(", ", UnlockablesManager.Instance.UnlockedGeneralLicenses);
            string packetGen = string.Join(", ", data.GetStringArray(SaveGameKeys.Licenses_General));

            string unlockedJob = string.Join(", ", UnlockablesManager.Instance.UnlockedJobLicenses);
            string packetJob = string.Join(", ", data.GetStringArray(SaveGameKeys.Licenses_Jobs));

            return $"ClientboundSaveGameDataPacket.CreatePacket() UnlockedGen: {{{unlockedGen}}}, PacketGen: {{{packetGen}}},  UnlockedJob: {{{unlockedJob}}}, PacketJob: {{{packetJob}}}";
        });

        // The belt they logged out with. Null means the save has never seen this player, and
        // only then do they get the starting kit - handing that out every time was what made a
        // shop purchase vanish by the next login (B13). The host's own belt is vanilla's to
        // load, exactly as their wallet and licences are.
        PlayerItemSaveData[] savedInventory = player.IsHost
            ? null
            : NetworkedSaveGameManager.Server_GetPlayerInventory(playerData);

        PlayerItemSaveData[] playerItems = savedInventory ?? StartingInventory();

        Multiplayer.Log($"[Diag] Inventory: {player.Username} joins with {(savedInventory != null ? $"their saved belt - {savedInventory.Length} item(s)" : "the starting kit")}");

        return new ClientboundSaveGameDataPacket
        {
            GameMode = data.GetString(SaveGameKeys.Game_mode),
            SerializedDifficulty = difficulty.ToString(Formatting.None),
            Money = (float)player.Money,
            // The host's licences come from the vanilla save, which owns them; everyone else
            // gets their own. Handing out the save's arrays is what gave every joiner the
            // host's progress. Garages stay shared - see NetworkedSaveGameManager.
            AcquiredGeneralLicenses = player.IsHost ? data.GetStringArray(SaveGameKeys.Licenses_General) : player.AcquiredGeneralLicenses.ToArray(),
            AcquiredJobLicenses = player.IsHost ? data.GetStringArray(SaveGameKeys.Licenses_Jobs) : player.AcquiredJobLicenses.ToArray(),
            UnlockedGarages = data.GetStringArray(SaveGameKeys.Garages),
            Position = playerData?.GetVector3(SaveGameKeys.Player_position) ?? LevelInfo.DefaultSpawnPosition,
            Rotation = playerData?.GetFloat(SaveGameKeys.Player_rotation) ?? LevelInfo.DefaultSpawnRotation.y,
            HasDebt = data.GetFloat(SaveGameKeys.Debt_total).GetValueOrDefault(CareerManagerDebtController.Instance != null ? CareerManagerDebtController.Instance.NumberOfNonZeroPricedDebts : 0) > 0,
            // Debt_existing_locos = data.GetJObjectArray(SaveGameKeys.Debt_existing_locos)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_deleted_locos = data.GetJObjectArray(SaveGameKeys.Debt_deleted_locos)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_existing_jobs = data.GetJObjectArray(SaveGameKeys.Debt_existing_jobs)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_staged_jobs = data.GetJObjectArray(SaveGameKeys.Debt_staged_jobs)?.NotNull().Select(j => j.ToString()).ToArray(),
            // Debt_existing_jobless_cars = data.GetJObject(SaveGameKeys.Debt_existing_jobless_cars)?.ToString(),
            // Debt_deleted_jobless_cars = data.GetJObject(SaveGameKeys.Debt_deleted_jobless_cars)?.ToString(),
            // Debt_insurance = data.GetJObject(SaveGameKeys.Debt_insurance)?.ToString()

            JobManagerTime = JobsManager.Instance.Time,

            PlayerItems = playerItems
        };
    }

    /// <summary>
    /// What a first-time player starts with. This was handed to everyone on every login; it is
    /// the equivalent of ServerPlayer.StartingGeneralLicenses, and belongs in the same place -
    /// given once, then theirs to keep or lose.
    /// </summary>
    private static PlayerItemSaveData[] StartingInventory()
    {
        string[] items = ["shovel", "lighter", "Oiler", "Lantern", "Flashlight", "Hanger", "DuctTape"];
        string[] states = ["", "", "", "", "{\"Restock\": true,\"Battery_power\": 100}", "", ""];

        PlayerItemSaveData[] kit = new PlayerItemSaveData[items.Length];

        for (int i = 0; i < items.Length; i++)
            kit[i] = new PlayerItemSaveData
            {
                ItemPrefabName = items[i],
                BelongsToPlayer = true,
                InventorySlotIndex = 14 + i,
                State = string.IsNullOrEmpty(states[i]) ? [] : JObject.Parse(states[i])
            };

        return kit;
    }

    public ClientboundSaveGameDataPacket Clone()
    {
        return MemberwiseClone() as ClientboundSaveGameDataPacket;
    }
}
