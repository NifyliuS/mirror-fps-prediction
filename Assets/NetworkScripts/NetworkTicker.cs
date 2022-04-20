using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using NetworkScripts;
using UnityEngine;

namespace NetworkScripts {
  public class NetworkTicker : NetworkBehaviour {
    private enum TickSyncerStateEnum {
      Initializing,
      Ready,
      Pending,
      ReSyncing,
    }

    private struct TickPingItem {
      public uint TickRTT;
      public int  TickFixAmount;
    }

    private const uint InitialTickOffset = 5; //Initial guesstimate for client Tick offset from server ( server to client )

    [Header("Synchronization Settings")] [Tooltip("When client just connected how often should we ping the server: Every X ticks")]
    public int TickInitFrequency = 30;

    [Tooltip("How many pings to send before exiting initialization state")]
    public int TickInitThreshold = 12;


    [Tooltip("When client is ready how often should we ping the server: Every X ticks")]
    public int TickFrequency = 30;

    [Tooltip("When client is re-syncing how often should we ping the server: Every X ticks")]
    public int TickReSyncFrequency = 30;


    private static NetworkTick   _networkTickInstance;
    private        uint          _networkTick     = 0;
    private        uint          _networkPeerTick = 0;
    private        TickPingState _tickPingState   = TickPingState.Initial;

    private TickSyncerStateEnum _status              = TickSyncerStateEnum.Initializing;
    private int                 _forwardPhysicsSteps = 0;
    private int                 _skipPhysicsSteps    = 0;
    private uint                _lastPingRecieved    = 0;

    private TickPingItem[] _tickPingHistory      = new TickPingItem[255]; // Circular buffer ping item history
    private int            _tickPingHistoryCount = 0;                     // Circular buffer ping item history counter
    private int            _initTickCount        = 0;

  #region Initial Sync/Spawn

    public override bool OnSerialize(NetworkWriter writer, bool initialState) {
      base.OnSerialize(writer, initialState);
      if (initialState) {
        writer.WriteUInt(_networkTick);
        return true;
      }

      return false;
    }

    public override void OnDeserialize(NetworkReader reader, bool initialState) {
      base.OnDeserialize(reader, initialState);
      if (initialState) {
        uint serverTick = reader.ReadUInt();
        _networkTick = serverTick + InitialTickOffset;     //Assume initial ping between client and server ( server to client )
        _networkPeerTick = serverTick - InitialTickOffset; //Assume initial ping between client and server ( client to server to client )
      }
    }

  #endregion

  #region Tick Ping Stabilization And Adjustment

    public virtual bool ConsiderClientTickAdjustment() {
      return false;
    }

    public virtual bool ConsiderPeerTickAdjustment() {
      return false;
    }

  #endregion

  #region Tick Ping Handling

    [Command(requiresAuthority = false, channel = Channels.Unreliable)]
    private void CmdPingTick(uint clientTick, NetworkConnectionToClient sender = null) {
      RpcTickPong(sender, clientTick, (short) (_networkTick - clientTick));
    }

    [TargetRpc(channel = Channels.Unreliable)]
    private void RpcTickPong(NetworkConnection _, uint clientTick, short serverTickOffset) {
      if (_lastPingRecieved >= clientTick) return;
      _lastPingRecieved = clientTick;
      AddPingHistoryItem(new TickPingItem() {
        TickFixAmount = serverTickOffset,
        TickRTT = _networkTick - clientTick,
      });
      ConsiderClientTickAdjustment();
      ConsiderPeerTickAdjustment();
      Debug.Log($"clientTickTime = {_networkTick - clientTick} serverTickOffset = {serverTickOffset}");
    }

  #endregion

  #region Tick Update Handling

    [Client]
    private void RequestServerSync() {
      switch (_status) {
        case TickSyncerStateEnum.Initializing:
          if (_networkTick % TickInitFrequency == 0) {
            if (_initTickCount < TickInitThreshold) CmdPingTick(_networkTick);
            else _status = TickSyncerStateEnum.Pending; //Switch to pending state and wait for initialization resolution
            _initTickCount++;
            //TODO: add server side ping dos protection
          }

          break;
        case TickSyncerStateEnum.Pending: //Keep sending ticks in Pending state in case of lost packets or large latency
        case TickSyncerStateEnum.Ready:
          if (_networkTick % TickFrequency == 0) {
            CmdPingTick(_networkTick);
          }

          break;
        case TickSyncerStateEnum.ReSyncing:
          if (_networkTick % TickReSyncFrequency == 0) {
            CmdPingTick(_networkTick);
          }

          break;
      }
    }

    [Server]
    public virtual void ServerFixedUpdate(double deltaTime) { }

    [Client]
    public virtual void ClientFixedUpdate(double deltaTime) {
      //RequestServerSync();
    }

    private void TickAdvance() {
      uint deltaTicks = GetDeltaTicks();
      _networkTick += deltaTicks;
      _networkPeerTick += deltaTicks;
    }

    public virtual void FixedUpdate() {
      if (isClient) RequestServerSync();
      /* Start Logic */
      TickAdvance();
      if (isServer) ServerFixedUpdate(Time.deltaTime);
      else ClientFixedUpdate(Time.deltaTime);
      /* Update Physics */
      PhysicsStepHandle();
    }

  #endregion

  #region Physics Change Handling

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

    private int NonNegativeValue(int value) => value > 0 ? value : 0;

    private uint GetDeltaTicks() {
      return (uint) (Time.deltaTime / Time.fixedDeltaTime);
    }

    private void AddPingHistoryItem(TickPingItem item) {
      _tickPingHistory[(byte) _tickPingHistoryCount] = item;
      _tickPingHistoryCount++;
    }

    private TickPingItem GetPingHistoryItem(int index) => _tickPingHistory[(byte) index];

  #endregion
  }
}