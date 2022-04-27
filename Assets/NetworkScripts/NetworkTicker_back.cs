using System.Diagnostics;
using Mirror;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace NetworkScripts {
  public class NetworkTickerOld : NetworkBehaviour {
    private enum TickSyncerStateEnum {
      Initializing,
      Ready,
      Pending,
      ReSyncing,
      ReSyncPending,
    }

    private struct TickPingItem {
      public int TickRTT;
      public int TickFixAmount;
    }

    private struct ServerHeartBeatItem {
      public uint LocalTick;
      public uint RemoteTick;
      public int  Offset;
    }


    [Tooltip("Amout of ticks to Average out to smooth network inconsistencies")]
    public static int ServerTickAdjustmentSize = 7;

    [Tooltip("How often server sends his current tick to clients: Every X ticks")]
    public byte ServerTickHeartBeatFrequency = 30;

    [Tooltip("How often clients sends tick ping to server: Every X ticks")]
    public byte CleintTickPingFrequency = 30;

    [Tooltip("By what amount client has to be behind before base tick adjustment ( recommended 0 )")]
    public byte ServerTickAdjustmentBehindThreshhold = 0;

    [Tooltip("By what amount client has to be ahead before base tick adjustment ( recommended 1 )")]
    public byte ServerTickAdjustmentForwardThreshhold = 1; //TODO: add compensation over time - if client is ahead by 1 over 30s adjust ignoring the threshold

    private const            uint          InitialTickOffset = 5; //Initial guesstimate for client Tick offset from server ( server to client )
    // [SerializeField] private TickPingState _tickPingState    = TickPingState.Initial;

    private static NetworkTick _networkTickInstance;
    private        uint        _networkTickBase   = 0;
    private        int         _networkTickOffset = 0;

    private TickSyncerStateEnum _status              = TickSyncerStateEnum.Initializing;
    private int                 _forwardPhysicsSteps = 0;
    private int                 _skipPhysicsSteps    = 0;
    private uint                _lastPingRecieved    = 0;

    private TickPingItem[] _tickPingHistory      = new TickPingItem[256]; // Circular buffer ping item history
    private int            _tickPingHistoryCount = 0;                     // Circular buffer ping item history counter

    private ExponentialMovingAverage _serverTickExponentialAverage = new ExponentialMovingAverage(ServerTickAdjustmentSize);
    private ServerHeartBeatItem[]    _serverTickHBHistory          = new ServerHeartBeatItem[256];
    private int                      _serverTickHBCount            = 0;
    private uint                     _lastServerHeartBeat          = 0; //Used to avoid duplications
    private int                      _lastTickAdjustmentRequest    = 0;

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

  #region Tick Ping Stabilization And Adjustment

  #endregion

  #region Tick Ping Handling

    [Server]
    private void ServerTickHeartBeat() {
      if (_networkTickBase % ServerTickHeartBeatFrequency == 0) {
        RpcServerTickHeartBeat(_networkTickBase);
      }
    }

    [Client]
    private void ClientSendTickPing() {
      switch (_status) {
        case TickSyncerStateEnum.Initializing:
        case TickSyncerStateEnum.Ready:
          if (_networkTickBase % CleintTickPingFrequency == 0) {
            CmdPingTick((uint)(_networkTickBase + _networkTickOffset+ _networkTickOffset)); // send current base tick - server should return the time it took to arrive
            Debug.Log($"clientServerDiff ---->> [{_networkTickBase}] // [{(uint)(_networkTickBase + _networkTickOffset)}] == {_networkTickOffset}");
          }

          break;
      }

      ;
    }

    [Command(requiresAuthority = false, channel = Channels.Unreliable)]
    private void CmdPingTick(uint clientTick, NetworkConnectionToClient sender = null) {
      RpcTickPong(sender, clientTick, (short) (clientTick - _networkTickBase)); //return client predicted tick vs server local tick ( value must be positive or the client is behind on its predictions )
    }

    [TargetRpc(channel = Channels.Unreliable)]
    private void RpcTickPong(NetworkConnection _, uint clientTick, short serverTickOffset) {
      if (_lastPingRecieved >= clientTick) return;
      AddPingTickItem(new TickPingItem() {
        TickRTT = (int) (_networkTickBase + _networkTickOffset) - (int) clientTick,
        TickFixAmount = serverTickOffset
      });

      if (_status == TickSyncerStateEnum.Initializing) {
        //_networkTickOffset = serverTickOffset;
        _status = TickSyncerStateEnum.Ready;
        return;
      }

      ConsiderOffsetTickAdjustment();


      float average = GetFilteredAverage(ConvertTickHistoryToIntByOffset(GetPingTickCompareSequence()));
      int clientServerDiff = Mathf.RoundToInt(average);
      int rtt = (int) (_networkTickBase + _networkTickOffset) - (int) clientTick;
      Debug.Log($"clientServerDiff [{clientServerDiff}][{average}]  rtt = {rtt} serverTickOffset = [{serverTickOffset}] == [{_networkTickOffset}]==");
    }

    private void ConsiderOffsetTickAdjustment() {
      float average = GetFilteredAverage(ConvertTickHistoryToIntByOffset(GetPingTickCompareSequence()));
      int clientServerDiff = Mathf.RoundToInt(average);
      // if (clientServerDiff > ServerTickAdjustmentForwardThreshhold) {
      //   AdjustBaseTick(clientServerDiff);
      //   return;
      // }
      //
      // if (clientServerDiff < -ServerTickAdjustmentBehindThreshhold) {
      //   AdjustBaseTick(clientServerDiff);
      //   return;
      // }
      //
      // _lastTickAdjustmentRequest = 0;
    }

    [ClientRpc(channel = Channels.Unreliable)]
    private void RpcServerTickHeartBeat(uint serverTick) {
      if (isServer || _lastServerHeartBeat >= serverTick) return; // Avoid old or duplicate ticks
      _lastServerHeartBeat = serverTick;
      AddTickHbItem(new ServerHeartBeatItem() {
        LocalTick = _networkTickBase,
        RemoteTick = serverTick,
        Offset = (int) (_networkTickBase - serverTick)
      });
      float average = GetFilteredAverage(ConvertHeartBeatToIntByOffset(GetTickHbCompareSequence()));

      Debug.Log(
        $"_networkTick = {_networkTickBase} serverTick = {serverTick} = {(int) (_networkTickBase - serverTick)} == [{Mathf.RoundToInt(average)}] / [{Mathf.RoundToInt((float) _serverTickExponentialAverage.Value)} || [{average}] / [{_serverTickExponentialAverage.Value}");
      ConsiderBaseTickAdjustment();
    }

    private void ConsiderBaseTickAdjustment() {
      float average = GetFilteredAverage(ConvertHeartBeatToIntByOffset(GetTickHbCompareSequence()));
      int clientServerDiff = Mathf.RoundToInt(average);

      if (clientServerDiff > ServerTickAdjustmentForwardThreshhold) {
        AdjustBaseTick(clientServerDiff);
        return;
      }

      if (clientServerDiff < -ServerTickAdjustmentBehindThreshhold) {
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

      _networkTickBase -= (uint) adjustment;
      _networkTickOffset += adjustment;  //We also move the network offset ( since base tick is sensitive to latency )
      if (_networkTickOffset < 0) {
        _networkTickOffset = 0;
      }
      Debug.Log($"clientServerDiff ------- {adjustment}");
      ResetBuffers();
      AdjustClientPhysicsTick(adjustment);
    }

    private void ResetBuffers() {
      _lastTickAdjustmentRequest = 0;
      _serverTickHBHistory = new ServerHeartBeatItem[256];
      _serverTickHBCount = 0;
      _serverTickExponentialAverage = new ExponentialMovingAverage(ServerTickAdjustmentSize);
    }

  #endregion

  #region Tick Update Handling

    [Server]
    public virtual void ServerFixedUpdate(double deltaTime) {
      ServerTickHeartBeat();
    }

    [Client]
    public virtual void ClientFixedUpdate(double deltaTime) {
      ClientSendTickPing();
    }

    private void TickAdvance() {
      uint deltaTicks = GetDeltaTicks();
      _networkTickBase += deltaTicks;
    }

    public virtual void FixedUpdate() {
      // if (isClient) RequestServerSync();
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

  #region Circular Buffer Functions

    private void AddTickHbItem(ServerHeartBeatItem item) {
      _serverTickHBHistory[(byte) _serverTickHBCount] = item;
      _serverTickHBCount++;
    }

    private ServerHeartBeatItem GetTickHbItem(int index) {
      return _serverTickHBHistory[(byte) index];
    }

    private void AddPingTickItem(TickPingItem item) {
      _tickPingHistory[(byte) _tickPingHistoryCount] = item;
      _tickPingHistoryCount++;
    }

    private TickPingItem GetPingTickItem(int index) {
      return _tickPingHistory[(byte) index];
    }

  #endregion

  #region Helper Functions

    private static int NonNegativeValue(int value) => value > 0 ? value : 0;

    private uint GetDeltaTicks() {
      return (uint) (Time.deltaTime / Time.fixedDeltaTime);
    }

    private ServerHeartBeatItem[] GetTickHbCompareSequence() {
      ServerHeartBeatItem[] result = new ServerHeartBeatItem[ServerTickAdjustmentSize];
      int offset = _serverTickHBCount - ServerTickAdjustmentSize;
      for (int i = 0; i < ServerTickAdjustmentSize; i++) {
        result[i] = GetTickHbItem(i + offset);
      }

      return result;
    }

    private TickPingItem[] GetPingTickCompareSequence() {
      TickPingItem[] result = new TickPingItem[ServerTickAdjustmentSize];
      int offset = _serverTickHBCount - ServerTickAdjustmentSize;
      for (int i = 0; i < ServerTickAdjustmentSize; i++) {
        result[i] = GetPingTickItem(i + offset);
      }

      return result;
    }

    private int[] ConvertHeartBeatToIntByOffset(ServerHeartBeatItem[] heartBeats) {
      int[] result = new int[heartBeats.Length];
      for (int i = 0; i < heartBeats.Length; i++) {
        result[i] = heartBeats[i].Offset;
      }

      return result;
    }

    private int[] ConvertTickHistoryToIntByOffset(TickPingItem[] heartBeats) {
      int[] result = new int[heartBeats.Length];
      for (int i = 0; i < heartBeats.Length; i++) {
        result[i] = heartBeats[i].TickFixAmount;
      }

      return result;
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
          _serverTickExponentialAverage.Add(array[i]);
          sumCounter++;
        }
      }


      return sum / sumCounter;
    }

  #endregion
  }
}

// private void AddPingHistoryItem(TickPingItem item) {
//   _tickPingHistory[(byte) _tickPingHistoryCount] = item;
//   _tickPingHistoryCount++;
// }
//
// private TickPingItem GetPingHistoryItem(int index) => _tickPingHistory[(byte) index];

// [Command(requiresAuthority = false, channel = Channels.Unreliable)]
// private void CmdPingTick(uint clientTick, NetworkConnectionToClient sender = null) {
//   RpcTickPong(sender, clientTick, (short) (_networkTick - clientTick));
// }
//
// [TargetRpc(channel = Channels.Unreliable)]
// private void RpcTickPong(NetworkConnection _, uint clientTick, short serverTickOffset) {
//   if (_lastPingRecieved >= clientTick) return;
//   _lastPingRecieved = clientTick;
//   AddPingHistoryItem(new TickPingItem() {
//     TickFixAmount = serverTickOffset,
//     TickRTT = _networkTick - clientTick,
//   });
//   ConsiderClientTickAdjustment();
//   ConsiderPeerTickAdjustment();
//   Debug.Log($"clientTickTime = {_networkTick - clientTick} serverTickOffset = {serverTickOffset}");
// }

// [Client]
// private void RequestServerSync() {
//   switch (_status) {
//     case TickSyncerStateEnum.Initializing:
//       if (_networkTick % TickInitFrequency == 0) {
//         if (_initTickCount < TickInitThreshold) CmdPingTick(_networkTick);
//         else _status = TickSyncerStateEnum.Pending; //Switch to pending state and wait for initialization resolution
//         _initTickCount++;
//         //TODO: add server side ping dos protection
//       }
//
//       break;
//     case TickSyncerStateEnum.Pending: //Keep sending ticks in Pending state in case of lost packets or large latency
//     case TickSyncerStateEnum.Ready:
//       if (_networkTick % TickFrequency == 0) {
//         CmdPingTick(_networkTick);
//       }
//
//       break;
//     case TickSyncerStateEnum.ReSyncing:
//       if (_networkTick % TickReSyncFrequency == 0) {
//         if (_reSyncTickCount < TickInitThreshold) {
//           _reSyncTickCount++;
//           CmdPingTick(_networkTick);
//         }
//         else {
//           _status = TickSyncerStateEnum.ReSyncPending; //Switch to pending state and wait for initialization resolution
//           _reSyncTickCount = 0;
//         }
//         //TODO: add server side ping dos protection
//       }
//
//       break;
//   }
// }