#define onlySyncOnChange_BANDWIDTH_SAVING
using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using UnityEngine.Animations;

/*
    Documentation: https://mirror-networking.gitbook.io/docs/components/network-transform
    API Reference: https://mirror-networking.com/docs/api/Mirror.NetworkTransformBase.html
*/
namespace NetworkScripts {
  public struct ParentOffset {
    public Vector3    PositonOffset;
    public Quaternion RotationOffset;
    public Vector3    ScaleOffset;
  }

  public class DynamicNetworkTransform : NetworkTransformBase {
    protected override       Transform       targetComponent => transform;
    [SerializeField] private Transform       _parentTransform;
    [SerializeField] private NetworkIdentity _parentIdentity;
    private ParentOffset    _parentOffset;
    
    public                   NetworkIdentity ParentNetworkIdentity  => _parentIdentity;
    public                   Transform       ParentNetworkTransform => _parentTransform;

    public void SetNetworkTransformParent(NetworkIdentity identity) {
      _parentTransform = identity.transform;
      _parentIdentity = identity;
    }

    public void UnSetNetworkTransformParent() {
      _parentTransform = null;
      _parentIdentity = null;
    }

    public void SetParentOffset(Vector3? position, Quaternion? rotation, Vector3? scale) {
      _parentOffset = new ParentOffset();
      if (position != null) _parentOffset.PositonOffset = (Vector3) position;
      if (rotation != null) _parentOffset.RotationOffset = (Quaternion) rotation;
      if (scale != null) _parentOffset.ScaleOffset = (Vector3) scale;
    }


    [Command(channel = Channels.Unreliable)]
    void CmdClientToServerParentOffsetSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      //TODO: Add client authority integration
    }

    [ClientRpc(channel = Channels.Unreliable)]
    void RpcServerToClientParentOffsetSync(Vector3? position, Quaternion? rotation, Vector3? scale)
      => SetParentOffset(position, rotation, scale);


    public void UpdateParentOffset() {
      if (_parentIdentity) {
        SetParentOffset(
          _parentTransform.position - targetComponent.position,
          _parentTransform.rotation * Quaternion.Inverse(targetComponent.rotation),
          _parentTransform.localScale - targetComponent.localScale
        );
      }
    }


    /* Called by CmdClientToServerSync() */
    protected override void OnClientToServerSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      // Debug.Log("OnClientToServerSync");
      UpdateParentOffset();
      RpcServerToClientParentOffsetSync(_parentOffset.PositonOffset, _parentOffset.RotationOffset, _parentOffset.ScaleOffset);
      if (_parentIdentity) {
        base.OnClientToServerSync(
          _parentTransform.position + _parentOffset.PositonOffset,
          _parentTransform.rotation * _parentOffset.RotationOffset,
          _parentTransform.localScale + _parentOffset.ScaleOffset
        );
      }
      else {
        base.OnClientToServerSync(position, rotation, scale);
      }
    }

    /* Called by RpcServerToClientSync() */
    protected override void OnServerToClientSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      // Debug.Log("OnServerToClientSync");
      if (_parentIdentity) {
        base.OnServerToClientSync(
          _parentTransform.position + _parentOffset.PositonOffset,
          _parentTransform.rotation * _parentOffset.RotationOffset,
          _parentTransform.localScale + _parentOffset.ScaleOffset
        );
      }

      base.OnServerToClientSync(position, rotation, scale);
    }


    // If you need this template to reference a child target,
    // replace the line above with the code below.

    /*
    [Header("Target")]
    public Transform target;

    protected override Transform targetComponent => target;
    */

  #region Unity Callbacks

    protected override void OnValidate() {
      base.OnValidate();
    }

    /// <summary>
    /// This calls Reset()
    /// </summary>
    protected override void OnEnable() {
      base.OnEnable();
    }

    /// <summary>
    /// This calls Reset()
    /// </summary>
    protected override void OnDisable() {
      base.OnDisable();
    }

    /// <summary>
    /// Buffers are cleared and interpolation times are reset to zero here.
    /// This may be called when you are implementing some system of not sending
    /// if nothing changed, or just plain resetting if you have not received data
    /// for some time, as this will prevent a long interpolation period between old
    /// and just received data, as it will look like a lag. Reset() should also be
    /// called when authority is changed to another client or server, to prevent
    /// old buffers bugging out the interpolation if authority is changed back.
    /// </summary>
    public override void Reset() {
      base.Reset();
    }

  #endregion

  #region NT Base Callbacks

    /// <summary>
    /// NTSnapshot struct is created from incoming data from server
    /// and added to SnapshotInterpolation sorted list.
    /// You may want to skip calling the base method for the local player
    /// if doing client-side prediction, or perhaps pass altered values,
    /// or compare the server data to local values and correct large differences.
    /// </summary>
    /// <summary>
    /// NTSnapshot struct is created from incoming data from client
    /// and added to SnapshotInterpolation sorted list.
    /// You may want to implement anti-cheat checks here in client authority mode.
    /// </summary>
    /// <summary>
    /// Called by both CmdTeleport and RpcTeleport on server and clients, respectively.
    /// Here you can disable a Character Controller before calling the base method,
    /// and re-enable it after the base method call to avoid conflicting with it.
    /// </summary>
    protected override void OnTeleport(Vector3 destination) {
      base.OnTeleport(destination);
    }

    /// <summary>
    /// Called by both CmdTeleport and RpcTeleport on server and clients, respectively.
    /// Here you can disable a Character Controller before calling the base method,
    /// and re-enable it after the base method call to avoid conflicting with it.
    /// </summary>
    protected override void OnTeleport(Vector3 destination, Quaternion rotation) {
      base.OnTeleport(destination, rotation);
    }

    /// <summary>
    /// NTSnapshot struct is created here
    /// </summary>
    protected override NTSnapshot ConstructSnapshot() {
      return base.ConstructSnapshot();
    }

    /// <summary>
    /// localPosition, localRotation, and localScale are set here:
    /// interpolated values are used if interpolation is enabled.
    /// goal values are used if interpolation is disabled.
    /// </summary>
    protected override void ApplySnapshot(NTSnapshot start, NTSnapshot goal, NTSnapshot interpolated) {
      base.ApplySnapshot(start, goal, interpolated);
    }

    #if onlySyncOnChange_BANDWIDTH_SAVING

    /// <summary>
    /// Returns true if position, rotation AND scale are unchanged, within given sensitivity range.
    /// </summary>
    protected override bool CompareSnapshots(NTSnapshot currentSnapshot) {
      return base.CompareSnapshots(currentSnapshot);
    }

    #endif

  #endregion


  #region GUI

    #if UNITY_EDITOR || DEVELOPMENT_BUILD
    // OnGUI allocates even if it does nothing. avoid in release.

    protected override void OnGUI() {
      base.OnGUI();
    }

    protected override void DrawGizmos(SortedList<double, NTSnapshot> buffer) {
      base.DrawGizmos(buffer);
    }

    protected override void OnDrawGizmos() {
      base.OnDrawGizmos();
    }

    #endif

  #endregion
  }
}