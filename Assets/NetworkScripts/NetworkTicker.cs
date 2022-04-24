using System;
using System.Diagnostics;
using Mirror;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NetworkScripts {
  public class NetworkTicker : NetworkBehaviour {
    private enum TickSyncerStateEnum {
      Initializing,
      Syncing,
      Ready,
      OutOfSync,
    }


    private TickSyncerStateEnum _state = TickSyncerStateEnum.Initializing; //Defined in what state the NetworkTicker is

    private int _forwardPhysicsSteps = 0; //Used to fast forward physics engine
    private int _skipPhysicsSteps    = 0; //Used to skip ticks or reverse the physics engine


    [Tooltip("Amout of ticks to Average out to smooth network inconsistencies - Base Tick")]
    public static int ServerTickAdjustmentSize = 7;

    [Tooltip("Amout of ticks to Average out to smooth network inconsistencies - Tick Offset")]
    public static int ServerTickOffsetAdjustmentSize = 12;

    [Tooltip("How often server sends his current tick to clients: Every X ticks")]
    public byte ServerTickHeartBeatFrequency = 30;

    [Tooltip("How often server sends his current tick to clients: Every X ticks")]
    public byte ServerTickOffsetSyncFrequency = 30;

    [Tooltip("By what amount client has to be behind before base tick adjustment ( recommended 0 )")]
    public byte ClientBaseTickBehindThreshhold = 1; //Allow the client base tick to be x ticks behind

    [Tooltip("By what amount client has to be ahead before base tick adjustment ( recommended 1 )")]
    public byte ClientBaseTickForwardThreshhold = 0; //Allow the client base tick to be x ticks ahead


    private uint _networkTickBase   = 0; //Client wont execute server commands marked with tick lower than this value
    private int  _networkTickOffset = 0; //Used to calculate how far the client should be further in the future than the server

    private uint _lastHeartBeatTick         = 0; //Used to ignore older or duplicate entries from the server
    private uint _lastSyncTick              = 0; //Used to ignore older or duplicate entries from the server syncing
    private int  _lastTickAdjustmentRequest = 0; //Used to avoid changing ticks due to network spikes ( used for consistency )

    private int[] _serverTickHBHistory     = new int[256];
    private int   _serverTickHBCount       = 0;
    private int[] _serverTickOffsetHistory = new int[256];
    private int   _serverTickOffsetCount   = 0;
    private bool  _isTickSyncQueued        = false;

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
        _networkTickBase = reader.ReadUInt() + 1; //Adding one due to the way server handles sending ticks ( in most cases the client will be behind more than it should be otherwise )
      }
    }

  #endregion


  #region Tick Ping Handling

    [Server]
    private void ServerTickHeartBeat() {
      //if (_networkTickBase % ServerTickHeartBeatFrequency == 0) {
      if (_isTickSyncQueued) {
        RpcServerTickHeartBeat(_networkTickBase);
        _isTickSyncQueued = false;
      }
    }

    [Client]
    private void ServerTickSync() {
      if (_networkTickBase % ServerTickOffsetSyncFrequency == 0 && _state != TickSyncerStateEnum.Initializing) {
        CmdPingTick((uint) (_networkTickBase + _networkTickOffset));
      }
    }

    [ClientRpc(channel = Channels.Unreliable)]
    private void RpcServerTickHeartBeat(uint serverTick) {
      if (_lastHeartBeatTick >= serverTick) return;
      _lastHeartBeatTick = serverTick;
      AddTickHbItem((int) (serverTick - _networkTickBase));
      ConsiderBaseTickAdjustment();
      _isTickSyncQueued = true;
      if (_state == TickSyncerStateEnum.Initializing) {
        _state = TickSyncerStateEnum.Syncing;
        _networkTickBase = serverTick;
      }
    }


    [Command(requiresAuthority = false, channel = Channels.Unreliable)]
    private void CmdPingTick(uint clientTick, NetworkConnectionToClient sender = null) {
      RpcTickPong(sender, clientTick, (short) (clientTick - _networkTickBase)); //Return server-client tick difference ( positive = client is ahead )
    }

    [TargetRpc(channel = Channels.Unreliable)]
    private void RpcTickPong(NetworkConnection _, uint syncTick, short serverTickOffset) {
      if (_lastSyncTick >= syncTick) return;
      _lastSyncTick = syncTick;
      int tickRTT = (int) (_networkTickBase - syncTick);
      // AddTickOffsetItem(Math.Min(serverTickOffset, tickRTT));
      AddTickOffsetItem(serverTickOffset);
      if (_state == TickSyncerStateEnum.Syncing) {
        _state = TickSyncerStateEnum.Ready;
        _networkTickOffset = serverTickOffset;
        SetTickOffsetHistory(_networkTickOffset);
      }


      float average = GetFilteredAverage(GetTickOffsetCompareSequence());
      int clientServerDiff = Mathf.RoundToInt(average);
      Debug.Log($"LT[{_networkTickBase}] / LTO[{_networkTickOffset}] - RTT[{tickRTT}] STO[{serverTickOffset}] -  STOA[{clientServerDiff}] == [{average}]");
    }

  #endregion

  #region Base Tick Adjustments

    private void ConsiderBaseTickAdjustment() {
      float average = GetFilteredAverage(GetTickHbCompareSequence());
      int clientServerDiff = Mathf.RoundToInt(average);

      if (
        clientServerDiff < -ClientBaseTickForwardThreshhold  //Cleint is behind of the server below threshhold
        || clientServerDiff > ClientBaseTickBehindThreshhold //Cleint is ahead of the server above threshhold
      ) {
        AdjustBaseTick(clientServerDiff);
        return;
      }

      _lastTickAdjustmentRequest = 0;
    }


    private void AdjustBaseTick(int adjustment) {
      if (_lastTickAdjustmentRequest != adjustment) {
        _lastTickAdjustmentRequest = adjustment;
        return;
      }

      /* We want to be at the same tick as the server or 1 tick ahead of the server */
      _networkTickBase += (uint) adjustment;                                      //Adjust the base tick by the amout changed due to latency
      _networkTickOffset = NonNegativeValue(_networkTickOffset - adjustment * 2); //Adjust the offset as well - usually this will be caused by change in latency

      ResetBuffers();
      SetTickOffsetHistory(_networkTickOffset);
      AdjustClientPhysicsTick(adjustment);
      Debug.Log($"Adjusted Client Base Tick By {adjustment}");
    }

    private void ResetBuffers() {
      _lastTickAdjustmentRequest = 0;
      _serverTickHBHistory = new int[256];
      _serverTickHBCount = 0;
    }

    private void SetTickOffsetHistory(int tickOffset) {
      for (int i = 0; i < ServerTickOffsetAdjustmentSize; i++) {
        AddTickOffsetItem(tickOffset);
      }
    }

  #endregion

  #region Tick Update Handling

    [Server]
    public virtual void ServerFixedUpdate(double deltaTime) {
      ServerTickHeartBeat(); //Send right after tick update to increase consistency
    }

    [Client]
    public virtual void ClientFixedUpdate(double deltaTime) {
      ServerTickSync(); //Send right after tick update to increase consistency
    }

    private void TickAdvance() {
      uint deltaTicks = GetDeltaTicks();
      _networkTickBase += deltaTicks;
    }

    public virtual void FixedUpdate() {
      /* Start Logic */
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
          PhysicStepSkip(Time.fixedDeltaTime, Time.deltaTime, (int) GetDeltaTicks(), _skipPhysicsSteps)
        );
        return;
      }

      if (_forwardPhysicsSteps > 0) {
        _forwardPhysicsSteps = NonNegativeValue(
          PhysicStepFastForward(Time.fixedDeltaTime, Time.deltaTime, (int) GetDeltaTicks(), _forwardPhysicsSteps)
        );
        return;
      }

      PhysicStep(Time.deltaTime, (int) GetDeltaTicks());
    }

  #endregion

  #region Physics Virtual Callbacks

    public virtual void PhysicStep(float deltaTime, int deltaTicks) { }

    [Client]
    public virtual int PhysicStepSkip(float fixedDeltaTime, float deltaTime, int deltaTicks, int skipSteps) {
      Debug.Log($"Ignored 'PhysicStep' step and calling PhysicStepSkip( {fixedDeltaTime}, {deltaTime}, {deltaTicks}, {skipSteps} )");
      return skipSteps - 1;
    }

    [Client]
    public virtual int PhysicStepFastForward(float fixedDeltaTime, float deltaTime, int deltaTicks, int fastForwardSteps) {
      Debug.Log($"Ignored 'PhysicStep' step and calling PhysicStepFastForward( {fixedDeltaTime}, {deltaTime}, {deltaTicks}, {fastForwardSteps} )");
      return 0;
    }

  #endregion

  #region Helper Functions

    private void AddTickHbItem(int item) {
      _serverTickHBHistory[(byte) _serverTickHBCount] = item;
      _serverTickHBCount++;
    }

    private int GetTickHbItem(int index) {
      return _serverTickHBHistory[(byte) index];
    }

    private void AddTickOffsetItem(int item) {
      _serverTickOffsetHistory[(byte) _serverTickOffsetCount] = item;
      _serverTickOffsetCount++;
    }

    private int GetTickOffsetItem(int index) {
      return _serverTickOffsetHistory[(byte) index];
    }

    private int[] GetTickHbCompareSequence() {
      int[] result = new int[ServerTickAdjustmentSize];
      int offset = _serverTickHBCount - ServerTickAdjustmentSize;
      for (int i = 0; i < ServerTickAdjustmentSize; i++) {
        result[i] = GetTickHbItem(i + offset);
      }

      return result;
    }

    private int[] GetTickOffsetCompareSequence() {
      int[] result = new int[ServerTickOffsetAdjustmentSize];
      int offset = _serverTickOffsetCount - ServerTickOffsetAdjustmentSize;
      for (int i = 0; i < ServerTickOffsetAdjustmentSize; i++) {
        result[i] = GetTickOffsetItem(i + offset);
      }

      return result;
    }

    private static int NonNegativeValue(int value) => value > 0 ? value : 0;

    private uint GetDeltaTicks() {
      return (uint) (Time.deltaTime / Time.fixedDeltaTime);
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