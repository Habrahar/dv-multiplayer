using DV.InventorySystem;
using DV.Utils;
using JetBrains.Annotations;
using Multiplayer.Components.Networking;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using System.Collections.Generic;
using UnityEngine;

namespace Multiplayer.Components.SaveGame;

/// <summary>
/// Tells the host what is on this client's belt, so it can be remembered (B13).
///
/// The inventory lives here: it is this game that owns the slots, and the host has no way to
/// read them. Until now nobody told it, so the host handed every joiner the same hardcoded kit
/// and anything bought in a shop was gone by the next login.
/// </summary>
public class NetworkedPlayerInventory : SingletonBehaviour<NetworkedPlayerInventory>
{
    // A single action can touch two slots and fire twice, and swapping things about fires on
    // every step. Coalesce: the belt is worth reporting once it settles, not per movement.
    private const float SEND_PERIOD = 1f;

    private bool dirty;
    private float lastSent;

    protected override void Awake()
    {
        base.Awake();

        //the host's own belt is saved by vanilla, as their wallet and licences are
        if (NetworkLifecycle.Instance.IsHost())
            return;

        Inventory.Instance.InventoryStatusChanged += OnInventoryStatusChanged;
        NetworkLifecycle.Instance.OnTick += OnTick;
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();

        if (UnloadWatcher.isQuitting || UnloadWatcher.isUnloading)
            return;

        if (NetworkLifecycle.Instance.IsHost())
            return;

        if (Inventory.Instance != null)
            Inventory.Instance.InventoryStatusChanged -= OnInventoryStatusChanged;

        NetworkLifecycle.Instance.OnTick -= OnTick;
    }

    private void OnInventoryStatusChanged(InventorySlotState primarySlotState, InventoryActionType primaryActionType, InventorySlotState secondarySlotState, InventoryActionType secondaryActionType)
    {
        dirty = true;
    }

    private void OnTick(uint tick)
    {
        if (!dirty || Time.time - lastSent < SEND_PERIOD)
            return;

        if (NetworkLifecycle.Instance.Client?.LoadingState != PlayerLoadingState.Complete)
            return;

        dirty = false;
        lastSent = Time.time;

        SendInventory();
    }

    private void SendInventory()
    {
        List<PlayerItemSaveData> items = ReadInventory();

        if (items == null)
            return;

        Multiplayer.LogDebug(() => $"NetworkedPlayerInventory.SendInventory() {items.Count} item(s)");
        NetworkLifecycle.Instance.Client.SendPlayerInventory(items.ToArray());
    }

    /// <summary>
    /// Reads the belt through the game's own serialiser rather than walking the slots by hand:
    /// it is what vanilla saves with, so whatever it captures is exactly what a save would.
    /// </summary>
    private static List<PlayerItemSaveData> ReadInventory()
    {
        StorageController storage = StorageController.Instance;

        if (storage == null || storage.StorageInventory == null)
            return null;

        //a scratch save to serialise into; nothing else ever reads it
        SaveGameData scratch = new();
        storage.StorageInventory.SaveStorage(scratch);

        List<StorageItemData> stored = scratch.GetObject<List<StorageItemData>>(SaveGameKeys.Storage_Inventory);

        if (stored == null)
            return null;

        List<PlayerItemSaveData> items = new(stored.Count);

        foreach (StorageItemData item in stored)
            items.Add(PlayerItemSaveData.FromStorageItemData(item));

        return items;
    }

    [UsedImplicitly]
    public new static string AllowAutoCreate()
    {
        // Only clients have a belt to report; the host's is vanilla's to save.
        if (NetworkLifecycle.Instance.IsHost())
            return null;

        return $"[{nameof(NetworkedPlayerInventory)}]";
    }
}
