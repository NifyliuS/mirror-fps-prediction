using System;
using System.Diagnostics;
using System.Linq;
using Mirror;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NetworkScripts{
  public class NetworkTicker : NetworkBehaviour{
    private enum TickSyncerStateEnum{
      Initializing,
      Syncing,
      Ready,
      OutOfSync,
    }

    private struct HeartBeat{
      public byte ClientTickFraction;
      public uint ServerTick;
      public short TickOffset;
    }

    private struct SyncResponse{
      public double ServerTick;
      public double ServerTickOffset;
      public double LocalTick;
      public double LocalTickOffset;
      public double HeartBeatOffset;
    }

    private Buffer256<uint> _syncRequestBuffer = new Buffer256<uint>();
    private Buffer256<SyncResponse> _syncBuffer = new Buffer256<SyncResponse>();

    private TickSyncerStateEnum
      _state = TickSyncerStateEnum.Initializing; //Defined in what state the NetworkTicker is

    private int _forwardPhysicsSteps = 0; //Used to fast forward physics engine
    private int _skipPhysicsSteps = 0; //Used to skip ticks or reverse the physics engine

    [Tooltip("How often server sends his current tick to clients: Every X ticks")]
    public byte ServerTickOffsetSyncFrequency = 30;

    [Header("Tick Timing Smoothing")]
    [Tooltip("Amount of ticks to Average out to smooth network inconsistencies ( 2 might be removed as Spikes )")]
    public static int ServerTickAdjustmentSize = 10;

    [Header("Tick Accuracy Settings")]
    [Tooltip("Allow automatic adjustment based on accuracy (will add or reduce to MaxClientAhead)")]
    public bool AutoAdjustLimits = false;

    [Tooltip("Max allowed ticks client will be ahead when packets arrive on the server")]
    public int MaxClientAhead = 8; //Max allowed ticks ahead

    [Tooltip("Max allowed ticks client will be ahead when packets arrive on the server")]
    public int MinClientAhead = 5; //Max allowed ticks behind

    [field: SerializeField] public float Accuracy { get; private set; }


    private ExponentialMovingAverage hartBeatAverage = new ExponentialMovingAverage(ServerTickAdjustmentSize);
    private ExponentialMovingAverage offsetAverage = new ExponentialMovingAverage(ServerTickAdjustmentSize);
    private ExponentialMovingAverage accuracyAverage = new ExponentialMovingAverage(ServerTickAdjustmentSize);

    private uint _networkTickBase = 0; //Client wont execute server commands marked with tick lower than this value
    private float _networkTickOffset = 0; //how far the client should be further in the future than the server

    private uint _lastSyncTick = 0; //Used to ignore older or duplicate entries from the server syncing
    private double _lastTickStart; //Used to calculate time from tick start

    #region Initial Sync/Spawn

    public override bool OnSerialize(NetworkWriter writer, bool initialState) {
      base.OnSerialize(writer, initialState);
      if (initialState) {
        writer.WriteUInt(_networkTickBase);
        return true;
      }

      return false;
    }

    public override void OnDeserialize(NetworkReader reader, bool initialState) {
      base.OnDeserialize(reader, initialState);
      if (initialState) {
        _networkTickBase = (uint)(reader.ReadUInt() - MinClientAhead - 1);
        //Adding one due to the way server handles sending ticks ( in most cases the client will be behind more than it should be otherwise )
      }
    }

    #endregion

    #region Tick Ping Handling

    [Command(channel = Channels.Unreliable, requiresAuthority = false)]
    private void CmdPingTick(byte clientTickId, NetworkConnectionToClient sender = null) {
      byte fraction = GetTickFraction(); // Get tick fraction as soon as possible
      //Return server-client tick difference ( positive = client is ahead )
      RpcTickPong(sender, clientTickId, (ushort)(_networkTickBase), fraction);
    }

    [TargetRpc(channel = Channels.Unreliable)]
    private void RpcTickPong(NetworkConnection _, byte clientTickId, ushort serverTickUShort, byte tickFraction) {
      uint localTick = _syncRequestBuffer.Get(clientTickId);
      if (_lastSyncTick >= localTick) return; //Avoid duplicates
      byte localTickFraction = GetTickFraction(); // Get tick fraction as soon as possible

      double currentTick = _networkTickBase + localTickFraction / 100f;
      double serverOffset = GetFixedDiff(serverTickUShort, (ushort)(localTick)) + tickFraction / 100f;
      double serverTick = localTick + serverOffset;
      double localOffset = _networkTickBase - localTick + localTickFraction / 100f;
      double heartBeatOffset = serverOffset - localOffset;
      var syncItem = new SyncResponse() {
        ServerTick = serverTick,
        ServerTickOffset = serverOffset,
        LocalTick = localTick,
        LocalTickOffset = localOffset,
        HeartBeatOffset = heartBeatOffset,
      };
      _syncBuffer.Add(syncItem);
      Debug.Log(
        $"    <color=green>{currentTick} -> {localTick} {serverTick}</color>\n" +
        $"                        localTick=[{_networkTickBase}]");
      Debug.Log($"    localOffset=[{localOffset}]\n" +
                $"                        serverTick=[{serverTick}]");
      Debug.Log($"    serverOffset=[{serverOffset}]\n" +
                $"                        heartBeatOffset=[{heartBeatOffset}]");


      if (AutoAdjustLimits && _syncBuffer.Count > ServerTickAdjustmentSize) {
        AutoAdjustLimit();
      }

      int tickAdjustmentValue = 0;
      if (_syncBuffer.Count > ServerTickAdjustmentSize || _state == TickSyncerStateEnum.Initializing) {
        _state = TickSyncerStateEnum.Ready;
        var syncSequence = _syncBuffer.GetTail(ServerTickAdjustmentSize);
        //tickAdjustmentValue += CheckAdjustBaseTick(syncSequence);
        //tickAdjustmentValue += CheckAdjustOffsetTick(syncSequence);

        CheckAdjustBaseTickV2(syncItem);
      }


      _lastSyncTick = localTick;
    }

    #endregion

    #region Tick Adjustments

    private void AutoAdjustLimit() {
      (var min, var max) = GetMinMaxD(
        Array.ConvertAll(
          _syncBuffer.GetTail(ServerTickAdjustmentSize),
          x => (double)x.ServerTickOffset)
      );
      accuracyAverage.Add(max - min);
      MaxClientAhead = MinClientAhead + Mathf.CeilToInt((float)(accuracyAverage.Value));
      Accuracy = (float)(accuracyAverage.Value);
    }

    private void CheckAdjustBaseTickV2(SyncResponse syncItem) {
      hartBeatAverage.Add(syncItem.HeartBeatOffset);
      float heartBeatAverage = (float)hartBeatAverage.Value;
      if (heartBeatAverage < MinClientAhead || heartBeatAverage > MaxClientAhead) {
        int adjustment = Mathf.FloorToInt(heartBeatAverage) - MinClientAhead;
        _networkTickBase = (uint)(_networkTickBase + adjustment);
        ResetHBAverage();
        Debug.Log(
          $"Base Tick adjusted By [{adjustment}] base=[{_networkTickBase}] offset=[{_networkTickOffset}] Min=[{MinClientAhead}] Max[{MaxClientAhead}]");
      }
    }


    private void ResetHBAverage() {
      float avg = (float)hartBeatAverage.Value;
      hartBeatAverage = new ExponentialMovingAverage(ServerTickAdjustmentSize);
      hartBeatAverage.Add(avg);
      hartBeatAverage.Add(avg);
      hartBeatAverage.Add(avg);
    }
    private int CheckAdjustBaseTick(SyncResponse[] syncSequence) {
      int adjustment = 0;
      float averageHeartBeat = GetFilteredAverage(Array.ConvertAll(syncSequence, x => (float)x.HeartBeatOffset));

      if (averageHeartBeat < MinClientAhead) {
        adjustment -= 1;
        _networkTickBase--;
      }

      if (averageHeartBeat > MaxClientAhead) {
        adjustment += 1;
        _networkTickBase++;
      }

      if (adjustment != 0) {
        Debug.Log($"Base Tick adjusted By [{adjustment}] base=[{_networkTickBase}] offset=[{_networkTickOffset}]");
        _syncBuffer.EditTail(ServerTickAdjustmentSize, item => {
          item.HeartBeatOffset -= adjustment;
          item.ServerTickOffset -= adjustment;
          return item;
        });
      }

      return adjustment;
    }

    private int CheckAdjustOffsetTick(SyncResponse[] syncSequence) {
      int adjustment = 0;
      float averageOffset = GetFilteredAverage(Array.ConvertAll(syncSequence, x => (float)x.ServerTickOffset));
      float offsetDiff = averageOffset - _networkTickOffset;

      if (MinClientAhead > offsetDiff || offsetDiff > MaxClientAhead) {
        adjustment = offsetDiff > 0 ? Mathf.CeilToInt(offsetDiff) : Mathf.FloorToInt(offsetDiff);
        _networkTickOffset += adjustment;
        Debug.Log(
          $"Offset Tick adjusted By [{adjustment}] base=[{_networkTickBase}] offset=[{_networkTickOffset}] || [[{offsetDiff}]] MinClientAhead{MinClientAhead} MaxClientAhead{MaxClientAhead}");
      }


      return adjustment;
    }

    #endregion

    #region Tick Update Handling

    [Server]
    public virtual void ServerFixedUpdate(double deltaTime) {
    }

    [Client]
    public virtual void ClientFixedUpdate(double deltaTime) {
      if (_networkTickBase % ServerTickOffsetSyncFrequency == 0) {
        CmdPingTick(
          (byte)_syncRequestBuffer.Add(_networkTickBase)
        );
      }
    }

    private void TickAdvance() {
      uint deltaTicks = GetDeltaTicks();
      _networkTickBase += deltaTicks;
    }


    public virtual void FixedUpdate() {
      _lastTickStart = Time.fixedTimeAsDouble;
      TickAdvance();
      if (isServer) ServerFixedUpdate(Time.deltaTime);
      else ClientFixedUpdate(Time.deltaTime);
      /* Update Physics */
      PhysicsStepHandle();
    }

    #endregion

    #region Physics Change Handling

    // Queue physics adjustment
    private void AdjustClientPhysicsTick(int tickAdjustment) {
      if (tickAdjustment > 0) {
        _forwardPhysicsSteps += tickAdjustment;
      }
      else {
        _skipPhysicsSteps += -tickAdjustment;
      }
    }

    private void PhysicsStepHandle() {
      if (_skipPhysicsSteps > 0) {
        _skipPhysicsSteps = NonNegativeValue(
          PhysicStepSkip(Time.fixedDeltaTime, Time.deltaTime, (int)GetDeltaTicks(), _skipPhysicsSteps)
        );
        return;
      }

      if (_forwardPhysicsSteps > 0) {
        _forwardPhysicsSteps = NonNegativeValue(
          PhysicStepFastForward(Time.fixedDeltaTime, Time.deltaTime, (int)GetDeltaTicks(),
            _forwardPhysicsSteps)
        );
        return;
      }

      PhysicStep(Time.deltaTime, (int)GetDeltaTicks());
    }

    #endregion

    #region Physics Virtual Callbacks

    public virtual void PhysicStep(float deltaTime, int deltaTicks) {
    }

    [Client]
    public virtual int PhysicStepSkip(float fixedDeltaTime, float deltaTime, int deltaTicks, int skipSteps) {
      Debug.Log(
        $"Ignored 'PhysicStep' step and calling PhysicStepSkip( {fixedDeltaTime}, {deltaTime}, {deltaTicks}, {skipSteps} )");
      return skipSteps - 1;
    }

    [Client]
    public virtual int PhysicStepFastForward(float fixedDeltaTime, float deltaTime, int deltaTicks,
      int fastForwardSteps) {
      Debug.Log(
        $"Ignored 'PhysicStep' step and calling PhysicStepFastForward( {fixedDeltaTime}, {deltaTime}, {deltaTicks}, {fastForwardSteps} )");
      return 0;
    }

    #endregion

    #region Helper Functions

    private static int NonNegativeValue(int value) => value > 0 ? value : 0;

    private uint GetDeltaTicks() {
      return (uint)(Time.deltaTime / Time.fixedDeltaTime);
    }

    //Even though we split each tick to 100 sometimes we see 104 or larger ticks - we allow for this by not using 255 split but using byte to transfer
    private byte GetTickFraction() {
      int tickFraction =
        Mathf.RoundToInt((float)((Time.timeAsDouble - _lastTickStart) / Time.fixedDeltaTime * 100));
      if (tickFraction > 255) tickFraction = 255;
      return (byte)(tickFraction);
    }

    private static (int, int) GetMAxMinIndex(float[] array) {
      if (array.Length == 0) return (-1, -1);
      int minIndex = 0;
      int maxIndex = 0;
      float max = array[0];
      float min = array[0];

      for (int i = 0; i < array.Length; i++) {
        if (max < array[i]) {
          max = array[i];
          maxIndex = i;
        }

        if (min > array[i]) {
          min = array[i];
          minIndex = i;
        }
      }

      return (maxIndex, minIndex);
    }


    private static (double, double) GetMinMaxD(double[] array) {
      if (array.Length == 0) return (-1, -1);
      double max = array[0];
      double min = array[0];
      for (int i = 0; i < array.Length; i++) {
        if (max < array[i]) max = array[i];
        if (min > array[i]) min = array[i];
      }

      return (min, max);
    }

    /* Average out ping numbers - exclude highest and lowest ping numbers ( ignores short spikes ) */
    private float GetFilteredAverage(float[] array) {
      (int maxIndex, int minIndex) = GetMAxMinIndex(array);
      float sumCounter = 0;
      float sum = 0;

      for (int i = 0; i < array.Length; i++) {
        if (i != maxIndex && i != minIndex) {
          sum += array[i];
          sumCounter++;
        }
      }

      return sum / sumCounter;
    }

    private int GetFixedDiff(ushort one, ushort two) {
      int diff = one - two;
      if (diff > 32767) return diff - 65536;
      if (diff < -32767) return diff + 65536;
      return diff;
    }

    #endregion
  }
}