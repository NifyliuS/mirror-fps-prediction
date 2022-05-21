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
      Ready,
    }

    private struct SyncResponse{
      public double PredictionTick;
      public double PredictionTickOffset;
      public double ServerTick;
      public double ServerTickOffset;
      public double LocalTick;
      public double LocalTickOffset;
      public double HeartBeatOffset;
    }

    private struct SyncRequest{
      public int RequestId;
      public uint BaseTick;
      public uint PredictionTick;
    }

    private Buffer256<SyncRequest> _syncRequestBuffer = new Buffer256<SyncRequest>();
    private Buffer256<SyncResponse> _syncBuffer = new Buffer256<SyncResponse>();

    private TickSyncerStateEnum _state = TickSyncerStateEnum.Initializing; //Defined in what state the NetworkTicker is

    private int _forwardPhysicsSteps = 0; //Used to fast forward physics engine
    private int _skipPhysicsSteps = 0; //Used to skip ticks or reverse the physics engine


    [Header("Ping Settings")] [Tooltip("How often server sends his current tick to clients: Every X ticks")]
    public byte ServerTickOffsetSyncFrequency = 25;

    [Tooltip("Tick compensation desired deviation limit")]
    public int TickDeviation = 2;

    [Tooltip("Max allowed ticks client will be ahead when packets arrive on the server")]
    public int MinClientBaseAhead = 5;

    [Tooltip("Max allowed ticks client will be ahead when packets arrive on the server")]
    public int MinClientPredictionAhead = 5;

    [Header("Base Tick Accuracy Settings")] [Tooltip("How many previous ticks to consider when adjusting")]
    public int ServerTickAdjustmentSize = 10;

    private uint _networkTickBase = 0; //Client wont execute server commands marked with tick lower than this value
    private uint _networkTickPrediction = 0;

    private int _lastSyncTick = -1; //Used to ignore older or duplicate entries from the server syncing
    private double _lastTickStart; //Used to calculate time from tick start
    private ExponentialMovingAverage _tickPrecision = new ExponentialMovingAverage(25);

    #region Initial Sync/Spawn

    private void Awake() {
      _tickPrecision = new ExponentialMovingAverage(ServerTickAdjustmentSize);
      _lastTickStart = Time.fixedTimeAsDouble;
    }

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
        _networkTickBase = (uint)(reader.ReadUInt() - MinClientBaseAhead - 1);
        _networkTickPrediction = _networkTickBase;
        //Adding one due to the way server handles sending ticks ( in most cases the client will be behind more than it should be otherwise )
      }
    }

    #endregion

    #region Tick Ping Handling

    [Command(channel = Channels.Unreliable, requiresAuthority = false)]
    private void CmdPingTick(byte clientTickId, NetworkConnectionToClient sender = null) {
      byte fraction = GetTickFraction(); // Get tick fraction as soon as possible
      RpcTickPong(sender, clientTickId, (ushort)(_networkTickBase), fraction); //Return server-client tick difference
    }

    [TargetRpc(channel = Channels.Unreliable)]
    private void RpcTickPong(NetworkConnection _, byte clientTickId, ushort serverTickUShort, byte tickFraction) {
      byte localTickFraction = GetTickFraction(); // Get tick fraction as soon as possible
      HandleTickPong(clientTickId, serverTickUShort, tickFraction, localTickFraction);
    }

    #endregion

    #region Tick Adjustments

    private void HandleTickPong(byte clientTickId, ushort serverTickUShort, byte tickFraction, byte localTickFraction) {
      SyncRequest request = _syncRequestBuffer.Get(clientTickId);
      int requestId = request.RequestId;
      if (_lastSyncTick >= requestId) return; //Avoid duplicates
      _lastSyncTick = requestId;

      uint localTick = request.BaseTick;
      uint predictionTick = request.PredictionTick;
      double localOffset = _networkTickBase + localTickFraction / 100f - localTick;
      double serverOffset = GetUshortFixedDiff(serverTickUShort, (ushort)localTick) + tickFraction / 100f;
      double serverTick = localTick + serverOffset;
      double predictionTickOffset = predictionTick - serverTick;
      double heartBeatOffset = serverOffset - localOffset;

      var syncItem = new SyncResponse() {
        PredictionTick = predictionTick,
        PredictionTickOffset = predictionTickOffset,
        HeartBeatOffset = heartBeatOffset,
        LocalTick = localTick,
        LocalTickOffset = localOffset,
        ServerTick = serverTick,
        ServerTickOffset = serverOffset,
      };
      _syncBuffer.Add(syncItem);
      if (_state == TickSyncerStateEnum.Initializing) {
        _state = TickSyncerStateEnum.Ready;
        _networkTickPrediction = (uint)(_networkTickBase + Mathf.CeilToInt((float)(MinClientPredictionAhead - predictionTickOffset) + 1));
        return;
      }

      RunAdjustments(syncItem);


      Debug.Log(
        $"    <color=green>currentTick={_networkTickBase + localTickFraction / 100f} </color>\n" +
        $"                        predictionTick=[{predictionTick}]");
      Debug.Log(
        $"    heartBeatOffset=[{(float)(heartBeatOffset)}] / {(float)_tickPrecision.Value}\n" +
        $"                        PredictionTickOffset=[{(float)(predictionTickOffset)}] / {(float)_tickPrecision.Value}");
      Debug.Log(
        $"    serverTick=[{serverTick}]\n" +
        $"                        serverOffset=[{(float)serverOffset}] / [{(float)tickFraction}]");
      Debug.Log(
        $"    localTick=[{localTick}]\n" +
        $"                        localOffset=[{(float)localOffset}] / {(float)localOffset * 20}ms");
      Debug.Log("==========================================");
    }

    private void RunAdjustments(SyncResponse syncItem) {
      (double baseMin, double baseMax, double predictMin, double predictMax) = GetMinMaxBasePrecision();
      _tickPrecision.Add(Math.Max(baseMax - baseMin, predictMax - predictMin));
      float compareValue = TickDeviation + TickDeviation * Mathf.InverseLerp(TickDeviation, TickDeviation * 2, (float)_tickPrecision.Value);
      int baseTickAdjustment = CheckAdjustBaseTick(syncItem, baseMin, compareValue);
      int predictionTickAdjustment = CheckAdjustPredictionTick(syncItem, predictMin, compareValue);
    }

    private int CheckAdjustBaseTick(SyncResponse syncItem, double minValue, float compareValue) {
      if (syncItem.HeartBeatOffset < MinClientBaseAhead) {
        return AdjustBaseTick(Mathf.FloorToInt((float)(syncItem.HeartBeatOffset - MinClientBaseAhead)));
      }

      if (_syncBuffer.Count > ServerTickAdjustmentSize) {
        double minDiff = minValue - MinClientBaseAhead;
        if (minDiff > compareValue) {
          return AdjustBaseTick(Mathf.CeilToInt((float)minDiff) - TickDeviation);
        }
      }

      return 0;
    }

    private int CheckAdjustPredictionTick(SyncResponse syncItem, double minValue, float compareValue) {
      if (syncItem.PredictionTickOffset < MinClientPredictionAhead) {
        return AdjustPredictionTick(Mathf.FloorToInt((float)(syncItem.PredictionTickOffset - MinClientPredictionAhead)));
      }

      if (_syncBuffer.Count > ServerTickAdjustmentSize) {
        double minDiff = minValue - MinClientPredictionAhead;
        if (minDiff > compareValue) {
          return AdjustPredictionTick(Mathf.CeilToInt((float)minDiff - TickDeviation));
        }
      }

      return 0;
    }

    private int AdjustBaseTick(int adjustment) {
      Debug.Log($" ================================================ Adjusted Base Tick {adjustment}");
      _networkTickBase = (uint)(_networkTickBase + adjustment);
      AdjustHistory(-adjustment, 0); //Instead of resseting the buffer we can just fix the values
      return adjustment;
    }

    private int AdjustPredictionTick(int adjustment) {
      Debug.Log($" ================================================ Adjusted Prediction Tick {-adjustment}");
      _networkTickPrediction = (uint)(_networkTickPrediction - adjustment);
      AdjustHistory(0, -adjustment); //Instead of resseting the buffer we can just fix the values
      return adjustment;
    }

    private void AdjustHistory(int baseTickAdjustment, int predictionTickAdjustment) {
      _syncBuffer.EditTail(ServerTickAdjustmentSize, item => {
        return new SyncResponse() {
          PredictionTick = item.PredictionTick,
          PredictionTickOffset = item.PredictionTickOffset + predictionTickAdjustment,
          LocalTick = item.LocalTick,
          LocalTickOffset = item.LocalTickOffset,
          ServerTick = item.ServerTick,
          ServerTickOffset = item.ServerTickOffset,
          HeartBeatOffset = item.HeartBeatOffset + baseTickAdjustment,
        };
      });
    }

    private (double, double, double, double) GetMinMaxBasePrecision() {
      (double baseMin, double baseMax) = GetMinMaxD(
        Array.ConvertAll(
          _syncBuffer.GetTail(ServerTickAdjustmentSize),
          x => Math.Round(x.HeartBeatOffset * 10) / 10) //round to 1 tenth to reduce noise
      );
      (double predictMin, double predictMax) = GetMinMaxD(
        Array.ConvertAll(
          _syncBuffer.GetTail(ServerTickAdjustmentSize),
          x => Math.Round(x.PredictionTickOffset * 10) / 10) //round to 1 tenth to reduce noise
      );
      return (baseMin, baseMax, predictMin, predictMax);
    }

    #endregion

    #region Tick Update Handling

    [Server]
    public virtual void ServerFixedUpdate(double deltaTime) {
    }

    [Client]
    public virtual void ClientFixedUpdate(double deltaTime) {
    }

    [Client]
    public virtual void ClientSendPing() {
      if (_networkTickBase % ServerTickOffsetSyncFrequency == 0) {
        CmdPingTick((byte)_syncRequestBuffer.Add(new SyncRequest() {
          RequestId = _syncRequestBuffer.Count,
          BaseTick = _networkTickBase,
          PredictionTick = _networkTickPrediction,
        }));
      }
    }

    private void TickAdvance() {
      uint deltaTicks = GetDeltaTicks();
      _networkTickBase += deltaTicks;
      _networkTickPrediction += deltaTicks;
    }


    public virtual void FixedUpdate() {
      _lastTickStart = Time.fixedTimeAsDouble;
      TickAdvance();
      if (isServer) ServerFixedUpdate(Time.deltaTime);
      else {
        ClientSendPing();
        ClientFixedUpdate(Time.deltaTime);
      }

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

    private uint GetDeltaTicks() => (uint)(Time.deltaTime / Time.fixedDeltaTime);

    //Even though we split each tick to 100 sometimes we see 104 or larger ticks - we allow for this by not using 255 split but using byte to transfer
    private byte GetTickFraction() {
      int tickFraction =
        Mathf.RoundToInt((float)((Time.timeAsDouble - _lastTickStart) / Time.fixedDeltaTime * 100));
      if (tickFraction > 255) tickFraction = 255;
      return (byte)(tickFraction);
    }

    private static int GetUshortFixedDiff(ushort one, ushort two) {
      int diff = one - two;
      if (diff > 32767) return diff - 65536;
      if (diff < -32767) return diff + 65536;
      return diff;
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

    #endregion
  }
}