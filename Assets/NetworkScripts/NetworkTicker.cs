using System;
using System.Collections;
using System.Collections.Generic;
using Mirror;
using NetworkScripts;
using UnityEngine;

namespace NetworkScripts {
  public class NetworkTicker : NetworkBehaviour {
    private enum TickSyncerStateEnum {
      Initial,
      Ready,
    }

    [Header("Synchronization Settings")] [Tooltip("How often should the client verify if its in-sync with the server: Every X ticks")]
    public int TickFrequency = 30;


    private static NetworkTick   _networkTickInstance;
    private        uint          _networkTick   = 0;
    private        TickPingState _tickPingState = TickPingState.Initial;

    private int _forwardPhysicsSteps = 0;
    private int _skipPhysicsSteps    = 0;


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
        _networkTick = reader.ReadUInt();
      }
    }

  #endregion

  #region Tick Update Handling

    [Client]
    private void RequestServerSync() {
      if (_networkTick % TickFrequency == 0) {
        //
      }
    }

    [Server]
    public virtual void ServerFixedUpdate(double deltaTime) { }

    [Client]
    public virtual void ClientFixedUpdate(double deltaTime) { }

    public virtual void FixedUpdate() {
      if (!NetworkServer.active) return;

      if (isServer) ServerFixedUpdate(Time.deltaTime);
      else ClientFixedUpdate(Time.deltaTime);

      PhysicsStepHandle();
    }

  #endregion

  #region Physics Change Handling

    private void PhysicsStepHandle() {
      if (_skipPhysicsSteps > 0) {
        _skipPhysicsSteps = NonNegativeValue(
          PhysicStepSkip(Time.fixedDeltaTime, Time.deltaTime, GetDeltaTicks(), _skipPhysicsSteps)
        );
        return;
      }

      if (_forwardPhysicsSteps > 0) {
        _forwardPhysicsSteps = NonNegativeValue(
          PhysicStepFastForward(Time.fixedDeltaTime, Time.deltaTime, GetDeltaTicks(), _forwardPhysicsSteps)
        );
        return;
      }

      PhysicStep(Time.deltaTime, GetDeltaTicks());
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

    private int GetDeltaTicks() {
      return (int) (Time.deltaTime / Time.fixedDeltaTime);
    }

  #endregion
  }
}