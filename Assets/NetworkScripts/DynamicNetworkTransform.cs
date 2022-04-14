#define onlySyncOnChange_BANDWIDTH_SAVING
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using Mirror;
using UnityEngine.Animations;

/*
    Documentation: https://mirror-networking.gitbook.io/docs/components/network-transform
    API Reference: https://mirror-networking.com/docs/api/Mirror.NetworkTransformBase.html
*/
namespace NetworkScripts {
  public class DynamicNetworkTransform : NetworkTransformBase {
    protected override Transform       targetComponent        => transform;
    public             NetworkIdentity ParentNetworkIdentity  => _parentIdentity;
    public             Transform       ParentNetworkTransform => _parentTransform;

    private Transform       _parentTransform;
    [SyncVar]private NetworkIdentity _parentIdentity;

    [SerializeField] private Nullable<Vector3>    _positionOffset = null;
    [SerializeField] private Nullable<Quaternion> _rotationOffset = null;
    [SerializeField] private Nullable<Vector3>    _scaleOffset    = null;

    public void SetNetworkTransformParent(NetworkIdentity identity) {
      _parentTransform = identity.transform;
      _parentIdentity = identity;
      UpdateParentOffset();
      RpcUpdateNetworkParent(identity);
    }

    public void UnSetNetworkTransformParent() {
      _parentTransform = null;
      _parentIdentity = null;
      RpcClearNetworkParent();
    }

    [Command(channel = Channels.Reliable)]
    public void CmdSetNetworkParent(NetworkIdentity identity) { }

    [Command(channel = Channels.Reliable)]
    public void CmdUnSetNetworkParent(NetworkIdentity identity) { }

    [ClientRpc(channel = Channels.Reliable)]
    public void RpcUpdateNetworkParent(NetworkIdentity identity) {
      _parentTransform = identity.transform;
      _parentIdentity = identity;
    }

    [ClientRpc(channel = Channels.Reliable)]
    public void RpcClearNetworkParent() {
      _parentTransform = null;
      _parentIdentity = null;
    }

    public void SetParentOffset(Vector3? position, Quaternion? rotation, Vector3? scale) {
      _positionOffset = position;
      _rotationOffset = rotation;
      _scaleOffset = scale;
    }


    [Command(channel = Channels.Unreliable)]
    void CmdClientToServerParentOffsetSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      //TODO: Add client authority integration
    }

    [ClientRpc(channel = Channels.Unreliable)]
    void RpcServerToClientParentOffsetSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      SetParentOffset(position, rotation, scale);
    }


    public void UpdateParentOffset() {
      if (_parentIdentity) {
        SetParentOffset(
          targetComponent.position - _parentTransform.position,
          targetComponent.rotation * Quaternion.Inverse(_parentTransform.rotation),
          targetComponent.localScale - _parentTransform.localScale
        );
      }
    }

 
      
    private bool _isParented = false;
    
    /* Called by RpcServerToClientSync() */
    protected override void OnServerToClientSync(Vector3? position, Quaternion? rotation, Vector3? scale) {
      if (_parentIdentity) {
        
        if (isServer) {
          UpdateParentOffset();
          RpcServerToClientParentOffsetSync(_positionOffset, _rotationOffset, _scaleOffset);
        }
       
      
        if (!_isParented && _positionOffset.HasValue && !isServer) {
          Vector3 _localOffset = targetComponent.position - _parentTransform.position;
          OnTeleport((Vector3)_localOffset);
          targetComponent.position = targetComponent.position + _localOffset;
          targetComponent.localPosition = _parentTransform.position + _localOffset;
          base.OnServerToClientSync(
            _localOffset,
            rotation,
            scale
          ); 
          _isParented = true;
        }
        else {
          base.OnServerToClientSync(
            _positionOffset,
            rotation,
            scale
          ); 
        }
        
       
        
      }
      else {
        if (_isParented) {
          if (position.HasValue) {
            OnTeleport((Vector3)position);  
            targetComponent.position = (Vector3)position;
            targetComponent.localPosition = (Vector3)position;
          }
          base.OnServerToClientSync(
            position, 
            rotation, 
            scale
            );
          _isParented = false;
        } 
          base.OnServerToClientSync(position, rotation, scale);
      }
    }

    private Vector3 lastKnownPosition;
    protected override void ApplySnapshot(NTSnapshot start, NTSnapshot goal, NTSnapshot interpolated) {
      if (_parentIdentity && _isParented) {
        interpolated.position += _parentTransform.position;
         goal.position += _parentTransform.position;
         lastKnownPosition = _parentTransform.position;
      }
      else {
        if (_isParented) {
          interpolated.position += lastKnownPosition;
          goal.position += lastKnownPosition;
        }
      }
      base.ApplySnapshot(start, goal, interpolated);
    }
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