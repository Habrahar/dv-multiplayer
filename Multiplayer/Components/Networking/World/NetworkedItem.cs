using DV.CabControls;
using DV.Interaction;
using DV.InventorySystem;
using DV.Items;
using Multiplayer.Components.Networking.Train;
using Multiplayer.Networking.Data;
using Multiplayer.Networking.Data.Items;
using Multiplayer.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Multiplayer.Components.Networking.World;

public enum ItemState : byte
{
    Dropped,        //belongs to the world
    Thrown,         //was thrown by player
    InHand,         //held by player
    InInventory,    //in player's inventory
    Attached        //attached to another object (e.g. EOT Lanterns)
}

public class NetworkedItem : IdMonoBehaviour<ushort, NetworkedItem>
{
    #region Lookup Cache
    private static readonly Dictionary<ItemBase, NetworkedItem> itemBaseToNetworkedItem = new(4096);

    public static Dictionary<ItemBase, NetworkedItem>.ValueCollection GetAll() => itemBaseToNetworkedItem.Values;

    // Belt-and-braces for B32: drop any entry whose key or value the engine has destroyed. Called
    // when a session starts, so dead rows a previous game leaked cannot pile up across loads.
    public static void PurgeDeadEntries()
    {
        List<ItemBase> dead = null;

        foreach (var kvp in itemBaseToNetworkedItem)
            if (kvp.Key == null || kvp.Value == null)
                (dead ??= new List<ItemBase>()).Add(kvp.Key);

        if (dead == null)
            return;

        foreach (var key in dead)
            itemBaseToNetworkedItem.Remove(key);

        Multiplayer.Log($"[Diag] Items: purged {dead.Count} dead lookup entr(y/ies) left over from a previous game.");
    }

    public static bool Get(ushort netId, out NetworkedItem obj)
    {
        bool b = Get(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
        obj = (NetworkedItem)rawObj;
        return b;
    }

    public static bool TryGet(ushort netId, out NetworkedItem obj)
    {
        bool b = TryGet(netId, out IdMonoBehaviour<ushort, NetworkedItem> rawObj);
        obj = (NetworkedItem)rawObj;
        return b;
    }

    public static bool GetItem(ushort netId, out ItemBase obj)
    {
        bool b = Get(netId, out NetworkedItem networkedItem);
        obj = b ? networkedItem.Item : null;
        return b;
    }

    public static bool TryGetNetworkedItem(ItemBase item, out NetworkedItem networkedItem)
    {
        return itemBaseToNetworkedItem.TryGetValue(item, out networkedItem);
    }

    public static bool TryGetNetId(ItemBase item, out ushort netID)
    {
        if (itemBaseToNetworkedItem.TryGetValue(item, out var networkedItem))
        {
            netID = networkedItem.NetId;
            return true;
        }

        netID = 0;
        return false;
    }
    #endregion

    private const float PositionThreshold = 0.1f;
    private const float RotationThreshold = 0.1f;

    public ItemBase Item { get; private set; }
    private GrabHandlerItem grabHandler;
    private SnappableItem snappableItem;
    private Component trackedItem;
    private List<object> trackedValues = new List<object>();
    public bool UsefulItem { get; private set; } = false;
    public Type TrackedItemType { get; private set; }
    public uint LastDirtyTick { get; private set; }
    private bool initialised;
    private bool registrationComplete = false;
    private Queue<ItemUpdateData> pendingSnapshots = new Queue<ItemUpdateData>();

    //Track dirty states
    private bool createdDirty = true;   //if set, we created this item dirty and have not sent an update
    private ItemState lastState;
    private bool stateDirty;
    private bool wasThrown;

    //Host only: a thrown or dropped item keeps moving after the state change, and nobody
    //re-reports where it stopped - every copy would come to rest somewhere else. Watch for
    //the rigidbody falling asleep and send one final position. A body that has not started
    //moving yet also reads as asleep, so wait until physics has visibly taken over; the
    //timeout covers an item that never wakes, or one that never rests (say, on a rolling car).
    private const uint SETTLE_TIMEOUT_TICKS = 5 * NetworkLifecycle.TICK_RATE;
    private const float REST_SPEED_SQR = 0.01f;  //0.1 m/s relative to whatever carries it
    private const float REST_SPIN_SQR = 0.05f;
    private const float STREAM_SNAP_DISTANCE_SQR = 0.25f;  //0.5 m apart: no longer a disagreement
    private const float STREAM_SMOOTHING = 0.35f;
    private const float STREAM_LEAD = 1f / NetworkLifecycle.TICK_RATE;  //a packet is a tick old
    private const float STREAM_CATCHUP = 4f;   //close the remaining gap over ~a quarter second
    private bool settleWatch;
    private bool settleSeenMoving;
    private uint settleWatchTick;
    private bool settleSyncDue;

    private Vector3 thrownPosition;
    private Quaternion thrownRotation;
    private Vector3 throwDirection;

    //Handle ownership
    public sbyte OwnerId { get; private set; } = -1; // 0 means no owner

    //public void SetOwner(ushort playerId)
    //{
    //    if (OwnerId != playerId)
    //    {
    //        if (OwnerId != 0)
    //        {
    //            NetworkedItemManager.Instance.RemoveItemFromPlayerInventory(this);
    //        }
    //        OwnerId = playerId;
    //        if (playerId != 0)
    //        {
    //            NetworkedItemManager.Instance.AddItemToPlayerInventory(playerId, this);
    //        }
    //    }
    //}

    protected override bool IsIdServerAuthoritative => true;

    protected override void Awake()
    {
        base.Awake();
        //Multiplayer.LogDebug(() => $"NetworkedItem.Awake() {name}");
        NetworkedItemManager.Instance.CheckInstance(); //Ensure the NetworkedItemManager is initialised

        Register();
    }

    protected void Start()
    {
        if (!initialised)
            Register();

        // Mark registration as complete for items that don't need tracked values
        if (!registrationComplete && !UsefulItem)
            registrationComplete = true;
    }

    public T GetTrackedItem<T>() where T : Component
    {
        return UsefulItem ? trackedItem as T : null;
    }

    public void Initialize<T>(T item, ushort netId = 0, bool createDirty = true) where T : Component
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.Initialize<{typeof(T)}>(netId: {netId}, name: {name}, createDirty: {createdDirty})");

        if (netId != 0)
            NetId = netId;

        trackedItem = item;
        TrackedItemType = typeof(T);
        UsefulItem = true;

        createdDirty = createDirty;

        if (Item == null)
            Register();

    }

    private bool Register()
    {
        if (initialised)
            return false;

        try
        {

            if (!TryGetComponent(out ItemBase itemBase))
            {
                Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}");
                return false;
            }

            Item = itemBase;
            itemBaseToNetworkedItem[Item] = this;

            Item.Grabbed += OnGrabbed;
            Item.Ungrabbed += OnUngrabbed;

            //Find special interaction components
            TryGetComponent<GrabHandlerItem>(out grabHandler);
            TryGetComponent<SnappableItem>(out snappableItem);

            lastState = GetItemState();
            stateDirty = false;

            initialised = true;
            return true;
        }
        catch (Exception ex)
        {
            Multiplayer.LogError($"NetworkedItem.Register() Unable to find ItemBase for {name}\r\n{ex.Message}");
            return false;
        }
    }

    private void OnUngrabbed(ControlImplBase obj)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.OnUngrabbed() NetID: {NetId}, {name}");
        stateDirty = true;
    }

    private void OnGrabbed(ControlImplBase obj)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.OnGrabbed() NetID: {NetId}, {name}");
        stateDirty = true;
    }

    public void OnThrow(Vector3 direction)
    {
        //block a received throw from 
        if (wasThrown)
        {
            wasThrown = false;
            return;
        }

        throwDirection = direction;
        thrownPosition = Item.transform.position;   //raw world; CreateUpdateData picks the frame
        thrownRotation = Item.transform.rotation;

        //Multiplayer.LogDebug(() => $"NetworkedItem.OnThrow() netId: {NetId}, Name: {name}, Raw Position: {Item.transform.position}, Position: {thrownPosition}, Rotation: {thrownRotation}, Direction: {throwDirection}");

        wasThrown = true;
        stateDirty = true;
    }


    #region Item Value Tracking
    public void RegisterTrackedValue<T>(string key, Func<T> valueGetter, Action<T> valueSetter, Func<T, T, bool> thresholdComparer = null, bool serverAuthoritative = false)
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.RegisterTrackedValue(\"{key}\", {valueGetter != null}, {valueSetter != null}, {thresholdComparer != null}, {serverAuthoritative}) itemNetId {NetId}, item name: {name}");
        trackedValues.Add(new TrackedValue<T>(key, valueGetter, valueSetter, thresholdComparer, serverAuthoritative));
    }

    public void FinaliseTrackedValues()
    {
        //Multiplayer.LogDebug(() => $"NetworkedItem.FinaliseTrackedValues() itemNetId: {NetId}, item name: {name}");

        while (pendingSnapshots.Count > 0)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.FinaliseTrackedValues() itemNetId: {NetId}, item name: {name}. Dequeuing");
            ApplySnapshot(pendingSnapshots.Dequeue());
        }

        registrationComplete = true;

    }

    private bool HasDirtyValues()
    {
        //clients should only send values that are not server authoritative
        if (!NetworkLifecycle.Instance.IsHost())
            return trackedValues.Any(tv => ((dynamic)tv).IsDirty && !((dynamic)tv).ServerAuthoritative);
        else
            return trackedValues.Any(tv => ((dynamic)tv).IsDirty);
    }

    private Dictionary<string, object> GetDirtyStateData()
    {
        var dirtyData = new Dictionary<string, object>();
        foreach (var trackedValue in trackedValues)
        {
            if (((dynamic)trackedValue).IsDirty)
            {
                dirtyData[((dynamic)trackedValue).Key] = ((dynamic)trackedValue).GetValueAsObject();
            }
        }
        return dirtyData;
    }
    private Dictionary<string, object> GetAllStateData()
    {
        var data = new Dictionary<string, object>();
        foreach (var trackedValue in trackedValues)
        {
            data[((dynamic)trackedValue).Key] = ((dynamic)trackedValue).GetValueAsObject();
        }
        return data;
    }

    private void MarkValuesClean()
    {
        foreach (var trackedValue in trackedValues)
        {
            ((dynamic)trackedValue).MarkClean();
        }
    }

    #endregion

    // Host only: the owner is gone, and nobody will ever send another snapshot for this item.
    // It was hidden on every other machine the moment they picked it up, so without this it
    // stays invisible and owned forever. Put it back in the world where they left it and let
    // the normal snapshot flow tell everyone.
    public void ReleaseFromDisconnectedOwner(Vector3 worldPosition)
    {
        Multiplayer.Log($"NetworkedItem.ReleaseFromDisconnectedOwner() netId: {NetId}, name: {name}, dropping at {worldPosition}");

        OwnerId = 0;
        wasThrown = false;

        Inventory.Instance.ReturnItemToWorld(gameObject, false);
        gameObject.SetActive(true);
        transform.position = worldPosition;

        Rigidbody rb = Item?.ItemRigidbody;
        if (rb != null && !rb.isKinematic)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        //a FullSync carries state and position to everyone nearby, whatever they last heard
        settleWatch = false;
        settleSyncDue = true;
    }

    // Host only: applying someone's snapshot clears the dirty flags, so nothing would ever tell
    // the *other* players about it. Dating the item now makes ProcessChanged send them a
    // FullSync; the sender is dated to the same tick so it does not echo back to them.
    public void MarkRelayDirty()
    {
        LastDirtyTick = NetworkLifecycle.Instance.Tick;
    }

    private void ArmSettleWatch()
    {
        settleWatch = true;
        settleSeenMoving = false;
        settleWatchTick = NetworkLifecycle.Instance.Tick;
    }

    // Unity sleeps a body that has stopped moving through the world, which an item riding a
    // train never does - it would keep the watch armed until it timed out. On a car, rest means
    // rest *relative to the car*: that is what "it stopped rolling around the cab" is.
    private bool IsAtRest(Rigidbody rb)
    {
        if (rb.IsSleeping())
            return true;

        TrainCar car = TrainCar.Resolve(transform);

        if (car == null || car.rb == null)
            return false;

        return (rb.velocity - car.rb.velocity).sqrMagnitude < REST_SPEED_SQR
            && rb.angularVelocity.sqrMagnitude < REST_SPIN_SQR;
    }

    //Decide whether the item has come to rest, and if so queue the one position everyone adopts.
    private void CheckSettled()
    {
        Rigidbody rb = Item.ItemRigidbody;

        // Nothing to wait for: no body, or a kinematic one that physics does not move. A job
        // paper parented to the origin shift is kinematic and never sleeps, so without this the
        // watch stayed armed forever and streamed the paper's position every tick (B29).
        if (rb == null || rb.isKinematic)
        {
            settleWatch = false;
            return;
        }

        // Still moving: keep watching, and let GetSnapshot stream it. No timeout here - an item
        // that never settles is exactly the one that must keep being told, not given up on.
        if (!IsAtRest(rb))
        {
            settleSeenMoving = true;
            return;
        }

        // Asleep before physics ever took over means the throw has yet to start, not that it
        // ended. Give it a moment before believing it.
        if (!settleSeenMoving && NetworkLifecycle.Instance.Tick - settleWatchTick < SETTLE_TIMEOUT_TICKS)
            return;

        Multiplayer.LogDebug(() => $"NetworkedItem.CheckSettled() netId: {NetId}, name: {name} came to rest at {transform.position}, moved first: {settleSeenMoving}");

        settleWatch = false;
        wasThrown = false;  //report where it lies, not where it flew from
        settleSyncDue = true;
    }

    public ItemUpdateData GetSnapshot()
    {
        ItemUpdateData snapshot;
        ItemUpdateData.ItemUpdateType updateType = ItemUpdateData.ItemUpdateType.None;

        bool hasDirtyVals = HasDirtyValues();

        if (Item == null && Register() == false)
            return null;

        if (settleWatch && (lastState == ItemState.Dropped || lastState == ItemState.Thrown))
            CheckSettled();

        // Still in motion, so where it is cannot be inferred from anything already sent: every
        // machine runs its own physics and diverges within a tick, and inside a moving cab the
        // frame is not even inertial. Stream it while it moves, the way players are streamed,
        // and fall silent the moment it rests. Only the host judges this, and ProcessChanged
        // already limits the traffic to players within MAX_DISTANCE_TO_ITEM.
        //
        // The overview and the booklet are carried, thrown and handed over, so they stream like
        // anything else. Only the throwaway reports - payout, expiry, licence refusal - are left
        // out: nobody moves those. The real cure for the flood was the kinematic guard above; a
        // paper resting on the origin shift now settles instead of streaming forever (B29).
        bool streaming = settleWatch && settleSeenMoving && !NetworkedItemManager.IsThrowawayReport(TrackedItemType);

        if (!stateDirty && !hasDirtyVals && !settleSyncDue && !streaming)
            return null;

        ItemState currentState = GetItemState();

        if (settleSyncDue)
        {
            updateType = ItemUpdateData.ItemUpdateType.FullSync;
            settleSyncDue = false;
        }
        else if (!createdDirty)
        {
            if (lastState != currentState)
                updateType |= ItemUpdateData.ItemUpdateType.ItemState;

            if (hasDirtyVals)
            {
                Multiplayer.LogDebug(GetDirtyValuesDebugString);
                updateType |= ItemUpdateData.ItemUpdateType.ObjectState;
            }

            //nothing changed - it is simply still moving
            if (updateType == ItemUpdateData.ItemUpdateType.None && streaming)
                updateType = ItemUpdateData.ItemUpdateType.ItemPosition;
        }
        else
        {
            updateType = ItemUpdateData.ItemUpdateType.Create;
        }

        //no changes this snapshot
        if (updateType == ItemUpdateData.ItemUpdateType.None)
            return null;

        //arm the settle watch on a fresh transition into a free-moving state (host judges rest)
        if (NetworkLifecycle.Instance.IsHost() && lastState != currentState &&
            (currentState == ItemState.Dropped || currentState == ItemState.Thrown))
            ArmSettleWatch();

        lastState = currentState;
        LastDirtyTick = NetworkLifecycle.Instance.Tick;
        snapshot = CreateUpdateData(updateType);

        createdDirty = false;
        stateDirty = false;
        wasThrown = false;

        MarkValuesClean();

        return snapshot;
    }

    public void ReceiveSnapshot(ItemUpdateData snapshot)
    {
        if (snapshot == null || snapshot.UpdateType == ItemUpdateData.ItemUpdateType.None)
            return;

        if (!registrationComplete)
        {
            // Motion is only worth anything now: replaying a queued position later would put the
            // item back where it was seconds ago. The next one is a tick away.
            if (ItemUpdateData.IsMotionStream(snapshot.UpdateType))
                return;

            Multiplayer.LogDebug(() => $"NetworkedItem.ReceiveSnapshot() netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}. Queuing");
            pendingSnapshots.Enqueue(snapshot);

            //a healthy item finalises within a frame or two; a growing queue means someone
            //called Initialize and never FinaliseTrackedValues - the item would stay deaf forever
            if (pendingSnapshots.Count == 10)
                Multiplayer.LogError($"NetworkedItem.ReceiveSnapshot() netId: {NetId}, name: {name}: 10 snapshots queued and registration still incomplete - missing FinaliseTrackedValues for this item type?");
            return;
        }

        ApplySnapshot(snapshot);
    }

    // The item is mid-flight or still rolling. Put it where the sender has it and hand its
    // physics the same motion, so it carries on from there instead of re-running the drop.
    private void ApplyMotionStream(ItemUpdateData snapshot)
    {
        Vector3 position = snapshot.ItemPosition;
        Quaternion rotation = snapshot.ItemRotation;
        Vector3 velocity = snapshot.ItemVelocity;
        Vector3 spin = snapshot.ItemAngularVelocity;

        if (snapshot.CarNetId != 0 && NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar car) && car != null)
        {
            position = car.transform.TransformPoint(position);
            rotation = car.transform.rotation * rotation;
            velocity = car.transform.TransformDirection(velocity);
            spin = car.transform.TransformDirection(spin);

            //motion was sent relative to the car, so put the car's own motion back in
            if (car.rb != null)
                velocity += car.rb.velocity;
        }
        else
        {
            position += WorldMover.currentMove;
        }

        Rigidbody rb = Item?.ItemRigidbody;

        if (rb == null || rb.isKinematic)
            return;

        // The position in hand is already a tick old - it is where the sender was when they sent
        // it - so aim at where that motion has carried it since. Correcting to the stale point
        // instead drags the item backwards every packet, which is what the shivering was (B25).
        position += velocity * STREAM_LEAD;

        Vector3 error = position - rb.position;

        // Badly wrong: teleport, and only here. Moving a rigidbody by hand skips the collision
        // sweep, so doing it every tick punched items through the floor of a moving car (B28).
        if (error.sqrMagnitude > STREAM_SNAP_DISTANCE_SQR)
        {
            rb.position = position;
            rb.rotation = rotation;
            rb.velocity = velocity;
            rb.angularVelocity = spin;
            return;
        }

        // Otherwise steer rather than shove: the sender's motion, plus just enough of a nudge to
        // close the gap. Physics keeps hold of the item, so it still collides with the world.
        rb.velocity = velocity + error * STREAM_CATCHUP;
        rb.angularVelocity = spin;
        rb.rotation = Quaternion.Slerp(rb.rotation, rotation, STREAM_SMOOTHING);
    }

    private void ApplySnapshot(ItemUpdateData snapshot)
    {
        if (ItemUpdateData.IsMotionStream(snapshot.UpdateType))
        {
            ApplyMotionStream(snapshot);
            return;
        }

        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ItemState) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.FullSync) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create))
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.ApplySnapshot() netId: {snapshot?.ItemNetId}, ItemUpdateType: {snapshot?.UpdateType}, ItemState: {snapshot?.ItemState}, Active state: {gameObject.activeInHierarchy}");

            switch (snapshot.ItemState)
            {
                case ItemState.Dropped:
                case ItemState.Thrown:
                    HandleDroppedOrThrownState(snapshot);
                    break;

                case ItemState.InHand:
                case ItemState.InInventory:
                    HandleInventoryOrHandState(snapshot);
                    break;

                case ItemState.Attached:
                    HandleAttachedState(snapshot);
                    break;

                default:
                    throw new Exception($"NetworkedItem.ApplySnapshot() Item state not implemented: {snapshot?.ItemState}");

            }

            //remember the applied state, or the next local event would diff against a stale one
            lastState = snapshot.ItemState;

            //a client threw or dropped it: the host's physics now decides where it comes to rest
            if (NetworkLifecycle.Instance.IsHost() &&
                (snapshot.ItemState == ItemState.Dropped || snapshot.ItemState == ItemState.Thrown))
                ArmSettleWatch();
        }

        if (snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || snapshot.UpdateType.HasFlag(ItemUpdateData.ItemUpdateType.ObjectState))
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.ApplySnapshot() netID: {snapshot?.ItemNetId}, States: {snapshot?.States?.Count}");

            if (trackedItem != null && snapshot.States != null)
            {
                ApplyTrackedValues(snapshot.States);
            }
        }

        //mark values as clean
        createdDirty = false;
        stateDirty = false;

        MarkValuesClean();
        return;
    }

    // The car an item is riding, or null for the world. The game parents an item to a car's
    // interior when it lands on one, so this is the game's own answer, not a guess.
    private TrainCar RestingCar()
    {
        TrainCar car = TrainCar.Resolve(transform);
        return car != null && car.GetNetId() != 0 ? car : null;
    }

    // Where the item is and how it moves, told in the frame the receiver can still make sense of
    // once the packet lands: a car's own position is read at apply time, so however far the train
    // travelled in between, the item goes back to the same spot in the cab (B20).
    private void FrameMotion(TrainCar car, ref Vector3 position, ref Quaternion rotation, ref Vector3 direction, ref Vector3 velocity, ref Vector3 spin)
    {
        if (car == null)
        {
            position -= WorldMover.currentMove;
            return;
        }

        //motion is relative to the car: an item at rest in a moving cab is not moving at all
        if (car.rb != null)
            velocity -= car.rb.velocity;

        position = car.transform.InverseTransformPoint(position);
        rotation = Quaternion.Inverse(car.transform.rotation) * rotation;
        direction = car.transform.InverseTransformDirection(direction);
        velocity = car.transform.InverseTransformDirection(velocity);
        spin = car.transform.InverseTransformDirection(spin);
    }

    public ItemUpdateData CreateUpdateData(ItemUpdateData.ItemUpdateType updateType)
    {
        if (transform == null || Item == null || Item?.InventorySpecs == null || Item?.InventorySpecs?.ItemPrefabName == null)
        {
            Multiplayer.LogDebug(()=>$"NetworkedItem.CreateUpdateData({updateType}) NetId: {NetId}, name: {name}. Transform is null: {transform == null}, Item is null: {Item == null}, Inventory Specs: {Item?.InventorySpecs == null}, ItemPrefabName is null: {Item?.InventorySpecs?.ItemPrefabName == null}");
            return null;
        }

        Vector3 position;
        Quaternion rotation;
        Vector3 direction = throwDirection;
        Vector3 velocity = Vector3.zero;
        Vector3 spin = Vector3.zero;
        Dictionary<string, object> states;
        ushort carId = 0;
        bool frontCoupler = true;

        Rigidbody body = Item.ItemRigidbody;

        if (body != null && !body.isKinematic)
        {
            velocity = body.velocity;
            spin = body.angularVelocity;
        }

        if (wasThrown)
        {
            position = thrownPosition;
            rotation = thrownRotation;
        }
        else
        {
            position = transform.position;
            rotation = transform.rotation;
        }

        // Anything loose in the world is told relative to the car carrying it, if any.
        TrainCar restingCar = (lastState == ItemState.Dropped || lastState == ItemState.Thrown)
            ? RestingCar()
            : null;

        carId = restingCar?.GetNetId() ?? 0;
        FrameMotion(restingCar, ref position, ref rotation, ref direction, ref velocity, ref spin);

        // A motion stream says nothing else - no state, no prefab, no tracked values.
        if (ItemUpdateData.IsMotionStream(updateType))
        {
            return new ItemUpdateData
            {
                UpdateType = updateType,
                ItemNetId = NetId,
                CarNetId = carId,
                ItemPosition = position,
                ItemRotation = rotation,
                ItemVelocity = velocity,
                ItemAngularVelocity = spin,
            };
        }

        if (updateType.HasFlag(ItemUpdateData.ItemUpdateType.Create) || updateType.HasFlag(ItemUpdateData.ItemUpdateType.FullSync))
        {
            states = GetAllStateData();
        }
        else
        {
            states = GetDirtyStateData();
        }

        if (lastState == ItemState.Attached)
        {
            ItemSnapPointCoupler itemSnapPointCoupler = snappableItem.SnappedTo as ItemSnapPointCoupler;

            if (itemSnapPointCoupler != null)
            {
                carId = itemSnapPointCoupler.Car.GetNetId();
                frontCoupler = itemSnapPointCoupler.IsFront;
            }
        }

        var updateData = new ItemUpdateData
        {
            UpdateType = updateType,
            ItemNetId = NetId,
            PrefabName = Item.InventorySpecs.ItemPrefabName,
            ItemState = lastState,
            ItemPosition = position,
            ItemRotation = rotation,
            ThrowDirection = direction,
            CarNetId = carId,
            AttachedFront = frontCoupler,
            States = states,
        };

        return updateData;
    }

    private ItemState GetItemState()
    {
        //Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}, isGrabbed: {Item.IsGrabbed()} Inventory.Contains(): {Inventory.Instance.Contains(this.gameObject, false)} Storage.Contains: {StorageController.Instance.StorageInventory.ContainsItem(Item)}");


        if (Item.transform.parent == WorldMover.OriginShiftParent && !wasThrown)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}");
            return ItemState.Dropped;
        }

        if (wasThrown)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, Parent: {Item.transform.parent} WorldMover: {WorldMover.OriginShiftParent}, wasThrown: {wasThrown}");
            return ItemState.Thrown;
        }

        if (Item.IsGrabbed())
            return ItemState.InHand;

        if (Inventory.Instance.Contains(this.gameObject, false))
            return ItemState.InInventory;

        if (snappableItem != null && snappableItem.IsSnapped)
        {
            Multiplayer.LogDebug(() => $"GetItemState() NetId: {NetId}, {name}, snapped! {this.transform.parent}");
            return ItemState.Attached;
        }

        //do we need a condition to check if it's attached to something else (last attach vs current attach)?
        return ItemState.Dropped;

    }

    private void ApplyTrackedValues(Dictionary<string, object> newValues)
    {
        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Null checks");

        if (newValues == null || newValues.Count == 0)
            return;


        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Registration complete: {registrationComplete}");

        foreach (var newValue in newValues)
        {
            var trackedValue = trackedValues.Find(tv => ((dynamic)tv).Key == newValue.Key);
            if (trackedValue != null)
            {
                if (!NetworkLifecycle.Instance.IsHost() || !((dynamic)trackedValue).ServerAuthoritative)
                {
                    try
                    {
                        ((dynamic)trackedValue).SetValueFromObject(newValue.Value);
                        Multiplayer.LogDebug(() => $"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}, Updated tracked value: {newValue.Key}, value: {newValue.Value} ");
                    }
                    catch (Exception ex)
                    {
                        Multiplayer.LogError($"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Error updating tracked value {newValue.Key}: {ex.Message}");
                    }
                }
                else
                {
                    Multiplayer.LogWarning($"NetworkedItem.ApplyTrackedValues() itemNetId: {NetId}, item name: {name}. Skipped server-authoritative value update from client: {newValue.Key}");
                }
            }
            else
            {
                Multiplayer.LogWarning($"Tracked value not found: {newValue.Key}\r\n {String.Join(", ", trackedValues.Select(val => ((dynamic)val).Key))}");
            }
        }
    }

    #region Item State Update Handlers

    private void HandleDroppedOrThrownState(ItemUpdateData snapshot)
    {
        //resolve attachment
        if (Item.IsSnapped)
        {
            Item.SnappableItem.SnappedTo.UnsnapItem(false);
        }

        //resolve ownership
        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && player.OwnsItem(NetId))
                player.RemoveOwnedItem(NetId);

        //An item in an inventory is not hidden, it is frozen: Inventory.FinalizeRemoveItemFromWorld
        //makes it kinematic and moves it to the inventory layer. Re-activating it is not enough -
        //without the game's own counterpart it comes back deaf to gravity and to the throw below,
        //hanging wherever we put it. Pass activate: false; we place it ourselves, and the game
        //would teleport it to the *local* player.
        Inventory.Instance.ReturnItemToWorld(gameObject, false);

        //Resolve the frame the sender described this in. A car's own position is read now, not
        //when the packet was sent, so the item lands where it belongs in the cab however far the
        //train has travelled since (B20).
        Vector3 worldPosition;
        Quaternion worldRotation;
        Vector3 worldThrow = snapshot.ThrowDirection;

        if (snapshot.CarNetId != 0 && NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar restingCar) && restingCar != null)
        {
            worldPosition = restingCar.transform.TransformPoint(snapshot.ItemPosition);
            worldRotation = restingCar.transform.rotation * snapshot.ItemRotation;
            worldThrow = restingCar.transform.TransformDirection(snapshot.ThrowDirection);
        }
        else
        {
            if (snapshot.CarNetId != 0)
                Multiplayer.LogWarning($"NetworkedItem.HandleDroppedOrThrownState() netId: {NetId}, car {snapshot.CarNetId} not found; placing in world space instead");

            worldPosition = snapshot.ItemPosition + WorldMover.currentMove;
            worldRotation = snapshot.ItemRotation;
        }

        //activate and relocate item
        gameObject.SetActive(true);
        transform.position = worldPosition;
        transform.rotation = worldRotation;
        OwnerId = 0;

        //A throw is replayed as GrabHandlerItem.Throw -> AddForce, which adds to whatever the
        //body was already doing. On the thrower the item leaves the hand at rest, so any
        //leftover motion here would send it somewhere else entirely. Start from the same rest.
        Rigidbody rb = Item.ItemRigidbody;
        if (rb != null && !rb.isKinematic)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        //handle throwing of the item
        if (snapshot.ItemState == ItemState.Thrown)
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Thrown. Position: {transform.position}, Direction: {worldThrow}, Car: {snapshot?.CarNetId}");

            //keep the throw in world terms: OnThrow's echo guard returns before recording it, so
            //without this the host would relay someone else's throw with a stale direction
            throwDirection = worldThrow;
            thrownPosition = worldPosition;
            thrownRotation = worldRotation;

            wasThrown = true;
            grabHandler?.Throw(worldThrow);
        }
        else
        {
            Multiplayer.LogDebug(() => $"NetworkedItem.HandleDroppedOrThrownState() ItemNetId: {snapshot?.ItemNetId} Dropped. Position: {transform.position}");
        }
    }

    private void HandleAttachedState(ItemUpdateData snapshot)
    {
        //resovle ownership
        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && player.OwnsItem(NetId))
                player.RemoveOwnedItem(NetId);

        //handle attaching the item
        gameObject.SetActive(true);
        Multiplayer.LogDebug(() => $"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId} attempting attachment to car {snapshot.CarNetId}, at the front {snapshot.AttachedFront}");

        if (!NetworkedTrainCar.TryGet(snapshot.CarNetId, out TrainCar trainCar))
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() CarNetId: {snapshot?.CarNetId} not found for ItemNetId: {snapshot?.ItemNetId}");
            return;
        }

        //Try to find the coupler snap point for the car and correct end to snap to
        var snapPoint = trainCar?.physicsLod?.GetCouplerSnapPoints()
            .FirstOrDefault(sp => sp.IsFront == snapshot.AttachedFront);

        if (snapPoint == null)
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() ItemNetId: {snapshot?.ItemNetId}. No valid snap point found for car {snapshot.CarNetId}");
            return;
        }

        //Attempt attachment to car
        Item.ItemRigidbody.isKinematic = false;
        if (!snapPoint.SnapItem(Item, false))
        {
            Multiplayer.LogWarning($"NetworkedItem.HandleAttachedState() Attachment failed for item {snapshot?.ItemNetId} to car {snapshot.CarNetId}");
        }
    }

    private void HandleInventoryOrHandState(ItemUpdateData snapshot)
    {
        if (Item.IsSnapped)
        {
            Item.SnappableItem.SnappedTo.UnsnapItem(false);
        }

        if (NetworkLifecycle.Instance.IsHost())
            if (NetworkLifecycle.Instance.Server.TryGetServerPlayer(snapshot.Player, out ServerPlayer player) && !player.OwnsItem(NetId))
                player.AddOwnedItem(NetId);

        //todo add to player model's hand
        this.gameObject.SetActive(false);
    }
    #endregion

    protected override void OnDestroy()
    {
        // Drop the dictionary entry FIRST, and always - even while unloading, and even after
        // Unity has zeroed the component. The dictionary is static and outlives the scene, so a
        // skipped removal leaks a dead entry into the next game; GetAll() then walks it every
        // tick, per player, and the game freezes worse the longer it runs - a full restart clears
        // the statics, a new game does not (B32). ReferenceEquals bypasses Unity's fake null: the
        // managed reference is still a valid dictionary key even when the object reads as null.
        if (!ReferenceEquals(Item, null))
            itemBaseToNetworkedItem.Remove(Item);

        if (UnloadWatcher.isQuitting || UnloadWatcher.isUnloading)
        {
            base.OnDestroy();
            return;
        }

        if (NetworkLifecycle.Instance.IsHost())
        {
            var updateData = CreateUpdateData(ItemUpdateData.ItemUpdateType.Destroy);
            if (updateData != null)
                NetworkedItemManager.Instance.AddDirtyItemSnapshot(this, updateData);
        }

        //Unity-alive check: only unhook events on a component that still exists
        if (Item != null)
        {
            Item.Grabbed -= OnGrabbed;
            Item.Ungrabbed -= OnUngrabbed;
        }

        base.OnDestroy();
    }

    public string GetDirtyValuesDebugString()
    {
        var dirtyValues = trackedValues.Where(tv => ((dynamic)tv).IsDirty).ToList();
        if (dirtyValues.Count == 0)
        {
            return "No dirty values";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Dirty values for NetworkedItem: {name}, NetId: {NetId}:");
        foreach (var value in dirtyValues)
        {
            sb.AppendLine(((dynamic)value).GetDebugString());
        }
        return sb.ToString();
    }
}
