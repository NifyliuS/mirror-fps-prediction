using System;
using System.Diagnostics;
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
      public uint ServerTick;
      public sbyte ServerTickOffset;
      public byte ServerTickFraction;
      public uint LocalTick;
      public byte LocalTickFraction;
      public sbyte LocalTickOffset;
    }

    private Buffer256<HeartBeat> _heartBeatBuffer = new Buffer256<HeartBeat>();
    private Buffer256<HeartBeat> _syncBeatBuffer = new Buffer256<HeartBeat>();
    private Buffer256<SyncResponse> _syncBuffer = new Buffer256<SyncResponse>();

    private TickSyncerStateEnum
      _state = TickSyncerStateEnum.Initializing; //Defined in what state the NetworkTicker is

    private int _forwardPhysicsSteps = 0; //Used to fast forward physics engine
    private int _skipPhysicsSteps = 0; //Used to skip ticks or reverse the physics engine


    [Header("Tick Sync Frequencty")] [Tooltip("How often server sends his current tick to clients: Every X ticks")]
    public short ServerTickHeartBeatFrequency = 300;

    [Tooltip("We want to send a burst of heart beats to average on the client - more = more accuracy")]
    public byte ServerTickHeartBeatSequenceSize = 5;

    [Tooltip("How often server sends his current tick to clients: Every X ticks")]
    public byte ServerTickOffsetSyncFrequency = 30;

    [Header("Tick Timing Smoothing")]
    [Tooltip("Amount of ticks to Average out to smooth network inconsistencies ( 2 might be removed as Spikes )")]
    public int ServerTickAdjustmentSize = 7;

    [Header("Tick Accuracy Settings")]
    [Tooltip("Allow automatic adjustment based on accuracy (will add or reduce to MaxClientAhead)")]
    public bool AutoAdjustLimits = false;

    [Tooltip("Max allowed ticks client will be ahead when packets arrive on the server")]
    public short MaxClientAhead = 2; //Max allowed ticks ahead

    [Tooltip("Max allowed ticks client will be ahead when packets arrive on the server")]
    public short MinClientAhead = 0; //Max allowed ticks behind


    private uint _networkTickBase = 0; //Client wont execute server commands marked with tick lower than this value

    private float
      _networkTickOffset =
        0; //Used to calculate how far the client should be further in the future than the server

    private uint _lastHeartBeatTick = 0; //Used to ignore older or duplicate entries from the server
    private uint _lastSyncTick = 0; //Used to ignore older or duplicate entries from the server syncing

    private int
      _lastTickAdjustmentRequest =
        0; //Used to avoid changing ticks due to network spikes ( used for consistency )

    private int[] _serverTickHBHistory = new int[256];
    private int _serverTickHBCount = 0;
    private int[] _serverTickOffsetHistory = new int[256];
    private int _serverTickOffsetCount = 0;
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
        _networkTickBase =
          reader.ReadUInt() +
          1; //Adding one due to the way server handles sending ticks ( in most cases the client will be behind more than it should be otherwise )
      }
    }

    #endregion

    #region Tick Ping Handling

    [Server]
    private void ServerTickHeartBeat() {
      if (_networkTickBase % ServerTickHeartBeatFrequency == 0) {
        RpcServerTickHeartBeat(_networkTickBase);
      }
    }

    [ClientRpc(channel = Channels.Unreliable)]
    private void RpcServerTickHeartBeat(uint serverTick) {
      if (_lastHeartBeatTick >= serverTick) return; //Avoid duplicates or late data
      _lastHeartBeatTick = serverTick;
      return;
      byte fraction = GetTickFraction(); // Get tick fraction as soon as possible
      _heartBeatBuffer.Add(new HeartBeat() {
        ClientTickFraction = fraction,
        ServerTick = serverTick,
        TickOffset = (short)(serverTick - _networkTickBase),
      });
      if (_state == TickSyncerStateEnum.Initializing) {
        _state = TickSyncerStateEnum.Syncing;
        _networkTickBase = serverTick;
      }
    }


    [Command(channel = Channels.Unreliable, requiresAuthority = false)]
    private void CmdPingTick(uint clientTick, NetworkConnectionToClient sender = null) {
      byte fraction = GetTickFraction(); // Get tick fraction as soon as possible
      RpcTickPong(
        sender,
        _networkTickBase,
        (sbyte)(clientTick - _networkTickBase),
        fraction
      ); //Return server-client tick difference ( positive = client is ahead )
    }


    private Buffer256<int> _test = new Buffer256<int>();

    [TargetRpc(channel = Channels.Unreliable)]
    private void RpcTickPong(NetworkConnection _, uint serverTick, sbyte serverTickOffset, byte tickFraction) {
      if (_lastSyncTick >= serverTick) return; //Avoid duplicates or late data
      byte localTickFraction = GetTickFraction(); // Get tick fraction as soon as possible
      _syncBuffer.Add(new SyncResponse() {
        ServerTick = serverTick,
        ServerTickOffset = serverTickOffset,
        ServerTickFraction = tickFraction,
        LocalTick = _networkTickBase,
        LocalTickFraction = localTickFraction,
        LocalTickOffset = (sbyte)(_networkTickBase - serverTick),
      });
      // _heartBeatBuffer.Add(new HeartBeat()); // TODO: extract heart beat from client ping
      if (_state == TickSyncerStateEnum.Syncing) {
        _state = TickSyncerStateEnum.Ready;
        _networkTickBase = serverTick;
        _networkTickOffset = serverTickOffset + tickFraction / 100;
      }


      if (AutoAdjustLimits) {
      }

      _lastSyncTick = serverTick;
      Debug.Log(
        $"[{serverTickOffset + tickFraction / 100f}] / [{(short)(_networkTickBase - serverTick) + localTickFraction / 100f}]");
    }

    #endregion

    #region Tick Adjustments

    private void LimitAdjustment() {
    }

    private void ConsiderBaseTickAdjustment() {
    }

    private void ConsiderOffsetAdjustment(int baseAdjustment) {
    }

    #endregion

    #region Tick Update Handling

    [Server]
    public virtual void ServerFixedUpdate(double deltaTime) {
      ServerTickHeartBeat(); //Send right after tick update to increase consistency
    }

    [Client]
    public virtual void ClientFixedUpdate(double deltaTime) {
      if (_networkTickBase % ServerTickOffsetSyncFrequency == 0) {
        CmdPingTick(_networkTickBase);
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

    private void AddTickHbItem(int item) {
      _serverTickHBHistory[(byte)_serverTickHBCount] = item;
      _serverTickHBCount++;
    }

    private int GetTickHbItem(int index) {
      return _serverTickHBHistory[(byte)index];
    }

    private void AddTickOffsetItem(int item) {
      _serverTickOffsetHistory[(byte)_serverTickOffsetCount] = item;
      _serverTickOffsetCount++;
    }

    private int GetTickOffsetItem(int index) {
      return _serverTickOffsetHistory[(byte)index];
    }

    private int[] GetTickHbCompareSequence() {
      int[] result = new int[ServerTickAdjustmentSize];
      int offset = _serverTickHBCount - ServerTickAdjustmentSize;
      for (int i = 0; i < ServerTickAdjustmentSize; i++) {
        result[i] = GetTickHbItem(i + offset);
      }

      return result;
    }

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

    private static (int, int) GetMAxMinIndex(int[] array) {
      if (array.Length == 0) return (-1, -1);
      int minIndex = 0;
      int maxIndex = 0;
      int max = array[0];
      int min = array[0];

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

    /* Average out ping numbers - exclude highest and lowest ping numbers ( ignores short spikes ) */
    private float GetFilteredAverage(int[] array) {
      (int maxIndex, int minIndex) = GetMAxMinIndex(array);
      int sumCounter = 0;
      float sum = 0;

      for (int i = 0; i < array.Length; i++) {
        if (i != maxIndex && i != minIndex) {
          sum += array[i];
          sumCounter++;
        }
      }

      return sum / sumCounter;
    }

    #endregion
  }
}